using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Jobs;
using WorkUp.Infrastructure.Notifications;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Scheduler core, manual runner and DI wiring (T23). It proves, against hand-written ports and the real composition
/// root, that: the hosted worker runs one immediate tick and then repeats on the <see cref="PeriodicTimer"/>; every
/// tick runs in a fresh scope that is disposed before the next tick; the clock is read per invocation and never at
/// construction; jobs are extensible through the scoped <see cref="IScheduledJob"/> registry and runnable one by one
/// by their stable key for the T26 tools; a failing tick logs a fixed message plus the exception type only and never
/// provider text, a recipient, a body or a password, and does not stop the following ticks; shutdown cancels a running
/// tick without hanging; the outbox tick claims nothing and contacts no provider while the mode retains notices; and
/// the composition root fails fast on an unknown mode or on unusable SMTP settings without echoing any configured
/// value, before any database claim or migration. With the T24 reminder job wired in, it also proves that the
/// composition root registers the reminder graph scoped and before the outbox job, that a due reminder queued in a
/// tick is dispatched by the outbox job of that same tick in SMTP mode and retained <c>Pending</c> in Outbox mode,
/// and that the clock is read again for every tick instead of cached.
/// </summary>
public sealed class ScheduledJobsTests
{
    private static readonly DateTime Start = new(2026, 3, 10, 9, 0, 0);

    private const string BaseUrl = "https://reservas.example.com";

    /// <summary>A database that is never created, so any real claim or migration would fail loudly.</summary>
    private const string NeverOpenableConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT23SchedulerNeverOpened;Integrated Security=true;Connect Timeout=1";

    // ---- Manual runner: clock per invocation, keyed runs and cancellation (T23, T26) ----

    [Fact]
    public async Task Runner_reads_the_clock_on_every_run_and_never_at_construction()
    {
        var clock = new FakeClock { Now = Start };
        var state = new RecordingJobState();
        var runner = new ScheduledJobRunner([new RecordingScheduledJob("outbox", state)], clock);

        // Moved after the runner was built and before the first run: a constructor-time capture would report Start.
        clock.Now = Start.AddMinutes(1);
        await runner.RunAllAsync();
        clock.Now = Start.AddMinutes(2);
        await runner.RunAllAsync();

        Assert.Equal(
            new[] { Start.AddMinutes(1), Start.AddMinutes(2) },
            state.Runs.Select(run => run.Now).ToArray());
    }

    [Fact]
    public async Task Runner_runs_every_registered_job_in_registration_order()
    {
        var state = new RecordingJobState();
        var runner = new ScheduledJobRunner(
            [new RecordingScheduledJob("outbox", state), new RecordingScheduledJob("reminders", state)],
            new FakeClock { Now = Start });

        await runner.RunAllAsync();

        Assert.Equal(new[] { "outbox", "reminders" }, state.Runs.Select(run => run.Key).ToArray());
    }

    [Fact]
    public async Task Runner_exposes_the_stable_keys_of_every_registered_job()
    {
        var state = new RecordingJobState();
        var runner = new ScheduledJobRunner(
            [new RecordingScheduledJob("outbox", state), new RecordingScheduledJob("reminders", state)],
            new FakeClock { Now = Start });

        Assert.Equal(new[] { "outbox", "reminders" }, runner.Keys);
    }

    [Fact]
    public async Task Keyed_manual_runner_runs_only_the_requested_job()
    {
        var state = new RecordingJobState();
        var runner = new ScheduledJobRunner(
            [new RecordingScheduledJob("outbox", state), new RecordingScheduledJob("reminders", state)],
            new FakeClock { Now = Start });

        await runner.RunOneAsync("outbox");

        var run = Assert.Single(state.Runs);
        Assert.Equal("outbox", run.Key);
        Assert.Equal(Start, run.Now);
    }

    [Fact]
    public async Task Keyed_manual_runner_rejects_an_unknown_key_without_running_anything()
    {
        var state = new RecordingJobState();
        var runner = new ScheduledJobRunner([new RecordingScheduledJob("outbox", state)], new FakeClock { Now = Start });

        await Assert.ThrowsAsync<KeyNotFoundException>(() => runner.RunOneAsync("outbox-typo"));

        Assert.Empty(state.Runs);
    }

    [Fact]
    public void Runner_rejects_an_empty_or_duplicate_job_key_at_construction_without_echoing_it()
    {
        var state = new RecordingJobState();
        var clock = new FakeClock { Now = Start };

        var duplicate = Assert.Throws<InvalidOperationException>(() => new ScheduledJobRunner(
            [new RecordingScheduledJob("outbox", state), new RecordingScheduledJob("outbox", state)], clock));
        Assert.Equal(ScheduledJobRunner.InvalidKeyMessage, duplicate.Message);
        Assert.DoesNotContain("outbox", duplicate.Message, StringComparison.Ordinal);

        var empty = Assert.Throws<InvalidOperationException>(() => new ScheduledJobRunner(
            [new RecordingScheduledJob("   ", state)], clock));
        Assert.Equal(ScheduledJobRunner.InvalidKeyMessage, empty.Message);
    }

