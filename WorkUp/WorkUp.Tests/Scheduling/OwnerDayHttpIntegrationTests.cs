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
/// Opt-in, reusable HTTP integration harness for the owner Jornada (RF-11/RF-15). It drives the real MVC host
/// over loopback with cookies and antiforgery, then verifies the persisted rows with ADO.NET:
/// owner login, one synthetic client through the owner form, GET /panel/Day, a walk-in with an exact agreed
/// price plus immediate payment and next-visit note, a second overlapping walk-in saved with a warning, and the
/// anti-CSRF rejection. It replaces manual scratch scripts: the same test is the smoke it used to need.
///
/// Without <c>WORKUP_T12_HTTP_CONNECTION</c> the test logs N/A and returns before any SQL connection or HTTP
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
