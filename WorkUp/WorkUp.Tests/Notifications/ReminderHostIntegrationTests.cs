using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Data;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Jobs;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in real-host/SQL/SMTP fixture for the scheduled reminder of T24 (RF-17) inside the shipped host. The core, the
/// SQL port and the outbox dispatcher are already proven against fakes, against real SQL and against a real child host
/// separately; what only this harness can prove is the whole wired path of one process: the <em>immediate tick</em> of
/// the registered <see cref="ScheduledJobsHostedService"/> runs the real <see cref="ReminderScheduledJob"/> on
/// <see cref="ReminderService"/> with the real SQL reminder port against a real migrated database, the queued notice
/// is the row the host itself wrote, and the outbox job of that same tick dispatches it — or, in <c>Outbox</c> mode,
/// deliberately retains it — through the composition root's sender.
/// <para>
/// Two scenarios, one retained database and one isolated DPAPI keyring each, driven by the same opt-in pair
/// <c>WORKUP_T24_HOST_CONNECTION</c> and <c>WORKUP_T24_HOST_KEYS_PATH</c>. Without that pair every scenario logs N/A and
/// returns before any SQL connection, database, keyring, HTTP host or SMTP listener exists, so the default suite is
/// unaffected and this file adds no real evidence by itself.
/// </para>
/// <para>
/// The derived suffixes are <c>_Retain</c> and <c>_Smtp</c>: the database is the configured catalog plus the suffix
/// (must start with <c>WorkUpDemoT24Host</c>, no credential, no attached file, integrated LocalDB only) and the keyring
/// is the configured path plus the suffix (must be an absolute path inside the OS temp directory whose leaf starts with
/// <c>WorkUpDemoT12KeysT24Host</c>, must not exist yet and must have no reparse point ancestor). Both derived targets
/// are validated before anything connects, the database is created only after <c>DB_ID</c> proved it absent, and both
/// the database and the encrypted keys are retained afterwards: never reused, never dropped and never deleted. Only the
/// owned child process is stopped.
/// </para>
/// <para>
/// The seeded private link is a real one: the raw token is protected <em>before</em> the host starts, with the shipped
/// <see cref="DataProtectionLinkTokenProtector"/> purpose over the very same isolated DPAPI keyring the child will use,
/// so the immediate tick can open it exactly as the owner flow would have. The protection runs through the production
/// composition root (<c>AddWorkUp</c>) with a simulated host environment whose content root path is the child's own
/// working directory, because the framework derives the DataProtection application discriminator from the host content
/// root: both processes therefore derive the same subkeys from the same key ring. The framework normalizes that path
/// itself -- a REAL run observed the discriminator as the content root with backslashes and a trailing separator -- so
/// the pre-seed guard compares the provider discriminator with the content root as canonical directories (separator
/// and trailing invariant, case-insensitive on Windows) while the payload keeps using the provider's own
/// discriminator unchanged. That guard runs <em>before</em> the derived database is created or migrated, and it creates
/// no key: a content root that truly differs stops the scenario while nothing irreversible exists yet. The test process
/// never reads or writes the shared default keyring, and it never falls back to a fake or unencrypted token.
/// </para>
/// <para>
/// Scenario 1 (<c>_Retain</c>): <c>Outbox</c> mode, a <c>Pendiente</c> individual turn and a <c>Confirmado</c> one,
/// each with one live link. After the committed fixed <see cref="ScheduledJobsHostedService.TickCompletedMessage"/>
/// marker — never a real minute of waiting — both <c>reminder:booking:{id}</c> notices are <c>Pending</c> with zero
/// attempts, the pending body carries the trusted private URL and the confirmation request, the confirmed body carries
/// its URL without the request, and the reachable loopback provider recorded no connection at all.
/// </para>
/// <para>
/// Scenario 2 (<c>_Smtp</c>): <c>Smtp</c> mode on a separate fresh database and keyring, with a loopback provider that
/// answers the final <c>250</c>. The notice the reminder job queued is <c>Sent</c> with one attempt and the claim
/// cleared, and the completion instant is still inside the scheduler interval of its creation instant, which is the
/// same-tick evidence: the dispatcher only runs when a tick gives it a chance, and a tick cannot start before one
/// minute. Exactly one SMTP transaction reached the provider and its body carries the private link.
/// </para>
/// <para>
/// Both scenarios also assert that the raw token, the private URL and the protected token never appear on the owned
/// child's captured standard output, so the single-use link of the reminder is proven not to be logged by the shipped
/// notification path. Nothing here replaces an independent run: until a human executes it against a real LocalDB
/// instance with the opt-in pair set, every scenario is compiled and N/A only, never real proof.
/// </para>
/// </summary>
public sealed class ReminderHostIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string ConnectionVariable = "WORKUP_T24_HOST_CONNECTION";
    private const string KeysPathVariable = "WORKUP_T24_HOST_KEYS_PATH";
    private const string DatabasePrefix = "WorkUpDemoT24Host";
    private const string KeysLeafPrefix = "WorkUpDemoT12KeysT24Host";
    private const string DevelopmentKeysPathKey = "WorkUp:Development:DataProtectionKeysPath";
    private const string BusinessTimeZone = "America/Argentina/Buenos_Aires";

    /// <summary>Derived suffix of each retained target; one fresh database and one fresh keyring per scenario.</summary>
    private const string RetainSuffix = "_Retain";
    private const string SmtpSuffix = "_Smtp";

    private const string BusinessName = "Peluqueria T24 Host";
    private const string BusinessSlug = "t24-host";
    private const string ContactPhone = "1145550000";
    private const string FromAddress = "WorkUp <no-responder@workup.local>";
    private const string Recipient = "t24-host@example.invalid";
    private const string ServiceName = "Corte T24";
    private const string WorkerFirstName = "Ana";
    private const string WorkerLastName = "Host";
    private const string ClientFirstName = "Lucia";

    /// <summary>Configured anticipation of the seeded business: the reminder window the host must honour.</summary>
    private const int LeadHours = 24;

    private const string PendingRole = "pending";
    private const string ConfirmedRole = "confirmed";

    private static readonly Regex SafeDatabaseName =
        new(@"\A" + DatabasePrefix + @"[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TickBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StatusBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StatusPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Inherited names that could retarget the child at another database, keyring, provider, environment or content
    /// root. Only these are dropped: the runtime location variables such as <c>DOTNET_ROOT</c> stay untouched.
    /// </summary>
    private static readonly string[] InheritedExactOverrides =
    [
        "DOTNET_ENVIRONMENT",
        "DOTNET_CONTENTROOT",
        "DOTNET_APPLICATIONNAME",
        "DOTNET_URLS",
        "DOTNET_HTTP_PORTS",
        "DOTNET_HTTPS_PORTS"
    ];

    // --- Scenario 1: Outbox retains the queued reminder without contacting the provider -------------------------

    [Fact]
    public async Task Retain_mode_host_tick_queues_the_reminder_with_its_private_link_and_dispatches_nothing()
    {
        var outcome = await RunScenarioAsync(
            RetainSuffix, "Outbox", includeConfirmedBooking: true, dispatchExpected: false, AcceptWithFinalReplyAsync);
        if (outcome is null)
            return;

        var booking = outcome.Bookings[PendingRole];
        var pending = outcome.Notices[PendingRole];

        // The immediate tick ran (fixed marker) and the notice it queued is the canonical scheduled one, untouched.
        Assert.Equal(booking.DedupKey, pending.DedupKey);
        Assert.Equal(outcome.BusinessId, pending.BusinessId);
        Assert.Equal((byte)NotificationType.AppointmentReminder, pending.Type);
        Assert.Equal(Recipient, pending.Recipient);
        Assert.Equal(ReminderService.PendingSubject, pending.Subject);
        Assert.Equal((byte)NotificationStatus.Pending, pending.Status);
        Assert.Equal(0, pending.Attempts);
        Assert.Null(pending.LastError);
        Assert.Null(pending.SentAt);
        Assert.Null(pending.NextAttemptAt);
        Assert.Null(pending.ClaimedAt);
        Assert.Null(pending.DispatchKey);

        // The trusted private URL and the confirmation request of a still pending turn are in the body.
        Assert.Contains(booking.PrivateUrl, pending.BodyHtml, StringComparison.Ordinal);
        Assert.Contains(ReminderService.ConfirmationLead, pending.BodyHtml, StringComparison.Ordinal);

        // A confirmed turn is informed, never asked: same link, no confirmation request.
        var confirmed = outcome.Notices[ConfirmedRole];
        Assert.Equal(outcome.Bookings[ConfirmedRole].DedupKey, confirmed.DedupKey);
        Assert.Equal((byte)NotificationType.AppointmentReminder, confirmed.Type);
        Assert.Equal((byte)NotificationStatus.Pending, confirmed.Status);
        Assert.Equal(0, confirmed.Attempts);
        Assert.Equal(ReminderService.ConfirmedSubject, confirmed.Subject);
        Assert.Contains(outcome.Bookings[ConfirmedRole].PrivateUrl, confirmed.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(ReminderService.ConfirmationLead, confirmed.BodyHtml, StringComparison.Ordinal);

        // A reachable loopback provider saw nothing: Outbox never claims and never sends.
        Assert.Equal(0, outcome.SmtpConnections);
        Assert.Empty(outcome.SmtpDataLines);

        AssertLinkNeverLeftTheDatabase(outcome);

        output.WriteLine(
            $"Retain assertions passed on retained database '{outcome.DatabaseName}' with isolated keyring " +
            $"'{outcome.KeysPath}': tick marker observed, two Pending reminders with 0 attempts, the private URL and " +
            "the confirmation request only in the pending body, and 0 SMTP connections.");
    }

    // --- Scenario 2: the same tick dispatches the queued reminder through the loopback provider ----------------

    [Fact]
    public async Task Smtp_mode_host_tick_dispatches_the_queued_reminder_in_the_same_tick_with_one_transaction()
    {
        var outcome = await RunScenarioAsync(
            SmtpSuffix, "Smtp", includeConfirmedBooking: false, dispatchExpected: true, AcceptWithFinalReplyAsync);
        if (outcome is null)
            return;

        var booking = outcome.Bookings[PendingRole];
        var notice = outcome.Notices[PendingRole];

        Assert.Equal(booking.DedupKey, notice.DedupKey);
        Assert.Equal((byte)NotificationStatus.Sent, notice.Status);
        Assert.Equal(1, notice.Attempts);
        Assert.Null(notice.LastError);
        Assert.NotNull(notice.SentAt);
        Assert.Null(notice.ClaimedAt);
        Assert.Null(notice.DispatchKey);
        Assert.Equal(ReminderService.PendingSubject, notice.Subject);
        Assert.Contains(booking.PrivateUrl, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains(ReminderService.ConfirmationLead, notice.BodyHtml, StringComparison.Ordinal);

        // Same-tick evidence: the dispatcher only runs when the scheduler gives it a chance and the interval between
        // two ticks is one minute, so a completion a whole interval after the creation instant would mean another tick.
        var elapsed = notice.SentAt!.Value - notice.CreatedAt;
        Assert.True(
            elapsed >= TimeSpan.Zero && elapsed < ScheduledJobsHostedService.Interval,
            $"The notice was created at {notice.CreatedAt:O} and completed at {notice.SentAt:O}; a completion of " +
            $"{elapsed.TotalSeconds:0} s is not inside the {ScheduledJobsHostedService.Interval.TotalSeconds:0} s " +
            "scheduler interval, so it was not the same tick.");

        // Exactly one SMTP transaction, and it carried the private link of the reminder it confirmed.
        Assert.Equal(1, outcome.SmtpConnections);
        Assert.NotEmpty(outcome.SmtpDataLines);
        Assert.True(
            SmtpTranscriptContainsPrivateLink(outcome.SmtpDataLines, booking.PrivateUrl),
            "The single SMTP transaction must carry the trusted private URL of the reminder, in whichever transfer " +
            "encoding the mail writer chose.");

        AssertLinkNeverLeftTheDatabase(outcome);

        output.WriteLine(
            $"Smtp assertions passed on retained database '{outcome.DatabaseName}' with isolated keyring " +
            $"'{outcome.KeysPath}': the reminder queued by the tick was Sent in that same tick with 1 attempt, and " +
            "exactly one SMTP transaction carried the private link.");
    }

    /// <summary>
    /// The single-use link of the reminder is proven never to be logged by the shipped path: not the raw token, not
    /// the URL that carries it and not its protected form may appear on the owned child's captured output.
    /// </summary>
    private static void AssertLinkNeverLeftTheDatabase(ScenarioOutcome outcome)
    {
        Assert.False(
            outcome.ChildOutputMentionedRawToken, "The raw single-use token of a reminder must never be logged.");
        Assert.False(
            outcome.ChildOutputMentionedPrivateUrl, "The private URL of a reminder must never be logged.");
        Assert.False(
            outcome.ChildOutputMentionedProtectedToken, "The protected token of a reminder must never be logged.");
    }

    /// <summary>
    /// Unit level guard of the pre-seed comparison, with no opt-in gate and no SQL, keyring or host: it builds no
    /// provider, opens no connection and writes nothing. A REAL run returned a discriminator with backslashes and a
    /// trailing separator, so separator, trailing-separator and (on Windows) case differences must be equivalent, while
    /// a genuinely different content root must still be rejected.
    /// </summary>
    [Fact]
    public void Application_discriminator_guard_accepts_canonical_equivalent_paths_and_rejects_a_different_root()
    {
        const string ContentRoot = @"C:\workup\WorkUp";

        // The framework's own normalization: backslashes and a trailing separator, and a trailing separator on ours.
        Assert.True(RefersToSameDirectory(ContentRoot, @"C:\workup\WorkUp\"));
        Assert.True(RefersToSameDirectory(ContentRoot, "C:/workup/WorkUp"));
        Assert.True(RefersToSameDirectory(ContentRoot, "C:/workup/WorkUp/"));
        Assert.True(RefersToSameDirectory(ContentRoot + "\\", @"C:\workup\WorkUp"));

        if (OperatingSystem.IsWindows())
            Assert.True(RefersToSameDirectory(ContentRoot, @"c:\WORKUP\workup\"));
        else
            Assert.False(RefersToSameDirectory(ContentRoot, @"c:\WORKUP\workup\"));

        // A truly different content root, a different drive and a missing discriminator are never accepted.
        Assert.False(RefersToSameDirectory(ContentRoot, @"C:\workup\WorkUp2\"));
        Assert.False(RefersToSameDirectory(ContentRoot, @"C:\workup\WorkUp\Other"));
        Assert.False(RefersToSameDirectory(ContentRoot, @"D:\workup\WorkUp\"));
        Assert.False(RefersToSameDirectory(ContentRoot, null));
        Assert.False(RefersToSameDirectory(ContentRoot, "   "));
    }

    /// <summary>
    /// The single pre-seed comparison used by the gated guard: whether the provider's application discriminator refers
    /// to the same directory as the owned host's content root. Separators and a trailing separator are normalized away
    /// and Windows paths are compared case-insensitively, because the framework normalizes the content root itself;
    /// nothing else is forgiven, so a genuinely different root is still rejected.
    /// </summary>
    private static bool RefersToSameDirectory(string contentRoot, string? discriminator)
    {
        if (string.IsNullOrWhiteSpace(discriminator))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            CanonicalDirectory(contentRoot), CanonicalDirectory(discriminator), comparison);
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
        bool includeConfirmedBooking,
        bool dispatchExpected,
        Func<SmtpSession, Task> smtpScript)
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
        output.WriteLine(
            $"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', host port {plan.Port}, " +
            $"content root '{plan.WebRoot}' in {configuredMode} mode.");

        using var smtp = new LoopbackSmtpServer(smtpScript);
        output.WriteLine(
            $"SMTP: loopback fake provider listening on 127.0.0.1:{smtp.Port} (no external network, no credentials, no TLS).");

        // The pre-seed assumption is validated before anything irreversible exists: building the production
        // DataProtection registration and reading its discriminator creates no keyring directory, writes no key and
        // opens no SQL connection, so a canonical mismatch stops the scenario before the database is created.
        using var preSeed = BuildPreSeedDataProtectionProvider(plan, configuredMode, smtp.Port);
        AssertApplicationDiscriminator(preSeed, plan);

        await AssertDatabaseAbsentAsync(plan.DatabaseName);
        var applied = await new DatabaseMigrator(plan.ConnectionString).MigrateAsync();
        Assert.Contains("001_schema.sql", applied);
        Assert.Contains("002_client_account_pending_names.sql", applied);
        Assert.Contains("003_outbox_dispatch.sql", applied);
        output.WriteLine(
            $"SQL: applied {applied.Count} schema script(s) to the new database '{plan.DatabaseName}' (retained, never dropped).");

        // The reminder window is derived from the real business clock, exactly as the host's own IClock reads it, and
        // every booking is created strictly outside the configured anticipation so the immediate tick finds it due.
        var businessNow = new BusinessClock(TimeZoneInfo.FindSystemTimeZoneById(BusinessTimeZone)).Now;
        var pendingToken = SecureToken.Create();
        var confirmedToken = includeConfirmedBooking ? SecureToken.Create() : (TokenPair?)null;
        var protectedTokens = ProtectLinkTokens(
            preSeed, plan, [pendingToken.Token, confirmedToken?.Token ?? string.Empty]);

        var seeds = new List<BookingSeed>
        {
            new(PendingRole, pendingToken, protectedTokens[0], BookingStatus.Pending, StartsHours: 20, CreatedHours: -10)
        };
        if (confirmedToken is { } token)
        {
            seeds.Add(new BookingSeed(
                ConfirmedRole, token, protectedTokens[1], BookingStatus.Confirmed, StartsHours: 21, CreatedHours: -9));
        }

        var seeded = await SeedAsync(plan.ConnectionString, plan.BaseUrl, businessNow, seeds);
        output.WriteLine(
            $"SQL: seeded business #{seeded.BusinessId} with a configured lead of {LeadHours} h, one worker, one " +
            $"service and one ficha, plus {seeded.Bookings.Count} scheduled individual turn(s) created outside that " +
            "lead, each with one live link opened from the isolated keyring before the host started.");

        var host = StartHost(plan, configuredMode, smtp.Port);
        try
        {
            await WaitUntilReadyAsync(host);
            output.WriteLine($"HTTP: owned host (pid {host.Process.Id}) became ready on {plan.BaseUrl} in {configuredMode} mode.");

            await WaitForOutputAsync(host, ScheduledJobsHostedService.TickCompletedMessage, TickBudget);
            output.WriteLine(
                "HOST: the immediate scheduled-jobs tick completed (fixed marker observed on the child output).");

            var diagnostic =
                $"The owned host (pid {host.Process.Id}) logged the fixed tick-completed marker. Tick-failed marker " +
                $"observed: {host.OutputContains(ScheduledJobsHostedService.TickFailedMessage)}. A completed tick " +
                "that wrote no notice means the seeded link was not exactly one live, openable link: either the " +
                "isolated keyring/application-discriminator assumption of this pre-seed, or the link itself.";
            var notices = new Dictionary<string, NotificationRow>(StringComparer.Ordinal);
            foreach (var booking in seeded.Bookings)
            {
                notices[booking.Role] = dispatchExpected
                    ? await WaitForNotificationStatusAsync(
                        plan.ConnectionString, booking.DedupKey, NotificationStatus.Sent, diagnostic)
                    : await ReadNotificationAsync(plan.ConnectionString, booking.DedupKey, diagnostic);
            }

            // The encrypted keyring was materialized by this run's own protection call and must stay isolated.
            AssertKeyringCreated(plan.KeysPath);

            return new ScenarioOutcome(
                plan.DatabaseName,
                plan.KeysPath,
                seeded.BusinessId,
                seeded.Bookings.ToDictionary(booking => booking.Role, StringComparer.Ordinal),
                notices,
                smtp.ConnectionCount,
                smtp.DataLines,
                seeded.Bookings.Any(booking => host.OutputContains(booking.RawToken)),
                seeded.Bookings.Any(booking => host.OutputContains(booking.PrivateUrl)),
                seeded.Bookings.Any(booking => host.OutputContains(booking.ProtectedToken)));
        }
        finally
        {
            await StopHostAsync(host.Process);
        }
    }

    // --- Private-link pre-seeding over the isolated keyring -----------------------------------------------------

    /// <summary>
    /// Builds the production composition root for the pre-seed step over the derived isolated keyring and the isolated
    /// SQL target. Building it and reading its application discriminator is inert: the key ring is only touched when a
    /// protector is used, which happens after the database exists, so a rejected discriminator leaves no database and
    /// no keyring behind.
    /// </summary>
    private static ServiceProvider BuildPreSeedDataProtectionProvider(
        HarnessPlan plan, string configuredMode, int smtpPort)
    {
        var environment = new SimulatedHostEnvironment(plan.WebRoot);
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:WorkUp"] = plan.ConnectionString,
            ["WorkUp:PublicBaseUrl"] = plan.BaseUrl,
            ["WorkUp:TimeZone"] = BusinessTimeZone,
            [DevelopmentKeysPathKey] = plan.KeysPath,
            ["WorkUp:Email:Mode"] = configuredMode,
            ["WorkUp:Email:From"] = FromAddress,
            ["WorkUp:Email:Smtp:Host"] = "127.0.0.1",
            ["WorkUp:Email:Smtp:Port"] = smtpPort.ToString(CultureInfo.InvariantCulture),
            ["WorkUp:Email:Smtp:EnableSsl"] = "false"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // The child registers a host environment by construction; the simulated one carries the same content root so
        // the framework computes the same application discriminator on both sides.
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddWorkUp(configuration, environment);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Loud guard of the one assumption this pre-seed depends on: the child derives its DataProtection application
    /// discriminator from its content root path, so the test process must compute an equivalent one, otherwise no
    /// seeded payload could be opened by the host. The comparison is the canonical-directory one of
    /// <see cref="RefersToSameDirectory"/>, because the framework normalizes that path itself, while the payload later
    /// uses the provider's discriminator unchanged. It runs before the derived database is created.
    /// </summary>
    private void AssertApplicationDiscriminator(ServiceProvider provider, HarnessPlan plan)
    {
        var discriminator = provider.GetService<IApplicationDiscriminator>()?.Discriminator;
        Assert.True(
            RefersToSameDirectory(plan.WebRoot, discriminator),
            $"The provider's application discriminator '{discriminator ?? "<none>"}' must refer to the owned host's " +
            $"content root '{plan.WebRoot}' (both compared as canonical directories); without it the seeded protected " +
            "token could not be opened by the host, and this harness never falls back to a forged or unencrypted token.");

        output.WriteLine(
            $"DataProtection: pre-seed application discriminator '{discriminator}' refers to the host content root " +
            $"'{plan.WebRoot}' (canonical forms '{CanonicalDirectory(plan.WebRoot)}' and " +
            $"'{CanonicalDirectory(discriminator!)}'); validated before the database was created.");
    }

    /// <summary>The separator and trailing-separator invariant form of a directory path used by the guard.</summary>
    private static string CanonicalDirectory(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Protects the synthetic raw tokens with the shipped link protector over the isolated keyring, before the host
    /// starts, so the seeded <c>ProtectedToken</c> is a real payload the child can open. It uses the production
    /// provider's own application discriminator, exactly as the host will. Only the derived temporary keyring is used;
    /// the shared default keyring is never read, written or fallen back to.
    /// </summary>
    private IReadOnlyList<string> ProtectLinkTokens(
        ServiceProvider provider, HarnessPlan plan, IReadOnlyList<string> rawTokens)
    {
        Assert.False(
            Directory.Exists(plan.KeysPath),
            $"The derived keyring '{plan.KeysPath}' already existed; this harness never reuses an earlier keyring.");

        var protector = provider.GetRequiredService<ILinkTokenProtector>();
        var protectedTokens = rawTokens
            .Where(token => !string.IsNullOrEmpty(token))
            .Select(protector.Protect)
            .ToArray();
        Assert.Equal(rawTokens.Count(token => !string.IsNullOrEmpty(token)), protectedTokens.Length);

        output.WriteLine(
            $"DataProtection: {protectedTokens.Length} link token(s) protected with the shipped purpose over the " +
            $"fresh isolated keyring '{plan.KeysPath}' before the host started.");
        return protectedTokens;
    }

    /// <summary>Encrypted key files are generated only inside the derived temporary folder, never in the shared one.</summary>
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
            $"DataProtection: {files.Length} encrypted key file(s) inside the dedicated temp keyring folder only.");
    }

    // --- Owned child process ------------------------------------------------------------------------------------

    private RunningHost StartHost(HarnessPlan plan, string configuredMode, int smtpPort)
    {
        var startInfo = new ProcessStartInfo(plan.WebHostPath)
        {
            // The working directory is also the content root the DataProtection discriminator derives from.
            WorkingDirectory = plan.WebRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Drop every inherited WorkUp/ASP.NET override first, so nothing from the caller's shell can retarget the child.
        ScrubInheritedOverrides(startInfo);
        // Both environment keys are set, so the child is in Development no matter which one the host reads first; the
        // isolated keyring registration is development-only and both the test process and the child depend on it.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__WorkUp"] = plan.ConnectionString;
        startInfo.Environment["ASPNETCORE_URLS"] = plan.BaseUrl;
        startInfo.Environment["WorkUp__Development__DataProtectionKeysPath"] = plan.KeysPath;
        startInfo.Environment["WorkUp__PublicBaseUrl"] = plan.BaseUrl;
        startInfo.Environment["WorkUp__MigrateOnStartup"] = "true";
        // The fixture must observe the real business clock and the shipped notification path; the test tools that this
        // flag gates are explicitly out of the T24 contract, so they stay off even though Development would enable them.
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
        // Capture stdout so the committed tick marker can be observed and the absence of any link in the log can be
        // asserted; the pipe buffers anything written before the asynchronous read starts. Nothing is echoed anywhere.
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
                InheritedExactOverrides.Contains(name, StringComparer.OrdinalIgnoreCase)
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
            ApplicationName = "WorkUp T24 reminder host integration test"
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
    /// Seeds the minimum the reminder path needs: one business with its configured anticipation, service, worker and
    /// ficha, and per role one scheduled individual appointment with a booking created strictly outside the lead window
    /// and exactly one live link whose protected token was opened from the isolated keyring before the host started.
    /// </summary>
    private static async Task<SeededTarget> SeedAsync(
        string connectionString, string baseUrl, DateTime businessNow, IReadOnlyList<BookingSeed> seeds)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var businessId = await InsertAsync(connection, """
            INSERT INTO Businesses (Name, Slug, ContactPhone, ReminderLeadHours, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@name, @slug, @contactPhone, @leadHours, @createdAt)
            """,
            ("@name", BusinessName), ("@slug", BusinessSlug), ("@contactPhone", ContactPhone),
            ("@leadHours", LeadHours), ("@createdAt", businessNow));

        var serviceId = await InsertAsync(connection, """
            INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity)
            OUTPUT INSERTED.Id
            VALUES (@business, @name, 12000.00, 30, 1)
            """,
            ("@business", businessId), ("@name", ServiceName));

        var workerId = await InsertAsync(connection, """
            INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil)
            OUTPUT INSERTED.Id
            VALUES (@business, @firstName, @lastName, @phone, @cuil)
            """,
            ("@business", businessId), ("@firstName", WorkerFirstName), ("@lastName", WorkerLastName),
            ("@phone", ContactPhone), ("@cuil", "20" + businessId.ToString("D9", CultureInfo.InvariantCulture)));

        var clientId = await InsertAsync(connection, """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@business, @firstName, 'Host', @email, @createdAt)
            """,
            ("@business", businessId), ("@firstName", ClientFirstName), ("@email", Recipient),
            ("@createdAt", businessNow));

        var bookings = new List<SeededBooking>(seeds.Count);
        foreach (var seed in seeds)
        {
            var startsAt = businessNow.AddHours(seed.StartsHours);
            var appointmentId = await InsertAsync(connection, """
                INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
                OUTPUT INSERTED.Id
                VALUES (@business, @kind, @service, @worker, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)
                """,
                ("@business", businessId), ("@kind", AppointmentKind.Individual), ("@service", serviceId),
                ("@worker", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddHours(1)),
                ("@origin", AppointmentOrigin.Reserved), ("@status", AppointmentStatus.Scheduled),
                ("@createdAt", businessNow));

            var bookingId = await InsertAsync(connection, """
                INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt)
                OUTPUT INSERTED.Id
                VALUES (@business, @appointment, @client, NULL, NULL, @status, @createdAt, NULL)
                """,
                ("@business", businessId), ("@appointment", appointmentId), ("@client", clientId),
                ("@status", seed.Status), ("@createdAt", businessNow.AddHours(seed.CreatedHours)));

            await InsertAsync(connection, """
                INSERT INTO AppointmentLinks (BookingId, TokenHash, ProtectedToken, ExpiresAt, RevokedAt, CreatedAt)
                OUTPUT INSERTED.Id
                VALUES (@booking, @tokenHash, @protectedToken, @expiresAt, NULL, @createdAt)
                """,
                ("@booking", bookingId), ("@tokenHash", seed.Token.Hash),
                ("@protectedToken", seed.ProtectedToken), ("@expiresAt", businessNow.AddHours(48)),
                ("@createdAt", businessNow));

            bookings.Add(new SeededBooking(
                bookingId,
                seed.Role,
                seed.Token.Token,
                seed.ProtectedToken,
                PublicAppointmentUrlRules.Build(baseUrl, seed.Token.Token),
                "reminder:booking:" + bookingId.ToString(CultureInfo.InvariantCulture)));
        }

        return new SeededTarget(businessId, bookings);
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
        Enum enumValue => new SqlParameter(name, Convert.ToByte(enumValue, CultureInfo.InvariantCulture)),
        byte[] bytes => new SqlParameter(name, SqlDbType.Binary, bytes.Length) { Value = bytes },
        _ => new SqlParameter(name, value)
    };

    /// <summary>Reads the canonical scheduled notice of one booking; a missing row is a failure, never a skip.</summary>
    private static async Task<NotificationRow> ReadNotificationAsync(
        string connectionString, string dedupKey, string diagnostic)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT Id, BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError,
                   CreatedAt, SentAt, NextAttemptAt, ClaimedAt, DispatchKey
              FROM Notifications
             WHERE DedupKey = @dedupKey
            """,
            connection);
        command.Parameters.Add("@dedupKey", SqlDbType.NVarChar, 150).Value = dedupKey;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(
                $"The reminder notice '{dedupKey}' was not written by the immediate tick. {diagnostic}");

        return new NotificationRow(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetByte(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetByte(7),
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetDateTime(10),
            reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            reader.IsDBNull(12) ? null : reader.GetDateTime(12),
            reader.IsDBNull(13) ? null : reader.GetDateTime(13),
            reader.IsDBNull(14) ? null : reader.GetGuid(14));
    }

    /// <summary>Bounded poll for the status the same tick is expected to persist before the next one can start.</summary>
    private static async Task<NotificationRow> WaitForNotificationStatusAsync(
        string connectionString, string dedupKey, NotificationStatus expected, string diagnostic)
    {
        var deadline = DateTime.UtcNow + StatusBudget;
        while (true)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"The reminder notice '{dedupKey}' did not reach {expected} within {StatusBudget.TotalSeconds:0} " +
                    $"seconds of the host tick. {diagnostic}");

            var row = await ReadNotificationAsync(connectionString, dedupKey, diagnostic);
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
        await session.WriteAsync("220 workup-t24 ESMTP ready");
        string? line;
        while ((line = await session.ReadLineAsync()) is not null)
        {
            if (StartsWith(line, "EHLO"))
            {
                await session.WriteAsync("250-workup-t24");
                await session.WriteAsync("250 OK");
            }
            else if (StartsWith(line, "DATA"))
            {
                await session.WriteAsync("354 End data with <CR><LF>.<CR><LF>");
                await session.ReadDataBlockAsync();
                await session.WriteAsync("250 OK queued as workup-t24");
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

    // --- Transfer-encoding tolerant body search -----------------------------------------------------------------

    /// <summary>
    /// Whether the single recorded SMTP transaction carried <paramref name="privateUrl"/>. The mail writer chooses the
    /// transfer encoding, so the recorded DATA block is searched as raw text, after quoted-printable decoding (which
    /// also removes soft line breaks) and after a base64 decoding of the body part.
    /// </summary>
    private static bool SmtpTranscriptContainsPrivateLink(IReadOnlyList<string> dataLines, string privateUrl)
    {
        var separator = -1;
        for (var index = 0; index < dataLines.Count; index++)
        {
            if (dataLines[index].Length == 0)
            {
                separator = index;
                break;
            }
        }

        var bodyLines = separator >= 0 ? dataLines.Skip(separator + 1) : dataLines;
        var body = string.Join("\n", bodyLines);
        return body.Contains(privateUrl, StringComparison.Ordinal)
               || DecodeQuotedPrintable(body).Contains(privateUrl, StringComparison.Ordinal)
               || DecodeBase64(body).Contains(privateUrl, StringComparison.Ordinal);
    }

    private static string DecodeQuotedPrintable(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '=')
            {
                builder.Append(text[index]);
                continue;
            }

            // A soft line break continues the same logical line: it is removed, never replaced by a character.
            if (index + 2 < text.Length && text[index + 1] == '\r' && text[index + 2] == '\n')
            {
                index += 2;
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == '\n')
            {
                index += 1;
                continue;
            }

            if (index + 2 < text.Length && IsHexDigit(text[index + 1]) && IsHexDigit(text[index + 2]))
            {
                builder.Append((char)Convert.ToByte(text.Substring(index + 1, 2), 16));
                index += 2;
                continue;
            }

            builder.Append('=');
        }

        return builder.ToString();
    }

    private static string DecodeBase64(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
                continue;
            if (char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=')
                builder.Append(character);
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(builder.ToString()));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    // --- Owned host output --------------------------------------------------------------------------------------

    /// <summary>
    /// The owned child host plus a bounded capture of its standard output, used to observe the committed fixed tick
    /// marker and to assert that no single-use link ever reached the log. The lines are never echoed anywhere: only a
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

        public bool OutputContains(string marker)
        {
            lock (_gate)
                return _lines.Any(line => line.Contains(marker, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The simulated host environment of the pre-seed step. Its content root path is the owned child's own working
    /// directory, because that is the path the framework turns into the DataProtection application discriminator on
    /// both sides; the environment name is Development exactly like the child's own environment.
    /// </summary>
    private sealed class SimulatedHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record HarnessPlan(
        string ConnectionString, string DatabaseName, string KeysPath, string WebHostPath, string WebRoot, int Port)
    {
        public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record BookingSeed(
        string Role, TokenPair Token, string ProtectedToken, BookingStatus Status, int StartsHours, int CreatedHours);

    private sealed record SeededBooking(
        int BookingId, string Role, string RawToken, string ProtectedToken, string PrivateUrl, string DedupKey);

    private sealed record SeededTarget(int BusinessId, IReadOnlyList<SeededBooking> Bookings);

    private sealed record NotificationRow(
        int Id,
        int BusinessId,
        byte Type,
        string Recipient,
        string Subject,
        string BodyHtml,
        string DedupKey,
        byte Status,
        int Attempts,
        string? LastError,
        DateTime CreatedAt,
        DateTime? SentAt,
        DateTime? NextAttemptAt,
        DateTime? ClaimedAt,
        Guid? DispatchKey);

    private sealed record ScenarioOutcome(
        string DatabaseName,
        string KeysPath,
        int BusinessId,
        IReadOnlyDictionary<string, SeededBooking> Bookings,
        IReadOnlyDictionary<string, NotificationRow> Notices,
        int SmtpConnections,
        IReadOnlyList<string> SmtpDataLines,
        bool ChildOutputMentionedRawToken,
        bool ChildOutputMentionedPrivateUrl,
        bool ChildOutputMentionedProtectedToken);
}
