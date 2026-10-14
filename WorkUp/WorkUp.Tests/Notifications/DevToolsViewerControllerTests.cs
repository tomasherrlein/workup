using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.Infrastructure.Jobs;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// T26 viewer UI (plan assumptions 10 and 11): the owner-only, fail-closed page over
/// <see cref="INotificationViewerRepository"/>. The persistence contract of the port is already pinned elsewhere; what
/// is pinned here is what the panel adds: every action refuses a closed gate with the same 404 and without touching the
/// repository, the tenant always comes from the owner cookie claim and never from the request, the list is a bounded
/// newest-first keyset page with no body, the detail is reachable only for the caller's own notice, both pages are
/// never cached and carry no referrer, and the rendered view shows the body as escaped plain text with no unsafe link
/// and no hidden attribute.
/// <para>
/// The fail-closed proof here is also about activation order: the controller defers every collaborator to the scoped
/// provider and resolves it inside each action only after the gate accepted the request, so a closed gate answers 404
/// with zero viewer, runner or lease resolutions. The tests use a recording provider that throws on any resolution, so
/// the assertion cannot be satisfied by a service that merely happened to be inactive.
/// </para>
/// <para>
/// The HTML-escaping proof here is structural: the Razor source must use the encoded <c>@Model.BodyHtml</c> inside a
/// <c>&lt;pre&gt;</c> and must never call <c>Html.Raw</c>, build an <c>&lt;iframe&gt;</c> or place the body in an
/// attribute or a URL. Rendering the real markup through the shipped pipeline is deferred to the opt-in host fixture,
/// because this project has no in-process Razor renderer and the host harness is the only place where the composed page
/// is observed.
/// </para>
/// </summary>
public sealed class DevToolsViewerControllerTests
{
    private const int BusinessId = 7;
    private const string OwnerUsername = "lucia.demo";

    private static readonly DateTime Start = new(2026, 3, 10, 9, 0, 0);

    private readonly FakeNotificationViewerRepository _viewer = new();

    // One shared, in-process clock per test: it is the same singleton behind IClock and ISimulatedClockControl, so a
    // SPA request sees exactly the offset a previous one applied.
    private readonly SimulatedBusinessClock _clock = new(new FakeClock { Now = Start });

    // --- Fixtures -----------------------------------------------------------------------------------------------

    private static DevToolsGate Gate(bool enabled)
    {
        var options = new WorkUpOptions { Features = new FeatureOptions { DevTools = enabled } };
        var environment = new FakeHostEnvironment
        {
            EnvironmentName = enabled ? Environments.Development : Environments.Production
        };
        // Retain mode is the only email mode the tools accept, so the enabled gate stays a real gate rather than a test
        // stub; the disabled gate is a production host with the flag on, the exact refusal the task must keep.
        return new DevToolsGate(environment, Options.Create(options), OutboxMode.Retain);
    }

