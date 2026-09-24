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
using WorkUp.Infrastructure;
using Xunit.Abstractions;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// Opt-in, reusable HTTP integration harness for the owner panel. It drives the real MVC host over loopback with
/// cookies and antiforgery, then verifies the persisted rows with ADO.NET, and it covers three independent flows.
/// The Jornada (RF-11/RF-15): owner login, one synthetic client through the owner form, GET /panel/Day, a walk-in with
/// an exact agreed price plus immediate payment and next-visit note, a second overlapping walk-in saved with a
/// warning, and the anti-CSRF rejection. The week generation (RF-06): yoga owner login, the first real render of
/// GET /panel/Classes/Week, the default-week confirm that sends no date, the SQL shape of the generated sessions,
/// the idempotent second confirm, the anti-CSRF rejection and the past-week gate. The drop-in classes (RF-22, RF-07
/// cupo &gt; 1): yoga owner login, the default-week confirm that gives the roster its sessions, the first real render
/// of GET /panel/Classes/DropIns with its session rows, one client created through the owner form and booked as a
/// suelta, the owner cancellation that frees the place, the public booking from /r/{slug}/clases with its
/// confirmation and the client it creates without an account, and the anti-CSRF rejection. It replaces manual scratch
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

    private sealed record RunningHost(Process Process, string BaseUrl);
}
