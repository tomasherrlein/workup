using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data;
using WorkUp.Data.DemoData;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Jobs;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in real HTTP/SQL fixture for the T26 test tools (plan assumptions 10 and 11) exactly as the shipped MVC host
/// wires them: the owner-only generated-email viewer (<c>INotificationViewerRepository</c>), the manual scheduled-job
/// run and the installation-wide simulated clock, all behind the fail-closed <see cref="DevToolsGate"/> and serialized
/// by the singleton <see cref="ScheduledExecutionGate"/>. The cores, the SQL ports, the gate truth table and the
/// controller behaviour are already proven against fakes and against a real database; what only this harness can prove
/// is the whole path of one process: a real owner cookie + antiforgery POST reaches the real controller, the real
/// repository reads only the signed-in tenant out of the real <c>Notifications</c> table, the real job graph runs the
/// real <see cref="StockReviewService"/> against the migrated schema, and the one simulated clock moves both demo
/// businesses.
/// <para>
/// Three scenarios, one retained database and one isolated DPAPI keyring each, driven by the same opt-in pair
/// <c>WORKUP_T26_HTTP_CONNECTION</c> and <c>WORKUP_T26_HTTP_KEYS_PATH</c>. Without that pair every scenario logs N/A
/// and returns before any SQL connection, database, keyring or HTTP host exists, so the default suite is unaffected
/// and this file adds no real evidence by itself.
/// </para>
/// <para>
/// The derived suffixes are <c>_Enabled</c>, <c>_FeatureOff</c> and <c>_Smtp</c>: the database is the configured
/// catalog plus the suffix (must start with <c>WorkUpDemoT26Http</c>, no credential, no attached file, integrated
/// LocalDB only) and the keyring is the configured path plus the suffix (must be an absolute path inside the OS temp
/// directory whose leaf starts with <c>WorkUpDemoT12KeysT26Http</c>, must not exist yet and must have no reparse point
/// ancestor). Both derived targets are validated before anything connects, the database is created only after
/// <c>DB_ID</c> proved it absent, and both the database and the encrypted keys are retained afterwards: never reused,
/// never dropped and never deleted. Only the owned child process is stopped.
/// </para>
/// <para>
/// The business-time "now" is read once from the real <see cref="BusinessClock"/> of the business time zone and the
/// run refuses to start when the local clock is too close to midnight, before the derived database is created. The
/// explicit T07 demo seed (two businesses, two owner accounts) then has its stock anchor normalized to that real
/// calendar day with cadence 7, and its summary time pushed to the last minute of the day so the near-midnight guard
/// guarantees the daily summary is never due: the scenarios stay deterministic without inventing a clock.
/// </para>
/// <para>
/// Scenario 1 (<c>_Enabled</c>, Development, <c>Features.DevTools=true</c>, Outbox) seeds an own, a foreign and a
/// business-less notice — the own body carries a <c>&lt;script&gt;</c> payload and a <c>/t/private-token</c> path — then
/// logs in owner A with a real cookie jar and antiforgery, proves the viewer list never carries the private body,
/// proves the own detail escapes the body, proves foreign and business-less details are the same 404, proves the
/// anonymous panel redirects to the login, proves the login navigation never offers the tools, proves the two
/// antiforgery refusals and the explicit shared-clock confirmation, advances the shared clock seven days, runs
/// <c>stock-review</c> and asserts the two Pending stock notices the one clock produced for the two businesses, then
/// proves each owner cookie sees only its own list, restores the real clock and proves a second manual run adds
/// nothing. Scenario 2 (<c>_FeatureOff</c>, Development, <c>Features.DevTools=false</c>, Outbox) and scenario 3
/// (<c>_Smtp</c>, Development, <c>Features.DevTools=true</c>, Smtp) each prove the direct URL is a 404 and both the
/// test banner and the owner navigation are hidden, with a reachable loopback provider that records zero connections.
/// </para>
/// <para>
/// A Production host is deliberately not started: the isolated keyring path is development-only and would make the
/// host refuse to start, so weakening that is not an option. The Production (and wrong-environment) refusal is already
/// proven by the <c>DevToolsGate</c> unit truth table, which this harness does not duplicate.
/// </para>
/// <para>
/// Nothing here replaces an independent run: until a human executes it against a real LocalDB instance with the opt-in
/// pair set, every scenario is compiled and N/A only, never real proof.
/// </para>
/// </summary>
public sealed class DevToolsHttpIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string ConnectionVariable = "WORKUP_T26_HTTP_CONNECTION";
    private const string KeysPathVariable = "WORKUP_T26_HTTP_KEYS_PATH";
    private const string DatabasePrefix = "WorkUpDemoT26Http";
    private const string KeysLeafPrefix = "WorkUpDemoT12KeysT26Http";
    private const string BusinessTimeZone = "America/Argentina/Buenos_Aires";

    /// <summary>Derived suffix of each retained target; one fresh database and one fresh keyring per scenario.</summary>
    private const string EnabledSuffix = "_Enabled";
    private const string FeatureOffSuffix = "_FeatureOff";
    private const string SmtpSuffix = "_Smtp";

    private const string BusinessASlug = "peluqueria-marina-demo";
    private const string BusinessAUsername = "marina.demo";
    private const string BusinessARecipient = "marina@demo.invalid";
    private const string BusinessBSlug = "estudio-yoga-lucia-demo";
    private const string BusinessBUsername = "lucia.demo";
    private const string BusinessBRecipient = "lucia@demo.invalid";

    private const string FromAddress = "WorkUp <no-responder@workup.local>";

    /// <summary>
    /// Owner-only navigation href of the DevTools page. The tools label text is deliberately never used to detect the
    /// owner navigation: the retaining-mode banner copy also mentions "Herramientas de prueba", so that phrase is not
    /// an unambiguous marker of the link. The ASCII href generated by the tag helper is.
    /// </summary>
    private const string OwnerToolsPath = "/panel/DevTools";

    /// <summary>The stock cadence and the real-clock anchor the manual run is derived from.</summary>
    private const int StockReviewEveryDays = 7;
    private const int AdvanceDays = 7;

    /// <summary>Last minute of the day: the near-midnight guard keeps it permanently unreachable, so no summary is due.</summary>
    private const int ClosedSummaryHour = 23;
    private const int ClosedSummaryMinute = 59;

    /// <summary>A run that reaches this local time of day is refused before the derived database exists.</summary>
    private static readonly TimeSpan MidnightRunout = new(23, 50, 0);

    private static readonly Regex SafeDatabaseName =
        new(@"\A" + DatabasePrefix + @"[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TickBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(500);

    // --- Scenario 1: enabled tools, viewer + manual jobs + shared clock ---------------------------------------

    [Fact]
    public async Task Enabled_devtools_viewer_manual_jobs_and_shared_clock_run_on_an_isolated_host()
    {
        var target = await PrepareAsync(EnabledSuffix);
        if (target is null)
            return;

        using var smtp = new LoopbackConnectionCounter();
        output.WriteLine(
            $"SMTP: reachable loopback listener on 127.0.0.1:{smtp.Port}; Outbox mode must never contact it (loopback only, no external network).");

        var seeded = await SeedViewerNoticesAsync(
            target.Plan.ConnectionString, target.BusinessA, target.BusinessB, target.BusinessNow);
        output.WriteLine(
            $"SQL: seeded viewer notices #{seeded.OwnId} (own, hostile body), #{seeded.ForeignId} (foreign) and #{seeded.NullId} (no business).");

        var host = StartHost(target.Plan, "Outbox", devToolsEnabled: true, smtp.Port);
        try
        {
            await WaitUntilReadyAsync(host);
            output.WriteLine($"HTTP: owner host (pid {host.Process.Id}) became ready on {target.Plan.BaseUrl} in Outbox mode.");
            AssertIsolatedKeyringCreated(target.Plan.KeysPath);
            await WaitForTickAsync(host);
            output.WriteLine("HOST: the immediate scheduled-jobs tick completed (fixed marker observed on the child output).");

            await RunEnabledFlowAsync(host, target, seeded, smtp);
        }
        finally
        {
            await StopHostAsync(host.Process);
        }

        // A reachable loopback provider saw nothing: the retaining mode never claims and never sends.
        Assert.Equal(0, smtp.ConnectionCount);
        output.WriteLine(
            $"Enabled-scenario checkpoints passed on retained database '{target.Plan.DatabaseName}' with isolated keyring " +
            $"'{target.Plan.KeysPath}': viewer isolation, escaped detail, fixed 404s, antiforgery, shared clock and two " +
            "Pending stock notices with zero SMTP connections.");
    }

    // --- Closed scenarios: feature flag off and SMTP mode ------------------------------------------------

    [Fact]
    public async Task Feature_false_devtools_url_and_navigation_are_closed_on_an_isolated_host()
    {
        await RunClosedScenarioAsync(FeatureOffSuffix, devToolsEnabled: false, configuredMode: "Outbox");
    }

    [Fact]
    public async Task Smtp_mode_devtools_url_banner_and_navigation_are_closed_on_an_isolated_host()
    {
        await RunClosedScenarioAsync(SmtpSuffix, devToolsEnabled: true, configuredMode: "Smtp");
    }

    /// <summary>
    /// Drives one closed scenario: Development host with a reachable loopback provider, a real owner cookie, and the
    /// direct tool URL refused. Both the test banner (rendered through the same gate) and the owner navigation must be
    /// absent, even when the raw <c>Features.DevTools</c> flag is on but the email mode is Smtp. The seeded demo is the
    /// same clean shape as the enabled scenario: anchor today, cadence 7 and summary at 23:59, so no job has due work
    /// and the provider can only ever record zero connections.
    /// </summary>
    private async Task RunClosedScenarioAsync(string suffix, bool devToolsEnabled, string configuredMode)
    {
        var target = await PrepareAsync(suffix);
        if (target is null)
            return;

        using var smtp = new LoopbackConnectionCounter();
        output.WriteLine(
            $"SMTP: reachable loopback listener on 127.0.0.1:{smtp.Port}; the closed tools must never contact it (loopback only, no external network).");

        var host = StartHost(target.Plan, configuredMode, devToolsEnabled, smtp.Port);
        try
        {
            await WaitUntilReadyAsync(host);
            output.WriteLine($"HTTP: owner host (pid {host.Process.Id}) became ready on {target.Plan.BaseUrl} in {configuredMode} mode (DevTools flag {devToolsEnabled}).");
            AssertIsolatedKeyringCreated(target.Plan.KeysPath);
            await WaitForTickAsync(host);

            // Anonymous login page: the mode banner and the tools navigation are both gated, so both are absent.
            using var anonymous = NewClient(target.Plan.BaseUrl);
            var (loginStatus, loginHtml) = await GetHtmlAsync(anonymous, "/panel/Account/Login");
            Assert.Equal(HttpStatusCode.OK, loginStatus);
            Assert.DoesNotContain("devtools-banner", loginHtml, StringComparison.Ordinal);
            // The nav link, not the label phrase, is the owner-only marker: a page without it can never reach the tools.
            Assert.DoesNotContain(OwnerToolsPath, loginHtml, StringComparison.OrdinalIgnoreCase);
            output.WriteLine("HTTP: anonymous login page renders without the test banner and without the tools navigation link.");

            // A real owner session: the navigation is still hidden and the direct URL is refused.
            using var client = NewClient(target.Plan.BaseUrl);
            await LoginAsync(client, BusinessAUsername, target.Password);
            var (dayStatus, dayHtml) = await GetHtmlAsync(client, "/panel");
            Assert.Equal(HttpStatusCode.OK, dayStatus);
            // Gate closed: the owner navigation must not publish the tools link, banner or no banner.
            Assert.DoesNotContain(OwnerToolsPath, dayHtml, StringComparison.OrdinalIgnoreCase);

            using var direct = await client.GetAsync("/panel/DevTools");
            Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
            output.WriteLine("HTTP: authenticated owner page hides the tools navigation and GET /panel/DevTools -> 404 (fail closed).");
        }
        finally
        {
            await StopHostAsync(host.Process);
        }

        // No due work exists, so a reachable provider must have seen nothing even in Smtp mode.
        Assert.Equal(0, smtp.ConnectionCount);
        output.WriteLine(
            $"Closed-scenario '{suffix}' checkpoints passed on retained database '{target.Plan.DatabaseName}' with isolated " +
            $"keyring '{target.Plan.KeysPath}': direct URL 404, banner and navigation hidden, zero SMTP connections.");
    }

    // --- Enabled-scenario flow --------------------------------------------------------------------------

    private async Task RunEnabledFlowAsync(RunningHost host, PreparedTarget target, SeededNotices seeded, LoopbackConnectionCounter smtp)
    {
        var baseUrl = host.BaseUrl;

        // Anonymous visit: no cookie, no tools, and the panel redirects to the login.
        using (var anonymous = NewClient(baseUrl))
        {
            var (loginStatus, loginHtml) = await GetHtmlAsync(anonymous, "/panel/Account/Login");
            Assert.Equal(HttpStatusCode.OK, loginStatus);
            // Positive control for the closed scenarios: an open gate renders the mode banner, a closed gate does not.
            // The ASCII class name is matched instead of the accented copy, which the view engine encodes.
            Assert.Contains("devtools-banner", loginHtml, StringComparison.Ordinal);
            // The banner copy mentions the tools by name, so only the nav href proves the anonymous page publishes no link.
            Assert.DoesNotContain(OwnerToolsPath, loginHtml, StringComparison.OrdinalIgnoreCase);
            using var anonymousDirect = await anonymous.GetAsync("/panel/DevTools");
            Assert.Equal(HttpStatusCode.Redirect, anonymousDirect.StatusCode);
            Assert.True(IsLoginRedirect(anonymousDirect),
                $"The anonymous GET /panel/DevTools must redirect to the login (location '{DescribeRedirect(anonymousDirect)}').");
            output.WriteLine("HTTP: anonymous login page renders the mode banner, hides the tools navigation and GET /panel/DevTools -> 302 login.");
        }

        // Owner A: the login page never advertises the tools even while the gate is enabled; the authenticated page does.
        using var clientA = NewClient(baseUrl);
        var (loginStatusA, loginHtmlA) = await GetHtmlAsync(clientA, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatusA);
        Assert.DoesNotContain(OwnerToolsPath, loginHtmlA, StringComparison.OrdinalIgnoreCase);
        await LoginAsync(clientA, BusinessAUsername, target.Password);
        var (dayStatusA, dayHtmlA) = await GetHtmlAsync(clientA, "/panel");
        Assert.Equal(HttpStatusCode.OK, dayStatusA);
        // The owner page is the one that must publish the link, even though the banner happens to name the tools too.
        Assert.Contains(OwnerToolsPath, dayHtmlA, StringComparison.OrdinalIgnoreCase);
        output.WriteLine("HTTP: owner A login accepted with a real cookie + antiforgery; the login nav hides the tools and the owner nav offers them.");

        // The list is tenant-scoped and never carries the private body.
        var (indexStatus, indexHtml) = await GetHtmlAsync(clientA, "/panel/DevTools");
        Assert.Equal(HttpStatusCode.OK, indexStatus);
        Assert.Contains(seeded.OwnSubject, indexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.ForeignSubject, indexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.NullSubject, indexHtml, StringComparison.Ordinal);
        Assert.Contains(BusinessARecipient, indexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(BusinessBRecipient, indexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/t/private-token", indexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;script", indexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", indexHtml, StringComparison.Ordinal);
        Assert.Contains("<strong>tiempo real</strong>", indexHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: GET /panel/DevTools -> 200 with only owner A's notices, no private body and the real clock state.");

        // The own detail escapes the hostile body instead of rendering it.
        var (ownDetailStatus, ownDetailHtml) = await GetHtmlAsync(clientA, "/panel/DevTools/Detail/" + seeded.OwnId.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.OK, ownDetailStatus);
        Assert.Contains("&lt;script", ownDetailHtml, StringComparison.Ordinal);
        Assert.Contains("/t/private-token", ownDetailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", ownDetailHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: owner A's own detail renders the body HTML-escaped (no live script).");

        // Foreign and business-less notices share the same generic 404, and so does another tenant asking for A's row.
        Assert.Equal(HttpStatusCode.NotFound, (await GetHtmlAsync(clientA, "/panel/DevTools/Detail/" + seeded.ForeignId.ToString(CultureInfo.InvariantCulture))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetHtmlAsync(clientA, "/panel/DevTools/Detail/" + seeded.NullId.ToString(CultureInfo.InvariantCulture))).Status);
        output.WriteLine("HTTP: foreign and business-less detail -> the same collection-neutral 404.");

        // Anti-CSRF: a POST without the token is refused before any business logic runs.
        using (var noTokenAdvance = await clientA.PostAsync("/panel/DevTools/AdvanceDays", Form(new Dictionary<string, string>
        {
            ["days"] = AdvanceDays.ToString(CultureInfo.InvariantCulture),
            ["confirmAllBusinesses"] = "true"
        })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenAdvance.StatusCode);
        }

        using (var noTokenRun = await clientA.PostAsync("/panel/DevTools/RunJob", Form(new Dictionary<string, string>
        {
            ["key"] = StockReviewScheduledJob.JobKey
        })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenRun.StatusCode);
        }
        output.WriteLine("HTTP: clock and manual-run POSTs without the anti-CSRF token -> 400.");

        // Explicit acknowledgement is required: without it the shared clock does not move.
        var noConfirm = await PostClockAsync(clientA, nameof(DevToolsController.AdvanceDays), new Dictionary<string, string>
        {
            ["days"] = AdvanceDays.ToString(CultureInfo.InvariantCulture),
            ["confirmAllBusinesses"] = "false"
        });
        Assert.Equal("/panel/DevTools", noConfirm.RedirectPath);
        Assert.Contains("afecta a los dos negocios", noConfirm.FollowedBody, StringComparison.Ordinal);
        Assert.Contains("<strong>tiempo real</strong>", noConfirm.FollowedBody, StringComparison.Ordinal);
        output.WriteLine("HTTP: AdvanceDays without the confirmation -> PRG with the fixed warning and the clock still real.");

        // With the acknowledgement the one installation-wide clock moves.
        var advance = await PostClockAsync(clientA, nameof(DevToolsController.AdvanceDays), new Dictionary<string, string>
        {
            ["days"] = AdvanceDays.ToString(CultureInfo.InvariantCulture),
            ["confirmAllBusinesses"] = "true"
        });
        Assert.Equal("/panel/DevTools", advance.RedirectPath);
        Assert.Contains("El reloj simulado avanz", advance.FollowedBody, StringComparison.Ordinal);
        Assert.Contains("<strong>simulado</strong>", advance.FollowedBody, StringComparison.Ordinal);
        output.WriteLine($"HTTP: AdvanceDays({AdvanceDays}) with the confirmation -> PRG and the shared clock is now simulated.");

        // Manual stock review: the same job the tick would run, over the real repository.
        var run = await RunManualJobAsync(clientA, StockReviewScheduledJob.JobKey);
        Assert.Contains("La tarea se ejecut", run.FollowedBody, StringComparison.Ordinal);

        // SQL: the global scan enqueued exactly the two businesses' stock notices, both Pending, in this one database.
        var expectedDue = DateOnly.FromDateTime(target.BusinessNow).AddDays(AdvanceDays);
        var stock = await ReadStockNoticesAsync(target.Plan.ConnectionString);
        Assert.Equal(2, stock.Count);
        Assert.All(stock, row => Assert.Equal((byte)NotificationStatus.Pending, row.Status));
        var noticeA = stock.Single(row => row.BusinessId == target.BusinessA);
        var noticeB = stock.Single(row => row.BusinessId == target.BusinessB);
        Assert.Equal(StockReviewService.DedupKey(target.BusinessA, expectedDue), noticeA.DedupKey);
        Assert.Equal(StockReviewService.DedupKey(target.BusinessB, expectedDue), noticeB.DedupKey);
        Assert.Equal(BusinessARecipient, noticeA.Recipient);
        Assert.Equal(BusinessBRecipient, noticeB.Recipient);
        output.WriteLine(
            $"SQL: manual stock-review queued two Pending notices in '{target.Plan.DatabaseName}' (businesses #{target.BusinessA} and #{target.BusinessB}, bucket {expectedDue:yyyy-MM-dd}).");

        // Viewer A after the run: A's own list grows, B's recipient never appears.
        var (indexAfterRunStatus, indexAfterRunHtml) = await GetHtmlAsync(clientA, "/panel/DevTools");
        Assert.Equal(HttpStatusCode.OK, indexAfterRunStatus);
        Assert.Contains(BusinessARecipient, indexAfterRunHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(BusinessBRecipient, indexAfterRunHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: viewer A still shows only owner A's notices after the global job ran.");

        // Owner B sees only B's notices, including the one that is foreign to A.
        using var clientB = NewClient(baseUrl);
        await LoginAsync(clientB, BusinessBUsername, target.Password);
        var (indexStatusB, indexHtmlB) = await GetHtmlAsync(clientB, "/panel/DevTools");
        Assert.Equal(HttpStatusCode.OK, indexStatusB);
        Assert.Contains(BusinessBRecipient, indexHtmlB, StringComparison.Ordinal);
        Assert.DoesNotContain(BusinessARecipient, indexHtmlB, StringComparison.Ordinal);
        Assert.Contains(seeded.ForeignSubject, indexHtmlB, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.OwnSubject, indexHtmlB, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.NullSubject, indexHtmlB, StringComparison.Ordinal);
        var (crossTenantStatus, _) = await GetHtmlAsync(clientB, "/panel/DevTools/Detail/" + seeded.OwnId.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.NotFound, crossTenantStatus);
        output.WriteLine("HTTP: owner B's cookie sees only owner B's list (its own foreign notice), and A's detail is a 404 for B.");

        // Restore: the clock returns to the real instant.
        var restore = await PostClockAsync(clientA, nameof(DevToolsController.RestoreClock), new Dictionary<string, string>());
        Assert.Equal("/panel/DevTools", restore.RedirectPath);
        Assert.Contains("El reloj volvi", restore.FollowedBody, StringComparison.Ordinal);
        Assert.Contains("<strong>tiempo real</strong>", restore.FollowedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<strong>simulado</strong>", restore.FollowedBody, StringComparison.Ordinal);
        output.WriteLine("HTTP: RestoreClock -> PRG with the fixed message and the clock back to the real time.");

        // A second manual run at the real instant is not due, so the two rows stay two.
        var secondRun = await RunManualJobAsync(clientA, StockReviewScheduledJob.JobKey);
        Assert.Contains("La tarea se ejecut", secondRun.FollowedBody, StringComparison.Ordinal);
        Assert.Equal(2, (await ReadStockNoticesAsync(target.Plan.ConnectionString)).Count);
        output.WriteLine("SQL: a second manual stock-review after the restore added no notice (still exactly two).");

        // No send in the whole flow.
        Assert.Equal(0, smtp.ConnectionCount);
        output.WriteLine("SMTP: the reachable loopback provider recorded zero connections at the end of the enabled flow.");
    }

    // --- Scenario preparation ---------------------------------------------------------------------------

    /// <summary>
    /// Validates the opt-in pair and the derived targets, proves the database and the keyring are fresh, migrates
    /// <c>001</c>+<c>002</c>+<c>003</c>, seeds the explicit T07 demo and returns the prepared target. It returns
    /// <c>null</c> -- N/A -- when either opt-in variable is absent, and that check happens before the keyring path is
    /// read and long before any SQL connection, database, keyring or HTTP host exists. Everything created afterwards is
    /// retained: only the owned child process is stopped.
    /// </summary>
    private async Task<PreparedTarget?> PrepareAsync(string suffix)
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var suppliedKeysPath = Environment.GetEnvironmentVariable(KeysPathVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection) || string.IsNullOrWhiteSpace(suppliedKeysPath))
        {
            // The two variables are one opt-in pair: with either one absent the scenario is not opted in.
            var absent = string.IsNullOrWhiteSpace(suppliedConnection) ? ConnectionVariable : KeysPathVariable;
            output.WriteLine(
                $"N/A: {absent} is not set, so the opt-in pair {ConnectionVariable} and {KeysPathVariable} is " +
                "incomplete; no SQL connection, database, keyring or HTTP host was used.");
            return null;
        }

        // The connection string, the derived keyring path and the web host are validated before anything connects.
        var plan = PlanHarness(suppliedConnection, suffix);
        // The real business instant is the only source of the seeded dates, and a run too close to midnight is refused
        // here, before the derived database is created, so nothing irreversible exists yet.
        var businessNow = ReadBusinessNow();
        output.WriteLine(
            $"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', host port {plan.Port}, " +
            $"business now {businessNow:yyyy-MM-dd HH:mm:ss}.");

        // Freshness is proved per scenario before the derived database is created.
        await AssertDatabaseAbsentAsync(plan.DatabaseName);
        var applied = await new DatabaseMigrator(plan.ConnectionString).MigrateAsync();
        Assert.Contains("001_schema.sql", applied);
        Assert.Contains("002_client_account_pending_names.sql", applied);
        Assert.Contains("003_outbox_dispatch.sql", applied);
        output.WriteLine(
            $"SQL: applied {applied.Count} schema script(s) to the new database '{plan.DatabaseName}' (retained, never dropped).");

        // The password exists only in memory and is never written to output or to any log.
        var password = "T26-" + Guid.NewGuid().ToString("N") + "!aA1";
        await SeedDemoAsync(plan.ConnectionString, password, businessNow);

        var businessA = await QueryIntAsync(plan.ConnectionString, "SELECT Id FROM Businesses WHERE Slug = @slug", ("@slug", BusinessASlug));
        var businessB = await QueryIntAsync(plan.ConnectionString, "SELECT Id FROM Businesses WHERE Slug = @slug", ("@slug", BusinessBSlug));
        Assert.True(businessA > 0 && businessB > 0 && businessA != businessB,
            "The T07 demo seed must create two distinct businesses with owner accounts.");
        output.WriteLine($"SQL: demo seeded with owner accounts {BusinessAUsername} (business #{businessA}) and {BusinessBUsername} (business #{businessB}).");

        return new PreparedTarget(plan, businessNow, password, businessA, businessB);
    }

    /// <summary>
    /// Reads the real business instant once and refuses a run that is too close to midnight. It runs before the derived
    /// database is created, so a refusal leaves no database, keyring or host behind.
    /// </summary>
    private static DateTime ReadBusinessNow()
    {
        var now = new BusinessClock(TimeZoneInfo.FindSystemTimeZoneById(BusinessTimeZone)).Now;
        if (now.TimeOfDay >= MidnightRunout)
            throw new InvalidOperationException(
                $"The business clock reads {now:yyyy-MM-dd HH:mm:ss}, too close to midnight for a same-day stock anchor; " +
                "no SQL connection, database, keyring or host was created. Re-run earlier in the day or after midnight.");
        return now;
    }

    /// <summary>
    /// Seeds the explicit T07 demo (two businesses and two owner accounts) and normalizes the columns the stock-review
    /// job derives its due bucket from: the anchor is the real calendar day just read, the cadence is the configured
    /// seven days, and the summary is pushed to the last minute of the day so the near-midnight guard keeps it
    /// permanently unreachable. Both businesses keep the plan's valid notice email.
    /// </summary>
    private static async Task SeedDemoAsync(string connectionString, string password, DateTime businessNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(BusinessTimeZone);
        var seeder = new DemoDataSeeder(connectionString, new IdentityPasswordHasher(), new BusinessClock(zone));
        var outcome = await seeder.SeedAsync(password);
        if (outcome.CreatedBusinesses < 2)
            throw new InvalidOperationException("The T07 demo seed did not create the two expected businesses.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE Businesses SET CreatedAt = @createdAt, StockReviewEveryDays = @everyDays, DailySummaryTime = @summaryTime",
            connection);
        command.Parameters.Add("@createdAt", SqlDbType.DateTime2).Value = businessNow;
        command.Parameters.Add("@everyDays", SqlDbType.Int).Value = StockReviewEveryDays;
        command.Parameters.Add("@summaryTime", SqlDbType.Time).Value = new TimeOnly(ClosedSummaryHour, ClosedSummaryMinute).ToTimeSpan();
        var updated = await command.ExecuteNonQueryAsync();
        if (updated < 2)
            throw new InvalidOperationException("The T07 demo seed did not leave the two businesses to normalize.");
    }

    /// <summary>
    /// Seeds the viewer fixtures the enabled scenario needs: one notice owned by A whose encoded body carries a hostile
    /// <c>&lt;script&gt;</c> payload and a single-use <c>/t/private-token</c> path, one owned by B (foreign to A) and one
    /// with no business at all. All three are Pending and were never sent.
    /// </summary>
    private static async Task<SeededNotices> SeedViewerNoticesAsync(
        string connectionString, int businessA, int businessB, DateTime createdAt)
    {
        var ownSubject = "T26 aviso propio " + Guid.NewGuid().ToString("N");
        var foreignSubject = "T26 aviso ajeno " + Guid.NewGuid().ToString("N");
        var nullSubject = "T26 aviso sin negocio " + Guid.NewGuid().ToString("N");
        var ownBody = "<p>T26 aviso propio</p><script>alert('t26')</script><p>/t/private-token</p>";

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var ownId = await InsertIdAsync(connection, """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @type, @recipient, @subject, @body, @dedup, @status, @createdAt)
            """,
            ("@business", businessA),
            ("@type", (byte)NotificationType.PasswordReset),
            ("@recipient", BusinessARecipient),
            ("@subject", ownSubject),
            ("@body", ownBody),
            ("@dedup", "t26:own:" + Guid.NewGuid().ToString("N")),
            ("@status", (byte)NotificationStatus.Pending),
            ("@createdAt", createdAt));

        var foreignId = await InsertIdAsync(connection, """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @type, @recipient, @subject, @body, @dedup, @status, @createdAt)
            """,
            ("@business", businessB),
            ("@type", (byte)NotificationType.BookingCreated),
            ("@recipient", BusinessBRecipient),
            ("@subject", foreignSubject),
            ("@body", "<p>T26 aviso ajeno</p>"),
            ("@dedup", "t26:foreign:" + Guid.NewGuid().ToString("N")),
            ("@status", (byte)NotificationStatus.Pending),
            ("@createdAt", createdAt));

        var nullId = await InsertIdAsync(connection, """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (NULL, @type, @recipient, @subject, @body, @dedup, @status, @createdAt)
            """,
            ("@type", (byte)NotificationType.EmailVerification),
            ("@recipient", BusinessARecipient),
            ("@subject", nullSubject),
            ("@body", "<p>T26 aviso sin negocio</p>"),
            ("@dedup", "t26:null:" + Guid.NewGuid().ToString("N")),
            ("@status", (byte)NotificationStatus.Pending),
            ("@createdAt", createdAt));

        return new SeededNotices(ownId, foreignId, nullId, ownSubject, foreignSubject, nullSubject);
    }

    // --- Isolation harness ------------------------------------------------------------------------------

    private HarnessPlan PlanHarness(string suppliedConnection, string suffix)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                "The isolated DataProtection keyring requires Windows DPAPI, so this harness refuses to run on this platform. No SQL connection was opened.");

        SqlConnectionStringBuilder input;
        try
        {
            input = new SqlConnectionStringBuilder(suppliedConnection);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException($"{ConnectionVariable} is not a valid SQL connection string. No SQL connection was opened.", ex);
        }

        var databaseName = input.InitialCatalog + suffix;
        if (!string.Equals(input.DataSource, Server, StringComparison.OrdinalIgnoreCase)
            || !input.IntegratedSecurity
            || string.IsNullOrEmpty(databaseName) || databaseName.Length > 128
            || !SafeDatabaseName.IsMatch(databaseName)
            || !string.IsNullOrEmpty(input.UserID) || !string.IsNullOrEmpty(input.Password)
            || !string.IsNullOrEmpty(input.AttachDBFilename) || input.UserInstance
            || !string.IsNullOrEmpty(input.FailoverPartner))
            throw new InvalidOperationException(
                $"{ConnectionVariable} must use integrated {Server} with a fresh {DatabasePrefix}* database. No SQL connection was opened.");

        var keysPath = ResolveKeysPath(suffix);
        var (webHostPath, webRoot) = LocateWebHost();
        var port = FindFreeLoopbackPort();

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T26 devtools HTTP integration test"
        }.ConnectionString;

        return new HarnessPlan(connectionString, databaseName, keysPath, webHostPath, webRoot, port);
    }

    /// <summary>
    /// Derives this scenario's fresh keyring by appending the scenario suffix to the configured path. Validation is
    /// purely lexical plus an existence and reparse-ancestor check, so an invalid or existing path is never read,
    /// created or written.
    /// </summary>
    private static string ResolveKeysPath(string suffix)
    {
        var value = Environment.GetEnvironmentVariable(KeysPathVariable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{KeysPathVariable} is required once {ConnectionVariable} is set. No SQL connection was opened.");
        if (value != value.Trim() || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{KeysPathVariable} must be an absolute path without surrounding whitespace. No SQL connection was opened.");

        string basePath;
        try
        {
            basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{KeysPathVariable} is not a valid file system path. No SQL connection was opened.", ex);
        }

        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        RequireInsideTemp(basePath, tempRoot);
        if (!Path.GetFileName(basePath).StartsWith(KeysLeafPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{KeysPathVariable} must end with a directory whose name starts with \"{KeysLeafPrefix}\". No SQL connection was opened.");

        var canonical = basePath + suffix;
        RequireInsideTemp(canonical, tempRoot);
        if (!Path.GetFileName(canonical).StartsWith(KeysLeafPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The derived keyring directory must keep the \"{KeysLeafPrefix}\" prefix. No SQL connection was opened.");

        if (Directory.Exists(canonical) || File.Exists(canonical))
            throw new InvalidOperationException(
                $"{KeysPathVariable} resolves to an existing keyring '{canonical}'; this harness never reuses an earlier keyring and created nothing. No SQL connection was opened.");

        EnsureNoReparseAncestors(canonical, tempRoot);
        return canonical;
    }

    private static void RequireInsideTemp(string canonicalPath, string tempRoot)
    {
        var relative = Path.GetRelativePath(tempRoot, canonicalPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{KeysPathVariable} must resolve inside the OS temp directory ({tempRoot}). No SQL connection was opened.");
    }

    private static void EnsureNoReparseAncestors(string canonicalPath, string tempRoot)
    {
        var reachedTempRoot = false;
        for (var current = new DirectoryInfo(canonicalPath); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    $"{KeysPathVariable} has a reparse point ancestor at '{current.FullName}'. No SQL connection was opened.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                reachedTempRoot = true;
                break;
            }
        }

        if (!reachedTempRoot)
            throw new InvalidOperationException($"{KeysPathVariable} must resolve under the OS temp directory. No SQL connection was opened.");
    }

    private static (string WebHostPath, string WebRoot) LocateWebHost()
    {
        // AppContext.BaseDirectory is <repo>/WorkUp/WorkUp.Tests/bin/<Configuration>/net9.0.
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = testOutput.Parent?.Name;
        var solutionDirectory = testOutput.Parent?.Parent?.Parent?.Parent;
        if (configuration is null || solutionDirectory is null)
            throw new InvalidOperationException("Could not derive the repository layout from the test output directory. No SQL connection was opened.");

        var webProjectDirectory = Path.Combine(solutionDirectory.FullName, "WorkUp");
        var webHostPath = Path.Combine(webProjectDirectory, "bin", configuration, "net9.0", "WorkUp.exe");
        if (!File.Exists(webHostPath))
            throw new InvalidOperationException(
                $"The WorkUp web host '{webHostPath}' was not found; build the solution before running this harness. No SQL connection was opened.");

        return (webHostPath, webProjectDirectory);
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("No free loopback TCP port is available for the owned WorkUp host. No SQL connection was opened.", ex);
        }
        finally
        {
            listener.Stop();
        }
    }

    // --- Owned child process ----------------------------------------------------------------------------

    private RunningHost StartHost(HarnessPlan plan, string configuredMode, bool devToolsEnabled, int smtpPort)
    {
        var startInfo = new ProcessStartInfo(plan.WebHostPath)
        {
            // The working directory is also the content root the DataProtection application discriminator derives from.
            WorkingDirectory = plan.WebRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Drop every inherited WorkUp/ASP.NET override first, so nothing from the caller's shell can retarget the child.
        ScrubInheritedOverrides(startInfo);
        // Both environment keys are set, so the child is in Development no matter which one the host reads first; the
        // isolated keyring and the gate both depend on it.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__WorkUp"] = plan.ConnectionString;
        startInfo.Environment["ASPNETCORE_URLS"] = plan.BaseUrl;
        startInfo.Environment["WorkUp__TimeZone"] = BusinessTimeZone;
        startInfo.Environment["WorkUp__Development__DataProtectionKeysPath"] = plan.KeysPath;
        startInfo.Environment["WorkUp__PublicBaseUrl"] = plan.BaseUrl;
        startInfo.Environment["WorkUp__MigrateOnStartup"] = "true";
        startInfo.Environment["WorkUp__Features__DevTools"] = devToolsEnabled ? "true" : "false";
        startInfo.Environment["WorkUp__Email__Mode"] = configuredMode;
        startInfo.Environment["WorkUp__Email__From"] = FromAddress;
        // The reachable loopback provider is configured in both modes, so "zero connections" is a real observation and
        // never just an unconfigured sender.
        startInfo.Environment["WorkUp__Email__Smtp__Host"] = "127.0.0.1";
        startInfo.Environment["WorkUp__Email__Smtp__Port"] = smtpPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["WorkUp__Email__Smtp__EnableSsl"] = "false";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the owned WorkUp web host process.");
        var host = new RunningHost(process, plan.BaseUrl);
        // Capture stdout so the committed tick marker can be observed; the pipe buffers anything written before the
        // asynchronous read starts. Nothing is ever echoed anywhere.
        process.OutputDataReceived += (_, e) => host.Capture(e.Data);
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (process.Id <= 0)
        {
            TryKill(process);
            throw new InvalidOperationException("The owned WorkUp web host process did not report a process id.");
        }

        return host;
    }

    /// <summary>Removes inherited configuration that could redirect the child away from the derived target.</summary>
    private static void ScrubInheritedOverrides(ProcessStartInfo startInfo)
    {
        var risky = startInfo.Environment.Keys
            .Where(name =>
                name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_CONTENTROOT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_APPLICATIONNAME", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_URLS", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_HTTP_PORTS", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_HTTPS_PORTS", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WORKUP_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WorkUp__", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var name in risky)
            startInfo.Environment.Remove(name);
    }

    private static async Task StopHostAsync(Process process)
    {
        TryKill(process);
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The process was already reaped; nothing left to wait for.
        }

        process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited on its own between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited while the kill was being delivered.
        }
    }

    private static async Task WaitUntilReadyAsync(RunningHost host)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + ReadinessBudget;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Process.HasExited)
                throw new InvalidOperationException(
                    $"The owned WorkUp host (pid {host.Process.Id}) exited with code {host.Process.ExitCode} before becoming ready.");
            try
            {
                using var response = await client.GetAsync("/panel/Account/Login");
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }

            await Task.Delay(ReadinessPoll);
        }

        throw new TimeoutException(
            $"The owned WorkUp host did not answer GET /panel/Account/Login within {ReadinessBudget.TotalSeconds:0} seconds.");
    }

    private static async Task WaitForTickAsync(RunningHost host)
    {
        var deadline = DateTime.UtcNow + TickBudget;
        while (DateTime.UtcNow < deadline)
        {
            if (host.OutputContains(ScheduledJobsHostedService.TickCompletedMessage))
                return;
            if (host.Process.HasExited)
                throw new InvalidOperationException(
                    $"The owned WorkUp host (pid {host.Process.Id}) exited with code {host.Process.ExitCode} before logging the marker.");

            await Task.Delay(ReadinessPoll);
        }

        throw new TimeoutException(
            $"The owned WorkUp host did not log the fixed scheduled-jobs marker within {TickBudget.TotalSeconds:0} seconds.");
    }

    /// <summary>
    /// The readiness page materializes this run's own encrypted keyring. Both the directory and its encrypted key files
    /// must exist only inside the derived temporary folder: the shared default keyring is never read or written.
    /// </summary>
    private void AssertIsolatedKeyringCreated(string keysPath)
    {
        Assert.True(Directory.Exists(keysPath), "The isolated DataProtection keyring directory was not created.");
        var files = Directory.GetFiles(keysPath, "key-*.xml", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(keysPath));
        foreach (var file in files)
        {
            var canonicalFile = Path.GetFullPath(file);
            Assert.True(
                canonicalFile.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Every DataProtection key file must be inside the dedicated temporary keyring folder.");
            Assert.False(
                File.ReadAllText(file).Contains("<masterKey", StringComparison.Ordinal),
                "Isolated DataProtection keys must be encrypted at rest with DPAPI.");
        }

        output.WriteLine(
            $"DataProtection: {files.Length} encrypted key file(s) inside the dedicated temp keyring folder only.");
    }

    // --- SQL target, migration and helpers --------------------------------------------------------------

    private async Task AssertDatabaseAbsentAsync(string databaseName)
    {
        var masterConnectionString = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        }.ConnectionString;

        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync();
        await using var check = new SqlCommand("SELECT DB_ID(@database)", master);
        check.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = databaseName;
        var existing = await check.ExecuteScalarAsync();
        if (existing is not null && !Convert.IsDBNull(existing))
            throw new InvalidOperationException(
                $"Database '{databaseName}' already exists; this harness never reuses or drops an earlier target and created nothing.");

        output.WriteLine($"SQL: DB_ID('{databaseName}') is NULL (absent) on {Server}.");
    }

    private static async Task<int> QueryIntAsync(string connectionString, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.Add(ToParameter(name, value));
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertIdAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.Add(ToParameter(name, value));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<StockNoticeRow>> ReadStockNoticesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, BusinessId, Recipient, Status, DedupKey FROM Notifications WHERE Type = @type ORDER BY Id",
            connection);
        command.Parameters.Add("@type", SqlDbType.TinyInt).Value = (byte)NotificationType.StockReview;
        var rows = new List<StockNoticeRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new StockNoticeRow(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetByte(3), reader.GetString(4)));
        }

        return rows;
    }

    private static SqlParameter ToParameter(string name, object? value) => value switch
    {
        null => new SqlParameter(name, DBNull.Value),
        DateTime dateTime => new SqlParameter(name, SqlDbType.DateTime2) { Value = dateTime },
        DateOnly date => new SqlParameter(name, SqlDbType.Date) { Value = date.ToDateTime(TimeOnly.MinValue) },
        TimeOnly time => new SqlParameter(name, SqlDbType.Time) { Value = time.ToTimeSpan() },
        Enum enumValue => new SqlParameter(name, Convert.ToByte(enumValue, CultureInfo.InvariantCulture)),
        _ => new SqlParameter(name, value)
    };

    // --- HTTP helpers -----------------------------------------------------------------------------------

    private static HttpClient NewClient(string baseUrl)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Logs one owner in with the real cookie jar and antiforgery flow; the password is never written anywhere.</summary>
    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        var (status, html) = await GetHtmlAsync(client, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, status);
        using var response = await client.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(IsOwnerRedirect(response),
            $"Login for '{username}' did not reach the owner panel (location '{DescribeRedirect(response)}').");
    }

    private static async Task<PostRedirect> PostClockAsync(HttpClient client, string action, Dictionary<string, string> fields)
    {
        // The periodic tick shares the singleton lease, so a genuinely busy answer changes nothing and is retried a
        // bounded number of times; every clock mutation is idempotent while the lease was held by someone else.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var (indexStatus, indexHtml) = await GetHtmlAsync(client, "/panel/DevTools");
            Assert.Equal(HttpStatusCode.OK, indexStatus);
            fields["__RequestVerificationToken"] = ExtractAntiforgeryToken(indexHtml);
            var outcome = await PostAndFollowAsync(client, "/panel/DevTools/" + action, fields);
            Assert.Equal(HttpStatusCode.Redirect, outcome.Status);
            Assert.Equal(HttpStatusCode.OK, outcome.FollowedStatus);
            if (!outcome.FollowedBody.Contains(DevToolsController.ClockBusyMessage, StringComparison.Ordinal))
                return outcome;

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new InvalidOperationException("The shared clock post stayed busy for every bounded attempt; the lease was never free.");
    }

    /// <summary>
    /// Runs one manual job and follows the Post/Redirect/Get. The periodic tick shares the singleton lease, so a
    /// genuinely busy answer is retried a bounded number of times instead of being treated as a failure; the retry is
    /// idempotent because the stock notice is keyed by its due bucket.
    /// </summary>
    private static async Task<PostRedirect> RunManualJobAsync(HttpClient client, string key)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var (indexStatus, indexHtml) = await GetHtmlAsync(client, "/panel/DevTools");
            Assert.Equal(HttpStatusCode.OK, indexStatus);
            var outcome = await PostAndFollowAsync(client, "/panel/DevTools/RunJob", new Dictionary<string, string>
            {
                ["key"] = key,
                ["__RequestVerificationToken"] = ExtractAntiforgeryToken(indexHtml)
            });
            Assert.Equal(HttpStatusCode.Redirect, outcome.Status);
            Assert.Equal(HttpStatusCode.OK, outcome.FollowedStatus);
            if (!outcome.FollowedBody.Contains(DevToolsController.RunBusyMessage, StringComparison.Ordinal))
                return outcome;

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new InvalidOperationException("The manual job stayed busy for every bounded attempt; the shared lease was never free.");
    }

    private static async Task<PostRedirect> PostAndFollowAsync(HttpClient client, string path, Dictionary<string, string> fields)
    {
        using var response = await client.PostAsync(path, Form(fields));
        var status = response.StatusCode;
        var location = response.Headers.Location;
        var redirectPath = location is null
            ? ""
            : location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
        var followedStatus = default(HttpStatusCode);
        var followedBody = "";
        if (status == HttpStatusCode.Redirect && location is not null)
        {
            var (pageStatus, pageBody) = await GetHtmlAsync(client, location.OriginalString);
            followedStatus = pageStatus;
            followedBody = pageBody;
        }

        return new PostRedirect(status, redirectPath, followedStatus, followedBody);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetHtmlAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    private static bool IsOwnerRedirect(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
            return false;
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
        return path.Equals("/panel", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/panel/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoginRedirect(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
            return false;
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
        return path.Equals("/panel/Account/Login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Redirect path without host or query, so diagnostics never expose cookies, tokens or other secrets.</summary>
    private static string DescribeRedirect(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
            return "<none>";
        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", RegexOptions.CultureInvariant);
        if (!tag.Success)
            throw new InvalidOperationException("No antiforgery token was rendered in the expected form.");
        var value = Regex.Match(tag.Value, "value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        if (!value.Success)
            throw new InvalidOperationException("The antiforgery token input had no value.");
        return value.Groups[1].Value;
    }

    // --- Contract records -------------------------------------------------------------------------------

    /// <summary>
    /// The owned child host plus a bounded capture of its standard output, used to observe the committed fixed tick
    /// marker. The lines are never echoed anywhere: only a substring search happens.
    /// </summary>
    private sealed class RunningHost(Process process, string baseUrl)
    {
        private const int MaxCapturedLines = 5000;
        private readonly List<string> _lines = [];
        private readonly object _gate = new();

        public Process Process { get; } = process;

        public string BaseUrl { get; } = baseUrl;

        public void Capture(string? line)
        {
            if (line is null)
                return;

            lock (_gate)
            {
                if (_lines.Count < MaxCapturedLines)
                    _lines.Add(line);
            }
        }

        public bool OutputContains(string value)
        {
            lock (_gate)
                return _lines.Any(line => line.Contains(value, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Minimal real listener on a random loopback port. It counts accepted connections and closes them immediately;
    /// the scenarios only ever need the count, because both the retaining mode and the closed Smtp tool must record
    /// zero. It never touches the external network, requires no credential and offers no TLS.
    /// </summary>
    private sealed class LoopbackConnectionCounter : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _connections;

        public LoopbackConnectionCounter()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int ConnectionCount => Volatile.Read(ref _connections);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                client.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            _cts.Dispose();
        }
    }

    private sealed record HarnessPlan(
        string ConnectionString, string DatabaseName, string KeysPath, string WebHostPath, string WebRoot, int Port)
    {
        public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record PreparedTarget(HarnessPlan Plan, DateTime BusinessNow, string Password, int BusinessA, int BusinessB);

    private sealed record SeededNotices(
        int OwnId, int ForeignId, int NullId, string OwnSubject, string ForeignSubject, string NullSubject);

    private sealed record StockNoticeRow(int Id, int BusinessId, string Recipient, byte Status, string DedupKey);

    private sealed record PostRedirect(HttpStatusCode Status, string RedirectPath, HttpStatusCode FollowedStatus, string FollowedBody);
}
