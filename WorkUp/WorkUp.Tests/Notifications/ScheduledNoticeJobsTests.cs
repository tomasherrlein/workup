using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Jobs;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Host wiring of the three T25 notice jobs (RF-18, RF-19, RF-30): the daily summary, the stock review and the
/// membership expiry notice. Each adapter is the only bridge between the T23 scheduler and its T25 business core, so it
/// must expose the stable key the manual runner of T26 uses, forward the runner-supplied instant and the caller's token
/// unchanged, keep no <c>IClock</c> of its own and never swallow a failure.
/// <para>
/// The composition root is asserted on the real <see cref="ServiceRegistration.AddWorkUp"/> graph: the three
/// repository ports map to their <c>Sql*</c> implementations scoped, the three services are scoped, and the jobs are
/// registered <b>after</b> the reminder job and <b>before</b> the outbox job, so a notice queued by a T25 job of a tick
/// already exists when the outbox job of that same tick runs. The tick-level tests drive the registered hosted worker
/// with a test-only in-memory bridge between the T25 ports and the outbox fake; that bridge models the shared
/// <c>Notifications</c> table and is explicitly <b>not</b> real SQL and <b>not</b> real SMTP, which the SQL fixture
/// covers separately.
/// </para>
/// </summary>
public sealed class ScheduledNoticeJobsTests
{
    private static readonly DateTime Start = new(2026, 3, 10, 9, 0, 0);

    private const string BaseUrl = "https://reservas.example.com";

    /// <summary>A database that is never created, so any real claim or migration would fail loudly.</summary>
    private const string NeverOpenableConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT25NoticeJobsNeverOpened;Integrated Security=true;Connect Timeout=1";

    private readonly FakeDailySummaryRepository _dailySummaries = new();
    private readonly FakeStockReviewRepository _stockReviews = new();
    private readonly FakeMembershipExpiryRepository _membershipExpiries = new();

    // ---- Daily summary adapter (RF-18): stable key, instant and token forwarding, no clock, no swallowed failure ----

    [Fact]
    public void Daily_summary_job_exposes_the_stable_daily_summary_key()
    {
        var job = NewDailySummaryJob();

        Assert.Equal("daily-summary", DailySummaryScheduledJob.JobKey);
        Assert.Equal("daily-summary", job.Key);
    }

    [Fact]
    public async Task Daily_summary_job_delegates_the_run_with_the_supplied_instant()
    {
        var business = _dailySummaries.AddBusiness(businessId: 1, configuredTime: new TimeOnly(8, 0));
        _dailySummaries.AddIndividual(business, 1, Start.Date.AddDays(1).AddHours(10));
        var job = NewDailySummaryJob();

        await job.RunAsync(Start);

        Assert.Equal(1, _dailySummaries.ListCount);
        Assert.Equal(1, _dailySummaries.EnqueueAttempts);
        Assert.Equal(DailySummaryService.BatchSize, _dailySummaries.LastBatchSize);
        var notice = Assert.Single(_dailySummaries.Notifications);
        Assert.Equal("summary:business:1:20260311", notice.DedupKey);
        Assert.StartsWith(DailySummaryService.SubjectPrefix, notice.Subject, StringComparison.Ordinal);
        // The instant of the invocation, not DateTime.Now and not a value captured when the job was built.
        Assert.Equal(Start, notice.CreatedAt);
    }

    [Fact]
    public async Task Daily_summary_job_uses_the_instant_of_every_invocation_and_never_a_cached_one()
    {
        var business = _dailySummaries.AddBusiness(businessId: 1, configuredTime: new TimeOnly(8, 0));
        _dailySummaries.AddIndividual(business, 1, Start.Date.AddDays(1).AddHours(10));
        // Only the second invocation may reach the summary of the following target day.
        _dailySummaries.AddIndividual(business, 2, Start.Date.AddDays(2).AddHours(10));
        var job = NewDailySummaryJob();

        await job.RunAsync(Start);
        await job.RunAsync(Start.AddDays(1));

        Assert.Equal(
            new[] { Start, Start.AddDays(1) },
            _dailySummaries.Notifications.Select(notice => notice.CreatedAt).ToArray());
        Assert.Equal(
            new[] { "summary:business:1:20260311", "summary:business:1:20260312" },
            _dailySummaries.Notifications.Select(notice => notice.DedupKey).ToArray());
    }