    [Fact]
    public void Runner_rejects_a_null_job_element_with_the_fixed_message()
    {
        var clock = new FakeClock { Now = Start };

        // The guard must run before the key projection, otherwise a null element escapes as a NullReferenceException.
        var error = Assert.Throws<InvalidOperationException>(() => new ScheduledJobRunner([null!], clock));

        Assert.Equal(ScheduledJobRunner.InvalidKeyMessage, error.Message);
    }

    [Fact]
    public async Task Run_all_finishes_the_tick_after_a_failure_and_reports_one_fixed_safe_failure()
    {
        var state = new RecordingJobState();
        var clock = new FakeClock { Now = Start };
        var runner = new ScheduledJobRunner(
            [
                new RecordingScheduledJob("outbox", state, _ =>
                {
                    clock.Now = Start.AddMinutes(1);
                    return Task.FromException(new TimeoutException("password=no-debe-salir"));
                }),
                new RecordingScheduledJob("reminders", state)
            ],
            clock);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAllAsync());

        // Fixed, non-echoing failure: no raw exception, no inner exception, no secret and no job key.
        Assert.Equal(ScheduledJobRunner.JobFailureMessage, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("no-debe-salir", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("outbox", error.Message, StringComparison.Ordinal);
        // An outbox outage must not starve a later, time-sensitive job: both jobs ran in the same tick, and the clock
        // was read separately for each invoked job.
        Assert.Equal(new[] { "outbox", "reminders" }, state.Runs.Select(run => run.Key).ToArray());
        Assert.Equal(new[] { Start, Start.AddMinutes(1) }, state.Runs.Select(run => run.Now).ToArray());
    }

    [Fact]
    public async Task Run_all_stops_at_caller_cancellation_without_running_the_remaining_jobs()
    {
        var state = new RecordingJobState();
        using var cancellation = new CancellationTokenSource();
        var runner = new ScheduledJobRunner(
            [
                new RecordingScheduledJob("outbox", state, _ =>
                {
                    cancellation.Cancel();
                    return Task.FromException(new OperationCanceledException(cancellation.Token));
                }),
                new RecordingScheduledJob("reminders", state)
            ],
            new FakeClock { Now = Start });

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAllAsync(cancellation.Token));

        var run = Assert.Single(state.Runs);
        Assert.Equal("outbox", run.Key);
    }

    [Fact]
    public async Task Run_all_reports_cancellation_instead_of_a_previous_job_failure()
    {
        var state = new RecordingJobState();
        using var cancellation = new CancellationTokenSource();
        var runner = new ScheduledJobRunner(
            [
                new RecordingScheduledJob("outbox", state, _ =>
                    Task.FromException(new TimeoutException("password=no-debe-salir"))),
                new RecordingScheduledJob("reminders", state, _ =>
                {
                    cancellation.Cancel();
                    return Task.FromException(new OperationCanceledException(cancellation.Token));
                }),
                new RecordingScheduledJob("daily-summary", state)
            ],
            new FakeClock { Now = Start });

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAllAsync(cancellation.Token));

