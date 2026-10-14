using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Jobs;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in real-host/SQL/SMTP fixture for the T25 scheduled notices (RF-18/RF-19/RF-30) as they are actually wired in
/// the shipped MVC host. The three cores and their SQL ports are already proven against fakes and against a real
/// database through a direct service call; what only this harness can prove is the whole path of one process: the
/// immediate tick of the registered <see cref="ScheduledJobsHostedService"/> runs the real
/// <see cref="DailySummaryScheduledJob"/>, <see cref="StockReviewScheduledJob"/> and
/// <see cref="MembershipExpiryScheduledJob"/> with the real <c>Sql*Repository</c> ports against a real migrated
/// database, and the outbox job of that very same tick then retains or dispatches the rows the host itself wrote.
/// <para>
/// Two scenarios, one retained database and one isolated DPAPI keyring each, driven by the same opt-in pair
/// <c>WORKUP_T25_HOST_CONNECTION</c> and <c>WORKUP_T25_HOST_KEYS_PATH</c>. Without that pair every scenario logs N/A and
/// returns before any SQL connection, database, keyring, HTTP host or SMTP listener exists, so the default suite is
/// unaffected and this file adds no real evidence by itself.
/// </para>
/// <para>
/// The derived suffixes are <c>_Retain</c> and <c>_Smtp</c>: the database is the configured catalog plus the suffix
/// (must start with <c>WorkUpDemoT25Host</c>, no credential, no attached file, integrated LocalDB only) and the keyring
/// is the configured path plus the suffix (must be an absolute path inside the OS temp directory whose leaf starts with
/// <c>WorkUpDemoT12KeysT25Host</c>, must not exist yet and must have no reparse point ancestor). Both derived targets
/// are validated before anything connects, the database is created only after <c>DB_ID</c> proved it absent, and both
/// the database and the encrypted keys are retained afterwards: never reused, never dropped and never deleted. Only the
/// owned child process is stopped.
/// </para>
/// <para>
/// The business-time "now" is read once from the real <see cref="BusinessClock"/> of the business time zone and used to
/// derive every seeded date; the run refuses to start when the local clock is too close to midnight, before the derived
/// database is created, because a tick that crossed midnight would shift the target day of the summary and change the
/// membership stage. The seed is one synthetic business with its notice email, <c>DailySummaryTime = 00:00</c> and a
/// creation date exactly seven days before now under <c>StockReviewEveryDays = 7</c> (so the current review bucket is
/// today), one scheduled individual turn with a live booking and one scheduled class for tomorrow, one active supply,
/// and two separate clients with an active paid period expiring today and three days from now. The recipient of every
/// owner notice is the business notice email; every membership notice goes to the same-tenant <c>Clients.Email</c>, and
/// a decoy <c>ClientAccounts.Email</c> is seeded to prove the account email is never a fallback. No private link is
/// seeded and the configured anticipation is one hour, so the reminder job has zero eligible candidates and its
/// invisible contribution can never be mistaken for one of the four notices.
/// </para>
/// <para>
/// Scenario 1 (<c>_Retain</c>, <c>Outbox</c> mode): after the committed fixed
/// <see cref="ScheduledJobsHostedService.TickCompletedMessage"/> marker — never a real minute of waiting — the
/// database holds exactly the four expected notices, all <c>Pending</c> with zero attempts and no claim, and the
/// reachable loopback provider recorded no connection at all. Scenario 2 (<c>_Smtp</c>, <c>Smtp</c> mode, a separate
/// fresh database and keyring): the same tick queues and dispatches all four, each <c>Sent</c> with one attempt and the
/// claim cleared, each completion still inside the scheduler interval of its creation instant (same-tick evidence), and
/// exactly four SMTP transactions reached the provider with its final <c>250</c>. Every recipient is checked against
/// its owner/client expectation and none of them may appear on the owned child's captured output.
/// </para>
/// <para>
/// Nothing here replaces an independent run: until a human executes it against a real LocalDB instance with the opt-in
/// pair set, every scenario is compiled and N/A only, never real proof.
/// </para>
/// </summary>
public sealed class ScheduledNoticesHostIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string ConnectionVariable = "WORKUP_T25_HOST_CONNECTION";
    private const string KeysPathVariable = "WORKUP_T25_HOST_KEYS_PATH";
    private const string DatabasePrefix = "WorkUpDemoT25Host";
    private const string KeysLeafPrefix = "WorkUpDemoT12KeysT25Host";
    private const string BusinessTimeZone = "America/Argentina/Buenos_Aires";

    /// <summary>Derived suffix of each retained target; one fresh database and one fresh keyring per scenario.</summary>
    private const string RetainSuffix = "_Retain";
    private const string SmtpSuffix = "_Smtp";

    private const string BusinessName = "Peluqueria T25 Host";
    private const string BusinessSlug = "t25-host";
    private const string ContactPhone = "1145550000";
    private const string FromAddress = "WorkUp <no-responder@workup.local>";
    private const string OwnerRecipient = "t25-host.owner@example.invalid";
    private const string BookingRecipient = "t25-host.booking@example.invalid";
    private const string ExpiringRecipient = "t25-host.expiring@example.invalid";
    private const string ExpiredRecipient = "t25-host.expired@example.invalid";
    private const string DecoyAccountRecipient = "t25-host.account-decoy@example.invalid";
    private const string ServiceName = "Corte T25";
    private const string SupplyName = "Shampoo T25";

    /// <summary>Configured anticipation: one hour, so tomorrow's individual booking is never inside the window today.</summary>
    private const int ReminderLeadHours = 1;

    private const int StockReviewEveryDays = 7;
    private const int SummaryHour = 0;
    private const int UpcomingIndividualHour = 10;
    private const int UpcomingClassHour = 18;

    // Role keys of the four expected notices, so the mapping between the seed and the assertions stays explicit.
    private const string SummaryRole = "summary";
    private const string StockRole = "stock";
    private const string ExpiringRole = "expiring";
    private const string ExpiredRole = "expired";

    /// <summary>A run that reaches this local time of day is refused before the derived database exists.</summary>
    private static readonly TimeSpan MidnightRunout = new(23, 50, 0);

    private static readonly Regex SafeDatabaseName =
        new(@"\A" + DatabasePrefix + @"[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TickBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(500);

    // --- Scenario 1: Outbox retains the four notices and never contacts the provider --------------------------

    [Fact]
    public async Task Retain_mode_tick_queues_the_four_scheduled_notices_and_sends_nothing()
    {
        var outcome = await RunScenarioAsync(RetainSuffix, "Outbox");
        if (outcome is null)
            return;

        // Exactly the three T25 jobs of the tick queued one notice each; the reminder job contributed nothing.
        Assert.Equal(4, outcome.Notices.Count);
        Assert.All(
            new[] { SummaryRole, StockRole, ExpiringRole, ExpiredRole },
            role => Assert.True(outcome.Notices.ContainsKey(role), $"The {role} notice was missing."));

        foreach (var (role, notice) in outcome.Notices)
        {
            var expected = outcome.Expected[role];
            Assert.Equal((byte)expected.Type, notice.Type);
            Assert.Equal(expected.Recipient, notice.Recipient);
            Assert.Equal((byte)NotificationStatus.Pending, notice.Status);
            Assert.Equal(0, notice.Attempts);
            Assert.Null(notice.LastError);
            Assert.Null(notice.SentAt);
            Assert.Null(notice.ClaimedAt);
            Assert.Null(notice.DispatchKey);
        }

        // A reachable loopback provider saw nothing: Outbox never claims and never sends.
        Assert.Equal(0, outcome.SmtpConnections);
        Assert.False(outcome.ChildOutputMentionedAnyRecipient);

        output.WriteLine(
            $"Retain assertions passed on retained database '{outcome.DatabaseName}' with isolated keyring " +
            $"'{outcome.KeysPath}': tick marker observed, exactly four Pending notices with 0 attempts and 0 SMTP " +
            "connections, and no recipient on the child output.");
    }

    // --- Scenario 2: the same tick dispatches the four notices through the loopback provider -------------------

    [Fact]
    public async Task Smtp_mode_tick_dispatches_all_four_notices_in_the_same_tick_with_one_transaction_each()
    {
        var outcome = await RunScenarioAsync(SmtpSuffix, "Smtp");
        if (outcome is null)
            return;

        Assert.Equal(4, outcome.Notices.Count);

        foreach (var (role, notice) in outcome.Notices)
        {
            var expected = outcome.Expected[role];
            Assert.Equal((byte)expected.Type, notice.Type);
            Assert.Equal(expected.Recipient, notice.Recipient);
            Assert.Equal((byte)NotificationStatus.Sent, notice.Status);
            Assert.Equal(1, notice.Attempts);
            Assert.Null(notice.LastError);
            Assert.NotNull(notice.SentAt);
            Assert.Null(notice.ClaimedAt);
            Assert.Null(notice.DispatchKey);

            // Same-tick evidence: the outbox job only runs after the T25 jobs of the same tick, and two ticks are one
            // interval apart, so a completion a whole interval after creation would mean another tick.
            var elapsed = notice.SentAt!.Value - notice.CreatedAt;
            Assert.True(
                elapsed >= TimeSpan.Zero && elapsed < ScheduledJobsHostedService.Interval,
                $"Notice '{expected.DedupKey}' was created at {notice.CreatedAt:O} and completed at " +
                $"{notice.SentAt:O}; {elapsed.TotalSeconds:0} s is not inside the " +
                $"{ScheduledJobsHostedService.Interval.TotalSeconds:0} s scheduler interval, so it was not the same tick.");
        }

        // Exactly one SMTP transaction per notice, each answered with the final 250.
        Assert.Equal(4, outcome.SmtpConnections);
        Assert.False(outcome.ChildOutputMentionedAnyRecipient);

        output.WriteLine(
            $"Smtp assertions passed on retained database '{outcome.DatabaseName}' with isolated keyring " +
            $"'{outcome.KeysPath}': all four notices were Sent in the tick that queued them with 1 attempt, the claim " +
            "cleared and exactly four SMTP transactions, and no recipient on the child output.");
    }

    // --- Shared scenario ----------------------------------------------------------------------------------------

    /// <summary>
    /// Drives one scenario end to end. It returns <c>null</c> -- N/A -- when the opt-in pair is incomplete, and that
    /// check happens before the keyring path is read and long before any SQL connection, database, keyring, HTTP host
    /// or SMTP listener exists. Everything created afterwards is retained: only the owned child process is stopped.
    /// </summary>
    private async Task<ScenarioOutcome?> RunScenarioAsync(string suffix, string configuredMode)
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var suppliedKeysPath = Environment.GetEnvironmentVariable(KeysPathVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection) || string.IsNullOrWhiteSpace(suppliedKeysPath))
        {
            // The two variables are one opt-in pair: with either one absent the scenario is not opted in.
            var absent = string.IsNullOrWhiteSpace(suppliedConnection) ? ConnectionVariable : KeysPathVariable;
            output.WriteLine(
                $"N/A: {absent} is not set, so the opt-in pair {ConnectionVariable} and {KeysPathVariable} is " +
                "incomplete; no SQL connection, database, keyring, HTTP host or SMTP listener was used.");
            return null;
        }

        // Validate the connection string, the derived keyring path and the web host before anything connects.
        var plan = PlanHarness(suppliedConnection, suffix);
        // The real business instant is the only source of the seeded dates, and a run too close to midnight is refused
        // here, before the derived database is created, so nothing irreversible exists yet.
        var businessNow = ReadBusinessNow();
        output.WriteLine(
            $"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', host port {plan.Port}, " +
            $"mode {configuredMode}, business now {businessNow:yyyy-MM-dd HH:mm:ss}.");

        using var smtp = new LoopbackSmtpServer(AcceptWithFinalReplyAsync);
        output.WriteLine(
            $"SMTP: loopback fake provider listening on 127.0.0.1:{smtp.Port} (no external network, no credentials, no TLS).");

        await AssertDatabaseAbsentAsync(plan.DatabaseName);
        var applied = await new DatabaseMigrator(plan.ConnectionString).MigrateAsync();
        Assert.Contains("001_schema.sql", applied);
        Assert.Contains("002_client_account_pending_names.sql", applied);
        Assert.Contains("003_outbox_dispatch.sql", applied);
        output.WriteLine(
            $"SQL: applied {applied.Count} schema script(s) to the new database '{plan.DatabaseName}' (retained, never dropped).");

        var seeded = await SeedAsync(plan.ConnectionString, businessNow);
        output.WriteLine(
            $"SQL: seeded business #{seeded.BusinessId} with four expected notices " +
            $"({string.Join(", ", seeded.Notices.Select(notice => notice.DedupKey))}) before the host started.");

        var host = StartHost(plan, configuredMode, smtp.Port);
        try
        {
            await WaitUntilReadyAsync(host);
            output.WriteLine($"HTTP: owned host (pid {host.Process.Id}) became ready on {plan.BaseUrl} in {configuredMode} mode.");
            AssertIsolatedKeyringCreated(plan.KeysPath);

            await WaitForOutputAsync(host, ScheduledJobsHostedService.TickCompletedMessage, TickBudget);
            output.WriteLine("HOST: the immediate scheduled-jobs tick completed (fixed marker observed on the child output).");

            var notices = new Dictionary<string, NotificationRow>(StringComparer.Ordinal);
            foreach (var expected in seeded.Notices)
                notices[expected.Role] = await ReadNotificationAsync(plan.ConnectionString, expected.DedupKey);

            // Nothing beyond the four expected rows: the reminder job had zero candidates and the outbox never invented
            // a fifth notice.
            var total = await CountAsync(plan.ConnectionString, "SELECT COUNT(*) FROM Notifications");
            Assert.Equal(seeded.Notices.Count, total);

            var mentioned = seeded.Notices.Any(notice => host.OutputContains(notice.Recipient));

            return new ScenarioOutcome(
                plan.DatabaseName,
                plan.KeysPath,
                seeded.Notices.ToDictionary(notice => notice.Role, StringComparer.Ordinal),
                notices,
                smtp.ConnectionCount,
                mentioned);
        }
        finally
        {
            await StopHostAsync(host.Process);
        }
    }

    // --- Isolation harness --------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the real business instant once and refuses a run that is too close to midnight. It runs before the derived
    /// database is created, so a refusal leaves no database, keyring or host behind.
    /// </summary>
    private static DateTime ReadBusinessNow()
    {
        var now = new BusinessClock(TimeZoneInfo.FindSystemTimeZoneById(BusinessTimeZone)).Now;
        if (now.TimeOfDay >= MidnightRunout)
            throw new InvalidOperationException(
                $"The business clock reads {now:yyyy-MM-dd HH:mm:ss}, too close to midnight for a same-day seed whose " +
                "tick must land on the same calendar day; no SQL connection, database, keyring or host was created. " +
                "Re-run earlier in the day or after midnight.");
        return now;
    }

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
            ApplicationName = "WorkUp T25 scheduled notices host integration test"
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

    // --- Owned child process ------------------------------------------------------------------------------------

    private RunningHost StartHost(HarnessPlan plan, string configuredMode, int smtpPort)
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
        // isolated keyring is development-only.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__WorkUp"] = plan.ConnectionString;
        startInfo.Environment["ASPNETCORE_URLS"] = plan.BaseUrl;
        startInfo.Environment["WorkUp__TimeZone"] = BusinessTimeZone;
        startInfo.Environment["WorkUp__Development__DataProtectionKeysPath"] = plan.KeysPath;
        startInfo.Environment["WorkUp__PublicBaseUrl"] = plan.BaseUrl;
        startInfo.Environment["WorkUp__MigrateOnStartup"] = "true";
        // The fixture must observe the real business clock and the shipped notification path; the test tools that this
        // flag gates are out of the T25 contract, so they stay off even though Development would enable them.
        startInfo.Environment["WorkUp__Features__DevTools"] = "false";
        startInfo.Environment["WorkUp__Email__Mode"] = configuredMode;
        startInfo.Environment["WorkUp__Email__From"] = FromAddress;
        // The loopback provider listens in both scenarios, so "zero connections" in Outbox mode is a real observation.
        startInfo.Environment["WorkUp__Email__Smtp__Host"] = "127.0.0.1";
        startInfo.Environment["WorkUp__Email__Smtp__Port"] = smtpPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["WorkUp__Email__Smtp__EnableSsl"] = "false";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the owned WorkUp web host process.");
        var host = new RunningHost(process, plan.BaseUrl);
        // Capture stdout so the tick marker can be observed and the absence of any recipient can be asserted; the pipe
        // buffers anything written before the asynchronous read starts. Nothing is ever echoed anywhere.
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

    private static async Task WaitForOutputAsync(RunningHost host, string marker, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (host.OutputContains(marker))
                return;
            if (host.Process.HasExited)
                throw new InvalidOperationException(
                    $"The owned WorkUp host (pid {host.Process.Id}) exited with code {host.Process.ExitCode} before logging the marker.");

            await Task.Delay(ReadinessPoll);
        }

        throw new TimeoutException(
            $"The owned WorkUp host did not log the fixed scheduled-jobs marker within {budget.TotalSeconds:0} seconds.");
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

    // --- SQL target, migration and seed -------------------------------------------------------------------------

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

    /// <summary>
    /// Seeds one synthetic business shaped so every one of the three T25 jobs has exactly one eligible notice at the
    /// tick instant: the owner summary for tomorrow (an individual turn with a live booking plus a class), the current
    /// stock bucket (anchor exactly one cadence before now), and the two membership stages (expiry today and three days
    /// from now). All dates are derived from the real business instant; nothing is derived from a wall-clock literal.
    /// </summary>
    private static async Task<SeededTarget> SeedAsync(string connectionString, DateTime businessNow)
    {
        var today = DateOnly.FromDateTime(businessNow);
        var anchor = businessNow.AddDays(-StockReviewEveryDays);
        var stockDue = StockReviewService.CurrentDueDate(anchor, StockReviewEveryDays, businessNow)
            ?? throw new InvalidOperationException(
                "The seeded stock cadence did not produce a due bucket at the seed instant.");
        var summaryTarget = today.AddDays(1);
        var expiringOn = today.AddDays(MembershipListRules.ExpiresSoonThresholdDays);
        var createdAt = businessNow.AddDays(-30);
        var bookingCreatedAt = businessNow.AddDays(-1);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var businessId = await InsertAsync(connection, """
            INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, ReminderLeadHours, DailySummaryTime, StockReviewEveryDays, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@name, @slug, @phone, @owner, @lead, @summaryTime, @everyDays, @createdAt)
            """,
            ("@name", BusinessName), ("@slug", BusinessSlug), ("@phone", ContactPhone),
            ("@owner", OwnerRecipient), ("@lead", ReminderLeadHours),
            ("@summaryTime", new TimeOnly(SummaryHour, 0)), ("@everyDays", StockReviewEveryDays),
            ("@createdAt", anchor));

        var serviceId = await InsertAsync(connection, """
            INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity)
            OUTPUT INSERTED.Id
            VALUES (@business, @name, 12000.00, 30, 8)
            """,
            ("@business", businessId), ("@name", ServiceName));

        var workerId = await InsertAsync(connection, """
            INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil)
            OUTPUT INSERTED.Id
            VALUES (@business, 'Synthetic', 'T25', @phone, @cuil)
            """,
            ("@business", businessId), ("@phone", ContactPhone),
            ("@cuil", "20" + businessId.ToString("D9", CultureInfo.InvariantCulture)));

        var bookingClientId = await InsertAsync(connection, """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, 'T25', 'Booking', @email, @createdAt)
            """,
            ("@business", businessId), ("@email", BookingRecipient), ("@createdAt", createdAt));

        var expiringClientId = await InsertAsync(connection, """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, 'T25', 'Expiring', @email, @createdAt)
            """,
            ("@business", businessId), ("@email", ExpiringRecipient), ("@createdAt", createdAt));

        // Decoy account: the membership notice must travel to Clients.Email, never to the account email.
        await InsertAsync(connection, """
            INSERT INTO ClientAccounts (BusinessId, ClientId, Email, PasswordHash, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @client, @email, 'synthetic-hash', @createdAt)
            """,
            ("@business", businessId), ("@client", expiringClientId),
            ("@email", DecoyAccountRecipient), ("@createdAt", createdAt));

        var expiredClientId = await InsertAsync(connection, """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, 'T25', 'Expired', @email, @createdAt)
            """,
            ("@business", businessId), ("@email", ExpiredRecipient), ("@createdAt", createdAt));

        var (expiringEnrollmentId, expiringPeriodId) = await SeedMembershipAsync(
            connection, businessId, expiringClientId, coverageStart: expiringOn.AddMonths(-1), expiresOn: expiringOn,
            createdAt);
        var (expiredEnrollmentId, expiredPeriodId) = await SeedMembershipAsync(
            connection, businessId, expiredClientId, coverageStart: today.AddMonths(-1), expiresOn: today, createdAt);

        var individualAppointmentId = await InsertAsync(connection, """
            INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @kind, @service, @worker, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)
            """,
            ("@business", businessId), ("@kind", AppointmentKind.Individual), ("@service", serviceId),
            ("@worker", workerId), ("@startsAt", summaryTarget.ToDateTime(new TimeOnly(UpcomingIndividualHour, 0))),
            ("@endsAt", summaryTarget.ToDateTime(new TimeOnly(UpcomingIndividualHour + 1, 0))),
            ("@origin", AppointmentOrigin.Reserved), ("@status", AppointmentStatus.Scheduled),
            ("@createdAt", createdAt));

        await InsertAsync(connection, """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @appointment, @client, NULL, NULL, @status, @createdAt, NULL)
            """,
            ("@business", businessId), ("@appointment", individualAppointmentId), ("@client", bookingClientId),
            ("@status", BookingStatus.Pending), ("@createdAt", bookingCreatedAt));

        // A class session with no booking: the summary keeps it, and the reminder scan only reads bookings.
        await InsertAsync(connection, """
            INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @kind, @service, @worker, NULL, @startsAt, @endsAt, 8, NULL, @status, @createdAt)
            """,
            ("@business", businessId), ("@kind", AppointmentKind.ClassSession), ("@service", serviceId),
            ("@worker", workerId), ("@startsAt", summaryTarget.ToDateTime(new TimeOnly(UpcomingClassHour, 0))),
            ("@endsAt", summaryTarget.ToDateTime(new TimeOnly(UpcomingClassHour + 1, 0))),
            ("@status", AppointmentStatus.Scheduled), ("@createdAt", createdAt));

        await InsertAsync(connection, """
            INSERT INTO Supplies (BusinessId, Name, CurrentStock, MinimumStock, LastDeclaredAt, IsActive)
            OUTPUT INSERTED.Id
            VALUES (@business, @name, 12.50, 3.00, @lastDeclared, 1)
            """,
            ("@business", businessId), ("@name", SupplyName), ("@lastDeclared", businessNow));

        var notices = new List<ExpectedNotice>
        {
            new(SummaryRole, DailySummaryService.DedupKey(businessId, summaryTarget),
                NotificationType.DailySummary, OwnerRecipient),
            new(StockRole, StockReviewService.DedupKey(businessId, stockDue),
                NotificationType.StockReview, OwnerRecipient),
            new(ExpiringRole, MembershipExpiryService.DedupKey(
                    expiringEnrollmentId, expiringPeriodId, MembershipExpiryStage.Expiring),
                NotificationType.MembershipExpiring, ExpiringRecipient),
            new(ExpiredRole, MembershipExpiryService.DedupKey(
                    expiredEnrollmentId, expiredPeriodId, MembershipExpiryStage.Expired),
                NotificationType.MembershipExpired, ExpiredRecipient)
        };

        return new SeededTarget(businessId, notices);
    }

    /// <summary>One active enrollment paid through a single monthly period, as the expiry scan expects it.</summary>
    private static async Task<(int EnrollmentId, int PeriodId)> SeedMembershipAsync(
        SqlConnection connection, int businessId, int clientId,
        DateOnly coverageStart, DateOnly expiresOn, DateTime createdAt)
    {
        var enrollmentId = await InsertAsync(connection, """
            INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @client, 2, @status, 5000.00, @createdAt)
            """,
            ("@business", businessId), ("@client", clientId), ("@status", EnrollmentStatus.Active),
            ("@createdAt", createdAt));

        var paymentId = await InsertAsync(connection, """
            INSERT INTO Payments (BusinessId, ClientId, Concept, EnrollmentId, Amount, PaidAt, RecordedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @client, @membership, @enrollment, 5000.00, @paidAt, @recordedAt)
            """,
            ("@business", businessId), ("@client", clientId), ("@membership", PaymentConcept.Membership),
            ("@enrollment", enrollmentId), ("@paidAt", createdAt), ("@recordedAt", createdAt));

        var periodId = await InsertAsync(connection, """
            INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@enrollment, @coverageStart, @expiresOn, @payment, @createdAt)
            """,
            ("@enrollment", enrollmentId), ("@coverageStart", coverageStart), ("@expiresOn", expiresOn),
            ("@payment", paymentId), ("@createdAt", createdAt));

        return (enrollmentId, periodId);
    }

    private static async Task<int> InsertAsync(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.Add(ToParameter(name, value));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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

    private static async Task<NotificationRow> ReadNotificationAsync(string connectionString, string dedupKey)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT Type, Recipient, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt, ClaimedAt, DispatchKey
              FROM Notifications
             WHERE DedupKey = @dedupKey
            """,
            connection);
        command.Parameters.Add("@dedupKey", SqlDbType.NVarChar, 150).Value = dedupKey;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(
                $"The scheduled notice '{dedupKey}' was not written by the immediate tick; the tick marker was " +
                "observed, so the seeded shape did not produce an eligible candidate for that job.");

        return new NotificationRow(
            reader.GetByte(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetByte(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9));
    }

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // --- Loopback fake SMTP provider ----------------------------------------------------------------------------

    private static bool StartsWith(string line, string command) =>
        line.StartsWith(command, StringComparison.OrdinalIgnoreCase);

    /// <summary>Answers every command and confirms each message with the final <c>250</c> after the whole body.</summary>
    private static async Task AcceptWithFinalReplyAsync(SmtpSession session)
    {
        await session.WriteAsync("220 workup-t25 ESMTP ready");
        string? line;
        while ((line = await session.ReadLineAsync()) is not null)
        {
            if (StartsWith(line, "EHLO"))
            {
                await session.WriteAsync("250-workup-t25");
                await session.WriteAsync("250 OK");
            }
            else if (StartsWith(line, "DATA"))
            {
                await session.WriteAsync("354 End data with <CR><LF>.<CR><LF>");
                await session.ReadDataBlockAsync();
                await session.WriteAsync("250 OK queued as workup-t25");
            }
            else if (StartsWith(line, "QUIT"))
            {
                await session.WriteAsync("221 Bye");
                return;
            }
            else
            {
                await session.WriteAsync("250 OK");
            }
        }
    }

    /// <summary>One accepted connection: line-oriented SMTP that consumes the DATA block.</summary>
    private sealed class SmtpSession(StreamReader reader, StreamWriter writer)
    {
        public async Task<string?> ReadLineAsync() => await reader.ReadLineAsync();

        public async Task WriteAsync(string line)
        {
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
        }

        /// <summary>Reads the DATA block up to the terminating dot.</summary>
        public async Task ReadDataBlockAsync()
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null && line != ".")
            {
            }
        }
    }

    /// <summary>
    /// Minimal real SMTP server on a random loopback port. It never touches the external network, requires no
    /// credential and offers no TLS; each accepted connection runs one scripted session.
    /// </summary>
    private sealed class LoopbackSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<SmtpSession, Task> _script;
        private readonly Task _acceptLoop;
        private int _connections;

        public LoopbackSmtpServer(Func<SmtpSession, Task> script)
        {
            _script = script;
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
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(
                        stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                    using var writer = new StreamWriter(stream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                    {
                        NewLine = "\r\n",
                        AutoFlush = true
                    };
                    await _script(new SmtpSession(reader, writer));
                }
                catch (Exception)
                {
                    // The client closed the connection; the scripted session simply ends.
                }
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

    // --- Owned host output --------------------------------------------------------------------------------------

    /// <summary>
    /// The owned child host plus a bounded capture of its standard output, used to observe the committed fixed tick
    /// marker and to assert that no recipient ever reached the log. The lines are never echoed anywhere: only a
    /// substring search happens.
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

    private sealed record HarnessPlan(
        string ConnectionString, string DatabaseName, string KeysPath, string WebHostPath, string WebRoot, int Port)
    {
        public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record ExpectedNotice(string Role, string DedupKey, NotificationType Type, string Recipient);

    private sealed record SeededTarget(int BusinessId, IReadOnlyList<ExpectedNotice> Notices);

    private sealed record NotificationRow(
        byte Type,
        string Recipient,
        string DedupKey,
        byte Status,
        int Attempts,
        string? LastError,
        DateTime CreatedAt,
        DateTime? SentAt,
        DateTime? ClaimedAt,
        Guid? DispatchKey);

    private sealed record ScenarioOutcome(
        string DatabaseName,
        string KeysPath,
        IReadOnlyDictionary<string, ExpectedNotice> Expected,
        IReadOnlyDictionary<string, NotificationRow> Notices,
        int SmtpConnections,
        bool ChildOutputMentionedAnyRecipient);
}