    [Fact]
    public void Daily_summary_job_depends_only_on_the_service_and_holds_no_clock_it_could_re_read()
    {
        var parameters = SingleConstructorParameterTypes<DailySummaryScheduledJob>();

        Assert.Contains(typeof(DailySummaryService), parameters);
        // Exactly one dependency: nothing else is injected, so there is no logger to log a body or a recipient either.
        Assert.Single(parameters);
        // A clock here would be a second, potentially stale source of time; the runner-supplied instant is the only one.
        Assert.DoesNotContain(typeof(IClock), parameters);
    }

    [Fact]
    public async Task Daily_summary_job_forwards_the_callers_token_and_enqueues_nothing_when_it_is_already_cancelled()
    {
        var business = _dailySummaries.AddBusiness(businessId: 1, configuredTime: new TimeOnly(8, 0));
        _dailySummaries.AddIndividual(business, 1, Start.Date.AddDays(1).AddHours(10));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewDailySummaryJob().RunAsync(Start, cancellation.Token));

        Assert.Empty(_dailySummaries.Notifications);
    }

    [Fact]
    public async Task Daily_summary_job_forwards_the_live_token_and_enqueues_nothing_when_the_caller_cancels_mid_batch()
    {
        var business = _dailySummaries.AddBusiness(businessId: 1, configuredTime: new TimeOnly(8, 0));
        _dailySummaries.AddIndividual(business, 1, Start.Date.AddDays(1).AddHours(10));
        using var cancellation = new CancellationTokenSource();
        // The token is cancelled while the scan is running, i.e. after the invocation already started.
        _dailySummaries.AfterList = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewDailySummaryJob().RunAsync(Start, cancellation.Token));

        Assert.Empty(_dailySummaries.Notifications);
    }

    [Fact]
    public async Task Daily_summary_job_propagates_a_repository_failure_unchanged_instead_of_swallowing_it()
    {
        var job = new DailySummaryScheduledJob(new DailySummaryService(new FailingDailySummaryRepository()));

        // Sanitising the failure is the runner's job; this adapter must not swallow it nor rewrite its type.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(Start));

        Assert.Equal("fallo-del-repositorio", error.Message);
    }

    // ---- Stock review adapter (RF-19): stable key, instant and token forwarding, no clock, no swallowed failure ----

    [Fact]
    public void Stock_review_job_exposes_the_stable_stock_review_key()
    {
        var job = NewStockReviewJob();

        Assert.Equal("stock-review", StockReviewScheduledJob.JobKey);
        Assert.Equal("stock-review", job.Key);
    }

    [Fact]
    public async Task Stock_review_job_delegates_the_run_with_the_supplied_instant()
    {
        // Anchor 2026-01-01 with a 7-day cadence: the bucket due at 2026-03-10 is 2026-03-05.
        _stockReviews.AddBusiness(businessId: 1);
        var job = NewStockReviewJob();

        await job.RunAsync(Start);

        Assert.Equal(1, _stockReviews.ListCount);
        Assert.Equal(1, _stockReviews.EnqueueAttempts);
        Assert.Equal(StockReviewService.BatchSize, _stockReviews.LastBatchSize);
        var notice = Assert.Single(_stockReviews.Notifications);
        Assert.Equal("stock:business:1:on:20260305", notice.DedupKey);
        Assert.StartsWith(StockReviewService.SubjectPrefix, notice.Subject, StringComparison.Ordinal);
        Assert.Equal(Start, notice.CreatedAt);
    }

    [Fact]
    public async Task Stock_review_job_uses_the_instant_of_every_invocation_and_never_a_cached_one()
    {
        _stockReviews.AddBusiness(businessId: 1);
        var job = NewStockReviewJob();

        await job.RunAsync(Start);
        // One cadence later the bucket advanced, so only a fresh instant can reach the second period.
        await job.RunAsync(Start.AddDays(7));

        Assert.Equal(
            new[] { Start, Start.AddDays(7) },
            _stockReviews.Notifications.Select(notice => notice.CreatedAt).ToArray());
        Assert.Equal(
            new[] { "stock:business:1:on:20260305", "stock:business:1:on:20260312" },
            _stockReviews.Notifications.Select(notice => notice.DedupKey).ToArray());
    }

    [Fact]
    public void Stock_review_job_depends_only_on_the_service_and_holds_no_clock_it_could_re_read()
    {
        var parameters = SingleConstructorParameterTypes<StockReviewScheduledJob>();

        Assert.Contains(typeof(StockReviewService), parameters);
        // Exactly one dependency: no clock and no logger are constructor-injected.
        Assert.Single(parameters);
        Assert.DoesNotContain(typeof(IClock), parameters);
    }

    [Fact]
    public async Task Stock_review_job_forwards_the_callers_token_and_enqueues_nothing_when_it_is_already_cancelled()
    {
        _stockReviews.AddBusiness(businessId: 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewStockReviewJob().RunAsync(Start, cancellation.Token));

        Assert.Empty(_stockReviews.Notifications);
    }

    [Fact]
    public async Task Stock_review_job_forwards_the_live_token_and_enqueues_nothing_when_the_caller_cancels_mid_batch()
    {
        _stockReviews.AddBusiness(businessId: 1);
        using var cancellation = new CancellationTokenSource();
        _stockReviews.AfterList = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewStockReviewJob().RunAsync(Start, cancellation.Token));

        Assert.Empty(_stockReviews.Notifications);
    }

    [Fact]
    public async Task Stock_review_job_propagates_a_repository_failure_unchanged_instead_of_swallowing_it()
    {
        var job = new StockReviewScheduledJob(new StockReviewService(new FailingStockReviewRepository()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(Start));

        Assert.Equal("fallo-del-repositorio", error.Message);
    }

    // ---- Membership expiry adapter (RF-30): stable key, instant and token forwarding, no clock, no swallowed failure ----

    [Fact]
    public void Membership_expiry_job_exposes_the_stable_membership_expiry_key()
    {
        var job = NewMembershipExpiryJob();

        Assert.Equal("membership-expiry", MembershipExpiryScheduledJob.JobKey);
        Assert.Equal("membership-expiry", job.Key);
    }

    [Fact]
    public async Task Membership_expiry_job_delegates_the_run_with_the_supplied_instant()
    {
        var enrollment = _membershipExpiries.AddEnrollment(enrollmentId: 1);
        // Two days before the expiry date, i.e. inside the three-days-before window of RF-30 step 1.
        _membershipExpiries.AddPeriod(enrollment, 1, new DateOnly(2026, 2, 12), new DateOnly(2026, 3, 12));
        var job = NewMembershipExpiryJob();

        await job.RunAsync(Start);

        Assert.Equal(1, _membershipExpiries.ListCount);
        Assert.Equal(1, _membershipExpiries.EnqueueAttempts);
        Assert.Equal(MembershipExpiryService.BatchSize, _membershipExpiries.LastBatchSize);
        var notice = Assert.Single(_membershipExpiries.Notifications);
        Assert.Equal("membership:enrollment:1:period:1:expiring", notice.DedupKey);
        Assert.Equal(MembershipExpiryService.ExpiringSubject, notice.Subject);
        Assert.Equal(Start, notice.CreatedAt);
    }

    [Fact]
    public async Task Membership_expiry_job_uses_the_instant_of_every_invocation_and_never_a_cached_one()
    {
        var enrollment = _membershipExpiries.AddEnrollment(enrollmentId: 1);
        _membershipExpiries.AddPeriod(enrollment, 1, new DateOnly(2026, 2, 12), new DateOnly(2026, 3, 12));
        var job = NewMembershipExpiryJob();

        await job.RunAsync(Start);
        // On the expiry date itself the stage advanced to Expired, which a cached instant could never reach.
        await job.RunAsync(Start.AddDays(2));

        Assert.Equal(
            new[] { Start, Start.AddDays(2) },
            _membershipExpiries.Notifications.Select(notice => notice.CreatedAt).ToArray());
        Assert.Equal(
            new[] { "membership:enrollment:1:period:1:expiring", "membership:enrollment:1:period:1:expired" },
            _membershipExpiries.Notifications.Select(notice => notice.DedupKey).ToArray());
    }

    [Fact]
    public void Membership_expiry_job_depends_only_on_the_service_and_holds_no_clock_it_could_re_read()
    {
        var parameters = SingleConstructorParameterTypes<MembershipExpiryScheduledJob>();

        Assert.Contains(typeof(MembershipExpiryService), parameters);
        // Exactly one dependency: no clock and no logger are constructor-injected.
        Assert.Single(parameters);
        Assert.DoesNotContain(typeof(IClock), parameters);
    }

    [Fact]
    public async Task Membership_expiry_job_forwards_the_callers_token_and_enqueues_nothing_when_it_is_already_cancelled()
    {
        var enrollment = _membershipExpiries.AddEnrollment(enrollmentId: 1);
        _membershipExpiries.AddPeriod(enrollment, 1, new DateOnly(2026, 2, 12), new DateOnly(2026, 3, 12));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewMembershipExpiryJob().RunAsync(Start, cancellation.Token));

        Assert.Empty(_membershipExpiries.Notifications);
    }

    [Fact]
    public async Task Membership_expiry_job_forwards_the_live_token_and_enqueues_nothing_when_the_caller_cancels_mid_batch()
    {
        var enrollment = _membershipExpiries.AddEnrollment(enrollmentId: 1);
        _membershipExpiries.AddPeriod(enrollment, 1, new DateOnly(2026, 2, 12), new DateOnly(2026, 3, 12));
        using var cancellation = new CancellationTokenSource();
        _membershipExpiries.AfterList = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewMembershipExpiryJob().RunAsync(Start, cancellation.Token));

        Assert.Empty(_membershipExpiries.Notifications);
    }

    [Fact]
    public async Task Membership_expiry_job_propagates_a_repository_failure_unchanged_instead_of_swallowing_it()
    {
        var job = new MembershipExpiryScheduledJob(new MembershipExpiryService(new FailingMembershipExpiryRepository()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(Start));

        Assert.Equal("fallo-del-repositorio", error.Message);
    }

    // ---- Composition root: scoped Sql ports, scoped services and the exact job order ----

    [Fact]
    public void WorkUp_registers_the_t25_notice_graph_and_the_exact_job_order()
    {
        var services = new ServiceCollection();
        RegisterWorkUp(services, DefaultSettings());

        AssertSqlScoped<IDailySummaryRepository, SqlDailySummaryRepository>(services);
        AssertSqlScoped<IStockReviewRepository, SqlStockReviewRepository>(services);
        AssertSqlScoped<IMembershipExpiryRepository, SqlMembershipExpiryRepository>(services);

        // The T23/T24 graph is untouched: same tick scope, same single sender and same single hosted worker.
        AssertSqlScoped<IReminderRepository, SqlReminderRepository>(services);
        AssertSqlScoped<IOutboxRepository, SqlOutboxRepository>(services);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(DailySummaryService)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(StockReviewService)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, d => d.ServiceType == typeof(MembershipExpiryService)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(ScheduledJobRunner)).Lifetime);

        // Exact registration order: a notice queued by a T25 job must already exist when the outbox job of that very
        // tick runs, so each notice job sits after the reminder job and before the outbox job.
        var jobTypes = services
            .Where(d => d.ServiceType == typeof(IScheduledJob))
            .Select(d => d.ImplementationType)
            .ToArray();
        Assert.Equal(
            new[]
            {
                typeof(ReminderScheduledJob),
                typeof(DailySummaryScheduledJob),
                typeof(StockReviewScheduledJob),
                typeof(MembershipExpiryScheduledJob),
                typeof(OutboxScheduledJob)
            },
            jobTypes);
        Assert.All(
            services.Where(d => d.ServiceType == typeof(IScheduledJob)),
            job => Assert.Equal(ServiceLifetime.Scoped, job.Lifetime));

        Assert.Single(services, d => d.ServiceType == typeof(IEmailSender));
        Assert.Single(services, d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(ScheduledJobsHostedService));
    }

    [Fact]
    public async Task The_registered_graph_exposes_the_five_stable_keys_in_that_exact_order()
    {
        await using var provider = BuildProvider(DefaultSettings());
        // The scoped graph resolves a DbSession, which only implements IAsyncDisposable, so the scope is async too.
        await using var scope = provider.CreateAsyncScope();

        // The runner validates the registry at construction, so resolving it also proves no key is empty or duplicated.
        var runner = scope.ServiceProvider.GetRequiredService<ScheduledJobRunner>();

        Assert.Equal(
            new[] { "reminders", "daily-summary", "stock-review", "membership-expiry", "outbox" },
            runner.Keys);
    }

    // ---- Synthetic tick: retention queues the three T25 notices and sends none ----

    [Fact]
    public async Task Retain_mode_tick_queues_the_three_t25_notices_and_claims_and_sends_none()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Default settings are Outbox mode, so the outbox job must retain and the sender must never be reached.
        RegisterWorkUp(services, DefaultSettings());
        var outbox = new FakeOutboxRepository();
        var sender = new FakeEmailSender();
        var clock = new FakeClock { Now = Start };
        var bridge = NoticeBridge.New(outbox, Start);
        AddNoticeFakes(services, bridge, outbox, sender, clock);

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostedService>();
        await host.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => bridge.EnqueuedCount == 3,
                "the three T25 jobs of the immediate tick to queue their notice");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        // All three jobs ran inside the tick's scope and queued exactly one notice each.
        Assert.Equal(3, bridge.EnqueuedCount);
        Assert.Equal(
            new[]
            {
                "summary:business:1:20260311",
                "stock:business:1:on:20260305",
                "membership:enrollment:1:period:1:expiring"
            },
            bridge.OutboxRows.Select(row => row.DedupKey).ToArray());
        // Retention: the outbox job of the same tick claimed nothing and the fake sender observed nothing, so the three
        // fresh notices are still Pending for the T26 viewer.
        Assert.Equal(0, outbox.ClaimCount);
        Assert.Equal(0, sender.CallCount);
        Assert.All(bridge.OutboxRows, row => Assert.Equal(NotificationStatus.Pending, row.Status));
        Assert.All(bridge.OutboxRows, row => Assert.Null(row.LastError));
        Assert.All(bridge.OutboxRows, row => Assert.Equal(0, row.Attempts));
    }

    // ---- Synthetic tick: SMTP mode dispatches the three T25 notices produced by that same tick ----

    [Fact]
    public async Task Smtp_mode_tick_dispatches_the_three_t25_notices_queued_by_the_same_tick()
    {
        var settings = DefaultSettings();
        settings["WorkUp:Email:Mode"] = "Smtp";
        settings["WorkUp:Email:Smtp:Host"] = "smtp.example.test";
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterWorkUp(services, settings);
        var outbox = new FakeOutboxRepository();
        var sender = new FakeEmailSender();
        var clock = new FakeClock { Now = Start };
        var bridge = NoticeBridge.New(outbox, Start);
        AddNoticeFakes(services, bridge, outbox, sender, clock);
        // The real DbSession would open the never-created database; SQL persistence is proven by the SQL fixture.
        services.AddScoped<IUnitOfWork>(_ => new NoopUnitOfWork());

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostedService>();
        await host.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => sender.CallCount == 3,
                "the outbox job to dispatch the three notices queued by the same tick");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        // The outbox job could only see these three notices because the T25 jobs ran first in the same tick and scope:
        // the bridge copies a notice only after the guarded T25 enqueue accepted it.
        Assert.Equal(3, bridge.EnqueuedCount);
        Assert.Equal(3, outbox.ClaimCount);
        Assert.Equal(
            new[]
            {
                "Resumen de agenda: 11/03/2026",
                "Revisión de existencias: 05/03/2026",
                "Tu abono vence pronto"
            },
            sender.Messages.Select(message => message.Subject).ToArray());
        Assert.All(bridge.OutboxRows, row => Assert.Equal(NotificationStatus.Sent, row.Status));
    }

    // ---- Adapters and harness ----

    private static Type[] SingleConstructorParameterTypes<TJob>() =>
        Assert.Single(typeof(TJob).GetConstructors())
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

    private DailySummaryScheduledJob NewDailySummaryJob() =>
        new(new DailySummaryService(_dailySummaries));

    private StockReviewScheduledJob NewStockReviewJob() =>
        new(new StockReviewService(_stockReviews));

    private MembershipExpiryScheduledJob NewMembershipExpiryJob() =>
        new(new MembershipExpiryService(_membershipExpiries));

    private static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["ConnectionStrings:WorkUp"] = NeverOpenableConnectionString,
        ["WorkUp:PublicBaseUrl"] = BaseUrl,
        ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires"
    };

    private static void RegisterWorkUp(IServiceCollection services, IDictionary<string, string?> settings) =>
        services.AddWorkUp(BuildConfiguration(settings), new FakeHostEnvironment());

    private static ServiceProvider BuildProvider(IDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterWorkUp(services, settings);
        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static void AssertSqlScoped<TInterface, TImplementation>(IServiceCollection services)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TInterface));
        Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    /// <summary>
    /// Replaces every port the tick touches with an in-memory fake, exactly like the T24 reminder wiring: the T25
    /// repositories share the tick's scope through the bridge, the outbox fake receives the queued rows and the
    /// reminder port stays empty so this test observes the T25 path alone.
    /// </summary>
    private static void AddNoticeFakes(
        IServiceCollection services,
        NoticeBridge bridge,
        FakeOutboxRepository outbox,
        FakeEmailSender sender,
        FakeClock clock)
    {
        services.AddScoped<IDailySummaryRepository>(_ => bridge.DailySummaryPort);
        services.AddScoped<IStockReviewRepository>(_ => bridge.StockReviewPort);
        services.AddScoped<IMembershipExpiryRepository>(_ => bridge.MembershipExpiryPort);
        services.AddScoped<IReminderRepository>(_ => new FakeReminderRepository());
        services.AddScoped<IOutboxRepository>(_ => outbox);
        services.AddScoped<IEmailSender>(_ => sender);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ILinkTokenProtector>(new TestLinkTokenProtector());
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
    /// Test-only bridge between the in-memory T25 ports and the outbox fake. In production the three jobs and the
    /// outbox dispatcher share the same <c>Notifications</c> table, so a notice queued by a T25 job of a tick is exactly
    /// what the outbox job of that same tick claims. The fakes are independent objects, so every notice a guarded T25
    /// enqueue accepted is copied into the outbox fake, and only then; a rejected enqueue copies nothing.
    /// <para>
    /// This is deliberately <b>not</b> real SQL: the fake holds no row lock and no unique key, so it cannot prove the
    /// atomic guard. And it is <b>not</b> real SMTP: the sender is a fake, so nothing claims a real delivery.
    /// </para>
    /// </summary>
    private sealed class NoticeBridge(FakeOutboxRepository outbox)
    {
        public FakeDailySummaryRepository DailySummaries { get; } = new();

        public FakeStockReviewRepository StockReviews { get; } = new();

        public FakeMembershipExpiryRepository MembershipExpiries { get; } = new();

        public List<Notification> OutboxRows { get; } = [];

        public int EnqueuedCount { get; private set; }

        public IDailySummaryRepository DailySummaryPort => new DailySummaryAdapter(this);

        public IStockReviewRepository StockReviewPort => new StockReviewAdapter(this);

        public IMembershipExpiryRepository MembershipExpiryPort => new MembershipExpiryAdapter(this);

        /// <summary>
        /// Seeds exactly one due notice per T25 core at <paramref name="now"/>: a business whose configured summary
        /// hour already arrived with one individual turn on the target day, a business whose stock review bucket is due,
        /// and an active enrollment whose period expires inside the three-days-before window.
        /// </summary>
        public static NoticeBridge New(FakeOutboxRepository outbox, DateTime now)
        {
            var bridge = new NoticeBridge(outbox);
            var business = bridge.DailySummaries.AddBusiness(businessId: 1, configuredTime: new TimeOnly(8, 0));
            bridge.DailySummaries.AddIndividual(business, 1, now.Date.AddDays(1).AddHours(10));
            bridge.StockReviews.AddBusiness(businessId: 1);
            var enrollment = bridge.MembershipExpiries.AddEnrollment(enrollmentId: 1);
            bridge.MembershipExpiries.AddPeriod(enrollment, 1, new DateOnly(2026, 2, 12), new DateOnly(2026, 3, 12));
            return bridge;
        }

        private bool Accept(Notification notification)
        {
            EnqueuedCount++;
            OutboxRows.Add(outbox.Seed(
                notification.DedupKey,
                notification.CreatedAt,
                recipient: notification.Recipient,
                subject: notification.Subject,
                bodyHtml: notification.BodyHtml));
            return true;
        }

        private sealed class DailySummaryAdapter(NoticeBridge bridge) : IDailySummaryRepository
        {
            public Task<IReadOnlyList<DailySummaryCandidate>> ListCandidatesAsync(
                DateTime now, int batchSize, CancellationToken ct = default) =>
                bridge.DailySummaries.ListCandidatesAsync(now, batchSize, ct);

            public async Task<bool> TryEnqueueSummaryAsync(DailySummaryNotice notice, CancellationToken ct = default) =>
                await bridge.DailySummaries.TryEnqueueSummaryAsync(notice, ct) && bridge.Accept(notice.Notification);
        }

        private sealed class StockReviewAdapter(NoticeBridge bridge) : IStockReviewRepository
        {
            public Task<IReadOnlyList<StockReviewCandidate>> ListCandidatesAsync(
                DateTime now, int batchSize, CancellationToken ct = default) =>
                bridge.StockReviews.ListCandidatesAsync(now, batchSize, ct);

            public async Task<bool> TryEnqueueReviewAsync(StockReviewNotice notice, CancellationToken ct = default) =>
                await bridge.StockReviews.TryEnqueueReviewAsync(notice, ct) && bridge.Accept(notice.Notification);
        }

        private sealed class MembershipExpiryAdapter(NoticeBridge bridge) : IMembershipExpiryRepository
        {
            public Task<IReadOnlyList<MembershipExpiryCandidate>> ListCandidatesAsync(
                DateTime now, int batchSize, CancellationToken ct = default) =>
                bridge.MembershipExpiries.ListCandidatesAsync(now, batchSize, ct);

            public async Task<bool> TryEnqueueNoticeAsync(
                MembershipExpiryNotice notice, CancellationToken ct = default) =>
                await bridge.MembershipExpiries.TryEnqueueNoticeAsync(notice, ct) && bridge.Accept(notice.Notification);
        }
    }

    /// <summary>Repository whose scan fails, so the adapter's failure-propagation contract can be observed.</summary>
    private sealed class FailingDailySummaryRepository : IDailySummaryRepository
    {
        public Task<IReadOnlyList<DailySummaryCandidate>> ListCandidatesAsync(
            DateTime now, int batchSize, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<DailySummaryCandidate>>(
                new InvalidOperationException("fallo-del-repositorio"));

        public Task<bool> TryEnqueueSummaryAsync(DailySummaryNotice notice, CancellationToken ct = default) =>
            throw new InvalidOperationException("fallo-del-repositorio");
    }

    /// <summary>Repository whose scan fails, so the adapter's failure-propagation contract can be observed.</summary>
    private sealed class FailingStockReviewRepository : IStockReviewRepository
    {
        public Task<IReadOnlyList<StockReviewCandidate>> ListCandidatesAsync(
            DateTime now, int batchSize, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<StockReviewCandidate>>(
                new InvalidOperationException("fallo-del-repositorio"));

        public Task<bool> TryEnqueueReviewAsync(StockReviewNotice notice, CancellationToken ct = default) =>
            throw new InvalidOperationException("fallo-del-repositorio");
    }

    /// <summary>Repository whose scan fails, so the adapter's failure-propagation contract can be observed.</summary>
    private sealed class FailingMembershipExpiryRepository : IMembershipExpiryRepository
    {
        public Task<IReadOnlyList<MembershipExpiryCandidate>> ListCandidatesAsync(
            DateTime now, int batchSize, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<MembershipExpiryCandidate>>(
                new InvalidOperationException("fallo-del-repositorio"));

        public Task<bool> TryEnqueueNoticeAsync(MembershipExpiryNotice notice, CancellationToken ct = default) =>
            throw new InvalidOperationException("fallo-del-repositorio");
    }

    /// <summary>Fake protector, so these wiring tests never read or write the real data-protection keyring.</summary>
    private sealed class TestLinkTokenProtector : ILinkTokenProtector
    {
        public string Protect(string token) => "protected:" + token;

        public string Unprotect(string protectedToken) => protectedToken["protected:".Length..];
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
