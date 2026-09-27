using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Jobs;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in real-host/SQL/SMTP smoke of the scheduled outbox worker (T23, RF-10/RF-17). The core, the dispatcher and the
/// SQL port are already proven elsewhere against fakes and against a real database with a service call; what only this
/// harness can prove is the whole wired path of the shipped host: the immediate tick of the registered
/// <see cref="ScheduledJobsHostedService"/> running against a real migrated database, the composition root choosing the
/// sender from <c>WorkUp:Email:Mode</c>, the real <c>SmtpEmailSender</c> talking to a loopback provider, the rows the
/// tick leaves behind, and the mode-truthful Razor copy the pages actually render.
/// <para>
/// Three scenarios, one retained database and one isolated DPAPI keyring each, driven by the same opt-in pair
/// <c>WORKUP_T23_HOST_CONNECTION</c> and <c>WORKUP_T23_HOST_KEYS_PATH</c>. Without that pair every scenario logs N/A and
/// returns before any SQL connection, database, keyring, HTTP host or SMTP listener exists, so the default suite is
/// unaffected. With it set, the harness refuses to run unless it can prove the runtime is isolated first: a fresh
/// <c>WorkUpDemoT23Host*</c> database that does not exist yet and whose name is at most 128 characters, an encrypted
/// keyring folder under the OS temp directory whose leaf starts with <c>WorkUpDemoT12KeysT23</c> and that has no
/// reparse point ancestor, and a free loopback port. Both opt-in facts are validated before anything connects, the
/// database is created after <c>DB_ID</c> proved it absent, the schema is migrated before the host starts, and the
/// database and the encrypted keys are retained -- never reused, never dropped and never deleted.
/// </para>
/// <para>
/// The three scenarios, derived as <c>_Retain</c>, <c>_Sent</c> and <c>_Unknown</c>:
/// <list type="number">
/// <item><b>Outbox</b> (Retain): the immediate tick completes -- proven by the committed fixed
/// <see cref="ScheduledJobsHostedService.TickCompletedMessage"/> marker, never by waiting a real minute -- and the
/// queued notice stays <c>Pending</c> with zero attempts while a reachable loopback provider records no connection at
/// all.</item>
/// <item><b>Smtp</b> with a provider that answers <c>250</c> to the final <c>DATA</c>: the notice becomes <c>Sent</c>
/// with one attempt and the claim cleared, after exactly one SMTP transaction.</item>
/// <item><b>Smtp</b> with a provider that receives the whole body and then closes gracefully without the final
/// <c>250</c>: the outcome is unknown, so the notice becomes <c>ReviewRequired</c> with one attempt, a fixed safe
/// reason and the claim retained, and the single SMTP transaction is never retried.</item>
/// </list>
/// </para>
/// <para>
/// Every scenario also reads <c>/</c> and the public recovery and resend pages and compares them with the committed
/// <see cref="EmailDeliveryCopy"/> helpers, so the Razor views are proven to render the sentence that matches the
/// configured mode instead of merely compiling.
/// </para>
/// <para>
/// The +30 days "no automatic reclaim" property is deliberately not repeated here: it is already established by the
/// existing core and SQL fixtures. This harness does not replace an independent run either: until a human executes it
/// against a real LocalDB instance with the opt-in pair set, every scenario is compiled and N/A only, never real
/// proof.
/// </para>
/// </summary>
public sealed class OutboxSchedulerHostIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string ConnectionVariable = "WORKUP_T23_HOST_CONNECTION";
    private const string KeysPathVariable = "WORKUP_T23_HOST_KEYS_PATH";
    private const string KeysLeafPrefix = "WorkUpDemoT12KeysT23";
    private const string DatabasePrefix = "WorkUpDemoT23Host";
    private const string BusinessTimeZone = "America/Argentina/Buenos_Aires";

    /// <summary>Derived suffix of each retained target; one fresh database and one fresh keyring per scenario.</summary>
    private const string RetainSuffix = "_Retain";
    private const string SentSuffix = "_Sent";
    private const string UnknownSuffix = "_Unknown";

    private const string BusinessName = "Peluqueria T23 Host";
    private const string BusinessSlug = "t23-host";
    private const string ContactPhone = "1145550000";
    private const string FromAddress = "WorkUp <no-responder@workup.local>";
    private const string Recipient = "t23-host@example.invalid";
    private const string Subject = "Turno confirmado T23";
    private const string BodyHtml = "<p>Cuerpo del aviso T23</p>";
    private const string DedupKey = "t23-host:outbox:1";

    private static readonly Regex SafeDatabaseName =
        new(@"\A" + DatabasePrefix + @"[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TickBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StatusBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StatusPoll = TimeSpan.FromMilliseconds(250);

    // --- Scenario 1: Outbox retains the notice and never contacts the provider ---------------------------------

    [Fact]
    public async Task Outbox_mode_host_tick_retains_the_pending_notice_without_any_smtp_connection()
    {
        var outcome = await RunScenarioAsync(
            RetainSuffix, "Outbox", AcceptWithFinalReplyAsync, NotificationStatus.Pending, requireTickMarker: true);
        if (outcome is null)
            return;

        // The immediate tick really ran (fixed marker) and it left the queued notice exactly as it found it.
        Assert.Equal((byte)NotificationStatus.Pending, outcome.Notification.Status);
        Assert.Equal(0, outcome.Notification.Attempts);
        Assert.Null(outcome.Notification.LastError);
        Assert.Null(outcome.Notification.SentAt);
        Assert.Null(outcome.Notification.ClaimedAt);
        Assert.Null(outcome.Notification.DispatchKey);

        // A reachable loopback provider saw nothing: Retain never claims and never sends.
        Assert.Equal(0, outcome.SmtpConnections);
        Assert.Empty(outcome.SmtpDataLines);

        AssertModeCopy(outcome, OutboxMode.Retain);
        output.WriteLine(
            $"Retain assertions passed on retained database '{outcome.DatabaseName}' with isolated keyring " +
            $"'{outcome.KeysPath}': tick marker observed, notice Pending with 0 attempts and 0 SMTP connections.");
    }

    // --- Scenario 2: a confirmed SMTP acceptance marks the notice Sent ----------------------------------------

    [Fact]
    public async Task Smtp_mode_host_tick_sends_through_the_loopback_provider_and_marks_the_notice_sent()
    {
        var outcome = await RunScenarioAsync(
            SentSuffix, "Smtp", AcceptWithFinalReplyAsync, NotificationStatus.Sent, requireTickMarker: false);
        if (outcome is null)
            return;

        Assert.Equal((byte)NotificationStatus.Sent, outcome.Notification.Status);
        Assert.Equal(1, outcome.Notification.Attempts);
        Assert.Null(outcome.Notification.LastError);
        Assert.NotNull(outcome.Notification.SentAt);
        Assert.Null(outcome.Notification.ClaimedAt);
        Assert.Null(outcome.Notification.DispatchKey);

        // Exactly one SMTP transaction reached the provider and it carried the full body.
        Assert.Equal(1, outcome.SmtpConnections);
        Assert.NotEmpty(outcome.SmtpDataLines);

        AssertModeCopy(outcome, OutboxMode.Deliver);
        output.WriteLine(
            $"Sent assertions passed on retained database '{outcome.DatabaseName}' with isolated keyring " +
            $"'{outcome.KeysPath}': one SMTP transaction with final 250, notice Sent with 1 attempt and claim cleared.");
    }

    // --- Scenario 3: an unacknowledged body becomes ReviewRequired, never a retry ------------------------------

    [Fact]
    public async Task Smtp_mode_host_tick_keeps_an_unacknowledged_send_for_review_without_retrying_it()
    {
        var outcome = await RunScenarioAsync(
            UnknownSuffix, "Smtp", FullBodyWithoutFinalReplyAsync, NotificationStatus.ReviewRequired, requireTickMarker: false);
        if (outcome is null)
            return;

        // The whole body left, the provider never acknowledged it, so the outcome is unknown: a human decides.
        Assert.Equal((byte)NotificationStatus.ReviewRequired, outcome.Notification.Status);
        Assert.Equal(1, outcome.Notification.Attempts);
        Assert.Equal(OutboxDispatchService.UnknownReason, outcome.Notification.LastError);
        Assert.Null(outcome.Notification.SentAt);
        Assert.NotNull(outcome.Notification.ClaimedAt);
        Assert.NotNull(outcome.Notification.DispatchKey);

        // Only one SMTP transaction: the uncertain attempt is never repeated automatically.
        Assert.Equal(1, outcome.SmtpConnections);
        Assert.NotEmpty(outcome.SmtpDataLines);

        AssertModeCopy(outcome, OutboxMode.Deliver);
        output.WriteLine(
            $"ReviewRequired assertions passed on retained database '{outcome.DatabaseName}' with isolated keyring " +
            $"'{outcome.KeysPath}': full DATA without the final 250, safe reason, claim retained and one SMTP transaction.");
    }

    // --- Shared scenario ----------------------------------------------------------------------------------------

    /// <summary>
    /// Drives one scenario end to end. It returns <c>null</c> -- N/A -- when the opt-in pair is incomplete, and that
    /// check happens before the keyring path is read and long before any SQL connection, database, keyring, HTTP host
    /// or SMTP listener exists. Everything created afterwards is retained: only the owned child process is stopped.
    /// </summary>
    private async Task<ScenarioOutcome?> RunScenarioAsync(
        string suffix,
        string configuredMode,
        Func<SmtpSession, Task> smtpScript,
        NotificationStatus expectedStatus,
        bool requireTickMarker)
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var suppliedKeysPath = Environment.GetEnvironmentVariable(KeysPathVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection) || string.IsNullOrWhiteSpace(suppliedKeysPath))
        {
            // The two variables are one opt-in pair: with either one absent the scenario is not opted in.
            var absent = string.IsNullOrWhiteSpace(suppliedConnection) ? ConnectionVariable : KeysPathVariable;
            output.WriteLine(
                $"N/A: {absent} is not set, so the opt-in pair {ConnectionVariable} and {KeysPathVariable} is incomplete; " +
                "no SQL connection, database, keyring, HTTP host or SMTP listener was used.");
            return null;
        }

        // Validate the connection string, the derived keyring path and the web host before anything connects.
        var plan = PlanHarness(suppliedConnection, suffix);
        output.WriteLine(
            $"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', host port {plan.Port}.");

        using var smtp = new LoopbackSmtpServer(smtpScript);
        output.WriteLine(
            $"SMTP: loopback fake provider listening on 127.0.0.1:{smtp.Port} (no external network, no credentials, no TLS).");

        await AssertDatabaseAbsentAsync(plan.DatabaseName);
        var applied = await new DatabaseMigrator(plan.ConnectionString).MigrateAsync();
        Assert.Contains("001_schema.sql", applied);
        Assert.Contains("002_client_account_pending_names.sql", applied);
        Assert.Contains("003_outbox_dispatch.sql", applied);
        output.WriteLine(
            $"SQL: applied {applied.Count} schema script(s) to the new database '{plan.DatabaseName}' (retained, never dropped).");

        // One minute before the host starts, so the immediate tick finds the notice due without waiting a real minute.
        var createdAt = new BusinessClock(TimeZoneInfo.FindSystemTimeZoneById(BusinessTimeZone)).Now.AddMinutes(-1);
        var seeded = await SeedAsync(plan.ConnectionString, createdAt);
        output.WriteLine(
            $"SQL: seeded business #{seeded.BusinessId} and one Pending notice #{seeded.NotificationId} due at " +
            $"{createdAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} before the host started.");

        var host = StartHost(plan, configuredMode, smtp.Port);
        try
        {
            await WaitUntilReadyAsync(host);
            output.WriteLine($"HTTP: owned host (pid {host.Process.Id}) became ready on {host.BaseUrl} in {configuredMode} mode.");

            if (requireTickMarker)
            {
                await WaitForOutputAsync(host, ScheduledJobsHostedService.TickCompletedMessage, TickBudget);
                output.WriteLine("HOST: the immediate scheduled-jobs tick completed (fixed marker observed on the child output).");
            }

            // In Retain the row must be unchanged once the tick completed; in Smtp the tick writes the final status.
            var notification = expectedStatus == NotificationStatus.Pending
                ? await ReadNotificationAsync(plan.ConnectionString, seeded.NotificationId)
                : await WaitForStatusAsync(plan.ConnectionString, seeded.NotificationId, expectedStatus);

            var (homeStatus, homeHtml) = await GetHtmlAsync(host.BaseUrl, "/");
            Assert.Equal(HttpStatusCode.OK, homeStatus);
            var (recoverStatus, recoverHtml) = await GetHtmlAsync(host.BaseUrl, $"/r/{BusinessSlug}/cuenta/recuperar");
            Assert.Equal(HttpStatusCode.OK, recoverStatus);
            var (resendStatus, resendHtml) = await GetHtmlAsync(host.BaseUrl, $"/r/{BusinessSlug}/cuenta/reenviar");
            Assert.Equal(HttpStatusCode.OK, resendStatus);

            // The rendered antiforgery forms are what materialize the isolated keyring; assert it only after they ran.
            AssertKeyringCreated(plan.KeysPath);

            return new ScenarioOutcome(
                plan.DatabaseName, plan.KeysPath, notification, smtp.ConnectionCount, smtp.DataLines,
                homeHtml, recoverHtml, resendHtml);
        }
        finally
        {
            await StopHostAsync(host.Process);
        }
    }

    // --- Mode-truthful copy -------------------------------------------------------------------------------------

    /// <summary>
    /// Compares the rendered pages with the committed copy helpers. The bodies are HTML-decoded first, because Razor
    /// encodes non-basic-latin characters, and only the exact sentence of the configured mode may appear.
    /// </summary>
    private static void AssertModeCopy(ScenarioOutcome outcome, OutboxMode mode)
    {
        var other = mode == OutboxMode.Retain ? OutboxMode.Deliver : OutboxMode.Retain;

        var home = WebUtility.HtmlDecode(outcome.HomeHtml);
        Assert.Contains("devtools-banner", home, StringComparison.Ordinal);
        Assert.Contains(EmailDeliveryCopy.DevToolsBanner(mode), home, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailDeliveryCopy.DevToolsBanner(other), home, StringComparison.Ordinal);

        var recover = WebUtility.HtmlDecode(outcome.RecoverHtml);
        Assert.Contains(EmailDeliveryCopy.RecoverPrompt(mode, BusinessName), recover, StringComparison.Ordinal);

        var resend = WebUtility.HtmlDecode(outcome.ResendHtml);
        Assert.Contains(EmailDeliveryCopy.ResendPrompt(mode, BusinessName), resend, StringComparison.Ordinal);
    }

    // --- HTTP helpers -------------------------------------------------------------------------------------------

    private static async Task<(HttpStatusCode Status, string Body)> GetHtmlAsync(string baseUrl, string path)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
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

    // --- Owned child process ------------------------------------------------------------------------------------

    private RunningHost StartHost(HarnessPlan plan, string configuredMode, int smtpPort)
    {
        var baseUrl = $"http://127.0.0.1:{plan.Port}";
        var startInfo = new ProcessStartInfo(plan.WebHostPath)
        {
            WorkingDirectory = plan.WebRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Drop every inherited WorkUp/ASP.NET override first, so nothing from the caller's shell can retarget the child.
        ScrubInheritedOverrides(startInfo);
        // Both environment keys are set, so the child is in Development no matter which one the host reads first; the
        // isolated keyring and the DevTools banner depend on it.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__WorkUp"] = plan.ConnectionString;
        startInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
        startInfo.Environment["WorkUp__Development__DataProtectionKeysPath"] = plan.KeysPath;
        startInfo.Environment["WorkUp__PublicBaseUrl"] = baseUrl;
        startInfo.Environment["WorkUp__MigrateOnStartup"] = "true";
        startInfo.Environment["WorkUp__Features__DevTools"] = "true";
        startInfo.Environment["WorkUp__Email__Mode"] = configuredMode;
        startInfo.Environment["WorkUp__Email__From"] = FromAddress;
        startInfo.Environment["WorkUp__Email__Smtp__Host"] = "127.0.0.1";
        startInfo.Environment["WorkUp__Email__Smtp__Port"] = smtpPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["WorkUp__Email__Smtp__EnableSsl"] = "false";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the owned WorkUp web host process.");
        var host = new RunningHost(process, baseUrl);
        // Capture stdout so the committed tick marker can be observed; the pipe buffers anything written before the
        // asynchronous read starts, so no line is lost. Nothing is ever echoed to the test output.
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

    /// <summary>
    /// Removes inherited configuration that could redirect the child at a real database, keyring, provider or
    /// environment. Only the variables this harness sets afterwards survive, so the child is bound to the derived
    /// target alone.
    /// </summary>
    private static void ScrubInheritedOverrides(ProcessStartInfo startInfo)
    {
        var risky = startInfo.Environment.Keys
            .Where(name =>
                name.StartsWith("WORKUP_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WorkUp__", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
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

    private void AssertKeyringCreated(string keysPath)
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
            $"DataProtection: {files.Length} encrypted key file(s) generated only inside the dedicated temp keyring folder.");
    }

    // --- Isolation harness --------------------------------------------------------------------------------------

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
            ApplicationName = "WorkUp T23 outbox host integration test"
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
        var leaf = Path.GetFileName(basePath);
        if (!leaf.StartsWith(KeysLeafPrefix, StringComparison.Ordinal))
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
    /// Seeds the minimum the host needs: one business so the public recovery and resend pages resolve a slug, and one
    /// <c>Pending</c> notice shaped exactly like a pre-T23 producer -- the three dispatch columns stay <c>NULL</c> and
    /// the tenant column is <c>NULL</c>, so the effective due instant is the seeded <c>CreatedAt</c>.
    /// </summary>
    private async Task<SeededTarget> SeedAsync(string connectionString, DateTime createdAt)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var businessId = await InsertIdAsync(connection, """
            INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@name, @slug, @contactPhone, @createdAt)
            """,
            ("@name", BusinessName), ("@slug", BusinessSlug), ("@contactPhone", ContactPhone), ("@createdAt", createdAt));

        var notificationId = await InsertIdAsync(connection, """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt)
            OUTPUT INSERTED.Id
            VALUES (NULL, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL)
            """,
            ("@type", (byte)NotificationType.BookingCreated),
            ("@recipient", Recipient),
            ("@subject", Subject),
            ("@bodyHtml", BodyHtml),
            ("@dedupKey", DedupKey),
            ("@status", (byte)NotificationStatus.Pending),
            ("@createdAt", createdAt));

        return new SeededTarget(businessId, notificationId);
    }

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
            AddParameter(command, name, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static void AddParameter(SqlCommand command, string name, object value)
    {
        if (value is DateTime dateTime)
            command.Parameters.Add(name, SqlDbType.DateTime2).Value = dateTime;
        else
            command.Parameters.AddWithValue(name, value);
    }

    private static async Task<NotificationRow> ReadNotificationAsync(string connectionString, int id)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Status, Attempts, LastError, SentAt, ClaimedAt, DispatchKey FROM Notifications WHERE Id = @id",
            connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"Notice #{id} was not found in the isolated database.");

        return new NotificationRow(
            reader.GetByte(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5));
    }

    /// <summary>Bounded poll for the status the immediate tick is expected to persist.</summary>
    private static async Task<NotificationRow> WaitForStatusAsync(
        string connectionString, int id, NotificationStatus expected)
    {
        var deadline = DateTime.UtcNow + StatusBudget;
        while (true)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Notice #{id} did not reach {expected} within {StatusBudget.TotalSeconds:0} seconds of the host tick.");

            var row = await ReadNotificationAsync(connectionString, id);
            if (row.Status == (byte)expected)
                return row;

            await Task.Delay(StatusPoll);
        }
    }

    // --- Loopback fake SMTP provider ----------------------------------------------------------------------------

    private static bool StartsWith(string line, string command) =>
        line.StartsWith(command, StringComparison.OrdinalIgnoreCase);

    /// <summary>Answers every command and confirms the message with the final <c>250</c> after the whole body.</summary>
    private static async Task AcceptWithFinalReplyAsync(SmtpSession session)
    {
        await session.WriteAsync("220 workup-t23 ESMTP ready");
        string? line;
        while ((line = await session.ReadLineAsync()) is not null)
        {
            if (StartsWith(line, "EHLO"))
            {
                await session.WriteAsync("250-workup-t23");
                await session.WriteAsync("250 OK");
            }
            else if (StartsWith(line, "DATA"))
            {
                await session.WriteAsync("354 End data with <CR><LF>.<CR><LF>");
                await session.ReadDataBlockAsync();
                await session.WriteAsync("250 OK queued as workup-t23");
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

    /// <summary>
    /// Receives the complete body through the terminating dot and then closes gracefully without the final <c>2xx</c>
    /// acknowledgement, so the client can never learn whether the provider accepted the message.
    /// </summary>
    private static async Task FullBodyWithoutFinalReplyAsync(SmtpSession session)
    {
        await session.WriteAsync("220 workup-t23 ESMTP ready");
        string? line;
        while ((line = await session.ReadLineAsync()) is not null)
        {
            if (StartsWith(line, "EHLO"))
            {
                await session.WriteAsync("250-workup-t23");
                await session.WriteAsync("250 OK");
            }
            else if (StartsWith(line, "DATA"))
            {
                await session.WriteAsync("354 End data with <CR><LF>.<CR><LF>");
                await session.ReadDataBlockAsync();
                return;
            }
            else
            {
                await session.WriteAsync("250 OK");
            }
        }
    }

    /// <summary>One accepted connection: line-oriented SMTP that records the DATA block for assertions.</summary>
    private sealed class SmtpSession(StreamReader reader, StreamWriter writer, List<string> dataLines)
    {
        public async Task<string?> ReadLineAsync() => await reader.ReadLineAsync();

        public async Task WriteAsync(string line)
        {
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
        }

        /// <summary>Reads the DATA block up to the terminating dot and records each line.</summary>
        public async Task ReadDataBlockAsync()
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null && line != ".")
            {
                lock (dataLines)
                    dataLines.Add(line);
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
        private readonly List<string> _dataLines = [];
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

        public IReadOnlyList<string> DataLines
        {
            get
            {
                lock (_dataLines)
                    return _dataLines.ToArray();
            }
        }

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
                    await _script(new SmtpSession(reader, writer, _dataLines));
                }
                catch (Exception)
                {
                    // The client closed the connection (a scenario that never sends the final reply); the session ends.
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
    /// The owned child host plus a bounded capture of its standard output, used only to observe the committed fixed
    /// tick marker. The lines are never echoed anywhere: only a substring search happens.
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

        public bool OutputContains(string marker)
        {
            lock (_gate)
                return _lines.Any(line => line.Contains(marker, StringComparison.Ordinal));
        }
    }

    private sealed record HarnessPlan(
        string ConnectionString, string DatabaseName, string KeysPath, string WebHostPath, string WebRoot, int Port);

    private sealed record SeededTarget(int BusinessId, int NotificationId);

    private sealed record NotificationRow(
        byte Status, int Attempts, string? LastError, DateTime? SentAt, DateTime? ClaimedAt, Guid? DispatchKey);

    private sealed record ScenarioOutcome(
        string DatabaseName,
        string KeysPath,
        NotificationRow Notification,
        int SmtpConnections,
        IReadOnlyList<string> SmtpDataLines,
        string HomeHtml,
        string RecoverHtml,
        string ResendHtml);
}