    private DevToolsController Create(
        bool gateOpen = true,
        ScheduledExecutionGate? execution = null,
        ScheduledJobRunner? runner = null,
        RecordingServiceProvider? services = null,
        SimulatedBusinessClock? clock = null)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(WorkUpClaims.BusinessId, BusinessId.ToString()),
                new Claim(WorkUpClaims.BusinessName, "Estudio Lucía"),
                new Claim(ClaimTypes.Name, OwnerUsername)
            ], AuthSchemes.Owner))
        };

        // The controller defers every collaborator to the scoped provider after its fail-closed gate check, so the
        // harness passes exactly that provider. A test that proves the closed gate activated nothing passes an empty
        // recording provider, which throws on any resolution instead of hiding the access behind a harmless stub.
        var provider = services ?? new RecordingServiceProvider(
            (typeof(INotificationViewerRepository), _viewer),
            (typeof(ScheduledJobRunner), runner ?? Runner(new DevToolsJob("outbox"))),
            (typeof(ScheduledExecutionGate), execution ?? new ScheduledExecutionGate()),
            (typeof(IClock), clock ?? _clock),
            (typeof(ISimulatedClockControl), clock ?? _clock));

        return new DevToolsController(Gate(gateOpen), provider)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
    }

    private static ScheduledJobRunner Runner(params DevToolsJob[] jobs) =>
        new(jobs, new FakeClock { Now = Start });

    private static async Task<IReadOnlyList<NotificationViewerListItem>> List(
        DevToolsController controller, int? beforeId = null) =>
        Assert.IsAssignableFrom<IReadOnlyList<NotificationViewerListItem>>(
            Assert.IsType<ViewResult>(await controller.Index(beforeId, CancellationToken.None)).Model);

    private static async Task<NotificationViewerDetail> Detail(DevToolsController controller, int id) =>
        Assert.IsType<NotificationViewerDetail>(
            Assert.IsType<ViewResult>(await controller.Detail(id, CancellationToken.None)).Model);

    // --- Closed gate: the same neutral 404 and no repository read -----------------------------------------------

    [Fact]
    public async Task Index_answers_not_found_and_never_reads_when_the_gate_is_closed()
    {
        _viewer.Seed(businessId: BusinessId);
        // An empty provider throws on any resolution, so a 404 with an empty record proves the action touched nothing.
        var services = new RecordingServiceProvider();

        var result = await Create(gateOpen: false, services: services).Index(null, CancellationToken.None);

        // A flag mistakenly on in production is a refusal, and the refusal never reaches storage: the caller learns
        // nothing about whether a notice exists.
        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(services.Requested);
        Assert.Equal(0, _viewer.ListCallCount);
    }

    [Fact]
    public async Task Detail_answers_not_found_and_never_reads_when_the_gate_is_closed_for_a_valid_id()
    {
        var seeded = _viewer.Seed(businessId: BusinessId);
        var services = new RecordingServiceProvider();

        var result = await Create(gateOpen: false, services: services).Detail(seeded.Id, CancellationToken.None);

        // The direct URL is refused exactly like the list: the gate runs first and the repository is never consulted
        // nor even resolved.
        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(services.Requested);
        Assert.Equal(0, _viewer.DetailCallCount);
    }

    // --- The gate runs before any collaborator is resolved (T26) ------------------------------------------------

    [Fact]
    public async Task Detail_resolves_only_the_viewer_and_never_the_job_graph()
    {
        var seeded = _viewer.Seed(businessId: BusinessId, bodyHtml: "<p>Mi cuerpo</p>");
        // Only the read adapter is registered: resolving the runner or the lease would throw, so this pins that the
        // one-row page never activates the job graph or the execution lease.
        var services = new RecordingServiceProvider((typeof(INotificationViewerRepository), _viewer));

        var detail = await Detail(Create(services: services), seeded.Id);

        Assert.Equal(seeded.Id, detail.Id);
        Assert.Equal([typeof(INotificationViewerRepository)], services.Requested);
    }

    [Fact]
    public async Task Index_resolves_the_viewer_the_runner_and_the_clock_but_never_the_lease()
    {
        var services = new RecordingServiceProvider(
            (typeof(INotificationViewerRepository), _viewer),
            (typeof(ScheduledJobRunner), Runner(new DevToolsJob("outbox"))),
            (typeof(IClock), _clock),
            (typeof(ISimulatedClockControl), _clock));

        await Create(services: services).Index(null, CancellationToken.None);

        // The list needs the read adapter, the stable keys and the shared clock state; it never runs a job nor moves
        // the clock, so it must leave the lease alone.
        Assert.Contains(typeof(INotificationViewerRepository), services.Requested);
        Assert.Contains(typeof(ScheduledJobRunner), services.Requested);
        Assert.Contains(typeof(IClock), services.Requested);
        Assert.Contains(typeof(ISimulatedClockControl), services.Requested);
        Assert.DoesNotContain(typeof(ScheduledExecutionGate), services.Requested);
    }

    [Fact]
    public async Task Run_job_resolves_the_runner_and_the_lease_but_never_the_viewer()
    {
        var runs = new List<string>();
        var services = new RecordingServiceProvider(
            (typeof(ScheduledJobRunner), Runner(new DevToolsJob("outbox", () =>
            {
                runs.Add("outbox");
                return Task.CompletedTask;
            }))),
            (typeof(ScheduledExecutionGate), new ScheduledExecutionGate()));

        var result = await Create(services: services).RunJob("outbox", CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Contains(typeof(ScheduledJobRunner), services.Requested);
        Assert.Contains(typeof(ScheduledExecutionGate), services.Requested);
        Assert.DoesNotContain(typeof(INotificationViewerRepository), services.Requested);
        Assert.Equal(new[] { "outbox" }, runs);
    }

    // --- Manual run: owner-only, fail-closed, serialized and never echoing the input (T26) ----------------------

    [Fact]
    public async Task Run_job_answers_not_found_and_activates_nothing_when_the_gate_is_closed()
    {
        var runs = new List<string>();
        var services = new RecordingServiceProvider();

        var result = await Create(gateOpen: false, services: services).RunJob("outbox", CancellationToken.None);

        // The feature gate is checked before anything else, so a disabled tool cannot run a job by direct POST and does
        // not resolve the viewer, the runner or the lease.
        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(services.Requested);
        Assert.Empty(runs);
    }

    [Fact]
    public async Task Run_job_answers_the_neutral_not_found_for_an_unknown_key_without_running_or_echoing_it()
    {
        var runs = new List<string>();
        var services = new RecordingServiceProvider(
            (typeof(ScheduledJobRunner), Runner(new DevToolsJob("outbox", () =>
            {
                runs.Add("outbox");
                return Task.CompletedTask;
            }))),
            (typeof(ScheduledExecutionGate), new ScheduledExecutionGate()));
        var controller = Create(services: services);

        var result = await controller.RunJob("outbox-typo", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(runs);
        // No read adapter is even resolved for an unknown key: the refusal never touches storage.
        Assert.DoesNotContain(typeof(INotificationViewerRepository), services.Requested);
        // The untrusted key is never reflected into the state the next page renders or the log could carry.
        Assert.DoesNotContain(
            controller.TempData.Values,
            value => value?.ToString()?.Contains("outbox-typo", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Run_job_redirects_with_the_fixed_completion_message_after_a_successful_run()
    {
        var runs = new List<string>();
        var controller = Create(runner: Runner(new DevToolsJob("outbox", () =>
        {
            runs.Add("outbox");
            return Task.CompletedTask;
        })));

        var result = await controller.RunJob("outbox", CancellationToken.None);

        // Post/Redirect/Get: a fixed one-time message plus a redirect, never a view that re-posts on refresh.
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(DevToolsController.Index), redirect.ActionName);
        Assert.Equal(new[] { "outbox" }, runs);
        Assert.Equal(DevToolsController.RunCompletedMessage, controller.TempData["FlashMessage"]);
        Assert.Equal("Success", controller.TempData["FlashType"]);
    }

    [Fact]
    public async Task Run_job_redirects_with_the_fixed_busy_message_when_another_run_holds_the_lease()
    {
        var execution = new ScheduledExecutionGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = execution.TryRunOneAsync(
            Runner(new DevToolsJob("outbox", () =>
            {
                started.TrySetResult();
                return release.Task;
            })),
            "outbox",
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var runs = new List<string>();
        var controller = Create(
            execution: execution,
            runner: Runner(new DevToolsJob("outbox", () =>
            {
                runs.Add("outbox");
                return Task.CompletedTask;
            })));

        var result = await controller.RunJob("outbox", CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(DevToolsController.RunBusyMessage, controller.TempData["FlashMessage"]);
        Assert.Empty(runs);

        release.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Run_job_redirects_with_a_fixed_safe_message_when_the_job_fails()
    {
        var controller = Create(runner: Runner(new DevToolsJob("outbox", () =>
            Task.FromException(new InvalidOperationException("password=no-debe-salir")))));

        var result = await controller.RunJob("outbox", CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(DevToolsController.RunFailedMessage, controller.TempData["FlashMessage"]);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        // A provider error can carry a password and a job can touch a one-use token: neither reaches the page.
        var message = Assert.IsType<string>(controller.TempData["FlashMessage"]);
        Assert.DoesNotContain("no-debe-salir", message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_job_rethrows_cancellation_instead_of_reporting_it_as_a_job_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runs = new List<string>();
        var controller = Create(runner: Runner(new DevToolsJob("outbox", () =>
        {
            runs.Add("outbox");
            return Task.CompletedTask;
        })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.RunJob("outbox", cancellation.Token));

        // An aborted request is not a failed job: no failure message is flashed and nothing ran.
        Assert.Null(controller.TempData["FlashMessage"]);
        Assert.Empty(runs);
    }

    [Fact]
    public void Run_job_is_a_post_action_that_validates_the_antiforgery_token()
    {
        var method = typeof(DevToolsController).GetMethod(nameof(DevToolsController.RunJob))!;

        Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        // The global AutoValidate filter is what makes the token mandatory in the real host, so neither a GET nor a
        // bare POST can ever reach a job.
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "Program.cs"));
        Assert.Contains("AutoValidateAntiforgeryTokenAttribute", program, StringComparison.Ordinal);
        Assert.Contains("options.Filters.Add", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_exposes_the_stable_job_keys_of_the_runner_to_the_view()
    {
        var controller = Create(runner: Runner(
            new DevToolsJob("reminders"),
            new DevToolsJob("outbox")));

        await controller.Index(null, CancellationToken.None);

        var keys = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            controller.ViewData[DevToolsController.JobKeysViewDataKey]);
        Assert.Equal(new[] { "reminders", "outbox" }, keys);
    }

    // --- Simulated clock: fail-closed posts under the shared lease (T26) ----------------------------------------

    [Theory]
    [InlineData(nameof(DevToolsController.AdvanceHours))]
    [InlineData(nameof(DevToolsController.AdvanceDays))]
    [InlineData(nameof(DevToolsController.RestoreClock))]
    public async Task Clock_post_answers_not_found_and_resolves_nothing_when_the_gate_is_closed(string action)
    {
        // An empty provider throws on any resolution, so a 404 with an untouched clock proves the gate ran first and
        // no clock, no lease and no viewer was ever activated.
        var services = new RecordingServiceProvider();
        var controller = Create(gateOpen: false, services: services);

        var result = action switch
        {
            nameof(DevToolsController.AdvanceHours) =>
                await controller.AdvanceHours(3, confirmAllBusinesses: true, CancellationToken.None),
            nameof(DevToolsController.AdvanceDays) =>
                await controller.AdvanceDays(2, confirmAllBusinesses: true, CancellationToken.None),
            _ => await controller.RestoreClock(CancellationToken.None)
        };

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(services.Requested);
        Assert.False(_clock.IsSimulated);
        Assert.Equal(Start, _clock.Now);
    }

    [Theory]
    [InlineData(nameof(DevToolsController.AdvanceHours))]
    [InlineData(nameof(DevToolsController.AdvanceDays))]
    [InlineData(nameof(DevToolsController.RestoreClock))]
    public void Clock_posts_are_post_actions_that_validate_the_antiforgery_token(string action)
    {
        var method = typeof(DevToolsController).GetMethod(action)!;

        Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        // The owner cookie is inherited from OwnerControllerBase; no clock action opts a visitor out of it.
        Assert.False(method.IsDefined(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Fact]
    public void The_clock_actions_take_no_tenant_input()
    {
        // The only inputs are the amount, the shared-clock confirmation and the request token: there is no business,
        // business-id or employee parameter a caller could use to widen the mutation, and the confirmation keeps one
        // owner from silently moving both demo businesses.
        Assert.Equal(
            ["hours", "confirmAllBusinesses", "ct"],
            ParameterNames(nameof(DevToolsController.AdvanceHours)));
        Assert.Equal(
            ["days", "confirmAllBusinesses", "ct"],
            ParameterNames(nameof(DevToolsController.AdvanceDays)));
        Assert.Equal(["ct"], ParameterNames(nameof(DevToolsController.RestoreClock)));
    }

    [Fact]
    public async Task Advance_without_the_shared_clock_confirmation_changes_nothing_and_resolves_no_collaborator()
    {
        // An empty provider throws on any resolution: the unconfirmed advance is refused before the clock or the lease
        // is even asked for.
        var services = new RecordingServiceProvider();
        var controller = Create(services: services);

        var result = await controller.AdvanceHours(3, confirmAllBusinesses: false, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(DevToolsController.Index), redirect.ActionName);
        Assert.Equal(DevToolsController.ClockConfirmationRequiredMessage, controller.TempData["FlashMessage"]);
        Assert.Equal("Warning", controller.TempData["FlashType"]);
        Assert.Empty(services.Requested);
        Assert.False(_clock.IsSimulated);
        Assert.Equal(Start, _clock.Now);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SimulatedBusinessClock.MaxAdvanceHours + 1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Advance_hours_rejects_an_out_of_range_amount_with_a_fixed_safe_flash(int hours)
    {
        var controller = Create();

        var result = await controller.AdvanceHours(hours, confirmAllBusinesses: true, CancellationToken.None);

        // A rejected bound is a Post/Redirect/Get with the fixed message, and the shared clock is exactly where it was.
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(DevToolsController.Index), redirect.ActionName);
        Assert.Equal(SimulatedBusinessClock.AdvanceHoursMessage, controller.TempData["FlashMessage"]);
        Assert.DoesNotContain(hours.ToString(), (string)controller.TempData["FlashMessage"]!, StringComparison.Ordinal);
        Assert.False(_clock.IsSimulated);
        Assert.Equal(Start, _clock.Now);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SimulatedBusinessClock.MaxAdvanceDays + 1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Advance_days_rejects_an_out_of_range_amount_with_a_fixed_safe_flash(int days)
    {
        var controller = Create();

        var result = await controller.AdvanceDays(days, confirmAllBusinesses: true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(DevToolsController.Index), redirect.ActionName);
        Assert.Equal(SimulatedBusinessClock.AdvanceDaysMessage, controller.TempData["FlashMessage"]);
        Assert.DoesNotContain(days.ToString(), (string)controller.TempData["FlashMessage"]!, StringComparison.Ordinal);
        Assert.False(_clock.IsSimulated);
        Assert.Equal(Start, _clock.Now);
    }

    [Fact]
    public async Task Advance_hours_that_would_leave_the_date_range_flashes_the_fixed_message_and_changes_nothing()
    {
        var overflow = new SimulatedBusinessClock(new FakeClock { Now = DateTime.MaxValue });
        var controller = Create(clock: overflow);

        var result = await controller.AdvanceHours(1, confirmAllBusinesses: true, CancellationToken.None);

        // The clock is never silently clamped to another day: the loud overflow becomes one fixed message and the
        // simulation stays exactly at the representable instant it had.
        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(SimulatedBusinessClock.OverflowMessage, controller.TempData["FlashMessage"]);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.False(overflow.IsSimulated);
        Assert.Equal(9999, overflow.Now.Year);
    }

    [Fact]
    public async Task A_rejected_advance_releases_the_lease_for_the_next_manual_run()
    {
        var execution = new ScheduledExecutionGate();
        var runs = new List<string>();
        var controller = Create(
            execution: execution,
            runner: Runner(new DevToolsJob("outbox", () =>
            {
                runs.Add("outbox");
                return Task.CompletedTask;
            })));

        var rejected = await controller.AdvanceHours(0, confirmAllBusinesses: true, CancellationToken.None);
        Assert.IsType<RedirectToActionResult>(rejected);

        // The rejected bound threw synchronously inside the mutation: the shared lease must be free again immediately.
        var run = await controller.RunJob("outbox", CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(run);
        Assert.Equal(new[] { "outbox" }, runs);
    }

    [Fact]
    public async Task Advance_hours_moves_the_shared_clock_and_flashes_a_fixed_completion()
    {
        var controller = Create();

        var result = await controller.AdvanceHours(5, confirmAllBusinesses: true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(DevToolsController.Index), redirect.ActionName);
        Assert.Equal(DevToolsController.ClockHoursAdvancedMessage, controller.TempData["FlashMessage"]);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.True(_clock.IsSimulated);
        Assert.Equal(Start.AddHours(5), _clock.Now);
    }

    [Fact]
    public async Task Advance_days_moves_the_shared_clock_and_flashes_a_fixed_completion()
    {
        var controller = Create();

        var result = await controller.AdvanceDays(2, confirmAllBusinesses: true, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(DevToolsController.ClockDaysAdvancedMessage, controller.TempData["FlashMessage"]);
        Assert.True(_clock.IsSimulated);
        Assert.Equal(Start.AddDays(2), _clock.Now);
    }

    [Fact]
    public async Task Advance_reports_busy_with_a_fixed_flash_when_a_job_holds_the_lease_and_leaves_the_clock_alone()
    {
        var execution = new ScheduledExecutionGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = execution.TryRunOneAsync(
            Runner(new DevToolsJob("outbox", () =>
            {
                started.TrySetResult();
                return release.Task;
            })),
            "outbox",
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var controller = Create(execution: execution);

        var result = await controller.AdvanceHours(5, confirmAllBusinesses: true, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(DevToolsController.ClockBusyMessage, controller.TempData["FlashMessage"]);
        Assert.Equal("Warning", controller.TempData["FlashType"]);
        Assert.False(_clock.IsSimulated);
        Assert.Equal(Start, _clock.Now);

        release.TrySetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Restore_returns_to_the_real_time_and_is_idempotent()
    {
        _clock.AdvanceDays(3);

        var controller = Create();
        var first = await controller.RestoreClock(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(first);
        Assert.Equal(nameof(DevToolsController.Index), redirect.ActionName);
        Assert.Equal(DevToolsController.ClockRestoredMessage, controller.TempData["FlashMessage"]);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.False(_clock.IsSimulated);
        Assert.Equal(Start, _clock.Now);

        // Restoring again is not a toggle: the real instant is the same and the second call still succeeds.
        var again = Create();
        var second = await again.RestoreClock(CancellationToken.None);
        Assert.IsType<RedirectToActionResult>(second);
        Assert.Equal(DevToolsController.ClockRestoredMessage, again.TempData["FlashMessage"]);
        Assert.False(_clock.IsSimulated);
        Assert.Equal(Start, _clock.Now);
    }

    [Fact]
    public async Task Restore_flashes_a_fixed_message_when_the_clock_cannot_be_resolved()
    {
        var services = new RecordingServiceProvider((typeof(ScheduledExecutionGate), new ScheduledExecutionGate()));
        var controller = Create(services: services);

        var result = await controller.RestoreClock(CancellationToken.None);

        // A resolution failure is a fixed, content-free message: neither the provider's text nor the exception type
        // reaches the page.
        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(DevToolsController.ClockRestoreFailedMessage, controller.TempData["FlashMessage"]);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        var message = Assert.IsType<string>(controller.TempData["FlashMessage"]);
        Assert.DoesNotContain("ISimulatedClockControl", message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_exposes_the_shared_clock_state_without_any_tenant_data()
    {
        _clock.AdvanceHours(2);
        var controller = Create();

        await controller.Index(null, CancellationToken.None);

        var state = Assert.IsType<DevToolsClockState>(controller.ViewData[DevToolsController.ClockStateViewDataKey]);
        Assert.True(state.IsSimulated);
        Assert.Equal(Start.AddHours(2), state.LocalNow);
    }

    // --- Tenant scope comes from the owner claim only -----------------------------------------------------------

    [Fact]
    public async Task Index_lists_only_the_owners_business_newest_first()
    {
        _viewer.Seed(businessId: BusinessId, subject: "Propia antigua", id: 10);
        _viewer.Seed(businessId: BusinessId, subject: "Propia nueva", id: 11);
        _viewer.Seed(businessId: 8, subject: "Ajena", id: 12);
        _viewer.Seed(businessId: null, subject: "Sin negocio", id: 13);

        var items = await List(Create());

        Assert.Equal([11, 10], items.Select(item => item.Id));
        Assert.DoesNotContain(items, item => item.Subject == "Ajena" || item.Subject == "Sin negocio");
    }

    [Fact]
    public async Task Detail_returns_the_body_only_for_the_owners_own_notice()
    {
        var mine = _viewer.Seed(businessId: BusinessId, bodyHtml: "<p>Mi cuerpo</p>");
        var foreign = _viewer.Seed(businessId: 8, bodyHtml: "<p>Cuerpo ajeno</p>");

        var detail = await Detail(Create(), mine.Id);

        Assert.Equal(mine.Id, detail.Id);
        Assert.Equal("<p>Mi cuerpo</p>", detail.BodyHtml);

        // Another business's notice is answered like a missing one: the same 404, with no hint that it exists.
        var refused = await Create().Detail(foreign.Id, CancellationToken.None);
        Assert.IsType<NotFoundResult>(refused);
    }

    [Fact]
    public async Task Detail_answers_a_missing_or_non_positive_identifier_with_the_same_not_found()
    {
        var controller = Create();

        Assert.IsType<NotFoundResult>(await controller.Detail(9999, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Detail(0, CancellationToken.None));

        // A non-positive identifier is the neutral denial of the contract: no notice can carry it, so the repository
        // was never asked.
        Assert.Equal(1, _viewer.DetailCallCount);
    }

    // --- Bounded keyset page and empty state --------------------------------------------------------------------

    [Fact]
    public async Task Index_is_an_empty_page_when_the_business_has_no_notices()
    {
        _viewer.Seed(businessId: 8);

        var items = await List(Create());

        Assert.Empty(items);
    }

    [Fact]
    public async Task Index_pages_with_a_bounded_newest_first_keyset()
    {
        var seeded = Enumerable.Range(1, NotificationViewerBounds.MaxPageSize + 5)
            .Select(id => _viewer.Seed(businessId: BusinessId, id: id))
            .ToArray();
        var newest = seeded[^1].Id;

        var firstPage = await List(Create());

        // The page never exceeds the contract bound, and it starts at the newest row.
        Assert.Equal(NotificationViewerBounds.MaxPageSize, firstPage.Count);
        Assert.Equal(newest, firstPage[0].Id);
        Assert.Equal(newest - NotificationViewerBounds.MaxPageSize + 1, firstPage[^1].Id);

        var secondPage = await List(Create(), beforeId: firstPage[^1].Id);

        // The cursor is a strict keyset: the second page holds exactly the strictly older rows and repeats none.
        Assert.Equal(5, secondPage.Count);
        Assert.Equal(newest - NotificationViewerBounds.MaxPageSize, secondPage[0].Id);
        Assert.DoesNotContain(secondPage, item => firstPage.Any(first => first.Id == item.Id));
    }

    // --- Never cached, never a referrer -------------------------------------------------------------------------

    [Fact]
    public async Task Both_pages_are_marked_no_store_and_carry_no_referrer()
    {
        var seeded = _viewer.Seed(businessId: BusinessId);

        var controller = Create();
        await controller.Index(null, CancellationToken.None);
        AssertPrivateHeaders(controller);

        var detailController = Create();
        await detailController.Detail(seeded.Id, CancellationToken.None);
        AssertPrivateHeaders(detailController);
    }

    [Fact]
    public void Both_actions_ask_the_response_cache_not_to_store()
    {
        foreach (var action in new[] { nameof(DevToolsController.Index), nameof(DevToolsController.Detail) })
        {
            var attribute = Assert.Single(
                typeof(DevToolsController).GetMethod(action)!.GetCustomAttributes<ResponseCacheAttribute>());

            Assert.True(attribute.NoStore);
            Assert.Equal(ResponseCacheLocation.None, attribute.Location);
        }
    }

    // --- Owner-only surface and no tenant input -----------------------------------------------------------------

    [Fact]
    public void The_controller_is_owner_only_and_offers_no_public_access()
    {
        Assert.True(typeof(OwnerControllerBase).IsAssignableFrom(typeof(DevToolsController)));

        // The owner cookie is inherited from the base controller: an anonymous request never reaches an action.
        var authorize = Assert.Single(
            typeof(DevToolsController).GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal(AuthSchemes.Owner, authorize.AuthenticationSchemes);

        // Nothing on the controller or its actions opts a visitor out of that authorization.
        Assert.False(typeof(DevToolsController).IsDefined(typeof(AllowAnonymousAttribute), inherit: true));
        foreach (var action in new[] { nameof(DevToolsController.Index), nameof(DevToolsController.Detail) })
            Assert.False(typeof(DevToolsController).GetMethod(action)!.IsDefined(typeof(AllowAnonymousAttribute), true));
    }

    [Fact]
    public void The_actions_take_no_tenant_input_so_the_business_can_only_come_from_the_cookie()
    {
        // The only inputs are the page cursor and the notice identity. There is no business, email or business-id
        // parameter a caller could use to widen the read.
        Assert.Equal(
            ["beforeId", "ct"],
            ParameterNames(nameof(DevToolsController.Index)));
        Assert.Equal(
            ["id", "ct"],
            ParameterNames(nameof(DevToolsController.Detail)));
    }

    [Fact]
    public void Run_job_takes_only_the_job_key_so_the_business_still_comes_from_the_cookie()
    {
        // The only input is the stable job key; there is no business, email or business-id parameter a caller could
        // use to run a job of another business.
        Assert.Equal(["key", "ct"], ParameterNames(nameof(DevToolsController.RunJob)));
    }

    // --- Status and type labels ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(NotificationStatus.Pending, "Pendiente")]
    [InlineData(NotificationStatus.Sent, "Enviado")]
    [InlineData(NotificationStatus.Failed, "Falló")]
    [InlineData(NotificationStatus.Dispatching, "Enviando")]
    [InlineData(NotificationStatus.ReviewRequired, "Requiere revisión")]
    public void Status_label_is_spanish_for_every_status(NotificationStatus status, string expected) =>
        Assert.Equal(expected, DevToolsController.StatusLabel(status));

    [Theory]
    [InlineData(NotificationType.BookingCreated, "Reserva creada")]
    [InlineData(NotificationType.AppointmentReminder, "Recordatorio de turno")]
    [InlineData(NotificationType.DropInReminder, "Recordatorio de clase suelta")]
    [InlineData(NotificationType.DropInBooked, "Lugar en clase suelta")]
    [InlineData(NotificationType.ClassSuspended, "Clase suspendida")]
    [InlineData(NotificationType.Rescheduled, "Turno reprogramado")]
    [InlineData(NotificationType.MembershipExpiring, "Abono por vencer")]
    [InlineData(NotificationType.MembershipExpired, "Abono vencido")]
    [InlineData(NotificationType.DailySummary, "Resumen diario")]
    [InlineData(NotificationType.StockReview, "Revisión de insumos")]
    [InlineData(NotificationType.EmailVerification, "Verificación de email")]
    [InlineData(NotificationType.PasswordReset, "Recuperación de contraseña")]
    [InlineData(NotificationType.AccountInvitation, "Invitación de cuenta")]
    public void Type_label_is_spanish_for_every_type(NotificationType type, string expected) =>
        Assert.Equal(expected, DevToolsController.TypeLabel(type));

    // --- Static view contract -----------------------------------------------------------------------------------

    [Fact]
    public void The_list_shows_status_type_time_recipient_and_subject_and_never_a_body_or_a_private_link()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        Assert.Contains("StatusLabel", view, StringComparison.Ordinal);
        Assert.Contains("TypeLabel", view, StringComparison.Ordinal);
        Assert.Contains("item.CreatedAt", view, StringComparison.Ordinal);
        Assert.Contains("item.Recipient", view, StringComparison.Ordinal);
        Assert.Contains("item.Subject", view, StringComparison.Ordinal);

        // No body is ever part of the list: a queued body can embed a single-use private link or a reset token.
        Assert.DoesNotContain("BodyHtml", view, StringComparison.Ordinal);
        Assert.DoesNotContain("LastError", view, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatchKey", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Html.Raw", view, StringComparison.Ordinal);
        Assert.DoesNotContain("iframe", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_offers_the_next_bounded_page_through_a_keyset_cursor()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        // The next page is the same action with the last row's identity as the cursor, bound by the same bound the
        // repository enforces, so the page can only ever move strictly backwards.
        Assert.Contains("beforeId", view, StringComparison.Ordinal);
        Assert.Contains("MaxPageSize", view, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"Detail\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_shows_the_body_as_escaped_plain_text_and_warns_about_private_tokens()
    {
        var view = ReadOwnerView("DevTools", "Detail.cshtml");

        // Razor encodes @Model.BodyHtml by default; the body is rendered as preformatted text, never as markup.
        Assert.Contains("<pre", view, StringComparison.Ordinal);
        Assert.Contains("@Model.BodyHtml", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Html.Raw", view, StringComparison.Ordinal);
        Assert.DoesNotContain("iframe", view, StringComparison.Ordinal);
        Assert.DoesNotContain("srcdoc", view, StringComparison.Ordinal);

        // The body is never placed in an attribute or a URL, and no link is built from its content.
        Assert.DoesNotContain("value=\"@Model.BodyHtml\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"@Model.BodyHtml\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"@Model.BodyHtml\"", view, StringComparison.Ordinal);

        // The warning names the private tokens and single-use links a body can carry, so the owner does not share it.
        Assert.Contains("privado", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Neither_page_exposes_the_stored_error_the_claim_or_a_manual_retry()
    {
        foreach (var view in new[]
                 {
                     ReadOwnerView("DevTools", "Index.cshtml"),
                     ReadOwnerView("DevTools", "Detail.cshtml")
                 })
        {
            Assert.DoesNotContain("LastError", view, StringComparison.Ordinal);
            Assert.DoesNotContain("DispatchKey", view, StringComparison.Ordinal);
            Assert.DoesNotContain("ClaimedAt", view, StringComparison.Ordinal);

            // Retry and manual intervention over a stored error are still not offered: the one write this page allows is
            // running a registered job by its stable key, not retrying a failed notice or editing its error.
            Assert.DoesNotContain("Reintentar", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Revisión manual", view, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_list_offers_a_post_form_for_every_registered_job_key()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        // The keys come from the registered runner through ViewData, so the page can only offer a job that exists.
        Assert.Contains(DevToolsController.JobKeysViewDataKey, view, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"RunJob\"", view, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"key\"", view, StringComparison.Ordinal);
        // Accessible labels, because the button text alone does not name which job it runs.
        Assert.Contains("aria-label", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_explains_that_the_outbox_job_reaches_no_provider_in_retain_mode()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        // The gate opens only in Retain mode, so the page must tell the owner that running `outbox` sends nothing.
        Assert.Contains("OutboxScheduledJob.JobKey", view, StringComparison.Ordinal);
        Assert.Contains("retenid", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_viewer_link_stays_inside_the_owner_area()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        Assert.Contains("asp-area=\"Owner\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", view, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", view, StringComparison.Ordinal);
    }

    // --- Simulated clock view contract (T26) -------------------------------------------------------------------

    [Fact]
    public void The_index_warns_that_the_simulated_clock_is_global_in_memory_and_retained()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        // A prominent, accessible warning: the clock moves both demo businesses at once, the retained notices never
        // leave, the offset is only in memory and the automatic ticks can enqueue pending notices while it runs ahead.
        Assert.Contains("notice-warning", view, StringComparison.Ordinal);
        Assert.Contains("role=\"note\"", view, StringComparison.Ordinal);
        Assert.Contains("ambos negocios", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("memoria", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reinici", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no se env", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pendiente", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_index_renders_the_current_clock_state()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        // The page shows the current simulated local instant and whether it is simulated or the real time.
        Assert.Contains(DevToolsController.ClockStateViewDataKey, view, StringComparison.Ordinal);
        Assert.Contains("DevToolsClockState", view, StringComparison.Ordinal);
        Assert.Contains("IsSimulated", view, StringComparison.Ordinal);
        Assert.Contains("LocalNow", view, StringComparison.Ordinal);
        Assert.Contains("Formatting", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_index_offers_the_three_clock_forms_with_bounds_and_confirmation()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        // Every clock form is a POST with the antiforgery token the FormTagHelper adds; the advances are bounded
        // numbers and carry the explicit acknowledgement that both businesses move at once.
        Assert.Contains("asp-action=\"AdvanceHours\"", view, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"AdvanceDays\"", view, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"RestoreClock\"", view, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", view, StringComparison.Ordinal);
        Assert.Contains("type=\"number\"", view, StringComparison.Ordinal);
        Assert.Contains("min=\"@SimulatedBusinessClock.MinAdvanceHours\"", view, StringComparison.Ordinal);
        Assert.Contains("max=\"@SimulatedBusinessClock.MaxAdvanceHours\"", view, StringComparison.Ordinal);
        Assert.Contains("min=\"@SimulatedBusinessClock.MinAdvanceDays\"", view, StringComparison.Ordinal);
        Assert.Contains("max=\"@SimulatedBusinessClock.MaxAdvanceDays\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"confirmAllBusinesses\"", view, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_clock_forms_stay_inside_the_owner_area_and_expose_no_stored_error()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        Assert.Contains("asp-area=\"Owner\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Html.Raw", view, StringComparison.Ordinal);
        Assert.DoesNotContain("BusinessId", view, StringComparison.Ordinal);
    }

    // --- Gate-based layout --------------------------------------------------------------------------------------

    [Fact]
    public void The_owner_layout_links_the_viewer_only_through_the_gate()
    {
        var layout = ReadView("_OwnerLayout.cshtml");

        Assert.Contains("DevToolsGate", layout, StringComparison.Ordinal);
        Assert.Contains("IsEnabled", layout, StringComparison.Ordinal);
        // The raw flag is replaced: a production host with Features.DevTools on must not render the tools link.
        Assert.DoesNotContain("Features.DevTools", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void The_owner_layout_offers_the_viewer_only_to_a_signed_in_owner()
    {
        var layout = ReadView("_OwnerLayout.cshtml");

        // Anonymous login and register pages render this same layout, so the section is added only for an
        // authenticated principal that also carries a positive owner business claim.
        Assert.Contains("DevToolsGate.IsEnabled && isAuthenticated && hasOwnerBusiness", layout, StringComparison.Ordinal);
        Assert.Contains("IsAuthenticated", layout, StringComparison.Ordinal);
        Assert.Contains("WorkUpClaims.BusinessId", layout, StringComparison.Ordinal);
        Assert.Contains("ownerBusinessId > 0", layout, StringComparison.Ordinal);

        // Authentication is unchanged for every other section: only the tools item carries the extra guard.
        Assert.Contains("OwnerNavigation.Sections", layout, StringComparison.Ordinal);
        Assert.Contains("NavigationProvider.VisibleSectionsAsync", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void The_public_layout_and_home_use_the_same_gate_instead_of_the_raw_flag()
    {
        foreach (var view in new[] { ReadView("_Layout.cshtml"), ReadHomeView("Index.cshtml") })
        {
            Assert.Contains("DevToolsGate", view, StringComparison.Ordinal);
            Assert.Contains("IsEnabled", view, StringComparison.Ordinal);
            Assert.DoesNotContain("Features.DevTools", view, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_test_tools_section_exists_in_the_owner_navigation()
    {
        Assert.Equal("DevTools", OwnerNavigation.DevTools.Controller);
        Assert.Equal("Herramientas de prueba", OwnerNavigation.DevTools.Label);
    }

    // --- Banner copy and viewer styling -------------------------------------------------------------------------

    [Fact]
    public void The_retaining_banner_points_the_owner_to_the_viewer_without_claiming_a_send()
    {
        var text = EmailDeliveryCopy.DevToolsBanner(OutboxMode.Retain);

        Assert.Contains("retenidos sin enviarse", text, StringComparison.Ordinal);
        Assert.Contains("la dueña puede verlos en Herramientas de prueba", text, StringComparison.Ordinal);

        // The banner also renders on the public home page, so it names who can open the panel page instead of
        // promising an anonymous visitor a page she cannot reach.
        Assert.DoesNotContain("se pueden ver", text, StringComparison.Ordinal);
        // Retaining semantics stay accurate: no send is claimed and no inbox delivery is promised.
        Assert.DoesNotContain("no podemos garantizar", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_notice_body_wraps_long_private_tokens_and_uses_theme_tokens_only()
    {
        var css = ReadCss();

        var start = css.IndexOf(".notice-body {", StringComparison.Ordinal);
        Assert.True(start >= 0, "workup.css must style the .notice-body surface.");
        var block = css[start..css.IndexOf('}', start)];

        // A single-use private link or a reset token has no spaces, so the surface must wrap instead of overflowing.
        Assert.Contains("white-space: pre-wrap", block, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", block, StringComparison.Ordinal);
        Assert.Contains("max-width", block, StringComparison.Ordinal);
        Assert.Contains("padding: var(", block, StringComparison.Ordinal);

        // Design-system rule: no raw color outside :root, so the surface keeps themed, readable contrast.
        Assert.Contains("var(--", block, StringComparison.Ordinal);
        Assert.DoesNotContain("#", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_warning_uses_the_notice_component_instead_of_a_field_hint()
    {
        var view = ReadOwnerView("DevTools", "Detail.cshtml");

        Assert.Contains("notice-warning", view, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"hint\"", view, StringComparison.Ordinal);
    }

    // --- DI graph -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_viewer_repository_is_registered_scoped_in_the_composition_root()
    {
        var services = BuildServices();

        var repository = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(INotificationViewerRepository));
        Assert.Equal(ServiceLifetime.Scoped, repository.Lifetime);
        Assert.Equal(typeof(SqlNotificationViewerRepository), repository.ImplementationType);

        var gate = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DevToolsGate));
        Assert.Equal(ServiceLifetime.Singleton, gate.Lifetime);

        var execution = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ScheduledExecutionGate));
        Assert.Equal(ServiceLifetime.Singleton, execution.Lifetime);
    }

    [Fact]
    public void The_execution_gate_is_a_singleton_shared_across_scopes()
    {
        var services = BuildServices();

        // The lease has to span the hosted tick's scope and every owner request scope, so both scopes must see the
        // very same gate; a scoped registration would silently allow two overlapping executions.
        using var provider = services.BuildServiceProvider();
        using var tickScope = provider.CreateScope();
        using var requestScope = provider.CreateScope();

        Assert.Same(
            tickScope.ServiceProvider.GetRequiredService<ScheduledExecutionGate>(),
            requestScope.ServiceProvider.GetRequiredService<ScheduledExecutionGate>());
    }

    [Fact]
    public void The_controller_constructor_takes_only_the_feature_gate_and_the_scoped_provider()
    {
        // The provider is a deliberate, narrow fail-closed exception: the viewer, the runner and the lease are resolved
        // inside each action only after the gate accepts the request, so a closed gate activates none of them and a
        // future failure while building the job graph cannot turn a 404 into a 500.
        var parameters = typeof(DevToolsController)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(DevToolsGate), typeof(IServiceProvider)], parameters);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------

    private static void AssertPrivateHeaders(DevToolsController controller)
    {
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.Equal("no-cache", controller.Response.Headers.Pragma);
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"]);
    }

    private static string[] ParameterNames(string action) =>
        typeof(DevToolsController)
            .GetMethod(action)!
            .GetParameters()
            .Select(parameter => parameter.Name!)
            .ToArray();

    private static string ReadOwnerView(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "Areas", "Owner", "Views", folder, fileName));

    private static string ReadView(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "Views", "Shared", fileName));

    private static string ReadHomeView(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "Views", "Home", fileName));

    private static string ReadCss() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "wwwroot", "css", "workup.css"));

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static IServiceCollection BuildServices()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:WorkUp"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT26ViewerUi;Trusted_Connection=True;TrustServerCertificate=True",
            ["WorkUp:PublicBaseUrl"] = "https://reservas.example.com",
            ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires",
            ["WorkUp:Features:DevTools"] = "true",
            ["WorkUp:Email:Mode"] = "Outbox"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        // Registration only: no provider is built, so no key is generated and no database is touched.
        services.AddWorkUp(configuration, new FakeHostEnvironment { EnvironmentName = Environments.Development });
        return services;
    }

    /// <summary>
    /// Test double of the scoped provider the controller defers to. It records every type the controller asks for and
    /// throws on any type the test did not register, so "the closed gate activated nothing" is proven by construction
    /// instead of by observing a side effect that a harmless stub could hide.
    /// </summary>
    private sealed class RecordingServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services;

        public RecordingServiceProvider(params (Type Service, object Implementation)[] services)
        {
            _services = services.ToDictionary(entry => entry.Service, entry => entry.Implementation);
        }

        public List<Type> Requested { get; } = [];

        public object? GetService(Type serviceType)
        {
            Requested.Add(serviceType);
            if (_services.TryGetValue(serviceType, out var service))
                return service;

            throw new InvalidOperationException(
                $"The controller resolved {serviceType.Name} before the gate allowed it.");
        }
    }

    private sealed class DevToolsJob(string key, Func<Task>? behavior = null) : IScheduledJob
    {
        public string Key { get; } = key;

        public Task RunAsync(DateTime now, CancellationToken ct = default) =>
            behavior is null ? Task.CompletedTask : behavior();
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