        // Cancellation wins over the failure already recorded by the first job, and the third job never starts.
        Assert.Equal(new[] { "outbox", "reminders" }, state.Runs.Select(run => run.Key).ToArray());
    }

    [Fact]
    public async Task Runner_forwards_the_callers_live_token_to_every_job()
    {
        var state = new RecordingJobState();
        var runner = new ScheduledJobRunner([new RecordingScheduledJob("outbox", state)], new FakeClock { Now = Start });
        using var cancellation = new CancellationTokenSource();

        await runner.RunAllAsync(cancellation.Token);

        var run = Assert.Single(state.Runs);
        Assert.Equal(cancellation.Token, run.Token);
        Assert.False(run.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Run_all_does_not_invoke_a_job_with_an_already_cancelled_token()
    {
        var state = new RecordingJobState();
        var runner = new ScheduledJobRunner([new RecordingScheduledJob("outbox", state)], new FakeClock { Now = Start });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAllAsync(cancellation.Token));

        // The job ignores the token by design: only the runner's pre-check can keep it from running.
        Assert.Empty(state.Runs);
    }

    [Fact]
    public async Task Run_one_does_not_invoke_a_job_with_an_already_cancelled_token()
    {
        var state = new RecordingJobState();
        var runner = new ScheduledJobRunner([new RecordingScheduledJob("outbox", state)], new FakeClock { Now = Start });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.RunOneAsync("outbox", cancellation.Token));

        Assert.Empty(state.Runs);
    }

    [Fact]
    public async Task Run_all_stops_between_jobs_when_the_token_is_cancelled_after_a_swallowed_failure()
    {
        var state = new RecordingJobState();
        using var cancellation = new CancellationTokenSource();
        var runner = new ScheduledJobRunner(
            [
                new RecordingScheduledJob("outbox", state, _ =>
                {
                    // The token is cancelled while the failing job runs, but the job reports a failure, not an OCE.
                    cancellation.Cancel();
                    return Task.FromException(new TimeoutException("password=no-debe-salir"));
                }),
                new RecordingScheduledJob("reminders", state)
            ],
            new FakeClock { Now = Start });

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAllAsync(cancellation.Token));

        // The failure of the first job was swallowed and no safe failure is reported: cancellation wins and the
        // second job never starts.
        var run = Assert.Single(state.Runs);
        Assert.Equal("outbox", run.Key);
    }

    // ---- The outbox job: stable key, dispatch, retain inertness and no automatic recovery ----

    [Fact]
    public async Task Outbox_job_uses_the_stable_key_and_dispatches_a_due_notice()
    {
        var repository = new FakeOutboxRepository();
        var sender = new FakeEmailSender();
        var clock = new FakeClock { Now = Start };
        var notification = repository.Seed("booking:1", Start.AddMinutes(-5));
        var job = new OutboxScheduledJob(
            new OutboxDispatchService(repository, sender, new NoopUnitOfWork(), clock, OutboxMode.Deliver));

        Assert.Equal("outbox", job.Key);

        await job.RunAsync(clock.Now);

        Assert.Equal(1, sender.CallCount);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
    }

    [Fact]
    public async Task Outbox_job_claims_and_sends_nothing_in_retain_mode_even_with_a_due_notice()
    {
        var repository = new FakeOutboxRepository();
        var sender = new FakeEmailSender();
        var clock = new FakeClock { Now = Start };
        var notification = repository.Seed("booking:1", Start.AddMinutes(-5));
        var job = new OutboxScheduledJob(
            new OutboxDispatchService(repository, sender, new NoopUnitOfWork(), clock, OutboxMode.Retain));

        await job.RunAsync(clock.Now);

        Assert.Equal(0, repository.ClaimCount);
        Assert.Equal(0, sender.CallCount);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
    }

    [Fact]
    public async Task Outbox_job_never_recovers_a_dispatching_or_review_required_notice()
    {
        var repository = new FakeOutboxRepository();
        var sender = new FakeEmailSender();
        var clock = new FakeClock { Now = Start };
        var dispatching = repository.Seed("booking:interrupted", Start.AddMinutes(-10), NotificationStatus.Dispatching);
        var review = repository.Seed("booking:unknown", Start.AddMinutes(-9), NotificationStatus.ReviewRequired);
        var job = new OutboxScheduledJob(
            new OutboxDispatchService(repository, sender, new NoopUnitOfWork(), clock, OutboxMode.Deliver));

        await job.RunAsync(clock.Now);

        Assert.Equal(0, sender.CallCount);
        Assert.Equal(NotificationStatus.Dispatching, dispatching.Status);
        Assert.Equal(NotificationStatus.ReviewRequired, review.Status);
    }

    // ---- The retain sender is fail-closed and never sends ----

    [Fact]
    public async Task Retain_sender_fails_closed_without_sending_or_echoing_the_message()
    {
        var sender = new RetainEmailSender();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(
            new EmailMessage("ana@example.com", "Turno confirmado", "<p>cuerpo-secreto</p>")));

        Assert.Equal(RetainEmailSender.UnreachableMessage, error.Message);
        Assert.DoesNotContain("ana@example.com", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cuerpo-secreto", error.Message, StringComparison.Ordinal);
    }

    // ---- Hosted worker mechanics ----

    [Fact]
    public async Task Hosted_service_runs_one_immediate_tick_before_waiting_for_the_timer()
    {
        var state = new RecordingJobState();
        var firstTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = BuildJobProvider(state, _ =>
        {
            firstTick.TrySetResult();
            return Task.CompletedTask;
        });
        var host = NewHost(provider);

        await host.StartAsync(CancellationToken.None);
        try
        {
            // The tick is immediate, so it must land far below the one-minute interval.
            await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(state.Runs);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Hosted_service_uses_a_fresh_disposed_scope_for_every_tick()
    {
        var state = new RecordingJobState();
        await using var provider = BuildJobProvider(state);
        var host = NewHost(provider, extraTicks: 1);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, state.Runs.Count);
        Assert.NotEqual(state.Runs[0].InstanceId, state.Runs[1].InstanceId);
        // The first tick's scope was already disposed when the second tick's job started.
        Assert.Equal(1, state.Runs[1].DisposedBefore);
        Assert.Equal(2, state.DisposedJobs.Count);
    }

    [Fact]
    public async Task A_failed_tick_logs_only_a_safe_fixed_message_and_the_next_tick_still_runs()
    {
        var state = new RecordingJobState();
        var logger = new CapturingLogger<ScheduledJobsHostedService>();
        var attempts = 0;
        await using var provider = BuildJobProvider(state, _ =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("password=no-debe-salir"))
                : Task.CompletedTask, logger);
        var host = NewHost(provider, extraTicks: 1);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, state.Runs.Count);
        var entry = Assert.Single(logger.Entries, logged => logged.Level == LogLevel.Error);
        Assert.Contains(ScheduledJobsHostedService.TickFailedMessage, entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no-debe-salir", entry.Message, StringComparison.Ordinal);
        // The exception object is never handed to the logger, so no provider text can reach a sink.
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task A_failed_tick_logs_only_fixed_text_and_the_safe_failure_type_never_the_provider_secret()
    {
        var state = new RecordingJobState();
        var logger = new CapturingLogger<ScheduledJobsHostedService>();
        // A raw provider failure whose type and message must never reach the log.
        await using var provider = BuildJobProvider(state, _ =>
            Task.FromException(new TimeoutException("password=no-debe-salir")), logger);
        var host = NewHost(provider);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(state.Runs);
        var entry = Assert.Single(logger.Entries, logged => logged.Level == LogLevel.Error);
        Assert.Contains(ScheduledJobsHostedService.TickFailedMessage, entry.Message, StringComparison.Ordinal);
        // The safe failure reported by the runner is an InvalidOperationException; the raw type is not logged.
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(TimeoutException), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no-debe-salir", entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Hosted_service_cancels_a_running_tick_and_stops_without_hanging()
    {
        var state = new RecordingJobState();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = BuildJobProvider(state, async run =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, run.Token);
        });
        var host = NewHost(provider);

        await host.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var run = Assert.Single(state.Runs);
        Assert.True(run.Token.IsCancellationRequested);
    }

    // ---- Composition root: fail fast, mode-conditional sender and the single hosted worker ----

    [Fact]
    public void Unknown_email_mode_fails_at_registration_without_echoing_the_value()
    {
        var settings = DefaultSettings();
        settings["WorkUp:Email:Mode"] = "Smpt-con-tr4za";

        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => RegisterWorkUp(services, settings));

        Assert.Contains("WorkUp:Email:Mode", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Smpt-con-tr4za", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Smtp_mode_with_an_invalid_host_fails_at_registration_without_echoing_the_value()
    {
        var settings = DefaultSettings();
        settings["WorkUp:Email:Mode"] = "Smtp";
        settings["WorkUp:Email:Smtp:Host"] = "host con espacios";

        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => RegisterWorkUp(services, settings));

        Assert.Contains("WorkUp:Email:Smtp:Host", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("host con espacios", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Smtp_mode_with_plaintext_credentials_fails_at_registration_without_echoing_the_password()
    {
        var settings = DefaultSettings();
        settings["WorkUp:Email:Mode"] = "Smtp";
        settings["WorkUp:Email:Smtp:Host"] = "smtp.example.test";
        settings["WorkUp:Email:Smtp:User"] = "ana@example.com";
        settings["WorkUp:Email:Smtp:Password"] = "p4ssw0rd-no-debe-salir";
        settings["WorkUp:Email:Smtp:EnableSsl"] = "false";

        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => RegisterWorkUp(services, settings));

        Assert.Contains("EnableSsl", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("p4ssw0rd-no-debe-salir", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no-debe-salir", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbox_mode_registers_the_retain_sender_and_never_validates_the_smtp_settings()
    {
        var settings = DefaultSettings();
        // Deliberately unusable SMTP settings: retaining notices must not read or validate them.
        settings["WorkUp:Email:Smtp:Host"] = "host con espacios";

        var services = new ServiceCollection();
        RegisterWorkUp(services, settings);

        var emailSender = services.Single(descriptor => descriptor.ServiceType == typeof(IEmailSender));
        Assert.Equal(typeof(RetainEmailSender), emailSender.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, emailSender.Lifetime);

        var outboxRepository = services.Single(descriptor => descriptor.ServiceType == typeof(IOutboxRepository));
        Assert.Equal(typeof(SqlOutboxRepository), outboxRepository.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, outboxRepository.Lifetime);

        var dispatcher = services.Single(descriptor => descriptor.ServiceType == typeof(OutboxDispatchService));
        Assert.Equal(ServiceLifetime.Scoped, dispatcher.Lifetime);

        var hostedWorkers = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(ScheduledJobsHostedService))
            .ToList();
        var worker = Assert.Single(hostedWorkers);
        Assert.Equal(ServiceLifetime.Singleton, worker.Lifetime);
    }

    [Fact]
    public async Task Outbox_mode_resolves_the_retain_graph_and_claims_nothing_even_with_a_fake_smtp_host()
    {
        var settings = DefaultSettings();
        settings["WorkUp:Email:Smtp:Host"] = "smtp.fake.invalid";

        await using var provider = BuildProvider(settings);

        Assert.IsType<RetainEmailSender>(provider.GetRequiredService<IEmailSender>());
        Assert.IsType<SqlOutboxRepository>(provider.GetRequiredService<IOutboxRepository>());
        var dispatcher = provider.GetRequiredService<OutboxDispatchService>();
        Assert.Equal(OutboxMode.Retain, dispatcher.Mode);

        // The connection string names a database that is never created and the retain sender throws if it is reached:
        // a zero and no exception prove that no claim and no send were attempted.
        Assert.Equal(0, await dispatcher.DispatchDueAsync());
    }

    [Fact]
    public async Task Smtp_mode_resolves_the_real_adapter()
    {
        var settings = DefaultSettings();
        settings["WorkUp:Email:Mode"] = "Smtp";
        settings["WorkUp:Email:Smtp:Host"] = "smtp.example.test";

        // The dispatcher resolves a scoped DbSession, which only implements IAsyncDisposable.
        await using var provider = BuildProvider(settings);

        Assert.IsType<SmtpEmailSender>(provider.GetRequiredService<IEmailSender>());
        Assert.Equal(OutboxMode.Deliver, provider.GetRequiredService<OutboxDispatchService>().Mode);
    }

    [Fact]
    public async Task Outbox_tick_completes_with_the_fixed_marker_and_leaves_the_notice_pending()
    {
        var settings = DefaultSettings();
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterWorkUp(services, settings);
        var repository = new FakeOutboxRepository();
        var notification = repository.Seed("booking:1", Start.AddMinutes(-1));
        var capture = new CapturingLogger<ScheduledJobsHostedService>();
        services.AddScoped<IOutboxRepository>(_ => repository);
        services.AddSingleton<IClock>(new FakeClock { Now = Start });
        services.AddSingleton<ILogger<ScheduledJobsHostedService>>(capture);
        // T24: the tick now also runs the reminder job, whose real port would open the never-created database and
        // turn the tick into a reported failure. An empty fake reminder port and a fake protector keep this test's
        // premise — an inert Outbox tick that claims nothing — while the real reminder graph is covered below.
        services.AddScoped<IReminderRepository>(_ => new FakeReminderRepository());
        services.AddSingleton<ILinkTokenProtector>(new TestLinkTokenProtector());
        StubT25NoticeJobs(services);

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostedService>();
        await host.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => capture.Entries.Any(entry => entry.Message.Contains(
                    ScheduledJobsHostedService.TickCompletedMessage, StringComparison.Ordinal)),
                "the fixed tick-complete marker");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, repository.ClaimCount);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
    }

    [Fact]
    public async Task The_registered_worker_delivers_a_due_notice_through_the_smtp_graph()
    {
        var settings = DefaultSettings();
        settings["WorkUp:Email:Mode"] = "Smtp";
        settings["WorkUp:Email:Smtp:Host"] = "smtp.example.test";
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterWorkUp(services, settings);
        var repository = new FakeOutboxRepository();
        var sender = new FakeEmailSender();
        var notification = repository.Seed("booking:1", Start.AddMinutes(-1));
        services.AddScoped<IOutboxRepository>(_ => repository);
        services.AddScoped<IEmailSender>(_ => sender);
        // The real DbSession would open the never-created database; SQL persistence is out of scope for this wiring
        // test and is proven by the SQL fixture instead.
        services.AddScoped<IUnitOfWork>(_ => new NoopUnitOfWork());
        services.AddSingleton<IClock>(new FakeClock { Now = Start });
        // The reminder job of the tick has to be stubbed too, so this test observes the outbox path on its own instead
        // of relying on the runner swallowing a reminder failure.
        services.AddScoped<IReminderRepository>(_ => new FakeReminderRepository());
        services.AddSingleton<ILinkTokenProtector>(new TestLinkTokenProtector());
        StubT25NoticeJobs(services);

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostedService>();
        await host.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => notification.Status == NotificationStatus.Sent,
                "the queued notice to be sent by the registered worker");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, repository.ClaimCount);
        Assert.Equal(1, sender.CallCount);
        Assert.Equal("ana@example.com", Assert.Single(sender.Messages).Recipient);
    }

    // ---- Host wiring of the reminder job (T24): registration, order and same-tick visibility ----

    [Fact]
    public void WorkUp_registers_the_reminder_graph_and_orders_the_reminder_job_before_the_outbox_job()
    {
        var services = new ServiceCollection();
        RegisterWorkUp(services, DefaultSettings());

        var reminderRepository = Assert.Single(services, d => d.ServiceType == typeof(IReminderRepository));
        Assert.Equal(typeof(SqlReminderRepository), reminderRepository.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, reminderRepository.Lifetime);

        var reminderService = Assert.Single(services, d => d.ServiceType == typeof(ReminderService));
        Assert.Equal(ServiceLifetime.Scoped, reminderService.Lifetime);

        var jobTypes = services
            .Where(d => d.ServiceType == typeof(IScheduledJob))
            .Select(d => d.ImplementationType)
            .ToArray();
        var reminderIndex = Array.IndexOf(jobTypes, typeof(ReminderScheduledJob));
        var outboxIndex = Array.IndexOf(jobTypes, typeof(OutboxScheduledJob));
        Assert.True(reminderIndex >= 0, "the reminder job must be registered as a scheduled job");
        Assert.True(outboxIndex >= 0, "the outbox job must still be registered as a scheduled job");
        // Same-tick visibility: a notice queued by the reminder job has to exist before the outbox job of the same tick
        // claims it, so the reminder job must be registered first.
        Assert.True(reminderIndex < outboxIndex, "the reminder job must run before the outbox job in a tick");

        var reminderJob = Assert.Single(services, d =>
            d.ServiceType == typeof(IScheduledJob) && d.ImplementationType == typeof(ReminderScheduledJob));
        Assert.Equal(ServiceLifetime.Scoped, reminderJob.Lifetime);

        // Unrelated wiring stays untouched: exactly one sender and exactly one hosted worker.
        Assert.Single(services, d => d.ServiceType == typeof(IEmailSender));
        Assert.Single(services, d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(ScheduledJobsHostedService));
    }

    [Fact]
    public async Task Outbox_mode_tick_enqueues_a_due_reminder_in_the_same_scoped_tick_and_leaves_it_pending()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Default settings are Outbox mode, so the outbox job must retain and the sender must never be reached.
        RegisterWorkUp(services, DefaultSettings());
        var bridge = NewReminderBridge(out var outbox, out var sender, out var clock);
        services.AddScoped<IReminderRepository>(_ => bridge.Repository);
        services.AddScoped<IOutboxRepository>(_ => outbox);
        services.AddScoped<IEmailSender>(_ => sender);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ILinkTokenProtector>(new TestLinkTokenProtector());
        StubT25NoticeJobs(services);

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostedService>();
        await host.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => bridge.EnqueuedCount == 1,
                "the reminder job of the immediate tick to queue its due notice");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        // The reminder job ran inside the tick's scope and queued exactly one due notice.
        Assert.Equal(1, bridge.EnqueuedCount);
        var queued = Assert.Single(bridge.OutboxRows);
        Assert.Equal("reminder:booking:1", queued.DedupKey);
        Assert.Equal(ReminderService.PendingSubject, queued.Subject);
        // Retention: the outbox job of the same tick claimed nothing and the fake sender observed nothing, so the
        // fresh reminder is still Pending for the T26 viewer.
        Assert.Equal(NotificationStatus.Pending, queued.Status);
        Assert.Equal(0, outbox.ClaimCount);
        Assert.Equal(0, sender.CallCount);
        Assert.Null(queued.LastError);
        Assert.Equal(0, queued.Attempts);
    }

    [Fact]
    public async Task Smtp_mode_tick_sends_the_reminder_queued_by_the_reminder_job_of_the_same_tick()
    {
        var settings = DefaultSettings();
        settings["WorkUp:Email:Mode"] = "Smtp";
        settings["WorkUp:Email:Smtp:Host"] = "smtp.example.test";
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterWorkUp(services, settings);
        var bridge = NewReminderBridge(out var outbox, out var sender, out var clock);
        services.AddScoped<IReminderRepository>(_ => bridge.Repository);
        services.AddScoped<IOutboxRepository>(_ => outbox);
        services.AddScoped<IEmailSender>(_ => sender);
        // The real DbSession would open the never-created database; SQL persistence is proven by the SQL fixture.
        services.AddScoped<IUnitOfWork>(_ => new NoopUnitOfWork());
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ILinkTokenProtector>(new TestLinkTokenProtector());
        services.AddSingleton<IPublicAppointmentUrlBuilder>(new TestAppointmentUrlBuilder());
        StubT25NoticeJobs(services);

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostedService>();
        await host.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => sender.CallCount == 1,
                "the outbox job to dispatch the reminder queued by the same tick");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        // The outbox job could only see the notice because the reminder job ran first in the same tick and scope.
        var message = Assert.Single(sender.Messages);
        Assert.Equal("ana@example.com", message.Recipient);
        Assert.Equal(ReminderService.PendingSubject, message.Subject);
        Assert.Contains(ReminderService.ConfirmationLead, message.BodyHtml, StringComparison.Ordinal);
        Assert.Equal(1, outbox.ClaimCount);
        Assert.Equal(NotificationStatus.Sent, Assert.Single(bridge.OutboxRows).Status);
        // The raw private token lives only inside the body: it never reaches the recipient or the subject.
        Assert.Contains("https://workup.test/t/token-1", message.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("token-1", message.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("token-1", message.Recipient, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registered_jobs_read_the_clock_of_every_tick_and_never_a_cached_instant()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterWorkUp(services, DefaultSettings());
        var reminders = new FakeReminderRepository();
        var clock = new FakeClock { Now = Start };
        // Only the first booking is due at the first tick; the second one is due one minute later, so the notice it
        // produces proves the clock was read again for the second tick instead of reused.
        var first = reminders.AddIndividual(1, Start.AddHours(24), Start.AddHours(-30));
        reminders.AddLink(first, "token-1", Start.AddHours(48));
        var second = reminders.AddIndividual(2, Start.AddHours(24).AddMinutes(1), Start.AddHours(-30));
        reminders.AddLink(second, "token-2", Start.AddHours(48));
        services.AddScoped<IReminderRepository>(_ => reminders);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ILinkTokenProtector>(new TestLinkTokenProtector());
        StubT25NoticeJobs(services);

        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = provider.GetRequiredService<ILogger<ScheduledJobsHostedService>>();
        var host = new ClockAdvancingHostedService(scopeFactory, logger, clock, extraTicks: 1);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            new[] { Start, Start.AddMinutes(1) },
            reminders.Notifications.Select(notice => notice.CreatedAt).ToArray());
    }

    // ---- Test harness ----

    private static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["ConnectionStrings:WorkUp"] = NeverOpenableConnectionString,
        ["WorkUp:PublicBaseUrl"] = BaseUrl,
        ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires"
    };

    private static void RegisterWorkUp(IServiceCollection services, IDictionary<string, string?> settings) =>
        services.AddWorkUp(BuildConfiguration(settings), new FakeHostEnvironment());

    /// <summary>
    /// T25: the tick now also runs the daily-summary, stock-review and membership-expiry jobs. Their real ports would
    /// open the never-created database, turn the tick into a reported failure and hide the T23/T24 path this harness
    /// observes. Empty fakes keep the original premise — the T25 jobs enqueue nothing here — while the T25 graph itself
    /// is covered by <c>ScheduledNoticeJobsTests</c>.
    /// </summary>
    private static void StubT25NoticeJobs(IServiceCollection services)
    {
        services.AddScoped<IDailySummaryRepository>(_ => new FakeDailySummaryRepository());
        services.AddScoped<IStockReviewRepository>(_ => new FakeStockReviewRepository());
        services.AddScoped<IMembershipExpiryRepository>(_ => new FakeMembershipExpiryRepository());
    }

    private static ServiceProvider BuildProvider(IDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterWorkUp(services, settings);
        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static ServiceProvider BuildJobProvider(
        RecordingJobState state,
        Func<JobRun, Task>? behavior = null,
        ILogger<ScheduledJobsHostedService>? logger = null,
        string key = "outbox")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(state);
        services.AddScoped<IScheduledJob>(sp =>
            new RecordingScheduledJob(key, sp.GetRequiredService<RecordingJobState>(), behavior));
        services.AddScoped<ScheduledJobRunner>();
        // The runner reads the clock on every invocation; this harness only needs a stable instant.
        services.AddSingleton<IClock>(new FakeClock { Now = Start });
        if (logger is not null)
            services.AddSingleton(logger);

        return services.BuildServiceProvider();
    }

    private static ScheduledJobsHostedService NewHost(IServiceProvider provider, int extraTicks = 0)
    {
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = provider.GetRequiredService<ILogger<ScheduledJobsHostedService>>();

        return extraTicks == 0
            ? new ScheduledJobsHostedService(scopeFactory, logger)
            : new DrivenHostedService(scopeFactory, logger, extraTicks);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {description}.");
    }

    /// <summary>
    /// Same worker as production but with a controllable tick source, so a test drives several ticks without waiting a
    /// real minute. It never changes the production one-minute <see cref="ScheduledJobsHostedService.Interval"/>.
    /// </summary>
    private sealed class DrivenHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledJobsHostedService> logger,
        int extraTicks) : ScheduledJobsHostedService(scopeFactory, logger)
    {
        private int _remaining = extraTicks;

        protected override Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct) =>
            Task.FromResult(_remaining-- > 0);
    }

    private sealed class RecordingJobState
    {
        public List<JobRun> Runs { get; } = [];

        public List<Guid> DisposedJobs { get; } = [];
    }

    private sealed record JobRun(string Key, Guid InstanceId, DateTime Now, int DisposedBefore, CancellationToken Token);

    private sealed class RecordingScheduledJob(
        string key,
        RecordingJobState state,
        Func<JobRun, Task>? behavior = null) : IScheduledJob, IAsyncDisposable
    {
        private readonly Guid _instanceId = Guid.NewGuid();

        public string Key { get; } = key;

        public async Task RunAsync(DateTime now, CancellationToken ct = default)
        {
            // The dispose count is read before the behaviour runs, so a test can prove that the previous tick's scope
            // was already disposed when this tick started.
            var run = new JobRun(Key, _instanceId, now, state.DisposedJobs.Count, ct);
            state.Runs.Add(run);

            if (behavior is not null)
                await behavior(run);
        }

        public ValueTask DisposeAsync()
        {
            state.DisposedJobs.Add(_instanceId);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Same worker as production but with a clock that moves one minute between ticks, exactly like the real
    /// <c>BusinessClock</c> would on the next surrounding tick. It never changes the production one-minute interval.
    /// </summary>
    private sealed class ClockAdvancingHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledJobsHostedService> logger,
        FakeClock clock,
        int extraTicks) : ScheduledJobsHostedService(scopeFactory, logger)
    {
        private int _remaining = extraTicks;

        protected override Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
        {
            clock.Now = clock.Now.AddMinutes(1);
            return Task.FromResult(_remaining-- > 0);
        }
    }

    /// <summary>
    /// Test-only bridge between the two in-memory ports the reminder job and the outbox job touch. In production both
    /// write and read the same <c>Notifications</c> table, so a notice queued by the reminder job of a tick is exactly
    /// what the outbox job of that same tick claims. The two fakes are independent objects, so every notice the guarded
    /// reminder enqueue accepted is copied into the outbox fake, and only then; a rejected enqueue copies nothing.
    /// </summary>
    private sealed class ReminderBridge(FakeReminderRepository reminders, FakeOutboxRepository outbox)
    {
        public FakeReminderRepository Reminders { get; } = reminders;

        public List<Notification> OutboxRows { get; } = [];

        public int EnqueuedCount { get; private set; }

        public IReminderRepository Repository => new Adapter(this);

        private bool Accept(ReminderNotice notice)
        {
            EnqueuedCount++;
            OutboxRows.Add(outbox.Seed(
                notice.Notification.DedupKey,
                notice.Notification.CreatedAt,
                recipient: notice.Notification.Recipient,
                subject: notice.Notification.Subject,
                bodyHtml: notice.Notification.BodyHtml));
            return true;
        }

        private sealed class Adapter(ReminderBridge bridge) : IReminderRepository
        {
            public Task<IReadOnlyList<ReminderCandidate>> ListCandidatesAsync(
                DateTime now, int batchSize, CancellationToken ct = default) =>
                bridge.Reminders.ListCandidatesAsync(now, batchSize, ct);

            public async Task<bool> TryEnqueueReminderAsync(ReminderNotice notice, CancellationToken ct = default) =>
                await bridge.Reminders.TryEnqueueReminderAsync(notice, ct) && bridge.Accept(notice);
        }
    }

    private static ReminderBridge NewReminderBridge(
        out FakeOutboxRepository outbox,
        out FakeEmailSender sender,
        out FakeClock clock)
    {
        var reminders = new FakeReminderRepository();
        var booking = reminders.AddIndividual(1, Start.AddHours(24), Start.AddHours(-30));
        reminders.AddLink(booking, "token-1", Start.AddHours(48));
        outbox = new FakeOutboxRepository();
        sender = new FakeEmailSender();
        clock = new FakeClock { Now = Start };
        return new ReminderBridge(reminders, outbox);
    }

    /// <summary>Fake protector, so these wiring tests never read or write the real data-protection keyring.</summary>
    private sealed class TestLinkTokenProtector : ILinkTokenProtector
    {
        public string Protect(string token) => "protected:" + token;

        public string Unprotect(string protectedToken) => protectedToken["protected:".Length..];
    }

    /// <summary>Fake link builder, so the assertion can name the exact URL the raw token was embedded in.</summary>
    private sealed class TestAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://workup.test/t/" + token;
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Minimal unit of work: the outbox dispatcher only needs a transaction it can commit and dispose.</summary>
    private sealed class NoopUnitOfWork : IUnitOfWork
    {
        public Task<ITransaction> BeginAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default) =>
            Task.FromResult<ITransaction>(NoopTransaction.Instance);

        private sealed class NoopTransaction : ITransaction
        {
            public static readonly NoopTransaction Instance = new();

            public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
