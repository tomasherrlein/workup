using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.Areas.Owner.Models.Day;
using WorkUp.Data.DemoData;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;
using WorkUp.Infrastructure;
using Xunit.Abstractions;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// Opt-in, reusable HTTP integration harness for the owner panel. It drives the real MVC host over loopback with
/// cookies and antiforgery, then verifies the persisted rows with ADO.NET, and it covers six independent flows.
/// The Jornada (RF-11/RF-15): owner login, one synthetic client through the owner form, GET /panel/Day, a walk-in with
/// an exact agreed price plus immediate payment and next-visit note, a second overlapping walk-in saved with a
/// warning, and the anti-CSRF rejection. The week generation (RF-06): yoga owner login, the first real render of
/// GET /panel/Classes/Week, the default-week confirm that sends no date, the SQL shape of the generated sessions,
/// the idempotent second confirm, the anti-CSRF rejection and the past-week gate. The drop-in classes (RF-22, RF-07
/// cupo &gt; 1): yoga owner login, the default-week confirm that gives the roster its sessions, the first real render
/// of GET /panel/Classes/DropIns with its session rows, one client created through the owner form and booked as a
/// suelta, the owner cancellation that frees the place, the public booking from /r/{slug}/clases with its
/// confirmation and the client it creates without an account, and the anti-CSRF rejection. The memberships (RF-05,
/// RF-20, RF-29): yoga owner login, the tariff configured from the owner Settings screen, the create form that offers
/// the grid classes and the configured tariff with the amount empty, the pending enrollment created with no amount,
/// the charge that activates it with one payment and one period matching MembershipCalendar, and the anti-CSRF
/// rejection; this fourth flow deliberately reuses the T12 harness variables and prefix to avoid duplicating the
/// isolation harness. The class roster (RF-10, RF-11, RF-12, RF-22): yoga owner login, the generated week, two of its
/// synthetic sessions shifted — one still ahead of the clock and one already inside its arrival window — the class
/// link followed from the drop-in roster and from the Jornada, the detail render with its roster, occupancy and stored
/// end, the antiforgery-protected attendance with a drop-in charge and the absence, the cancellation of an already
/// recorded annotation before the start, the at-arrival walk-in inside the stored window with its charge and zero
/// email, the suspension with its mandatory reason and deduplicated notices, the no-token rejection, and the SQL rows
/// behind every one of them; this fifth flow reuses the same T12 harness variables and prefix. The fixed-schedule
/// change (RF-21): yoga owner login, the explicit next-Monday week confirmed so the guard has real sessions, the
/// guided form followed from the link the Abonos list publishes, the anti-CSRF rejection, the effective date that
/// equals the day the old window started, the effective date that overlaps the generated sessions of either affected
/// grid class — both refused with the first usable date and no write — and the change that finally commits, asserted
/// in SQL as the old window closed the day before and the new one opened on it while the enrollment, its payment, its
/// period and the annotation of a generated session stayed untouched; this sixth flow also reuses the same T12 harness
/// variables and prefix. The payment ledger (RF-11, RF-28, D-20): yoga owner login, the confirmed week, a seeded
/// attended drop-in without a charge, the late charge that registers its payment from the class detail, GET
/// /panel/Payments with the charge and its annotation origin, the antiforgery-protected annulment whose responsible is
/// the authenticated owner, the corrected charge, the partial refund dated today and the two refusals -- a refund past
/// what is left and an annulment of an already refunded payment -- all asserted in SQL as the original charge
/// retained, one annulment and one refund, and the enrollment, its period, the attendance, the notes and the outbox
/// untouched; this seventh flow also reuses the same T12 harness variables and prefix. It replaces manual scratch
/// scripts: the same test is the smoke it used to need.
///
/// Without <c>WORKUP_T12_HTTP_CONNECTION</c> each test logs N/A and returns before any SQL connection or HTTP
/// request, so the default suite is unaffected. With it set, the harness refuses to run unless it can prove the
/// runtime is isolated first: Windows DPAPI, integrated <c>(localdb)\MSSQLLocalDB</c>, a fresh
/// <c>WorkUpDemoT12Http*</c> database, a fresh encrypted keyring folder under the OS temp directory with no
/// reparse-point ancestor, and a free loopback port. It then creates the database atomically, starts an owned
/// web child to migrate, seeds the T07 demo with an ephemeral in-memory password, restarts the owned host and
/// runs the flow. The database and encrypted keys are retained (never dropped or deleted), and every assertion
/// reports only booleans, identifiers and paths -- never passwords, cookies or tokens.
/// </summary>
public sealed class OwnerDayHttpIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string ConnectionVariable = "WORKUP_T12_HTTP_CONNECTION";
    private const string KeysPathVariable = "WORKUP_T12_HTTP_KEYS_PATH";
    private const string KeysLeafPrefix = "WorkUpDemoT12Keys";
    private const string DatabasePrefix = "WorkUpDemoT12Http";
    private const decimal ExpectedPrice = 100.25m;

    /// <summary>Agreed price of the synthetic T16 fixed enrollment; the flow asserts its payment is never mutated.</summary>
    private const decimal MembershipFixtureAmount = 6500m;

    private static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Owner_jornada_registers_a_walk_in_payment_and_note_on_an_isolated_http_host()
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection))
        {
            output.WriteLine($"N/A: {ConnectionVariable} is not set; no SQL connection, database, or HTTP host was used.");
            return;
        }

        // Preflight runs before any SQL connection: an unsafe assistant or a reused target stops the run here.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var password = "T12-" + Guid.NewGuid().ToString("N") + "!aA1";

        // First owned host migrates the fresh database and materializes the isolated encrypted keyring.
        var migrations = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(migrations);
            Assert.False(migrations.Process.HasExited, "The owned migration host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned migration host (pid {migrations.Process.Id}) became ready on {migrations.BaseUrl}.");
        }
        finally
        {
            await StopWebHostAsync(migrations.Process);
        }
        AssertKeyringCreated(plan.KeysPath);

        // The explicit T07 seed into the migrated, dedicated database, reusing the real hasher and clock.
        await SeedDemoAsync(plan.ConnectionString, password);
        output.WriteLine("SQL: T07 demo seeded (marina.demo and lucia.demo) into the isolated database.");

        // Second owned host serves the authenticated owner flow with a brand-new loopback port.
        var host = StartWebHost(plan, FindFreeLoopbackPort());
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            await RunOwnerWalkInFlowAsync(host.BaseUrl, plan, password);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    [Fact]
    public async Task Week_generation_previews_and_confirms_a_week_on_an_isolated_http_host()
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection))
        {
            output.WriteLine($"N/A: {ConnectionVariable} is not set; no SQL connection, database, or HTTP host was used.");
            return;
        }

        // Preflight runs before any SQL connection: an unsafe assistant or a reused target stops the run here.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var password = "T13-" + Guid.NewGuid().ToString("N") + "!aA1";

        // First owned host migrates the fresh database and materializes the isolated encrypted keyring.
        var migrations = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(migrations);
            Assert.False(migrations.Process.HasExited, "The owned migration host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned migration host (pid {migrations.Process.Id}) became ready on {migrations.BaseUrl}.");
        }
        finally
        {
            await StopWebHostAsync(migrations.Process);
        }
        AssertKeyringCreated(plan.KeysPath);

        // The explicit T07 seed into the migrated, dedicated database, reusing the real hasher and clock.
        await SeedDemoAsync(plan.ConnectionString, password);
        output.WriteLine("SQL: T07 demo seeded (marina.demo and lucia.demo) into the isolated database.");

        // Second owned host serves the authenticated owner flow with a brand-new loopback port.
        var host = StartWebHost(plan, FindFreeLoopbackPort());
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            await RunWeekGenerationFlowAsync(host.BaseUrl, plan, password);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    [Fact]
    public async Task Drop_in_classes_render_and_book_and_cancel_on_an_isolated_http_host()
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection))
        {
            output.WriteLine($"N/A: {ConnectionVariable} is not set; no SQL connection, database, or HTTP host was used.");
            return;
        }

        // Preflight runs before any SQL connection: an unsafe assistant or a reused target stops the run here.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var password = "T14-" + Guid.NewGuid().ToString("N") + "!aA1";

        // First owned host migrates the fresh database and materializes the isolated encrypted keyring.
        var migrations = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(migrations);
            Assert.False(migrations.Process.HasExited, "The owned migration host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned migration host (pid {migrations.Process.Id}) became ready on {migrations.BaseUrl}.");
        }
        finally
        {
            await StopWebHostAsync(migrations.Process);
        }
        AssertKeyringCreated(plan.KeysPath);

        // The explicit T07 seed into the migrated, dedicated database, reusing the real hasher and clock.
        await SeedDemoAsync(plan.ConnectionString, password);
        output.WriteLine("SQL: T07 demo seeded (marina.demo and lucia.demo) into the isolated database.");

        // Second owned host serves the authenticated owner flow with a brand-new loopback port.
        var host = StartWebHost(plan, FindFreeLoopbackPort());
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            await RunDropInFlowAsync(host.BaseUrl, plan, password);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    [Fact]
    public async Task Memberships_render_and_activate_on_an_isolated_http_host()
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection))
        {
            output.WriteLine($"N/A: {ConnectionVariable} is not set; no SQL connection, database, or HTTP host was used.");
            return;
        }

        // Preflight runs before any SQL connection: an unsafe assistant or a reused target stops the run here.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var password = "T15-" + Guid.NewGuid().ToString("N") + "!aA1";

        // First owned host migrates the fresh database and materializes the isolated encrypted keyring.
        var migrations = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(migrations);
            Assert.False(migrations.Process.HasExited, "The owned migration host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned migration host (pid {migrations.Process.Id}) became ready on {migrations.BaseUrl}.");
        }
        finally
        {
            await StopWebHostAsync(migrations.Process);
        }
        AssertKeyringCreated(plan.KeysPath);

        // The explicit T07 seed into the migrated, dedicated database, reusing the real hasher and clock.
        await SeedDemoAsync(plan.ConnectionString, password);
        output.WriteLine("SQL: T07 demo seeded (marina.demo and lucia.demo) into the isolated database.");

        // Second owned host serves the authenticated owner flow with a brand-new loopback port.
        var host = StartWebHost(plan, FindFreeLoopbackPort());
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            await RunMembershipFlowAsync(host.BaseUrl, plan, password);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    [Fact]
    public async Task Class_roster_attendance_and_suspension_run_on_an_isolated_http_host()
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection))
        {
            output.WriteLine($"N/A: {ConnectionVariable} is not set; no SQL connection, database, or HTTP host was used.");
            return;
        }

        // Preflight runs before any SQL connection: an unsafe assistant or a reused target stops the run here.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var password = "T16-" + Guid.NewGuid().ToString("N") + "!aA1";

        // First owned host migrates the fresh database and materializes the isolated encrypted keyring.
        var migrations = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(migrations);
            Assert.False(migrations.Process.HasExited, "The owned migration host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned migration host (pid {migrations.Process.Id}) became ready on {migrations.BaseUrl}.");
        }
        finally
        {
            await StopWebHostAsync(migrations.Process);
        }
        AssertKeyringCreated(plan.KeysPath);

        // The explicit T07 seed into the migrated, dedicated database, reusing the real hasher and clock.
        await SeedDemoAsync(plan.ConnectionString, password);
        output.WriteLine("SQL: T07 demo seeded (marina.demo and lucia.demo) into the isolated database.");

        // Second owned host serves the authenticated owner flow with a brand-new loopback port.
        var host = StartWebHost(plan, FindFreeLoopbackPort());
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            await RunClassRosterFlowAsync(host.BaseUrl, plan, password);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    [Fact]
    public async Task Fixed_schedule_change_moves_one_fixed_class_on_an_isolated_http_host()
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection))
        {
            output.WriteLine($"N/A: {ConnectionVariable} is not set; no SQL connection, database, or HTTP host was used.");
            return;
        }

        // Preflight runs before any SQL connection: an unsafe assistant or a reused target stops the run here.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var password = "T17-" + Guid.NewGuid().ToString("N") + "!aA1";

        // First owned host migrates the fresh database and materializes the isolated encrypted keyring.
        var migrations = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(migrations);
            Assert.False(migrations.Process.HasExited, "The owned migration host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned migration host (pid {migrations.Process.Id}) became ready on {migrations.BaseUrl}.");
        }
        finally
        {
            await StopWebHostAsync(migrations.Process);
        }
        AssertKeyringCreated(plan.KeysPath);

        // The explicit T07 seed into the migrated, dedicated database, reusing the real hasher and clock.
        await SeedDemoAsync(plan.ConnectionString, password);
        output.WriteLine("SQL: T07 demo seeded (marina.demo and lucia.demo) into the isolated database.");

        // Second owned host serves the authenticated owner flow with a brand-new loopback port.
        var host = StartWebHost(plan, FindFreeLoopbackPort());
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            await RunFixedScheduleChangeFlowAsync(host.BaseUrl, plan, password);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    [Fact]
    public async Task Payment_ledger_annuls_and_refunds_a_late_drop_in_charge_on_an_isolated_http_host()
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection))
        {
            output.WriteLine($"N/A: {ConnectionVariable} is not set; no SQL connection, database, or HTTP host was used.");
            return;
        }

        // Preflight runs before any SQL connection: an unsafe assistant or a reused target stops the run here.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var password = "T18-" + Guid.NewGuid().ToString("N") + "!aA1";

        // First owned host migrates the fresh database and materializes the isolated encrypted keyring.
        var migrations = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(migrations);
            Assert.False(migrations.Process.HasExited, "The owned migration host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned migration host (pid {migrations.Process.Id}) became ready on {migrations.BaseUrl}.");
        }
        finally
        {
            await StopWebHostAsync(migrations.Process);
        }
        AssertKeyringCreated(plan.KeysPath);

        // The explicit T07 seed into the migrated, dedicated database, reusing the real hasher and clock.
        await SeedDemoAsync(plan.ConnectionString, password);
        output.WriteLine("SQL: T07 demo seeded (marina.demo and lucia.demo) into the isolated database.");

        // Second owned host serves the authenticated owner flow with a brand-new loopback port.
        var host = StartWebHost(plan, FindFreeLoopbackPort());
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            await RunPaymentLedgerFlowAsync(host.BaseUrl, plan, password);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    private async Task RunOwnerWalkInFlowAsync(string baseUrl, HarnessPlan plan, string password)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Login: fetch the antiforgery token from the rendered form, then post it back with the same cookie jar.
        var (loginStatus, loginHtml) = await GetHtmlAsync(client, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        var loginResponse = await client.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
        {
            ["Username"] = "marina.demo",
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.True(IsDayRedirect(loginResponse), $"Login did not redirect to the owner Jornada (location '{DescribeRedirect(loginResponse)}').");
        output.WriteLine("HTTP: owner login accepted with antiforgery; redirected to the owner Jornada (/panel).");

        // One synthetic client created only through the owner form.
        var (createStatus, createHtml) = await GetHtmlAsync(client, "/panel/Clients/Create");
        Assert.Equal(HttpStatusCode.OK, createStatus);
        var email = $"t12http{Guid.NewGuid():N}@example.invalid";
        var createResponse = await client.PostAsync("/panel/Clients/Create", Form(new Dictionary<string, string>
        {
            ["FirstName"] = "Synthetic",
            ["LastName"] = "T12Http",
            ["Email"] = email,
            ["Phone"] = "0000000000",
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(createHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        var clientId = ExtractClientId(createResponse);
        output.WriteLine($"HTTP: synthetic client #{clientId} created through the owner form.");

        // The Jornada renders the at-arrival form inline.
        var (dayStatus, dayHtml) = await GetHtmlAsync(client, "/panel/Day");
        Assert.Equal(HttpStatusCode.OK, dayStatus);
        Assert.Contains("owner-walk-in-form", dayHtml, StringComparison.Ordinal);
        Assert.Contains(nameof(WalkInViewModel.AgreedPrice), dayHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: GET /panel/Day -> 200 with the inline walk-in form.");

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();
        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = 'peluqueria-marina-demo'");
        var serviceId = await ScalarIntAsync(connection,
            "SELECT TOP 1 Id FROM Services WHERE BusinessId = @businessId AND IsActive = 1 AND MaxCapacity = 1 ORDER BY Id",
            ("@businessId", businessId));
        var workerId = await ScalarIntAsync(connection,
            "SELECT TOP 1 Id FROM Workers WHERE BusinessId = @businessId AND IsActive = 1 ORDER BY Id",
            ("@businessId", businessId));
        Assert.Equal(businessId, await ScalarIntAsync(connection, "SELECT BusinessId FROM Clients WHERE Id = @clientId", ("@clientId", clientId)));

        // Walk-in with the agreed price written with a dot, immediate payment and a next-visit note.
        var firstNote = "T12 HTTP note " + Guid.NewGuid().ToString("N");
        var firstResponse = await client.PostAsync("/panel/Day/WalkIn", Form(WalkInFields(clientId, serviceId, workerId,
            firstNote, ExtractAntiforgeryToken(dayHtml))));
        Assert.Equal(HttpStatusCode.Redirect, firstResponse.StatusCode);
        Assert.True(IsDayRedirect(firstResponse), $"The walk-in POST did not redirect back to the Jornada (location '{DescribeRedirect(firstResponse)}').");
        var (afterFirstStatus, afterFirstHtml) = await GetHtmlAsync(client, firstResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterFirstStatus);
        Assert.Contains("Sin reserva", afterFirstHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: walk-in #1 saved and shown on the Jornada as 'Sin reserva'.");

        // A second overlapping walk-in at the same time is still saved and warns the owner.
        var (dayAgainStatus, dayAgainHtml) = await GetHtmlAsync(client, "/panel/Day");
        Assert.Equal(HttpStatusCode.OK, dayAgainStatus);
        var secondNote = "T12 HTTP overlap " + Guid.NewGuid().ToString("N");
        var secondResponse = await client.PostAsync("/panel/Day/WalkIn", Form(WalkInFields(clientId, serviceId, workerId,
            secondNote, ExtractAntiforgeryToken(dayAgainHtml))));
        Assert.Equal(HttpStatusCode.Redirect, secondResponse.StatusCode);
        var (afterSecondStatus, afterSecondHtml) = await GetHtmlAsync(client, secondResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterSecondStatus);
        // The flash message is HTML-encoded, so the accented characters become entities; match an ASCII-safe
        // prefix that only the warning banner contains (the static walk-in note uses a lower-case "se superpone").
        Assert.Contains("Se superpone con otra atenci", afterSecondHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: overlapping walk-in #2 saved with the warning banner.");

        // A POST without the antiforgery token must be rejected before any business logic runs.
        var rejected = await client.PostAsync("/panel/Day/WalkIn", Form(new Dictionary<string, string>
        {
            ["ClientId"] = clientId.ToString(CultureInfo.InvariantCulture),
            ["ServiceId"] = serviceId.ToString(CultureInfo.InvariantCulture),
            ["WorkerId"] = workerId.ToString(CultureInfo.InvariantCulture),
            ["AgreedPrice"] = "100.25",
            ["CollectPayment"] = "true"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        output.WriteLine("HTTP: walk-in POST without an anti-CSRF token -> 400.");

        // SQL verification runs after every HTTP checkpoint so one run reports the whole behavior.
        var walkInCount = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId AND Origin = @origin",
            ("@businessId", businessId), ("@origin", (byte)AppointmentOrigin.WalkIn));
        Assert.Equal(2, walkInCount);
        var firstAppointmentId = await ScalarIntAsync(connection,
            "SELECT TOP 1 Id FROM Appointments WHERE BusinessId = @businessId AND Origin = @origin ORDER BY Id ASC",
            ("@businessId", businessId), ("@origin", (byte)AppointmentOrigin.WalkIn));
        var secondAppointmentId = await ScalarIntAsync(connection,
            "SELECT TOP 1 Id FROM Appointments WHERE BusinessId = @businessId AND Origin = @origin ORDER BY Id DESC",
            ("@businessId", businessId), ("@origin", (byte)AppointmentOrigin.WalkIn));
        Assert.NotEqual(firstAppointmentId, secondAppointmentId);
        Assert.Equal(secondNote, await ScalarStringAsync(connection, "SELECT Text FROM ClientNotes WHERE AppointmentId = @id", ("@id", secondAppointmentId)));
        await AssertWalkInRowAsync(connection, businessId, firstAppointmentId, clientId, firstNote);
        output.WriteLine("SQL: walk-in #1 appointment Scheduled/WalkIn, booking Attended with an exact 100.25 agreed price, payment WalkIn with an exact 100.25 amount, note and same BusinessId asserted.");
    }

    /// <summary>
    /// Week generation flow (RF-06) over the real host. This T13 smoke deliberately reuses the T12 harness variables
    /// (<c>WORKUP_T12_HTTP_CONNECTION</c>, <c>WORKUP_T12_HTTP_KEYS_PATH</c>) and the <c>WorkUpDemoT12Http*</c> database
    /// prefix instead of duplicating the isolation harness: both facts are opt-in and each run gets a fresh database
    /// and keyring, so reusing the names never shares state between flows.
    /// </summary>
    private async Task RunWeekGenerationFlowAsync(string baseUrl, HarnessPlan plan, string password)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Login as the yoga owner: the hairdresser demo has no grid classes, so its week page would be empty.
        var (loginStatus, loginHtml) = await GetHtmlAsync(client, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        var loginResponse = await client.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
        {
            ["Username"] = "lucia.demo",
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.True(IsDayRedirect(loginResponse), $"Login did not redirect to the owner Jornada (location '{DescribeRedirect(loginResponse)}').");
        output.WriteLine("HTTP: yoga owner login accepted with antiforgery; redirected to the owner Jornada (/panel).");

        // First real render of the Classes/Week page: the heading, the summary labels and the confirm submit prove the
        // view, its partials and its icons rendered, and the absence of the empty state proves it is the grid branch.
        var (weekStatus, weekHtml) = await GetHtmlAsync(client, "/panel/Classes/Week");
        Assert.Equal(HttpStatusCode.OK, weekStatus);
        Assert.Contains("Generar semana", weekHtml, StringComparison.Ordinal);
        Assert.Contains("Clases nuevas", weekHtml, StringComparison.Ordinal);
        Assert.Contains("Alumnas fijas", weekHtml, StringComparison.Ordinal);
        Assert.Contains("Confirmar semana", weekHtml, StringComparison.Ordinal);
        Assert.Contains("tag-scheduled", weekHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("no hay clases en la grilla", weekHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: GET /panel/Classes/Week -> 200 with the full preview render (heading, summary, confirm and status tags).");

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();
        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = 'estudio-yoga-lucia-demo'");
        var gridClassCount = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM GridClasses WHERE BusinessId = @businessId AND IsActive = 1",
            ("@businessId", businessId));
        Assert.True(gridClassCount > 0, "The yoga demo must expose at least one active grid class or the week smoke proves nothing.");
        output.WriteLine($"SQL: yoga demo business #{businessId} has {gridClassCount} active grid class(es).");

        // Confirm the default week without sending a date at all: the missing value must bind to null and generate the
        // next week, then the redirect shows the regenerated week plus the success flash.
        var firstConfirm = await client.PostAsync("/panel/Classes/ConfirmWeek", Form(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(weekHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, firstConfirm.StatusCode);
        var (firstConfirmStatus, firstConfirmHtml) = await GetHtmlAsync(client, firstConfirm.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, firstConfirmStatus);
        Assert.Contains("Semana generada", firstConfirmHtml, StringComparison.Ordinal);
        Assert.Contains($"Semana generada: {gridClassCount} clases nuevas", firstConfirmHtml, StringComparison.Ordinal);
        // Every session now exists, so the summary reports them as already generated and none as new.
        Assert.Contains("Ya generadas", firstConfirmHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: POST ConfirmWeek with no date -> 302 -> 200 with the generated week and the success flash.");

        // SQL: the confirmed week persists exactly one session per active grid class, with the service-defined shape.
        var appointmentCount = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId AND GridClassId IS NOT NULL",
            ("@businessId", businessId));
        Assert.Equal(gridClassCount, appointmentCount);
        var shapedSessions = await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Appointments AS a
              JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
             WHERE a.BusinessId = @businessId
               AND a.Kind = @kind
               AND a.Status = @status
               AND a.MaxCapacity = s.MaxCapacity
               AND a.EndsAt = DATEADD(MINUTE, s.DurationMinutes, a.StartsAt)
            """,
            ("@businessId", businessId),
            ("@kind", (byte)AppointmentKind.ClassSession),
            ("@status", (byte)AppointmentStatus.Scheduled));
        Assert.Equal(appointmentCount, shapedSessions);
        // The T07 seed plans no enrollments, so a generated week must not invent a single fixed annotation.
        var fixedBookings = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND Modality = @modality",
            ("@businessId", businessId), ("@modality", (byte)BookingModality.Fixed));
        Assert.Equal(0, fixedBookings);
        output.WriteLine($"SQL: {appointmentCount} class session(s) Scheduled/ClassSession with the service capacity and end time, and 0 fixed bookings.");

        // Confirming the same week again must add nothing: the HTTP layer reports zero new classes and SQL is stable.
        var secondConfirm = await client.PostAsync("/panel/Classes/ConfirmWeek", Form(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(firstConfirmHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, secondConfirm.StatusCode);
        var (secondConfirmStatus, secondConfirmHtml) = await GetHtmlAsync(client, secondConfirm.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, secondConfirmStatus);
        Assert.Contains("Semana generada: 0 clases nuevas", secondConfirmHtml, StringComparison.Ordinal);
        Assert.Equal(appointmentCount, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId AND GridClassId IS NOT NULL",
            ("@businessId", businessId)));
        output.WriteLine("HTTP/SQL: second confirm of the same week -> 302 -> 200 reporting 0 new classes and no extra appointment row.");

        // A POST without the antiforgery token must be rejected before any business logic runs.
        var rejected = await client.PostAsync("/panel/Classes/ConfirmWeek", Form(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        output.WriteLine("HTTP: ConfirmWeek POST without an anti-CSRF token -> 400.");

        // A week that already ended must never offer a confirm that could only fail, while the preview still renders.
        // The Monday two whole weeks back stays in the past for any time zone the app could run with.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var mondayOfThisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var pastMonday = mondayOfThisWeek.AddDays(-14);
        var (pastStatus, pastHtml) = await GetHtmlAsync(client,
            "/panel/Classes/Week?date=" + pastMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.OK, pastStatus);
        Assert.Contains("Semana del", pastHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirmar semana", pastHtml, StringComparison.Ordinal);
        output.WriteLine("HTTP: past-week preview -> 200 with the week heading and without the confirm submit.");
    }

    /// <summary>
    /// Drop-in classes flow (RF-22, RF-07 cupo &gt; 1) over the real host. This T14 smoke deliberately reuses the T12
    /// harness variables (<c>WORKUP_T12_HTTP_CONNECTION</c>, <c>WORKUP_T12_HTTP_KEYS_PATH</c>) and the
    /// <c>WorkUpDemoT12Http*</c> database prefix instead of duplicating the isolation harness: every fact is opt-in and
    /// each run needs a fresh, absent database and keyring, so reusing the names never shares state between flows.
    /// </summary>
    private async Task RunDropInFlowAsync(string baseUrl, HarnessPlan plan, string password)
    {
        // Every diagnostic line goes through Note, so the last checkpoint can prove that no password, antiforgery token
        // or cookie value ever reached the test output.
        var written = new List<string>();
        var secrets = new HashSet<string>(StringComparer.Ordinal) { password };
        void Note(string line)
        {
            output.WriteLine(line);
            written.Add(line);
        }

        // The token is a secret: it is remembered for the output check instead of being written anywhere.
        string ExtractToken(string html)
        {
            var token = ExtractAntiforgeryToken(html);
            secrets.Add(token);
            return token;
        }

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Login as the yoga owner: the hairdresser demo has no grid classes, so it has no drop-in roster either.
        var (loginStatus, loginHtml) = await GetHtmlAsync(client, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        var loginResponse = await client.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
        {
            ["Username"] = "lucia.demo",
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractToken(loginHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.True(IsDayRedirect(loginResponse), $"Login did not redirect to the owner Jornada (location '{DescribeRedirect(loginResponse)}').");
        Note("HTTP: yoga owner login accepted with antiforgery; redirected to the owner Jornada (/panel).");

        // The roster only has something to offer once sessions exist: ConfirmWeek without a date binds null and
        // generates the default (next) week. The week page is read for the antiforgery token of that POST, which is
        // also the first real render of the page the confirm belongs to.
        var (weekStatus, weekHtml) = await GetHtmlAsync(client, "/panel/Classes/Week");
        Assert.Equal(HttpStatusCode.OK, weekStatus);
        var confirmResponse = await client.PostAsync("/panel/Classes/ConfirmWeek", Form(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(weekHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, confirmResponse.StatusCode);
        var (confirmStatus, confirmHtml) = await GetHtmlAsync(client, confirmResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, confirmStatus);
        Assert.Contains("Semana generada", confirmHtml, StringComparison.Ordinal);
        Note("HTTP: POST /panel/Classes/ConfirmWeek with no date -> 302 -> 200 with the generated week.");

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();
        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = 'estudio-yoga-lucia-demo'");
        var slug = await ScalarStringAsync(connection, "SELECT Slug FROM Businesses WHERE Id = @businessId", ("@businessId", businessId));
        Assert.False(string.IsNullOrWhiteSpace(slug), "The yoga demo business must expose its public slug.");
        var sessionCount = await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Appointments
             WHERE BusinessId = @businessId
               AND Kind = @kind
               AND Status = @status
               AND GridClassId IS NOT NULL
            """,
            ("@businessId", businessId),
            ("@kind", (byte)AppointmentKind.ClassSession),
            ("@status", (byte)AppointmentStatus.Scheduled));
        Assert.True(sessionCount > 0, "The confirmed week must persist at least one scheduled class session.");
        var sessionId = await ScalarIntAsync(connection, """
            SELECT TOP 1 Id
              FROM Appointments
             WHERE BusinessId = @businessId
               AND Kind = @kind
               AND Status = @status
               AND GridClassId IS NOT NULL
             ORDER BY Id
            """,
            ("@businessId", businessId),
            ("@kind", (byte)AppointmentKind.ClassSession),
            ("@status", (byte)AppointmentStatus.Scheduled));
        Note($"SQL: the confirmed week generated {sessionCount} scheduled class session(s); the flow uses session #{sessionId}.");

        // First real render of the owner roster: the heading, the window, the session rows, the status partial and an
        // inline icon prove the view and its partials rendered, while the absence of the empty state proves this is
        // the populated branch and not a page that failed to resolve its sessions.
        var (rosterStatus, rosterHtml) = await GetHtmlAsync(client, "/panel/Classes/DropIns");
        Assert.Equal(HttpStatusCode.OK, rosterStatus);
        Assert.Contains("Clases sueltas", rosterHtml, StringComparison.Ordinal);
        Assert.Contains("De hoy al", rosterHtml, StringComparison.Ordinal);
        Assert.Contains("row-time", rosterHtml, StringComparison.Ordinal);
        Assert.Contains("tag-scheduled", rosterHtml, StringComparison.Ordinal);
        Assert.Contains("<svg", rosterHtml, StringComparison.Ordinal);
        // The empty state is matched by an ASCII-only prefix because the rendered sentence carries an accent.
        Assert.DoesNotContain("No hay clases en los pr", rosterHtml, StringComparison.Ordinal);
        Note("HTTP: GET /panel/Classes/DropIns -> 200 with the roster render (heading, window, session rows, status tag and icons).");

        // One synthetic client created only through the owner form, exactly like the Jornada flow.
        var (createStatus, createHtml) = await GetHtmlAsync(client, "/panel/Clients/Create");
        Assert.Equal(HttpStatusCode.OK, createStatus);
        var ownerEmail = $"t14owner{Guid.NewGuid():N}@example.invalid";
        var createResponse = await client.PostAsync("/panel/Clients/Create", Form(new Dictionary<string, string>
        {
            ["FirstName"] = "Synthetic",
            ["LastName"] = "T14Owner",
            ["Email"] = ownerEmail,
            ["Phone"] = "0000000000",
            ["__RequestVerificationToken"] = ExtractToken(createHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        var clientId = ExtractClientId(createResponse);
        Note($"HTTP: synthetic client #{clientId} created through the owner form.");

        // Opening the roster on that session renders its booking form, which is also where the POST takes its token
        // from: the form fields are the picker of the client and the antiforgery token.
        var (formStatus, formHtml) = await GetHtmlAsync(client, $"/panel/Classes/DropIns?session={sessionId}");
        Assert.Equal(HttpStatusCode.OK, formStatus);
        Assert.Contains($"drop-in-client-{sessionId}", formHtml, StringComparison.Ordinal);
        var bookResponse = await client.PostAsync("/panel/Classes/BookDropIn", Form(new Dictionary<string, string>
        {
            ["appointmentId"] = sessionId.ToString(CultureInfo.InvariantCulture),
            ["clientId"] = clientId.ToString(CultureInfo.InvariantCulture),
            ["__RequestVerificationToken"] = ExtractToken(formHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, bookResponse.StatusCode);
        var (afterBookStatus, afterBookHtml) = await GetHtmlAsync(client, bookResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterBookStatus);
        // The redirected roster lists the annotation with the client's name and the status partial, so the booking is
        // visible on the screen the owner actually reads.
        Assert.Contains("Synthetic T14Owner", afterBookHtml, StringComparison.Ordinal);
        Assert.Contains("tag-pending", afterBookHtml, StringComparison.Ordinal);
        Note($"HTTP: owner booked client #{clientId} into session #{sessionId} -> 302 -> 200 with the annotated drop-in.");

        var bookingId = await ScalarIntAsync(connection, """
            SELECT TOP 1 Id
              FROM Bookings
             WHERE BusinessId = @businessId
               AND AppointmentId = @appointmentId
               AND Modality = @modality
             ORDER BY Id
            """,
            ("@businessId", businessId), ("@appointmentId", sessionId), ("@modality", (byte)BookingModality.DropIn));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Bookings
             WHERE BusinessId = @businessId
               AND AppointmentId = @appointmentId
               AND Modality = @modality
            """,
            ("@businessId", businessId), ("@appointmentId", sessionId), ("@modality", (byte)BookingModality.DropIn)));
        Assert.Equal((int)BookingStatus.Pending, await ScalarIntAsync(connection,
            "SELECT Status FROM Bookings WHERE Id = @bookingId AND BusinessId = @businessId",
            ("@bookingId", bookingId), ("@businessId", businessId)));
        Assert.Equal(clientId, await ScalarIntAsync(connection,
            "SELECT ClientId FROM Bookings WHERE Id = @bookingId", ("@bookingId", bookingId)));
        Note($"SQL: exactly one drop-in booking #{bookingId} on session #{sessionId}, Pending/Suelta for client #{clientId} of business #{businessId}.");

        // Cancelling the annotation before the class starts is what releases the place; the POST takes its token from
        // the roster that just rendered the cancel form.
        var cancelResponse = await client.PostAsync("/panel/Classes/CancelDropIn", Form(new Dictionary<string, string>
        {
            ["bookingId"] = bookingId.ToString(CultureInfo.InvariantCulture),
            ["__RequestVerificationToken"] = ExtractToken(afterBookHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, cancelResponse.StatusCode);
        var (afterCancelStatus, afterCancelHtml) = await GetHtmlAsync(client, cancelResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterCancelStatus);
        // A cancelled annotation stays listed with its status, which is how the owner sees the place that was freed.
        Assert.Contains("tag-cancelled", afterCancelHtml, StringComparison.Ordinal);
        Note($"HTTP: owner cancelled drop-in booking #{bookingId} -> 302 -> 200 with the annotation still listed as cancelled.");

        Assert.Equal((int)BookingStatus.Cancelled, await ScalarIntAsync(connection,
            "SELECT Status FROM Bookings WHERE Id = @bookingId", ("@bookingId", bookingId)));
        Assert.Equal((int)CancelledBy.Owner, await ScalarIntAsync(connection,
            "SELECT CancelledBy FROM Bookings WHERE Id = @bookingId", ("@bookingId", bookingId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE Id = @bookingId AND CancelledAt IS NOT NULL", ("@bookingId", bookingId)));
        Assert.Equal(0, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Bookings
             WHERE BusinessId = @businessId
               AND AppointmentId = @appointmentId
               AND ClientId = @clientId
               AND Status <> @cancelled
            """,
            ("@businessId", businessId), ("@appointmentId", sessionId), ("@clientId", clientId),
            ("@cancelled", (byte)BookingStatus.Cancelled)));
        Note($"SQL: booking #{bookingId} is Cancelled by the owner with a cancellation instant, and client #{clientId} holds no active booking of session #{sessionId}.");

        // The public screen is a different session: a brand-new cookie-less client, so nothing of the owner login leaks
        // into it and the public flow proves the anonymous path.
        using var publicHandler = new HttpClientHandler { AllowAutoRedirect = false };
        using var publicClient = new HttpClient(publicHandler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        var (publicStatus, publicHtml) = await GetHtmlAsync(publicClient, $"/r/{slug}/clases");
        Assert.Equal(HttpStatusCode.OK, publicStatus);
        var serviceName = await ScalarStringAsync(connection, """
            SELECT s.Name
              FROM Appointments AS a
              JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
             WHERE a.BusinessId = @businessId AND a.Id = @appointmentId
            """,
            ("@businessId", businessId), ("@appointmentId", sessionId));
        Assert.False(string.IsNullOrWhiteSpace(serviceName), "The booked class session must resolve its service name.");
        Assert.Contains(serviceName!, publicHtml, StringComparison.Ordinal);
        Assert.Contains("Quedan ", publicHtml, StringComparison.Ordinal);
        Assert.Contains(" lugares", publicHtml, StringComparison.Ordinal);
        // The public empty state is matched by an ASCII-only fragment because its sentence carries accents.
        Assert.DoesNotContain("no hay clases con lugar", publicHtml, StringComparison.Ordinal);
        Note($"HTTP: GET /r/{slug}/clases -> 200 with the class name and the remaining-places line of the public offer.");

        // The list offers a link per session; opening one renders the contact form, which carries the antiforgery token
        // the booking POST needs. The email is synthetic and identifies the client, so no account is required.
        var (publicFormStatus, publicFormHtml) = await GetHtmlAsync(publicClient, $"/r/{slug}/clases?session={sessionId}");
        Assert.Equal(HttpStatusCode.OK, publicFormStatus);
        var publicEmail = $"t14public{Guid.NewGuid():N}@example.invalid";
        var publicResponse = await publicClient.PostAsync($"/r/{slug}/clases/reservar", Form(new Dictionary<string, string>
        {
            ["AppointmentId"] = sessionId.ToString(CultureInfo.InvariantCulture),
            ["Email"] = publicEmail,
            ["FirstName"] = "Synthetic",
            ["LastName"] = "T14Public",
            ["__RequestVerificationToken"] = ExtractToken(publicFormHtml)
        }));
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        var publicConfirmation = await publicResponse.Content.ReadAsStringAsync();
        Assert.Contains("Recibimos tu reserva", publicConfirmation, StringComparison.Ordinal);
        Note($"HTTP: public drop-in booking for session #{sessionId} -> 200 with the confirmation screen.");

        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Bookings
             WHERE BusinessId = @businessId
               AND AppointmentId = @appointmentId
               AND Modality = @modality
               AND Status = @pending
            """,
            ("@businessId", businessId), ("@appointmentId", sessionId), ("@modality", (byte)BookingModality.DropIn),
            ("@pending", (byte)BookingStatus.Pending)));
        // The cancelled annotation and the public one are the two bookings of the session, so the cancelled one really
        // released its place instead of blocking the new client.
        Assert.Equal(2, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Bookings
             WHERE BusinessId = @businessId
               AND AppointmentId = @appointmentId
               AND Modality = @modality
            """,
            ("@businessId", businessId), ("@appointmentId", sessionId), ("@modality", (byte)BookingModality.DropIn)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", publicEmail)));
        var publicClientId = await ScalarIntAsync(connection,
            "SELECT Id FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", publicEmail));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM ClientAccounts WHERE BusinessId = @businessId AND ClientId = @clientId",
            ("@businessId", businessId), ("@clientId", publicClientId)));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM ClientAccounts WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", publicEmail)));
        Note($"SQL: session #{sessionId} holds two drop-in bookings (1 Pending from the public screen plus 1 Cancelled) and client #{publicClientId} was created without an account.");

        // A public POST without the antiforgery token must be rejected before any business logic runs.
        var rejected = await publicClient.PostAsync($"/r/{slug}/clases/reservar", Form(new Dictionary<string, string>
        {
            ["AppointmentId"] = sessionId.ToString(CultureInfo.InvariantCulture),
            ["Email"] = $"t14rejected{Guid.NewGuid():N}@example.invalid",
            ["FirstName"] = "Synthetic",
            ["LastName"] = "T14Rejected"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Note("HTTP: public drop-in POST without an anti-CSRF token -> 400.");

        // The harness never writes a secret: every diagnostic line reports only booleans, identifiers, counts or paths,
        // and this proves it against the password, every antiforgery token and every cookie of the authenticated run.
        // Short cookie values are skipped because they are not secrets and could match any line by accident.
        foreach (Cookie cookie in handler.CookieContainer.GetAllCookies())
        {
            if (cookie.Value.Length >= 8)
                secrets.Add(cookie.Value);
        }

        Assert.NotEmpty(written);
        var leaked = written.Any(line => secrets.Any(secret => line.Contains(secret, StringComparison.Ordinal)));
        Assert.False(leaked, "The test output must never contain a password, an antiforgery token or a cookie value.");
        Note("Secrets: the run wrote no password, antiforgery token or cookie value to the test output.");
    }

    /// <summary>
    /// Membership flow (RF-05, RF-20, RF-29) over the real host. This T15 smoke deliberately reuses the T12 harness
    /// variables (<c>WORKUP_T12_HTTP_CONNECTION</c>, <c>WORKUP_T12_HTTP_KEYS_PATH</c>) and the
    /// <c>WorkUpDemoT12Http*</c> database prefix instead of duplicating the isolation harness: every fact is opt-in and
    /// each run needs a fresh, absent database and keyring, so reusing the names never shares state between flows.
    /// </summary>
    private async Task RunMembershipFlowAsync(string baseUrl, HarnessPlan plan, string password)
    {
        // The membership tariff configured for frequency 1; the money text is what the owner reads in the frequency
        // option and in the amount placeholder of the create form.
        const decimal tariff = 12345m;
        var tariffText = Formatting.Money(tariff);

        // Every diagnostic line goes through Note, so the last checkpoint can prove that no password, antiforgery token
        // or cookie value ever reached the test output.
        var written = new List<string>();
        var secrets = new HashSet<string>(StringComparer.Ordinal) { password };
        void Note(string line)
        {
            output.WriteLine(line);
            written.Add(line);
        }

        // The token is a secret: it is remembered for the output check instead of being written anywhere.
        string ExtractToken(string html)
        {
            var token = ExtractAntiforgeryToken(html);
            secrets.Add(token);
            return token;
        }

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Login as the yoga owner: it is the demo business with class-type services and an active grid.
        var (loginStatus, loginHtml) = await GetHtmlAsync(client, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        var loginResponse = await client.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
        {
            ["Username"] = "lucia.demo",
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractToken(loginHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.True(IsDayRedirect(loginResponse), $"Login did not redirect to the owner Jornada (location '{DescribeRedirect(loginResponse)}').");
        Note("HTTP: yoga owner login accepted with antiforgery; redirected to the owner Jornada (/panel).");

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();
        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = 'estudio-yoga-lucia-demo'");

        // 1. The demo seed configures no membership tariff, so the owner configures frequency 1 through the Settings
        // screen using the form's own antiforgery token; the stored row is read back from SQL.
        var (servicesStatus, servicesHtml) = await GetHtmlAsync(client, "/panel/Settings/Services");
        Assert.Equal(HttpStatusCode.OK, servicesStatus);
        var setPriceResponse = await client.PostAsync("/panel/Settings/SetMembershipPrice", Form(new Dictionary<string, string>
        {
            ["weeklyFrequency"] = "1",
            ["price"] = "12345",
            ["__RequestVerificationToken"] = ExtractToken(servicesHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, setPriceResponse.StatusCode);
        Assert.Equal("/panel/Settings/Services", DescribeRedirect(setPriceResponse));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM MembershipPrices WHERE BusinessId = @businessId AND WeeklyFrequency = 1 AND Price = @price",
            ("@businessId", businessId), ("@price", tariff)));
        Note($"SQL: membership tariff {tariffText} stored for frequency 1 of business #{businessId}.");

        // 2. The membership list is a real render: its heading and the filter affordances are present, so the empty
        // state is not the only thing the page shows.
        var (listStatus, listHtml) = await GetHtmlAsync(client, "/panel/Enrollments");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Contains("Abonos", listHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"filter\"", listHtml, StringComparison.Ordinal);
        Assert.Contains("Aplicar filtro", listHtml, StringComparison.Ordinal);
        Note("HTTP: GET /panel/Enrollments -> 200 with its heading and the filter affordances.");

        // The create form is only offered once a client exists (its empty state otherwise replaces the form), so one
        // synthetic client is created through the owner form before the form is rendered.
        var (clientFormStatus, clientFormHtml) = await GetHtmlAsync(client, "/panel/Clients/Create");
        Assert.Equal(HttpStatusCode.OK, clientFormStatus);
        var memberEmail = $"t15member{Guid.NewGuid():N}@example.invalid";
        var createClientResponse = await client.PostAsync("/panel/Clients/Create", Form(new Dictionary<string, string>
        {
            ["FirstName"] = "Synthetic",
            ["LastName"] = "T15Member",
            ["Email"] = memberEmail,
            ["Phone"] = "0000000000",
            ["__RequestVerificationToken"] = ExtractToken(clientFormHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, createClientResponse.StatusCode);
        var clientId = ExtractClientId(createClientResponse);
        Note($"HTTP: synthetic client #{clientId} created through the owner form.");

        // 3. The create form offers a grid class and shows the configured tariff, and the amount starts empty with the
        // tariff only as its placeholder, so changing the frequency cannot post a stale amount.
        var (formStatus, formHtml) = await GetHtmlAsync(client, "/panel/Enrollments/Create");
        Assert.Equal(HttpStatusCode.OK, formStatus);
        Assert.Contains("Nueva inscripci", formHtml, StringComparison.Ordinal);
        var gridClassMatch = Regex.Match(formHtml,
            "name=\"GridClassIds\"\\s+value=\"(?<id>\\d+)\"", RegexOptions.CultureInvariant);
        Assert.True(gridClassMatch.Success, "The create form must offer at least one grid class.");
        var gridClassId = int.Parse(gridClassMatch.Groups["id"].Value, CultureInfo.InvariantCulture);
        Assert.Contains(tariffText, formHtml, StringComparison.Ordinal);
        Assert.Contains($"value=\"\" placeholder=\"{tariffText}\"", formHtml, StringComparison.Ordinal);
        Note($"HTTP: GET /panel/Enrollments/Create -> 200 offering grid class #{gridClassId}, the {tariffText} tariff and an empty amount input.");

        // 4. The pending enrollment is posted with no amount: the service must apply the configured tariff of
        // frequency 1 instead of trusting a preseeded value.
        var createEnrollmentResponse = await client.PostAsync("/panel/Enrollments/Create", Form(new Dictionary<string, string>
        {
            ["ClientId"] = clientId.ToString(CultureInfo.InvariantCulture),
            ["WeeklyFrequency"] = "1",
            ["GridClassIds"] = gridClassId.ToString(CultureInfo.InvariantCulture),
            ["__RequestVerificationToken"] = ExtractToken(formHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, createEnrollmentResponse.StatusCode);
        var (afterCreateStatus, afterCreateHtml) = await GetHtmlAsync(client, createEnrollmentResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterCreateStatus);
        Assert.Contains("Synthetic T15Member", afterCreateHtml, StringComparison.Ordinal);
        Assert.Contains("Pendiente de pago", afterCreateHtml, StringComparison.Ordinal);
        Note("HTTP: pending enrollment posted with no amount -> 302 -> 200 listing the client as pending payment.");

        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Enrollments WHERE BusinessId = @businessId AND ClientId = @clientId",
            ("@businessId", businessId), ("@clientId", clientId)));
        var enrollmentId = await ScalarIntAsync(connection,
            "SELECT Id FROM Enrollments WHERE BusinessId = @businessId AND ClientId = @clientId",
            ("@businessId", businessId), ("@clientId", clientId));
        Assert.Equal((int)EnrollmentStatus.PendingPayment, await ScalarIntAsync(connection,
            "SELECT Status FROM Enrollments WHERE Id = @enrollmentId", ("@enrollmentId", enrollmentId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Enrollments WHERE Id = @enrollmentId AND AgreedPrice = @price",
            ("@enrollmentId", enrollmentId), ("@price", tariff)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", enrollmentId)));
        Note($"SQL: exactly one PendingPayment enrollment #{enrollmentId} with the configured tariff and exactly one fixed class.");

        // 5. Charge with no amount: the agreed price (the tariff) is charged, the membership activates and the period
        // matches MembershipCalendar for the paid instant.
        var chargeResponse = await client.PostAsync(
            "/panel/Enrollments/Charge?enrollmentId=" + enrollmentId.ToString(CultureInfo.InvariantCulture),
            Form(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(afterCreateHtml)
            }));
        Assert.Equal(HttpStatusCode.Redirect, chargeResponse.StatusCode);
        var (afterChargeStatus, afterChargeHtml) = await GetHtmlAsync(client, chargeResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterChargeStatus);
        Assert.Contains("Synthetic T15Member", afterChargeHtml, StringComparison.Ordinal);
        Assert.Contains("Vigente", afterChargeHtml, StringComparison.Ordinal);
        Note("HTTP: membership charged with no amount -> 302 -> 200 listing it as current (Vigente).");

        Assert.Equal((int)EnrollmentStatus.Active, await ScalarIntAsync(connection,
            "SELECT Status FROM Enrollments WHERE Id = @enrollmentId", ("@enrollmentId", enrollmentId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Enrollments WHERE Id = @enrollmentId AND ActivatedAt IS NOT NULL",
            ("@enrollmentId", enrollmentId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId AND EnrollmentId = @enrollmentId AND Concept = @concept AND Amount = @price",
            ("@businessId", businessId), ("@enrollmentId", enrollmentId),
            ("@concept", (byte)PaymentConcept.Membership), ("@price", tariff)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", enrollmentId)));

        var paidOnText = await ScalarStringAsync(connection,
            "SELECT CONVERT(varchar(10), PaidAt, 23) FROM Payments WHERE EnrollmentId = @enrollmentId AND Concept = @concept",
            ("@enrollmentId", enrollmentId), ("@concept", (byte)PaymentConcept.Membership));
        Assert.False(string.IsNullOrWhiteSpace(paidOnText), "The membership payment must record its paid instant.");
        var expectedPeriod = MembershipCalendar.FirstPeriod(
            DateOnly.ParseExact(paidOnText!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        var coverageStartText = await ScalarStringAsync(connection,
            "SELECT CONVERT(varchar(10), CoverageStart, 23) FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", enrollmentId));
        var expiresOnText = await ScalarStringAsync(connection,
            "SELECT CONVERT(varchar(10), ExpiresOn, 23) FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", enrollmentId));
        Assert.Equal(expectedPeriod.CoverageStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), coverageStartText);
        Assert.Equal(expectedPeriod.ExpiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), expiresOnText);
        Assert.Equal(expectedPeriod.ReferenceDay, await ScalarIntAsync(connection,
            "SELECT ReferenceDay FROM Enrollments WHERE Id = @enrollmentId", ("@enrollmentId", enrollmentId)));
        Note($"SQL: enrollment #{enrollmentId} is Active with its activation instant, one Membership payment of the tariff and one period matching MembershipCalendar.");

        // 6. A charge POST without the antiforgery token must be rejected before any business logic runs.
        var rejected = await client.PostAsync(
            "/panel/Enrollments/Charge?enrollmentId=" + enrollmentId.ToString(CultureInfo.InvariantCulture),
            Form(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Note("HTTP: Charge POST without an anti-CSRF token -> 400.");

        // 7. The harness never writes a secret: every diagnostic line reports only booleans, identifiers, counts or
        // paths, and this proves it against the password, every antiforgery token and every cookie of the run.
        foreach (Cookie cookie in handler.CookieContainer.GetAllCookies())
        {
            if (cookie.Value.Length >= 8)
                secrets.Add(cookie.Value);
        }

        Assert.NotEmpty(written);
        var leaked = written.Any(line => secrets.Any(secret => line.Contains(secret, StringComparison.Ordinal)));
        Assert.False(leaked, "The test output must never contain a password, an antiforgery token or a cookie value.");
        Note("Secrets: the run wrote no password, antiforgery token or cookie value to the test output.");
    }

    /// <summary>
    /// Class roster, attendance and suspension flow (RF-10, RF-11, RF-12, RF-22) over the real host. This T16 smoke
    /// deliberately reuses the T12 harness variables (<c>WORKUP_T12_HTTP_CONNECTION</c>,
    /// <c>WORKUP_T12_HTTP_KEYS_PATH</c>) and the <c>WorkUpDemoT12Http*</c> database prefix instead of duplicating the
    /// isolation harness: every fact is opt-in and each run needs a fresh, absent database and keyring, so reusing the
    /// names never shares state between flows.
    /// <para>
    /// The app reads the real business clock (the simulated clock of T26 does not exist yet), so this flow never fakes
    /// time: it generates the week through the owner screen and then <b>shifts two of those generated sessions</b> —
    /// and only those synthetic fixture rows — so one is still ahead of the clock and the other is already inside its
    /// own arrival window. Nothing else the harness writes is anything but the synthetic clients, the synthetic
    /// enrollment and the class annotations it asserts.
    /// </para>
    /// </summary>
    private async Task RunClassRosterFlowAsync(string baseUrl, HarnessPlan plan, string password)
    {
        // Every diagnostic line goes through Note, so the last checkpoint can prove that no password, antiforgery token
        // or cookie value ever reached the test output.
        var written = new List<string>();
        var secrets = new HashSet<string>(StringComparer.Ordinal) { password };
        void Note(string line)
        {
            output.WriteLine(line);
            written.Add(line);
        }

        // The token is a secret: it is remembered for the output check instead of being written anywhere.
        string ExtractToken(string html)
        {
            var token = ExtractAntiforgeryToken(html);
            secrets.Add(token);
            return token;
        }

        const decimal AttendanceCharge = 4321.00m;
        const decimal WalkInCharge = 987.65m;
        const string SuspensionReason = "Paro del gremio T16";

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Login as the yoga owner: it is the demo business with class-type services and an active grid.
        var (loginStatus, loginHtml) = await GetHtmlAsync(client, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        var loginResponse = await client.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
        {
            ["Username"] = "lucia.demo",
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractToken(loginHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.True(IsDayRedirect(loginResponse), $"Login did not redirect to the owner Jornada (location '{DescribeRedirect(loginResponse)}').");
        Note("HTTP: yoga owner login accepted with antiforgery; redirected to the owner Jornada (/panel).");

        // Generation is what gives the roster real sessions; the week page also supplies the confirm token.
        var (weekStatus, weekHtml) = await GetHtmlAsync(client, "/panel/Classes/Week");
        Assert.Equal(HttpStatusCode.OK, weekStatus);
        var confirmResponse = await client.PostAsync("/panel/Classes/ConfirmWeek", Form(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(weekHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, confirmResponse.StatusCode);
        var (confirmStatus, confirmHtml) = await GetHtmlAsync(client, confirmResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, confirmStatus);
        Assert.Contains("Semana generada", confirmHtml, StringComparison.Ordinal);
        Note("HTTP: POST /panel/Classes/ConfirmWeek with no date -> 302 -> 200 with the generated week.");

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();
        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = 'estudio-yoga-lucia-demo'");

        // Two of the generated sessions are shifted, and only those two fixture rows: one stays ahead so the pre-start
        // actions and the suspension are open, the other is moved into its own arrival window so the walk-in is
        // judged after the start. The app keeps reading the real clock; the fixture is what moves.
        var sessionFutureId = await ScalarIntAsync(connection, """
            SELECT MIN(Id)
              FROM Appointments
             WHERE BusinessId = @businessId AND Kind = @kind AND GridClassId IS NOT NULL
            """, ("@businessId", businessId), ("@kind", (byte)AppointmentKind.ClassSession));
        var sessionStartedId = await ScalarIntAsync(connection, """
            SELECT MAX(Id)
              FROM Appointments
             WHERE BusinessId = @businessId AND Kind = @kind AND GridClassId IS NOT NULL
            """, ("@businessId", businessId), ("@kind", (byte)AppointmentKind.ClassSession));
        Assert.NotEqual(sessionFutureId, sessionStartedId);

        // The business clock the app reads: Argentina, fixed offset, truncated to whole seconds.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);

        // Two days ahead: far outside the 24-hour immediate-reminder window, so the owner bookings queue no email.
        var futureStarts = now.AddHours(48);
        var futureEnds = futureStarts.AddHours(1);
        // Ten minutes into its own class, and never before midnight, so the session date is always today and the
        // Jornada publishes its class link.
        var startedStarts = now.AddMinutes(-10);
        if (startedStarts.Date != now.Date)
            startedStarts = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var startedEnds = startedStarts.AddHours(1);

        Assert.Equal(1, await ExecuteAsync(connection, """
            UPDATE Appointments
               SET StartsAt = @startsAt, EndsAt = @endsAt
             WHERE Id = @id AND BusinessId = @businessId AND Kind = @kind AND GridClassId IS NOT NULL
            """,
            ("@startsAt", futureStarts), ("@endsAt", futureEnds), ("@id", sessionFutureId),
            ("@businessId", businessId), ("@kind", (byte)AppointmentKind.ClassSession)));
        Assert.Equal(1, await ExecuteAsync(connection, """
            UPDATE Appointments
               SET StartsAt = @startsAt, EndsAt = @endsAt
             WHERE Id = @id AND BusinessId = @businessId AND Kind = @kind AND GridClassId IS NOT NULL
            """,
            ("@startsAt", startedStarts), ("@endsAt", startedEnds), ("@id", sessionStartedId),
            ("@businessId", businessId), ("@kind", (byte)AppointmentKind.ClassSession)));

        var futureCapacity = await ScalarIntAsync(connection,
            "SELECT MaxCapacity FROM Appointments WHERE Id = @id", ("@id", sessionFutureId));
        var futureGridClassId = await ScalarIntAsync(connection,
            "SELECT GridClassId FROM Appointments WHERE Id = @id", ("@id", sessionFutureId));
        var futureServiceName = await ScalarStringAsync(connection, """
            SELECT s.Name
              FROM Appointments AS a
              JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
             WHERE a.Id = @id AND a.BusinessId = @businessId
            """, ("@id", sessionFutureId), ("@businessId", businessId));
        Assert.False(string.IsNullOrWhiteSpace(futureServiceName), "The shifted session must resolve its service name.");

        // One synthetic fixed enrollment with an active period that covers the class date, so the roster shows real
        // coverage and the flow can prove that cancelling a date and suspending a class touch no enrollment or payment.
        var today = DateOnly.FromDateTime(now);
        var fixture = await SeedClassRosterFixtureAsync(connection, businessId, sessionFutureId, futureGridClassId, today, now);
        Note($"SQL: shifted session #{sessionFutureId} two days ahead and session #{sessionStartedId} into its window; seeded the fixed enrollment #{fixture.EnrollmentId} of client #{fixture.ClientId}.");

        var dropInOne = await CreateSyntheticClientAsync(client, "T16DropInOne", ExtractToken);
        var dropInTwo = await CreateSyntheticClientAsync(client, "T16DropInTwo", ExtractToken);
        var walkInClient = await CreateSyntheticClientAsync(client, "T16WalkIn", ExtractToken);
        Note($"HTTP: synthetic clients #{dropInOne.ClientId}, #{dropInTwo.ClientId} and #{walkInClient.ClientId} created through the owner form.");

        // The drop-in roster lists the shifted future session and publishes the class link this flow follows.
        var (futureRosterStatus, futureRosterHtml) = await GetHtmlAsync(client, $"/panel/Classes/DropIns?session={sessionFutureId}");
        Assert.Equal(HttpStatusCode.OK, futureRosterStatus);
        Assert.Contains($"drop-in-client-{sessionFutureId}", futureRosterHtml, StringComparison.Ordinal);
        var futureDetailLink = ExtractClassDetailLink(futureRosterHtml, sessionFutureId);
        Note($"HTTP: GET /panel/Classes/DropIns -> 200 with the booking form and the class link for session #{sessionFutureId}.");

        async Task BookDropInAsync(int clientId)
        {
            var response = await client.PostAsync("/panel/Classes/BookDropIn", Form(new Dictionary<string, string>
            {
                ["appointmentId"] = sessionFutureId.ToString(CultureInfo.InvariantCulture),
                ["clientId"] = clientId.ToString(CultureInfo.InvariantCulture),
                ["__RequestVerificationToken"] = ExtractToken(futureRosterHtml)
            }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        await BookDropInAsync(dropInOne.ClientId);
        await BookDropInAsync(dropInTwo.ClientId);
        var (afterBooksStatus, afterBooksHtml) = await GetHtmlAsync(client, $"/panel/Classes/DropIns?session={sessionFutureId}");
        Assert.Equal(HttpStatusCode.OK, afterBooksStatus);
        Assert.Contains("Synthetic T16DropInOne", afterBooksHtml, StringComparison.Ordinal);
        Assert.Contains("Synthetic T16DropInTwo", afterBooksHtml, StringComparison.Ordinal);
        Note($"HTTP: two sueltas booked into session #{sessionFutureId} -> 302 -> 200 with both annotations listed.");

        // The detail page is reached through the link the roster published, and it renders the roster, the occupancy
        // resolved by the single capacity rule and the persisted end of the shifted session.
        var (detailStatus, detailHtml) = await GetHtmlAsync(client, futureDetailLink);
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Contains(futureServiceName!, detailHtml, StringComparison.Ordinal);
        Assert.Contains("Synthetic T16Fixed", detailHtml, StringComparison.Ordinal);
        Assert.Contains("Synthetic T16DropInOne", detailHtml, StringComparison.Ordinal);
        Assert.Contains("Synthetic T16DropInTwo", detailHtml, StringComparison.Ordinal);
        Assert.Contains("Fija", detailHtml, StringComparison.Ordinal);
        Assert.Contains("Suelta", detailHtml, StringComparison.Ordinal);
        // Three non-cancelled annotations hold a place before any action: the fixed one and both sueltas.
        Assert.Contains($"3 de {futureCapacity} lugares ocupados", detailHtml, StringComparison.Ordinal);
        Assert.Contains($"Hasta {Formatting.Time(TimeOnly.FromDateTime(futureEnds))}", detailHtml, StringComparison.Ordinal);
        // The arrival window is closed two days before the class, so the page never offers the walk-in there.
        Assert.DoesNotContain("owner-walk-in-form", detailHtml, StringComparison.Ordinal);
        Note($"HTTP: GET {futureDetailLink} -> 200 with the roster, '3 de {futureCapacity} lugares ocupados' and the stored end {Formatting.Time(TimeOnly.FromDateTime(futureEnds))}.");

        var detailToken = ExtractToken(detailHtml);
        var fixedBookingId = fixture.FixedBookingId;
        var dropInOneBookingId = await ScalarIntAsync(connection, """
            SELECT TOP 1 Id
              FROM Bookings
             WHERE BusinessId = @businessId AND AppointmentId = @appointmentId AND ClientId = @clientId
               AND Modality = @modality
             ORDER BY Id
            """,
            ("@businessId", businessId), ("@appointmentId", sessionFutureId), ("@clientId", dropInOne.ClientId),
            ("@modality", (byte)BookingModality.DropIn));
        var dropInTwoBookingId = await ScalarIntAsync(connection, """
            SELECT TOP 1 Id
              FROM Bookings
             WHERE BusinessId = @businessId AND AppointmentId = @appointmentId AND ClientId = @clientId
               AND Modality = @modality
             ORDER BY Id
            """,
            ("@businessId", businessId), ("@appointmentId", sessionFutureId), ("@clientId", dropInTwo.ClientId),
            ("@modality", (byte)BookingModality.DropIn));

        // Attendance with the optional drop-in charge and observation, one of the three antiforgery-protected POSTs.
        var attendResponse = await client.PostAsync($"/panel/Classes/Attend/{dropInOneBookingId}", Form(new Dictionary<string, string>
        {
            ["appointmentId"] = sessionFutureId.ToString(CultureInfo.InvariantCulture),
            ["amount"] = "4321.00",
            ["nextVisitNote"] = "T16 HTTP attendance note",
            ["__RequestVerificationToken"] = detailToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, attendResponse.StatusCode);
        var (afterAttendStatus, afterAttendHtml) = await GetHtmlAsync(client, attendResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterAttendStatus);
        Assert.Contains("Asistencia registrada.", afterAttendHtml, StringComparison.Ordinal);
        Assert.Contains(Formatting.Money(AttendanceCharge), afterAttendHtml, StringComparison.Ordinal);
        Note($"HTTP: attendance of drop-in booking #{dropInOneBookingId} with a {Formatting.Money(AttendanceCharge)} charge -> 302 -> 200 with its flash.");

        // The absence of a fixed student is a fact and never depends on coverage.
        var absentResponse = await client.PostAsync($"/panel/Classes/MarkAbsent/{fixedBookingId}", Form(new Dictionary<string, string>
        {
            ["appointmentId"] = sessionFutureId.ToString(CultureInfo.InvariantCulture),
            ["__RequestVerificationToken"] = detailToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, absentResponse.StatusCode);
        var (afterAbsentStatus, afterAbsentHtml) = await GetHtmlAsync(client, absentResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterAbsentStatus);
        Assert.Contains("Ausencia registrada.", afterAbsentHtml, StringComparison.Ordinal);
        Note($"HTTP: absence of fixed booking #{fixedBookingId} -> 302 -> 200 with its flash.");

        // The cancellation of an already recorded annotation: the attendance fact stops counting and the place is
        // released, while the enrollment and the charge stay exactly as they were.
        var cancelResponse = await client.PostAsync($"/panel/Classes/CancelBooking/{dropInOneBookingId}", Form(new Dictionary<string, string>
        {
            ["appointmentId"] = sessionFutureId.ToString(CultureInfo.InvariantCulture),
            ["__RequestVerificationToken"] = detailToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, cancelResponse.StatusCode);
        var (afterCancelStatus, afterCancelHtml) = await GetHtmlAsync(client, cancelResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterCancelStatus);
        Assert.Contains("cancelada: el lugar", afterCancelHtml, StringComparison.Ordinal);
        Note($"HTTP: already recorded booking #{dropInOneBookingId} cancelled before the start -> 302 -> 200 with the released-place flash.");

        // The class link of the session that already started is published by the Jornada, and its arrival window is
        // what opens the walk-in form.
        var (dayStatus, dayHtml) = await GetHtmlAsync(client, "/panel/Day");
        Assert.Equal(HttpStatusCode.OK, dayStatus);
        var startedDetailLink = ExtractClassDetailLink(dayHtml, sessionStartedId);
        Note($"HTTP: GET /panel/Day -> 200 with the class link of the in-progress session #{sessionStartedId}.");
        var (startedDetailStatus, startedDetailHtml) = await GetHtmlAsync(client, startedDetailLink);
        Assert.Equal(HttpStatusCode.OK, startedDetailStatus);
        Assert.Contains("owner-walk-in-form", startedDetailHtml, StringComparison.Ordinal);
        Assert.Contains($"Hasta {Formatting.Time(TimeOnly.FromDateTime(startedEnds))}", startedDetailHtml, StringComparison.Ordinal);

        // No booking of the future session may have queued an email: the 48-hour shift is outside the immediate
        // reminder window, so the outbox is empty before the suspension.
        var notificationsBeforeWalkIn = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", businessId));
        Assert.Equal(0, notificationsBeforeWalkIn);

        var walkInResponse = await client.PostAsync(
            $"/panel/Classes/RegisterWalkIn?appointmentId={sessionStartedId.ToString(CultureInfo.InvariantCulture)}",
            Form(new Dictionary<string, string>
            {
                ["clientId"] = walkInClient.ClientId.ToString(CultureInfo.InvariantCulture),
                ["amount"] = "987.65",
                ["nextVisitNote"] = "T16 HTTP walk-in note",
                ["__RequestVerificationToken"] = ExtractToken(startedDetailHtml)
            }));
        Assert.Equal(HttpStatusCode.Redirect, walkInResponse.StatusCode);
        var (afterWalkInStatus, afterWalkInHtml) = await GetHtmlAsync(client, walkInResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterWalkInStatus);
        Assert.Contains("Llegada registrada.", afterWalkInHtml, StringComparison.Ordinal);
        Assert.Contains(Formatting.Money(WalkInCharge), afterWalkInHtml, StringComparison.Ordinal);
        var notificationsAfterWalkIn = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", businessId));
        Assert.Equal(notificationsBeforeWalkIn, notificationsAfterWalkIn);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId AND Recipient = @email",
            ("@businessId", businessId), ("@email", walkInClient.Email)));
        Note($"HTTP/SQL: at-arrival walk-in of client #{walkInClient.ClientId} inside the stored window with a {Formatting.Money(WalkInCharge)} charge -> 302 -> 200 with its flash, and the outbox stayed empty.");

        // A suspension notice that a retry would leave is pre-seeded for the drop-in that stays open, so the real
        // suspension exercises the outbox deduplication: the seeded key is never duplicated and the other student
        // still gets her notice.
        await InsertSuspensionNotificationAsync(connection, businessId, dropInTwo.Email,
            SuspensionDedupKey(sessionFutureId, dropInTwoBookingId), now);

        // A POST without the antiforgery token must be rejected before any business logic runs.
        var rejected = await client.PostAsync($"/panel/Classes/Suspend/{sessionFutureId}", Form(new Dictionary<string, string>
        {
            ["reason"] = SuspensionReason
        }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Note("HTTP: Suspend POST without an anti-CSRF token -> 400.");

        var suspendResponse = await client.PostAsync($"/panel/Classes/Suspend/{sessionFutureId}", Form(new Dictionary<string, string>
        {
            ["reason"] = SuspensionReason,
            ["__RequestVerificationToken"] = detailToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, suspendResponse.StatusCode);
        var (afterSuspendStatus, afterSuspendHtml) = await GetHtmlAsync(client, suspendResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterSuspendStatus);
        Assert.Contains("Clase suspendida.", afterSuspendHtml, StringComparison.Ordinal);
        Assert.Contains("1 aviso", afterSuspendHtml, StringComparison.Ordinal);
        Note($"HTTP: suspension of session #{sessionFutureId} with its mandatory reason -> 302 -> 200 with the flash of one queued notice (the seeded one was deduplicated).");

        // SQL verification runs after every HTTP checkpoint so one run reports the whole behavior.
        Assert.Equal((int)AppointmentStatus.Suspended, await ScalarIntAsync(connection,
            "SELECT Status FROM Appointments WHERE Id = @id", ("@id", sessionFutureId)));
        Assert.Equal(SuspensionReason, await ScalarStringAsync(connection,
            "SELECT SuspensionReason FROM Appointments WHERE Id = @id", ("@id", sessionFutureId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE Id = @id AND SuspendedAt IS NOT NULL", ("@id", sessionFutureId)));
        Assert.Equal(futureStarts.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            await ScalarStringAsync(connection, "SELECT CONVERT(varchar(19), StartsAt, 120) FROM Appointments WHERE Id = @id", ("@id", sessionFutureId)));
        Assert.Equal(futureEnds.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            await ScalarStringAsync(connection, "SELECT CONVERT(varchar(19), EndsAt, 120) FROM Appointments WHERE Id = @id", ("@id", sessionFutureId)));
        Assert.Equal(startedStarts.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            await ScalarStringAsync(connection, "SELECT CONVERT(varchar(19), StartsAt, 120) FROM Appointments WHERE Id = @id", ("@id", sessionStartedId)));
        Assert.Equal(startedEnds.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            await ScalarStringAsync(connection, "SELECT CONVERT(varchar(19), EndsAt, 120) FROM Appointments WHERE Id = @id", ("@id", sessionStartedId)));

        Assert.Equal((int)BookingStatus.Absent, await ScalarIntAsync(connection,
            "SELECT Status FROM Bookings WHERE Id = @id", ("@id", fixedBookingId)));
        Assert.Equal((int)AbsenceSource.Owner, await ScalarIntAsync(connection,
            "SELECT AbsenceSource FROM Bookings WHERE Id = @id", ("@id", fixedBookingId)));
        Assert.Equal((int)BookingStatus.Cancelled, await ScalarIntAsync(connection,
            "SELECT Status FROM Bookings WHERE Id = @id", ("@id", dropInOneBookingId)));
        Assert.Equal((int)CancelledBy.Owner, await ScalarIntAsync(connection,
            "SELECT CancelledBy FROM Bookings WHERE Id = @id", ("@id", dropInOneBookingId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE Id = @id AND CancelledAt IS NOT NULL", ("@id", dropInOneBookingId)));
        Assert.Equal((int)BookingStatus.Pending, await ScalarIntAsync(connection,
            "SELECT Status FROM Bookings WHERE Id = @id", ("@id", dropInTwoBookingId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM ClientNotes WHERE AppointmentId = @id AND Text = @text",
            ("@id", sessionFutureId), ("@text", "T16 HTTP attendance note")));
        Note($"SQL: booking statuses asserted (fixed #{fixedBookingId} Ausente, suelta #{dropInOneBookingId} Cancelada by the owner, suelta #{dropInTwoBookingId} Pendiente) and the attendance note landed.");

        var walkInBookingId = await ScalarIntAsync(connection, """
            SELECT TOP 1 Id
              FROM Bookings
             WHERE BusinessId = @businessId AND AppointmentId = @appointmentId AND ClientId = @clientId
               AND Modality = @modality
             ORDER BY Id
            """,
            ("@businessId", businessId), ("@appointmentId", sessionStartedId), ("@clientId", walkInClient.ClientId),
            ("@modality", (byte)BookingModality.DropIn));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId",
            ("@businessId", businessId), ("@appointmentId", sessionStartedId)));
        Assert.Equal((int)BookingStatus.Attended, await ScalarIntAsync(connection,
            "SELECT Status FROM Bookings WHERE Id = @id", ("@id", walkInBookingId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE Id = @id AND AttendanceRecordedAt IS NOT NULL AND AbsenceSource IS NULL",
            ("@id", walkInBookingId)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Payments
             WHERE BusinessId = @businessId AND BookingId = @bookingId AND Concept = @concept AND Amount = @amount
            """,
            ("@businessId", businessId), ("@bookingId", dropInOneBookingId),
            ("@concept", (byte)PaymentConcept.DropInClass), ("@amount", AttendanceCharge)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Payments
             WHERE BusinessId = @businessId AND BookingId = @bookingId AND Concept = @concept AND Amount = @amount
            """,
            ("@businessId", businessId), ("@bookingId", walkInBookingId),
            ("@concept", (byte)PaymentConcept.DropInClass), ("@amount", WalkInCharge)));
        Note($"SQL: PaymentConcept.DropInClass asserted for the attendance charge of booking #{dropInOneBookingId} and for the walk-in charge of booking #{walkInBookingId}.");

        // The date actions write the class and its annotations only: the enrollment, its fixed assignment, its paid
        // period and every payment survive untouched, and nothing is refunded or annulled (D-24).
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Enrollments WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal((int)EnrollmentStatus.Active, await ScalarIntAsync(connection,
            "SELECT Status FROM Enrollments WHERE Id = @id", ("@id", fixture.EnrollmentId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Enrollments WHERE Id = @id AND ActivatedAt IS NOT NULL", ("@id", fixture.EnrollmentId)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM MembershipPeriods
             WHERE Id = @periodId AND EnrollmentId = @enrollmentId
               AND CONVERT(varchar(10), CoverageStart, 23) = @coverageStart
               AND CONVERT(varchar(10), ExpiresOn, 23) = @expiresOn
            """,
            ("@periodId", fixture.PeriodId), ("@enrollmentId", fixture.EnrollmentId),
            ("@coverageStart", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("@expiresOn", today.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE EnrollmentId = @id AND ValidTo IS NULL",
            ("@id", fixture.EnrollmentId)));
        Assert.Equal(fixture.EnrollmentId, await ScalarIntAsync(connection,
            "SELECT EnrollmentId FROM Bookings WHERE Id = @id", ("@id", fixedBookingId)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Payments
             WHERE Id = @id AND EnrollmentId = @enrollmentId AND Concept = @concept AND Amount = @amount
               AND NOT EXISTS (SELECT 1 FROM PaymentAnnulments AS pa WHERE pa.PaymentId = Payments.Id)
            """,
            ("@id", fixture.MembershipPaymentId), ("@enrollmentId", fixture.EnrollmentId),
            ("@concept", (byte)PaymentConcept.Membership), ("@amount", MembershipFixtureAmount)));
        Assert.Equal(3, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM PaymentAnnulments"));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM Refunds"));
        Note("SQL: no unwanted enrollment or payment mutation: one Active enrollment, its period and fixed assignment intact, its membership payment and both drop-in charges untouched, zero annulments and zero refunds.");

        Assert.Equal(2, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal(2, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId AND Type = @type",
            ("@businessId", businessId), ("@type", (byte)NotificationType.ClassSuspended)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId AND DedupKey = @key AND Recipient = @email",
            ("@businessId", businessId), ("@key", SuspensionDedupKey(sessionFutureId, fixedBookingId)),
            ("@email", fixture.Email)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId AND DedupKey = @key",
            ("@businessId", businessId), ("@key", SuspensionDedupKey(sessionFutureId, dropInTwoBookingId))));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId AND DedupKey = @key",
            ("@businessId", businessId), ("@key", SuspensionDedupKey(sessionFutureId, dropInOneBookingId))));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId AND Type = @type",
            ("@businessId", businessId), ("@type", (byte)NotificationType.DropInReminder)));
        Note($"SQL: NotificationType.ClassSuspended asserted with the key suspension:appointment:{{id}}:booking:{{id}} — one real notice for the absent student, the seeded notice deduplicated once, none for the cancelled annotation and zero emails for the walk-in.");

        // The harness never writes a secret: every diagnostic line reports only booleans, identifiers, counts or paths,
        // and this proves it against the password, every antiforgery token and every cookie of the run. Short cookie
        // values are skipped because they are not secrets and could match any line by accident.
        foreach (Cookie cookie in handler.CookieContainer.GetAllCookies())
        {
            if (cookie.Value.Length >= 8)
                secrets.Add(cookie.Value);
        }

        Assert.NotEmpty(written);
        var leaked = written.Any(line => secrets.Any(secret => line.Contains(secret, StringComparison.Ordinal)));
        Assert.False(leaked, "The test output must never contain a password, an antiforgery token or a cookie value.");
        Note("Secrets: the run wrote no password, antiforgery token or cookie value to the test output.");
    }

    /// <summary>
    /// Guided fixed-schedule change flow (RF-21) over the real host. This T17 smoke deliberately reuses the T12
    /// harness variables (<c>WORKUP_T12_HTTP_CONNECTION</c>, <c>WORKUP_T12_HTTP_KEYS_PATH</c>) and the
    /// <c>WorkUpDemoT12Http*</c> database prefix instead of duplicating the isolation harness: every fact is opt-in and
    /// each run needs a fresh, absent database and keyring, so reusing the names never shares state between flows.
    /// <para>
    /// The app reads the real business clock (the simulated clock of T26 does not exist yet), so this flow never fakes
    /// time: it confirms one explicit week — the one that starts next Monday — and reads the generated sessions back
    /// from SQL, so the effective dates it posts always match the grid that really exists. The membership is seeded as
    /// synthetic rows on one of those generated sessions, exactly like the T16 flow seeds its fixed enrollment.
    /// </para>
    /// </summary>
    private async Task RunFixedScheduleChangeFlowAsync(string baseUrl, HarnessPlan plan, string password)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Login as the yoga owner: it is the demo business with class-type services and an active grid.
        var (loginStatus, loginHtml) = await GetHtmlAsync(client, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        var loginResponse = await client.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
        {
            ["Username"] = "lucia.demo",
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.True(IsDayRedirect(loginResponse), $"Login did not redirect to the owner Jornada (location '{DescribeRedirect(loginResponse)}').");
        output.WriteLine("HTTP: yoga owner login accepted with antiforgery; redirected to the owner Jornada (/panel).");

        // The app clock is Argentina; the same date the service reads is computed here, so the seeded window and the
        // posted effective dates are decided from one instant.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
        var today = DateOnly.FromDateTime(now);

        // One explicit week: the change refuses any effective date that overlaps the generated sessions of either
        // affected grid class, so the guard needs sessions that really exist, and it needs them on a known Monday.
        var nextMonday = WeekCalendar.StartOfWeek(today).AddDays(7);
        var nextMondayText = nextMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (weekStatus, weekHtml) = await GetHtmlAsync(client, "/panel/Classes/Week?date=" + nextMondayText);
        Assert.Equal(HttpStatusCode.OK, weekStatus);
        var confirmResponse = await client.PostAsync("/panel/Classes/ConfirmWeek", Form(new Dictionary<string, string>
        {
            ["date"] = nextMondayText,
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(weekHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, confirmResponse.StatusCode);
        var (confirmStatus, confirmHtml) = await GetHtmlAsync(client, confirmResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, confirmStatus);
        Assert.Contains("Semana generada", confirmHtml, StringComparison.Ordinal);
        output.WriteLine($"HTTP: POST /panel/Classes/ConfirmWeek for the week of {nextMondayText} -> 302 -> 200 with the generated week.");

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();
        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = 'estudio-yoga-lucia-demo'");
        var fromDate = nextMonday.ToDateTime(TimeOnly.MinValue);

        // The two grid classes that move: two active class-type slots of the demo that both have a generated session
        // in the week just confirmed, so the guard has a real range to refuse and the seat is a real one.
        var currentGridClassId = await ScalarIntAsync(connection, """
            SELECT MIN(a.GridClassId)
              FROM Appointments AS a
             WHERE a.BusinessId = @businessId AND a.GridClassId IS NOT NULL AND a.StartsAt >= @fromDate
            """, ("@businessId", businessId), ("@fromDate", fromDate));
        var newGridClassId = await ScalarIntAsync(connection, """
            SELECT MAX(a.GridClassId)
              FROM Appointments AS a
             WHERE a.BusinessId = @businessId AND a.GridClassId IS NOT NULL AND a.StartsAt >= @fromDate
            """, ("@businessId", businessId), ("@fromDate", fromDate));
        Assert.NotEqual(currentGridClassId, newGridClassId);
        var sessionId = await ScalarIntAsync(connection, """
            SELECT MIN(a.Id)
              FROM Appointments AS a
             WHERE a.BusinessId = @businessId AND a.GridClassId = @gridClassId AND a.StartsAt >= @fromDate
            """, ("@businessId", businessId), ("@gridClassId", currentGridClassId), ("@fromDate", fromDate));
        Assert.True(sessionId > 0, "The confirmed week must persist a session of the current grid class.");
        var sessionStarts = await ScalarStringAsync(connection,
            "SELECT CONVERT(varchar(19), StartsAt, 120) FROM Appointments WHERE Id = @id", ("@id", sessionId));
        Assert.False(string.IsNullOrWhiteSpace(sessionStarts), "The generated session must record its start.");

        // One synthetic Active membership whose fixed window starts today — so the change refuses an effective date
        // equal to it — plus its paid period and a fixed annotation on the generated session the change must never
        // move. The helper is the one the T16 flow already uses.
        var fixture = await SeedClassRosterFixtureAsync(connection, businessId, sessionId, currentGridClassId, today, now);
        output.WriteLine($"SQL: seeded Active enrollment #{fixture.EnrollmentId} with its fixed window on grid class #{currentGridClassId} from {today:yyyy-MM-dd} and a fixed annotation on session #{sessionId}.");

        // The last session the guard will see on or after the effective date, read back from SQL so the posted date and
        // the asked-for first usable date always match the grid that really exists.
        var lastGeneratedText = await ScalarStringAsync(connection, """
            SELECT CONVERT(varchar(10), MAX(a.StartsAt), 23)
              FROM Appointments AS a
             WHERE a.BusinessId = @businessId
               AND a.GridClassId IN (@currentGridClassId, @newGridClassId)
               AND a.StartsAt >= @fromDate
            """, ("@businessId", businessId), ("@currentGridClassId", currentGridClassId),
            ("@newGridClassId", newGridClassId), ("@fromDate", fromDate));
        Assert.False(string.IsNullOrWhiteSpace(lastGeneratedText), "The affected grid classes must have generated sessions.");
        var lastGenerated = DateOnly.ParseExact(lastGeneratedText!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.True(lastGenerated >= nextMonday, "The generated week must lie ahead of the confirmed Monday.");
        var effectiveFrom = lastGenerated.AddDays(1);
        var effectiveText = effectiveFrom.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        // The Abonos list is where the owner starts: the row publishes the guided form, and the flow follows that link
        // instead of guessing the route.
        var (listStatus, listHtml) = await GetHtmlAsync(client, "/panel/Enrollments");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var changeLink = ExtractChangeScheduleLink(listHtml, fixture.EnrollmentId);
        var (formStatus, formHtml) = await GetHtmlAsync(client, changeLink);
        Assert.Equal(HttpStatusCode.OK, formStatus);
        Assert.Contains("Cambiar horario", formHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"CurrentGridClassId\"", formHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"NewGridClassId\"", formHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"EffectiveFrom\"", formHtml, StringComparison.Ordinal);
        Assert.Contains($"value=\"{fixture.EnrollmentId}\"", formHtml, StringComparison.Ordinal);
        Assert.Contains($"value=\"{currentGridClassId}\"", formHtml, StringComparison.Ordinal);
        Assert.Contains($"value=\"{newGridClassId}\"", formHtml, StringComparison.Ordinal);
        var formToken = ExtractAntiforgeryToken(formHtml);
        output.WriteLine($"HTTP: GET /panel/Enrollments -> 200 with the change link; the linked form offers grid classes #{currentGridClassId} and #{newGridClassId} and the effective date.");

        // A POST without the antiforgery token must be rejected before any business logic runs.
        var rejected = await client.PostAsync("/panel/Enrollments/ChangeSchedule",
            Form(ChangeScheduleFields(fixture.EnrollmentId, currentGridClassId, newGridClassId, today)));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        output.WriteLine("HTTP: ChangeSchedule POST without an anti-CSRF token -> 400.");

        // The window starts today, so the day before it would end before it began: the service refuses the effective
        // date equal to the window start, names the first usable day and writes nothing.
        var tomorrowText = today.AddDays(1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var tooEarly = await client.PostAsync("/panel/Enrollments/ChangeSchedule",
            Form(ChangeScheduleFields(fixture.EnrollmentId, currentGridClassId, newGridClassId, today, formToken)));
        Assert.Equal(HttpStatusCode.OK, tooEarly.StatusCode);
        var tooEarlyHtml = await tooEarly.Content.ReadAsStringAsync();
        Assert.Contains("fecha desde el " + tomorrowText, tooEarlyHtml, StringComparison.Ordinal);
        await AssertScheduleChangeWroteNothingAsync(connection, fixture.EnrollmentId);
        output.WriteLine($"HTTP/SQL: POST with the effective date equal to the window start -> 200 naming {tomorrowText} and no assignment was written.");

        // A date inside the generated range of either affected class is refused with the first usable day: the change
        // never migrates the sessions that already exist, and it leaves the seeded window untouched.
        var overlapping = await client.PostAsync("/panel/Enrollments/ChangeSchedule",
            Form(ChangeScheduleFields(fixture.EnrollmentId, currentGridClassId, newGridClassId, nextMonday, formToken)));
        Assert.Equal(HttpStatusCode.OK, overlapping.StatusCode);
        var overlappingHtml = await overlapping.Content.ReadAsStringAsync();
        Assert.Contains("Ya hay clases generadas", overlappingHtml, StringComparison.Ordinal);
        Assert.Contains("fecha desde el " + effectiveText, overlappingHtml, StringComparison.Ordinal);
        await AssertScheduleChangeWroteNothingAsync(connection, fixture.EnrollmentId);
        output.WriteLine($"HTTP/SQL: POST with a date overlapping the generated classes -> 200 naming {effectiveText} and no assignment was written.");

        // The date the service just named is the one that finally commits: the old window closes the day before and
        // the new one opens on it.
        var success = await client.PostAsync("/panel/Enrollments/ChangeSchedule",
            Form(ChangeScheduleFields(fixture.EnrollmentId, currentGridClassId, newGridClassId, effectiveFrom, formToken)));
        Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
        var (afterStatus, afterHtml) = await GetHtmlAsync(client, success.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterStatus);
        Assert.Contains("Horario cambiado desde el " + Formatting.ShortDate(effectiveFrom), afterHtml, StringComparison.Ordinal);
        Assert.Contains("El horario anterior queda vigente hasta el " + Formatting.ShortDate(effectiveFrom.AddDays(-1)), afterHtml, StringComparison.Ordinal);
        output.WriteLine($"HTTP: POST the free date {effectiveText} -> 302 -> 200 with the change flash.");

        // SQL verification runs after every HTTP checkpoint so one run reports the whole behavior. The old window
        // closed the day before and the new one opened on the effective date; no row was deleted.
        var closedWindowValidFrom = await ScalarStringAsync(connection,
            "SELECT CONVERT(varchar(10), ValidFrom, 23) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId AND GridClassId = @gridClassId",
            ("@enrollmentId", fixture.EnrollmentId), ("@gridClassId", currentGridClassId));
        var closedWindowValidTo = await ScalarStringAsync(connection,
            "SELECT CONVERT(varchar(10), ValidTo, 23) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId AND GridClassId = @gridClassId",
            ("@enrollmentId", fixture.EnrollmentId), ("@gridClassId", currentGridClassId));
        Assert.False(string.IsNullOrWhiteSpace(closedWindowValidFrom), "The replaced fixed window must keep its start.");
        Assert.NotNull(closedWindowValidTo);
        Assert.Equal(today, DateOnly.ParseExact(closedWindowValidFrom!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.Equal(effectiveFrom.AddDays(-1), DateOnly.ParseExact(closedWindowValidTo!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM EnrollmentFixedClasses
             WHERE EnrollmentId = @enrollmentId AND GridClassId = @gridClassId
               AND CONVERT(varchar(10), ValidFrom, 23) = @validFrom AND ValidTo IS NULL
            """,
            ("@enrollmentId", fixture.EnrollmentId), ("@gridClassId", newGridClassId),
            ("@validFrom", effectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        Assert.Equal(2, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", fixture.EnrollmentId)));
        output.WriteLine($"SQL: the window of grid class #{currentGridClassId} closed on {effectiveFrom.AddDays(-1):yyyy-MM-dd} and the window of #{newGridClassId} opened on {effectiveFrom:yyyy-MM-dd}; two rows and none deleted.");

        // Only the weekly slot moved (RF-29): status, agreed price, reference day, activation, payment and period are
        // exactly the seeded ones, and the generated annotation still points at the session it was seeded on.
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Enrollments
             WHERE Id = @enrollmentId AND Status = @status AND AgreedPrice = @agreedPrice AND ReferenceDay = @referenceDay
               AND ActivatedAt IS NOT NULL AND AnnulledAt IS NULL AND TerminatedAt IS NULL
            """,
            ("@enrollmentId", fixture.EnrollmentId), ("@status", (byte)EnrollmentStatus.Active),
            ("@agreedPrice", MembershipFixtureAmount), ("@referenceDay", today.Day)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Payments
             WHERE Id = @paymentId AND EnrollmentId = @enrollmentId AND Concept = @concept AND Amount = @amount
               AND NOT EXISTS (SELECT 1 FROM PaymentAnnulments AS pa WHERE pa.PaymentId = Payments.Id)
            """,
            ("@paymentId", fixture.MembershipPaymentId), ("@enrollmentId", fixture.EnrollmentId),
            ("@concept", (byte)PaymentConcept.Membership), ("@amount", MembershipFixtureAmount)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM MembershipPeriods
             WHERE Id = @periodId AND EnrollmentId = @enrollmentId
               AND CONVERT(varchar(10), CoverageStart, 23) = @coverageStart
               AND CONVERT(varchar(10), ExpiresOn, 23) = @expiresOn
            """,
            ("@periodId", fixture.PeriodId), ("@enrollmentId", fixture.EnrollmentId),
            ("@coverageStart", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("@expiresOn", today.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*)
              FROM Bookings
             WHERE Id = @bookingId AND EnrollmentId = @enrollmentId AND AppointmentId = @appointmentId
               AND Modality = @modality AND Status = @status
            """,
            ("@bookingId", fixture.FixedBookingId), ("@enrollmentId", fixture.EnrollmentId),
            ("@appointmentId", sessionId), ("@modality", (byte)BookingModality.Fixed),
            ("@status", (byte)BookingStatus.Pending)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND EnrollmentId = @enrollmentId",
            ("@businessId", businessId), ("@enrollmentId", fixture.EnrollmentId)));
        Assert.Equal(sessionStarts, await ScalarStringAsync(connection,
            "SELECT CONVERT(varchar(19), StartsAt, 120) FROM Appointments WHERE Id = @id", ("@id", sessionId)));
        output.WriteLine($"SQL: the Active enrollment, its price, reference day, payment and period are the seeded ones, and annotation #{fixture.FixedBookingId} still points at session #{sessionId}; the change moved no booking.");
    }

    /// <summary>
    /// The payment ledger flow (RF-11, RF-28, D-20) over the real host, opt-in like the others. It drives the late
    /// charge of an already attended drop-in from the class detail, then the two manual corrections of the Cobros
    /// history: the annulment with the authenticated owner as responsible -- financial only, so the coverage, the
    /// attendance, the notes and the outbox stay untouched -- and the partial refund capped by the amount charged,
    /// with the two corrections never coexisting. The T16 fixture seeds the membership baseline that must survive
    /// untouched and the class session; the attended drop-in without a charge is seeded beside it. Every fact is
    /// scoped by the booking or the payment, so an unexpectedly seeded row cannot change the answer.
    /// </summary>
    private async Task RunPaymentLedgerFlowAsync(string baseUrl, HarnessPlan plan, string password)
    {
        // Every diagnostic line goes through Note, so the last checkpoint can prove that no password, antiforgery
        // token or cookie value ever reached the test output.
        var written = new List<string>();
        var secrets = new HashSet<string>(StringComparer.Ordinal) { password };
        void Note(string line)
        {
            output.WriteLine(line);
            written.Add(line);
        }

        // The token is a secret: it is remembered for the output check instead of being written anywhere.
        string ExtractToken(string html)
        {
            var token = ExtractAntiforgeryToken(html);
            secrets.Add(token);
            return token;
        }

        const decimal OriginalCharge = 4000.00m;
        const decimal CorrectedCharge = 4200.00m;
        const decimal PartialRefund = 1500.00m;
        const decimal ExcessRefund = 3000.00m;
        const string AnnulReason = "Cobro mal registrado T18";
        const string RefundReason = "Devolución acordada T18";

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Login as the yoga owner: it is the demo business with class-type services and an active grid.
        var (loginStatus, loginHtml) = await GetHtmlAsync(client, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        var loginResponse = await client.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
        {
            ["Username"] = "lucia.demo",
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractToken(loginHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.True(IsDayRedirect(loginResponse), $"Login did not redirect to the owner Jornada (location '{DescribeRedirect(loginResponse)}').");
        Note("HTTP: yoga owner login accepted with antiforgery; redirected to the owner Jornada (/panel).");

        // Generation is what gives the flow real class sessions; the week page also supplies the confirm token.
        var (weekStatus, weekHtml) = await GetHtmlAsync(client, "/panel/Classes/Week");
        Assert.Equal(HttpStatusCode.OK, weekStatus);
        var confirmResponse = await client.PostAsync("/panel/Classes/ConfirmWeek", Form(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(weekHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, confirmResponse.StatusCode);
        var (confirmStatus, confirmHtml) = await GetHtmlAsync(client, confirmResponse.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, confirmStatus);
        Assert.Contains("Semana generada", confirmHtml, StringComparison.Ordinal);
        Note("HTTP: POST /panel/Classes/ConfirmWeek with no date -> 302 -> 200 with the generated week.");

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();
        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = 'estudio-yoga-lucia-demo'");

        // The business clock the app reads: Argentina, fixed offset, truncated to whole seconds.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
        var today = DateOnly.FromDateTime(now);

        // One real generated class session carries the seeded roster: the T16 fixture (a synthetic Active enrollment
        // whose period covers the class date, its membership payment and a fixed annotation) and, beside it, one
        // synthetic attended drop-in with no charge -- the omission this flow exists to correct.
        var sessionId = await ScalarIntAsync(connection, """
            SELECT MIN(Id)
              FROM Appointments
             WHERE BusinessId = @businessId AND Kind = @kind AND GridClassId IS NOT NULL
            """, ("@businessId", businessId), ("@kind", (byte)AppointmentKind.ClassSession));
        var gridClassId = await ScalarIntAsync(connection,
            "SELECT GridClassId FROM Appointments WHERE Id = @id", ("@id", sessionId));
        Assert.True(sessionId > 0 && gridClassId > 0, "The confirmed week must persist a class session of the grid.");

        var fixture = await SeedClassRosterFixtureAsync(connection, businessId, sessionId, gridClassId, today, now);
        var (dropInClientId, dropInBookingId) = await SeedAttendedDropInAsync(connection, businessId, sessionId, now);

        // The outbox and the observations are a baseline captured before the money corrections, so the assertion is a
        // delta and never an absolute total that an injected seed row could break.
        var notesBefore = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM ClientNotes WHERE AppointmentId = @id", ("@id", sessionId));
        var notificationsBefore = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", businessId));
        Note($"SQL: seeded the T16 enrollment #{fixture.EnrollmentId} and the attended drop-in annotation #{dropInBookingId} (client #{dropInClientId}) on session #{sessionId}.");

        // The class detail is the page the panel reaches the late charge from: it must offer the form on the attended
        // drop-in, and it supplies the antiforgery token of the write.
        var (detailStatus, detailHtml) = await GetHtmlAsync(client, $"/panel/Classes/Detail/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Contains($"/panel/Classes/ChargeDropIn/{dropInBookingId}", detailHtml, StringComparison.Ordinal);
        var detailToken = ExtractToken(detailHtml);
        Note($"HTTP: GET /panel/Classes/Detail/{sessionId} -> 200 and it offers the late charge of annotation #{dropInBookingId}.");

        async Task<HttpResponseMessage> ChargeAsync(decimal amount, string token) =>
            await client.PostAsync($"/panel/Classes/ChargeDropIn/{dropInBookingId}", Form(new Dictionary<string, string>
            {
                ["appointmentId"] = sessionId.ToString(CultureInfo.InvariantCulture),
                ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["priceReason"] = "Cobro tardío T18",
                ["__RequestVerificationToken"] = token
            }));

        var firstCharge = await ChargeAsync(OriginalCharge, detailToken);
        Assert.Equal(HttpStatusCode.Redirect, firstCharge.StatusCode);
        var paymentId = await ScalarIntAsync(connection, """
            SELECT MAX(Id)
              FROM Payments
             WHERE BusinessId = @businessId AND BookingId = @bookingId AND Concept = @concept
            """,
            ("@businessId", businessId), ("@bookingId", dropInBookingId), ("@concept", (byte)PaymentConcept.DropInClass));
        Assert.True(paymentId > 0, "The late charge must persist its payment.");
        Note($"HTTP: POST ChargeDropIn for annotation #{dropInBookingId} with {OriginalCharge:0.00} -> 302; SQL payment #{paymentId}.");

        // The Cobros history is the ledger the owner corrects from: it lists the drop-in charge with its concept and
        // its concrete origin, and it renders the two correction forms.
        var (ledgerStatus, ledgerHtml) = await GetHtmlAsync(client, "/panel/Payments");
        Assert.Equal(HttpStatusCode.OK, ledgerStatus);
        Assert.Contains("Clase suelta", ledgerHtml, StringComparison.Ordinal);
        Assert.Contains($"Reserva #{dropInBookingId}", ledgerHtml, StringComparison.Ordinal);
        Assert.Contains("Cobrado", ledgerHtml, StringComparison.Ordinal);
        var ledgerToken = ExtractToken(ledgerHtml);
        Note("HTTP: GET /panel/Payments -> 200 with the late charge as 'Clase suelta' and its annotation origin.");

        // A POST without the antiforgery token must be rejected before any business logic runs.
        var rejected = await client.PostAsync($"/panel/Payments/Annul?paymentId={paymentId}",
            Form(new Dictionary<string, string> { ["reason"] = AnnulReason }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Note("HTTP: Annul POST without an anti-CSRF token -> 400.");

        // The owner annuls with a reason; the person responsible is the authenticated username, never a form field.
        var annul = await client.PostAsync($"/panel/Payments/Annul?paymentId={paymentId}", Form(new Dictionary<string, string>
        {
            ["reason"] = AnnulReason,
            ["__RequestVerificationToken"] = ledgerToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, annul.StatusCode);
        var (afterAnnulStatus, afterAnnulHtml) = await GetHtmlAsync(client, annul.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterAnnulStatus);
        Assert.Contains("Cobro anulado.", afterAnnulHtml, StringComparison.Ordinal);
        Assert.Contains("Anulado", afterAnnulHtml, StringComparison.Ordinal);
        Assert.Contains(AnnulReason, afterAnnulHtml, StringComparison.Ordinal);
        Assert.Contains("lucia.demo", afterAnnulHtml, StringComparison.Ordinal);
        Note($"HTTP: POST Annul of payment #{paymentId} with its reason -> 302 -> 200 with the annulled row and the owner as responsible.");

        // The corrected charge lands on the same annotation now that the wrong one is annulled.
        var secondCharge = await ChargeAsync(CorrectedCharge, detailToken);
        Assert.Equal(HttpStatusCode.Redirect, secondCharge.StatusCode);
        var correctedPaymentId = await ScalarIntAsync(connection, """
            SELECT MAX(Id)
              FROM Payments
             WHERE BusinessId = @businessId AND BookingId = @bookingId AND Concept = @concept
            """,
            ("@businessId", businessId), ("@bookingId", dropInBookingId), ("@concept", (byte)PaymentConcept.DropInClass));
        Assert.True(correctedPaymentId > paymentId, "The corrected charge must be a new payment row.");
        Note($"HTTP: corrected POST ChargeDropIn of {CorrectedCharge:0.00} -> 302; SQL payment #{correctedPaymentId}.");

        async Task<HttpResponseMessage> RefundAsync(decimal amount, string token) =>
            await client.PostAsync($"/panel/Payments/Refund?paymentId={correctedPaymentId}", Form(new Dictionary<string, string>
            {
                ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["refundedOn"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["reason"] = RefundReason,
                ["__RequestVerificationToken"] = token
            }));

        var partial = await RefundAsync(PartialRefund, ledgerToken);
        Assert.Equal(HttpStatusCode.Redirect, partial.StatusCode);
        var (afterRefundStatus, afterRefundHtml) = await GetHtmlAsync(client, partial.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, afterRefundStatus);
        // Razor encodes accented flash text as entities; assert an ASCII-safe fragment and the ledger state below.
        Assert.Contains("registrada.", afterRefundHtml, StringComparison.Ordinal);
        Assert.Contains("Devuelto", afterRefundHtml, StringComparison.Ordinal);
        Assert.Contains("Resta por devolver", afterRefundHtml, StringComparison.Ordinal);
        Note($"HTTP: partial refund of {PartialRefund:0.00} on payment #{correctedPaymentId} dated today -> 302 -> 200 with the returned total and what is left.");

        // The cumulative cap is a rule the panel maps to words: a refund past what is left is refused and writes nothing.
        var overCap = await RefundAsync(ExcessRefund, ledgerToken);
        Assert.Equal(HttpStatusCode.Redirect, overCap.StatusCode);
        var (overCapStatus, overCapHtml) = await GetHtmlAsync(client, overCap.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, overCapStatus);
        Assert.Contains("Las devoluciones no pueden superar el importe cobrado.", overCapHtml, StringComparison.Ordinal);
        Note("HTTP: refund past the remaining amount -> 302 -> 200 with the cap sentence and no extra refund row.");

        // An annulment and a refund never coexist: the already refunded payment refuses the annulment.
        var annulRefunded = await client.PostAsync($"/panel/Payments/Annul?paymentId={correctedPaymentId}", Form(new Dictionary<string, string>
        {
            ["reason"] = AnnulReason,
            ["__RequestVerificationToken"] = ledgerToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, annulRefunded.StatusCode);
        var (annulRefundedStatus, annulRefundedHtml) = await GetHtmlAsync(client, annulRefunded.Headers.Location!.OriginalString!);
        Assert.Equal(HttpStatusCode.OK, annulRefundedStatus);
        Assert.Contains("Ese cobro tiene devoluciones: no se puede anular.", annulRefundedHtml, StringComparison.Ordinal);
        Note("HTTP: Annul on the refunded payment -> 302 -> 200 with the coexistence refusal.");

        // SQL verification runs after every HTTP checkpoint so one run reports the whole behavior. Every count is
        // scoped by the booking or the payment so an unexpectedly seeded row cannot change the answer.
        Assert.Equal(2, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM Payments
             WHERE BusinessId = @businessId AND BookingId = @bookingId AND Concept = @concept
            """,
            ("@businessId", businessId), ("@bookingId", dropInBookingId), ("@concept", (byte)PaymentConcept.DropInClass)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM Payments WHERE Id = @id AND BookingId = @bookingId AND Amount = @amount
            """,
            ("@id", paymentId), ("@bookingId", dropInBookingId), ("@amount", OriginalCharge)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM Payments WHERE Id = @id AND BookingId = @bookingId AND Amount = @amount
            """,
            ("@id", correctedPaymentId), ("@bookingId", dropInBookingId), ("@amount", CorrectedCharge)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM PaymentAnnulments
             WHERE PaymentId = @paymentId AND Reason = @reason AND AnnulledBy = @annulledBy
            """,
            ("@paymentId", paymentId), ("@reason", AnnulReason), ("@annulledBy", "lucia.demo")));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM PaymentAnnulments WHERE PaymentId = @paymentId", ("@paymentId", correctedPaymentId)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM Refunds
             WHERE PaymentId = @paymentId AND Amount = @amount
               AND CONVERT(varchar(10), RefundedOn, 23) = @refundedOn
            """,
            ("@paymentId", correctedPaymentId), ("@amount", PartialRefund),
            ("@refundedOn", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Refunds WHERE PaymentId = @paymentId", ("@paymentId", paymentId)));
        Note("SQL: the original charge is retained, exactly one annulment and one partial refund exist, and none of them coexists on the same payment.");

        // The annulment is financial only: the enrollment, its paid period, the attendance and the fixed annotation
        // are exactly the seeded ones, and the corrected charge never re-marked the attendance.
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Enrollments WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM Enrollments WHERE Id = @id AND Status = @status AND ActivatedAt IS NOT NULL
            """,
            ("@id", fixture.EnrollmentId), ("@status", (byte)EnrollmentStatus.Active)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM MembershipPeriods
             WHERE Id = @periodId AND EnrollmentId = @enrollmentId
               AND CONVERT(varchar(10), CoverageStart, 23) = @coverageStart
               AND CONVERT(varchar(10), ExpiresOn, 23) = @expiresOn
            """,
            ("@periodId", fixture.PeriodId), ("@enrollmentId", fixture.EnrollmentId),
            ("@coverageStart", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("@expiresOn", today.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM Payments
             WHERE Id = @id AND EnrollmentId = @enrollmentId AND Concept = @concept AND Amount = @amount
            """,
            ("@id", fixture.MembershipPaymentId), ("@enrollmentId", fixture.EnrollmentId),
            ("@concept", (byte)PaymentConcept.Membership), ("@amount", MembershipFixtureAmount)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM Bookings
             WHERE Id = @id AND Modality = @modality AND Status = @status AND AttendanceRecordedAt IS NOT NULL
            """,
            ("@id", dropInBookingId), ("@modality", (byte)BookingModality.DropIn), ("@status", (byte)BookingStatus.Attended)));
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT COUNT(*) FROM Bookings
             WHERE Id = @id AND EnrollmentId = @enrollmentId AND Status = @status
            """,
            ("@id", fixture.FixedBookingId), ("@enrollmentId", fixture.EnrollmentId), ("@status", (byte)BookingStatus.Pending)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE Id = @id AND Status = @status AND SuspendedAt IS NULL",
            ("@id", sessionId), ("@status", (byte)AppointmentStatus.Scheduled)));
        Note("SQL: the enrollment, its period, its paid payment, the fixed annotation and the attendance are untouched; the class stayed Programado.");

        // No observation and no notice is a by-product of the money corrections (D-07, D-20).
        Assert.Equal(notesBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM ClientNotes WHERE AppointmentId = @id", ("@id", sessionId)));
        Assert.Equal(notificationsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Note("SQL: the money corrections wrote no observation and queued no email.");

        // The harness never writes a secret: every diagnostic line reports only booleans, identifiers, counts or paths,
        // and this proves it against the password, every antiforgery token and every cookie of the run.
        foreach (Cookie cookie in handler.CookieContainer.GetAllCookies())
        {
            if (cookie.Value.Length >= 8)
                secrets.Add(cookie.Value);
        }

        Assert.NotEmpty(written);
        var leaked = written.Any(line => secrets.Any(secret => line.Contains(secret, StringComparison.Ordinal)));
        Assert.False(leaked, "The test output must never contain a password, an antiforgery token or a cookie value.");
        Note("Secrets: the run wrote no password, antiforgery token or cookie value to the test output.");
    }

    /// <summary>
    /// The refused change wrote nothing: the enrollment still has its single open fixed window and no second row, so a
    /// rejected date never leaves a half-moved schedule behind.
    /// </summary>
    private static async Task AssertScheduleChangeWroteNothingAsync(SqlConnection connection, int enrollmentId)
    {
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", enrollmentId)));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM EnrollmentFixedClasses WHERE EnrollmentId = @enrollmentId AND ValidTo IS NULL",
            ("@enrollmentId", enrollmentId)));
    }

    /// <summary>Reads the guided-schedule-change link the Abonos list publishes for one enrollment.</summary>
    private static string ExtractChangeScheduleLink(string html, int enrollmentId)
    {
        var match = Regex.Match(html,
            $"href=\"(?<href>[^\"]*?/panel/Enrollments/ChangeSchedule\\?enrollmentId={enrollmentId})\"",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException($"The Abonos list did not publish the schedule-change link of enrollment {enrollmentId}.");
        return match.Groups["href"].Value;
    }

    /// <summary>Posts the guided schedule-change form: the two weekly slots, the effective date and the token.</summary>
    private static Dictionary<string, string> ChangeScheduleFields(
        int enrollmentId, int currentGridClassId, int newGridClassId, DateOnly effectiveFrom, string? token = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["EnrollmentId"] = enrollmentId.ToString(CultureInfo.InvariantCulture),
            ["CurrentGridClassId"] = currentGridClassId.ToString(CultureInfo.InvariantCulture),
            ["NewGridClassId"] = newGridClassId.ToString(CultureInfo.InvariantCulture),
            ["EffectiveFrom"] = effectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        if (token is not null)
            fields["__RequestVerificationToken"] = token;
        return fields;
    }

    /// <summary>
    /// One synthetic fixed enrollment for the class roster flow: a client, an Active enrollment with its fixed grid
    /// assignment, one paid period covering the class date and one pending fixed annotation on the shifted session.
    /// It writes only those rows, in one transaction, and returns their identifiers so the flow can assert they were
    /// never mutated afterwards.
    /// </summary>
    private static async Task<ClassRosterFixture> SeedClassRosterFixtureAsync(
        SqlConnection connection, int businessId, int appointmentId, int gridClassId, DateOnly today, DateTime now)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var email = $"t16.fixed{Guid.NewGuid():N}@example.invalid";
        var clientId = await InsertIdAsync(connection, transaction, """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)
            """,
            ("@businessId", businessId), ("@firstName", "Synthetic"), ("@lastName", "T16Fixed"),
            ("@email", email), ("@phone", "0000000000"), ("@createdAt", now));

        var enrollmentId = await InsertIdAsync(connection, transaction, """
            INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, ReferenceDay, CreatedAt, ActivatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @clientId, @frequency, @status, @price, @referenceDay, @createdAt, @activatedAt)
            """,
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", 1),
            ("@status", EnrollmentStatus.Active), ("@price", MembershipFixtureAmount),
            ("@referenceDay", today.Day), ("@createdAt", now), ("@activatedAt", now));

        await InsertIdAsync(connection, transaction, """
            INSERT INTO EnrollmentFixedClasses (EnrollmentId, GridClassId, ValidFrom, ValidTo)
            OUTPUT INSERTED.Id
            VALUES (@enrollmentId, @gridClassId, @validFrom, NULL)
            """,
            ("@enrollmentId", enrollmentId),
            ("@gridClassId", gridClassId),
            ("@validFrom", today));

        var membershipPaymentId = await InsertIdAsync(connection, transaction, """
            INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @clientId, @concept, NULL, @enrollmentId, @amount, @listPrice, NULL, @paidAt, @recordedAt)
            """,
            ("@businessId", businessId), ("@clientId", clientId), ("@concept", PaymentConcept.Membership),
            ("@enrollmentId", enrollmentId), ("@amount", MembershipFixtureAmount),
            ("@listPrice", MembershipFixtureAmount), ("@paidAt", now), ("@recordedAt", now));

        var periodId = await InsertIdAsync(connection, transaction, """
            INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)
            """,
            ("@enrollmentId", enrollmentId), ("@coverageStart", today), ("@expiresOn", today.AddMonths(1)),
            ("@paymentId", membershipPaymentId), ("@createdAt", now));

        var bookingId = await InsertIdAsync(connection, transaction, """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, AbsenceSource, CancelledBy, CreatedAt, CancelledAt, AttendanceRecordedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, NULL, NULL, @createdAt, NULL, NULL)
            """,
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@enrollmentId", enrollmentId), ("@modality", BookingModality.Fixed),
            ("@status", BookingStatus.Pending), ("@createdAt", now));

        await transaction.CommitAsync();
        return new ClassRosterFixture(clientId, email, enrollmentId, periodId, membershipPaymentId, bookingId);
    }

    /// <summary>
    /// One synthetic attended drop-in without a charge: a fresh client (the active-booking index allows one booking per
    /// client and session, so it cannot reuse the fixed student of the T16 fixture) and its <c>Suelta</c> annotation
    /// already marked as present on the class session that fixture seeded, with no payment. That missing charge is
    /// exactly what the late-charge flow exists to register. It writes only those two rows, in one transaction, and
    /// returns their identifiers so the flow can scope its assertions to the booking.
    /// </summary>
    private static async Task<(int ClientId, int BookingId)> SeedAttendedDropInAsync(
        SqlConnection connection, int businessId, int appointmentId, DateTime now)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var email = $"t18.dropin{Guid.NewGuid():N}@example.invalid";
        var clientId = await InsertIdAsync(connection, transaction, """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)
            """,
            ("@businessId", businessId), ("@firstName", "Synthetic"), ("@lastName", "T18DropIn"),
            ("@email", email), ("@phone", "0000000000"), ("@createdAt", now));

        var bookingId = await InsertIdAsync(connection, transaction, """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, AbsenceSource, CancelledBy, CreatedAt, CancelledAt, AttendanceRecordedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @appointmentId, @clientId, NULL, @modality, @status, NULL, NULL, @createdAt, NULL, @attendedAt)
            """,
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@modality", BookingModality.DropIn), ("@status", BookingStatus.Attended),
            ("@createdAt", now), ("@attendedAt", now));

        await transaction.CommitAsync();
        return (clientId, bookingId);
    }

    /// <summary>
    /// One outbox row written outside the service, so the flow can pre-seed the notice a retry would leave and prove
    /// that the real suspension deduplicates it instead of queuing a second email.
    /// </summary>
    private static async Task<int> InsertSuspensionNotificationAsync(
        SqlConnection connection, int businessId, string recipient, string dedupKey, DateTime createdAt)
    {
        await using var command = new SqlCommand("""
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL)
            """, connection);
        command.Parameters.AddWithValue("@businessId", businessId);
        command.Parameters.AddWithValue("@type", (byte)NotificationType.ClassSuspended);
        command.Parameters.AddWithValue("@recipient", recipient);
        command.Parameters.AddWithValue("@subject", "Clase suspendida");
        command.Parameters.AddWithValue("@bodyHtml", "<p>Hola</p>");
        command.Parameters.AddWithValue("@dedupKey", dedupKey);
        command.Parameters.AddWithValue("@status", (byte)NotificationStatus.Pending);
        command.Parameters.AddWithValue("@createdAt", createdAt);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>The stable suspension key of the SRS error handling: one notice per student and class.</summary>
    private static string SuspensionDedupKey(int appointmentId, int bookingId) =>
        $"suspension:appointment:{appointmentId}:booking:{bookingId}";

    /// <summary>Posts a synthetic client through the owner form and returns its id and stored email.</summary>
    private static async Task<(int ClientId, string Email)> CreateSyntheticClientAsync(
        HttpClient client, string lastName, Func<string, string> extractToken)
    {
        var (status, html) = await GetHtmlAsync(client, "/panel/Clients/Create");
        Assert.Equal(HttpStatusCode.OK, status);
        var email = $"t16.{lastName.ToLowerInvariant()}{Guid.NewGuid():N}@example.invalid";
        var response = await client.PostAsync("/panel/Clients/Create", Form(new Dictionary<string, string>
        {
            ["FirstName"] = "Synthetic",
            ["LastName"] = lastName,
            ["Email"] = email,
            ["Phone"] = "0000000000",
            ["__RequestVerificationToken"] = extractToken(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return (ExtractClientId(response), email);
    }

    /// <summary>Reads the published class link of one session, so the flow follows the page instead of guessing it.</summary>
    private static string ExtractClassDetailLink(string html, int appointmentId)
    {
        var match = Regex.Match(html,
            $"href=\"(?<href>[^\"]*?/panel/Classes/Detail/{appointmentId})\"", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException($"The page did not publish the class link for session {appointmentId}.");
        return match.Groups["href"].Value;
    }

    /// <summary>Runs one non-query against the isolated database and reports the affected rows.</summary>
    private static async Task<int> ExecuteAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = CreateCommand(connection, sql, args);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>One insert that returns its identity, with the same parameter shapes the fixture helpers use.</summary>
    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] args)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in args)
            AddParameter(command, name, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static void AddParameter(SqlCommand command, string name, object? value)
    {
        switch (value)
        {
            case DateTime dateTime:
                command.Parameters.Add(name, SqlDbType.DateTime2).Value = dateTime;
                return;
            case DateOnly date:
                command.Parameters.Add(name, SqlDbType.Date).Value = date.ToDateTime(TimeOnly.MinValue);
                return;
            case null:
                command.Parameters.AddWithValue(name, DBNull.Value);
                return;
            case Enum enumeration:
                command.Parameters.AddWithValue(name, Convert.ToByte(enumeration, CultureInfo.InvariantCulture));
                return;
            default:
                command.Parameters.AddWithValue(name, value);
                return;
        }
    }

    private static async Task AssertWalkInRowAsync(SqlConnection connection, int businessId, int appointmentId,
        int clientId, string note)
    {
        await using var command = new SqlCommand("""
            SELECT a.Status, a.Kind, a.Origin, a.BusinessId,
                   b.Status, b.AgreedPrice, b.ClientId, b.BusinessId,
                   p.Concept, p.Amount, p.BusinessId,
                   n.Text
              FROM Appointments a
              JOIN Bookings b ON b.AppointmentId = a.Id
              LEFT JOIN Payments p ON p.BookingId = b.Id
              LEFT JOIN ClientNotes n ON n.AppointmentId = a.Id
             WHERE a.BusinessId = @businessId AND a.Id = @appointmentId
            """, connection);
        command.Parameters.AddWithValue("@businessId", businessId);
        command.Parameters.AddWithValue("@appointmentId", appointmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The walk-in appointment was not persisted.");
        Assert.Equal((byte)AppointmentStatus.Scheduled, reader.GetByte(0));
        Assert.Equal((byte)AppointmentKind.Individual, reader.GetByte(1));
        Assert.Equal((byte)AppointmentOrigin.WalkIn, reader.GetByte(2));
        Assert.Equal(businessId, reader.GetInt32(3));
        Assert.Equal((byte)BookingStatus.Attended, reader.GetByte(4));
        Assert.Equal(clientId, reader.GetInt32(6));
        Assert.Equal(businessId, reader.GetInt32(7));
        Assert.Equal((byte)PaymentConcept.WalkIn, reader.GetByte(8));
        Assert.Equal(businessId, reader.GetInt32(10));
        Assert.Equal(note, reader.GetString(11));
        Assert.Equal(ExpectedPrice, reader.GetDecimal(5));
        Assert.Equal(ExpectedPrice, reader.GetDecimal(9));
        Assert.False(await reader.ReadAsync(), "The walk-in must persist exactly one appointment, booking and payment row.");
    }

    private static Dictionary<string, string> WalkInFields(int clientId, int serviceId, int workerId,
        string note, string token) => new()
    {
        ["ClientId"] = clientId.ToString(CultureInfo.InvariantCulture),
        ["ServiceId"] = serviceId.ToString(CultureInfo.InvariantCulture),
        ["WorkerId"] = workerId.ToString(CultureInfo.InvariantCulture),
        ["AgreedPrice"] = "100.25",
        ["PriceReason"] = "T12 HTTP synthetic",
        ["NextVisitNote"] = note,
        ["CollectPayment"] = "true",
        ["__RequestVerificationToken"] = token
    };

    private HarnessPlan PlanHarness(string suppliedConnection)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The isolated DataProtection keyring requires Windows DPAPI, so this harness refuses to run on this platform. No SQL connection was opened.");

        SqlConnectionStringBuilder input;
        try
        {
            input = new SqlConnectionStringBuilder(suppliedConnection);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException($"{ConnectionVariable} is not a valid SQL connection string. No SQL connection was opened.", ex);
        }

        var databaseName = input.InitialCatalog;
        if (!string.Equals(input.DataSource, Server, StringComparison.OrdinalIgnoreCase)
            || !input.IntegratedSecurity
            || string.IsNullOrEmpty(databaseName) || databaseName.Length > 128
            || !Regex.IsMatch(databaseName, @"\A" + DatabasePrefix + @"[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)
            || !string.IsNullOrEmpty(input.UserID) || !string.IsNullOrEmpty(input.Password)
            || !string.IsNullOrEmpty(input.AttachDBFilename) || input.UserInstance
            || !string.IsNullOrEmpty(input.FailoverPartner))
            throw new InvalidOperationException($"{ConnectionVariable} must use integrated {Server} with a fresh {DatabasePrefix}* database. No SQL connection was opened.");

        var keysPath = ResolveKeysPath();
        var (webHostPath, webRoot) = LocateWebHost();
        var port = FindFreeLoopbackPort();

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        }.ConnectionString;

        return new HarnessPlan(connectionString, databaseName, keysPath, webHostPath, webRoot, port);
    }

    private static string ResolveKeysPath()
    {
        var value = Environment.GetEnvironmentVariable(KeysPathVariable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{KeysPathVariable} is required once {ConnectionVariable} is set. No SQL connection was opened.");
        if (value != value.Trim() || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{KeysPathVariable} must be an absolute path without surrounding whitespace. No SQL connection was opened.");

        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{KeysPathVariable} is not a valid file system path. No SQL connection was opened.", ex);
        }

        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var relative = Path.GetRelativePath(tempRoot, canonical);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"{KeysPathVariable} must resolve inside the OS temp directory ({tempRoot}). No SQL connection was opened.");

        var leaf = Path.GetFileName(canonical);
        if (!leaf.StartsWith(KeysLeafPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"{KeysPathVariable} must end with a directory whose name starts with \"{KeysLeafPrefix}\". No SQL connection was opened.");

        if (Directory.Exists(canonical) || File.Exists(canonical))
            throw new InvalidOperationException($"{KeysPathVariable} must not already exist so no earlier keyring is reused. No SQL connection was opened.");

        EnsureNoReparseAncestors(canonical, tempRoot);
        return canonical;
    }

    private static void EnsureNoReparseAncestors(string canonicalPath, string tempRoot)
    {
        var reachedTempRoot = false;
        for (var current = new DirectoryInfo(canonicalPath); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{KeysPathVariable} has a reparse point ancestor at '{current.FullName}'. No SQL connection was opened.");
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
            throw new InvalidOperationException($"The WorkUp web host '{webHostPath}' was not found; build the solution before running this harness. No SQL connection was opened.");

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

    private async Task CreateDatabaseAsync(HarnessPlan plan)
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
        await using (var check = new SqlCommand("SELECT DB_ID(@database)", master))
        {
            check.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = plan.DatabaseName;
            var existing = await check.ExecuteScalarAsync();
            if (existing is not null && !Convert.IsDBNull(existing))
                throw new InvalidOperationException($"Database '{plan.DatabaseName}' already exists; this harness never reuses an earlier database and created nothing.");
            output.WriteLine($"SQL: DB_ID('{plan.DatabaseName}') is NULL (absent) on {Server}.");
        }

        // CREATE DATABASE cannot run inside a transaction. SQL Server runs it atomically, so a competing
        // creator makes this statement fail instead of opening or reusing its database.
        await using (var create = new SqlCommand($"CREATE DATABASE [{plan.DatabaseName}]", master))
            await create.ExecuteNonQueryAsync();

        output.WriteLine($"SQL: created isolated database '{plan.DatabaseName}'; it will be retained without teardown.");
    }

    private static async Task SeedDemoAsync(string connectionString, string password)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        var seeder = new DemoDataSeeder(connectionString, new IdentityPasswordHasher(), new BusinessClock(zone));
        var outcome = await seeder.SeedAsync(password);
        if (outcome.CreatedBusinesses < 1)
            throw new InvalidOperationException("The T07 demo seed created no business in the isolated database.");
    }

    private RunningHost StartWebHost(HarnessPlan plan, int port)
    {
        var baseUrl = $"http://127.0.0.1:{port}";
        var startInfo = new ProcessStartInfo(plan.WebHostPath)
        {
            WorkingDirectory = plan.WebRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__WorkUp"] = plan.ConnectionString;
        startInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
        startInfo.Environment["WorkUp__Development__DataProtectionKeysPath"] = plan.KeysPath;
        startInfo.Environment["WorkUp__PublicBaseUrl"] = baseUrl;
        startInfo.Environment.Remove("ASPNETCORE_HTTPS_PORT");
        startInfo.Environment.Remove("WORKUP_DEMO_PASSWORD");
        startInfo.Environment.Remove("WorkUp__DemoData__Enabled");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the owned WorkUp web host process.");
        // Drain both pipes so a chatty child can never block on a full buffer; the lines are discarded, not logged.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (process.Id <= 0)
        {
            TryKill(process);
            throw new InvalidOperationException("The owned WorkUp web host process did not report a process id.");
        }

        return new RunningHost(process, baseUrl);
    }

    private static async Task WaitUntilReadyAsync(RunningHost host)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + ReadinessBudget;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Process.HasExited)
                throw new InvalidOperationException($"The owned WorkUp host (pid {host.Process.Id}) exited with code {host.Process.ExitCode} before becoming ready.");
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

        throw new TimeoutException($"The owned WorkUp host did not answer GET /panel/Account/Login within {ReadinessBudget.TotalSeconds:0} seconds.");
    }

    private static async Task StopWebHostAsync(Process process)
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

    private void AssertKeyringCreated(string keysPath)
    {
        Assert.True(Directory.Exists(keysPath), "The isolated DataProtection keyring directory was not created.");
        var files = Directory.GetFiles(keysPath, "key-*.xml", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(keysPath));
        foreach (var file in files)
        {
            var canonicalFile = Path.GetFullPath(file);
            Assert.True(canonicalFile.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Every DataProtection key file must be inside the dedicated temporary keyring folder.");
            Assert.False(File.ReadAllText(file).Contains("<masterKey", StringComparison.Ordinal),
                "Isolated DataProtection keys must be encrypted at rest with DPAPI.");
        }

        output.WriteLine($"DataProtection: {files.Length} encrypted key file(s) generated only inside the dedicated temp keyring folder.");
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetHtmlAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    private static bool IsDayRedirect(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
            return false;
        // The owner area route is panel/{controller=Day}/{action=Index}/{id?}, so the Jornada index is
        // "/panel": controller and action match their defaults and are omitted from the generated URL.
        var raw = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        var path = raw.Split('?')[0];
        return path.Equals("/panel", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/panel/", StringComparison.OrdinalIgnoreCase);
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

    private static int ExtractClientId(HttpResponseMessage response)
    {
        var location = response.Headers.Location?.OriginalString ?? "";
        var match = Regex.Match(location, @"/panel/Clients/Detail/(\d+)", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException("The synthetic client form did not redirect to a client detail page.");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ScalarIntAsync(SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = CreateCommand(connection, sql, args);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = CreateCommand(connection, sql, args);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, (string Name, object Value)[] args)
    {
        var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private sealed record HarnessPlan(string ConnectionString, string DatabaseName, string KeysPath,
        string WebHostPath, string WebRoot, int Port);

    /// <summary>Identifiers of the synthetic fixed enrollment the T16 flow seeds and then proves untouched.</summary>
    private sealed record ClassRosterFixture(
        int ClientId, string Email, int EnrollmentId, int PeriodId, int MembershipPaymentId, int FixedBookingId);

    private sealed record RunningHost(Process Process, string BaseUrl);
}
