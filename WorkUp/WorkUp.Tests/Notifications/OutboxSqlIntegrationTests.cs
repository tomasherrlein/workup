using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlOutboxRepository"/> (T23, RF-10/RF-17) driven both
/// directly and through <see cref="OutboxDispatchService"/>. It requires a new, dedicated database supplied through
/// <c>WORKUP_T23_OUTBOX_SQL_CONNECTION</c>; without it every SQL test returns N/A and no connection is opened. Each
/// test derives its own database name from the supplied one, so the whole class runs in a single command, the
/// preflight asserts <c>DB_ID</c> is absent before migrating, and nothing is torn down: the target is retained on
/// purpose and never reused.
/// <para>
/// The in-memory port and the service tests cannot prove what SQL Server does with the atomic claim, the row padlock,
/// the <c>NULL</c> retry gate or a second connection's visibility. This fixture is the runtime proof of the port: a
/// migration that adds the three nullable T23 columns without disturbing the seven existing producers (a legacy
/// <c>INSERT</c> that omits them leaves <c>NextAttemptAt</c> <c>NULL</c>, so the effective due instant is
/// <c>CreatedAt</c>), one atomic <c>UPDATE ... OUTPUT</c> claim that picks the oldest effective due instant then the
/// id with <c>UPDLOCK</c>/<c>READPAST</c>/<c>ROWLOCK</c>, a rewound clock that makes a row wait instead of treating a
/// <c>NULL</c> gate as unconditionally due, an <c>Attempts</c> count that is durable at claim time, a separate
/// connection that observes the committed <c>Dispatching</c> row while the sender runs, the bounded retry schedule and
/// its terminal <c>Failed</c>, an uncertain outcome that becomes <c>ReviewRequired</c> keeping its claim, a stale key
/// or attempt that writes nothing, a row left <c>Dispatching</c> that is never reclaimed, and two independent claims
/// that produce exactly one winner.
/// </para>
/// <para>
/// The last test needs no database: the storage layer refuses any <c>LastError</c> text outside the fixed safe set of
/// <see cref="OutboxDispatchService"/>, at least 500 characters are impossible by construction, and the refused value
/// is never echoed, so a direct port caller cannot persist raw provider text or a password.
/// </para>
/// </summary>
public sealed class OutboxSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T23_OUTBOX_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT23OutboxSql";
    private const string Recipient = "ana@example.com";
    private const string Subject = "Turno confirmado";
    private const string BodyHtml = "<p>Cuerpo del aviso</p>";
    private const string NeverOpenableConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT23ReasonGuardNeverExists;Integrated Security=true;Connect Timeout=1";

    private static readonly Regex SafeDatabaseName =
        new(@"\AWorkUpDemoT23OutboxSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>The single clock instant every scenario starts from.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task An_old_producer_insert_is_due_at_its_creation_instant_and_claims_atomically()
    {
        var target = await PrepareAsync("_OldProducer");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var id = await SeedOldProducerNotificationAsync(connectionString, "booking:1", FixedNow);

        // The seven existing producers never write the three T23 columns: they stay NULL, so the effective due
        // instant of the row is its CreatedAt and the row is Pending with no claim.
        var seeded = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Pending, seeded.Status);
        Assert.Equal(0, seeded.Attempts);
        Assert.Null(seeded.NextAttemptAt);
        Assert.Null(seeded.ClaimedAt);
        Assert.Null(seeded.DispatchKey);

        await using var session = new DbSession(connectionString);
        var repository = new SqlOutboxRepository(session);
        var dispatchKey = Guid.NewGuid();

        // One minute before CreatedAt the row is not due: the predicate uses the effective due instant, never a blanket
        // "any Pending row is due" that a rewound simulated clock (T26) would turn into a premature send.
        Assert.Null(await ClaimAsync(session, repository, Guid.NewGuid(), FixedNow.AddMinutes(-1)));

        var claimed = await ClaimAsync(session, repository, dispatchKey, FixedNow);

        Assert.NotNull(claimed);
        Assert.Equal(id, claimed!.Id);
        Assert.Equal(NotificationStatus.Dispatching, claimed.Status);
        Assert.Equal(FixedNow, claimed.ClaimedAt);
        Assert.Equal(dispatchKey, claimed.DispatchKey);
        Assert.Equal(1, claimed.Attempts);
        Assert.Null(claimed.NextAttemptAt);
        // The claim returns the whole row, so the dispatcher can send it without a second read.
        Assert.Equal(Recipient, claimed.Recipient);
        Assert.Equal(Subject, claimed.Subject);
        Assert.Equal(BodyHtml, claimed.BodyHtml);
        Assert.Equal("booking:1", claimed.DedupKey);

        // The one statement that claimed the row is what persisted the claim: a second, independent read sees it.
        var stored = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Dispatching, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(FixedNow, stored.ClaimedAt);
        Assert.Equal(dispatchKey, stored.DispatchKey);
        Assert.Null(stored.SentAt);

        output.WriteLine(
            "SQL claim assertions passed: a legacy INSERT left the three T23 columns NULL so the effective due " +
            "instant was CreatedAt, the row was refused one minute early and the single UPDATE ... OUTPUT statement " +
            "persisted Dispatching, ClaimedAt, DispatchKey and Attempts = 1.");
    }

    [Fact]
    public async Task Claim_order_follows_the_effective_due_instant_not_the_enqueue_time()
    {
        var target = await PrepareAsync("_Fifo");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        // Enqueued an hour ago but only due now (a retry), versus a fresh notice that became due ten minutes ago, versus
        // a notice whose creation instant is still ahead of the clock.
        var retry = await SeedNotificationWithGateAsync(connectionString, "booking:1", FixedNow.AddMinutes(-60), FixedNow);
        var fresh = await SeedOldProducerNotificationAsync(connectionString, "booking:2", FixedNow.AddMinutes(-10));
        var ahead = await SeedOldProducerNotificationAsync(connectionString, "booking:3", FixedNow.AddMinutes(5));

        await using var session = new DbSession(connectionString);
        var repository = new SqlOutboxRepository(session);

        var first = await ClaimAsync(session, repository, Guid.NewGuid(), FixedNow);
        var second = await ClaimAsync(session, repository, Guid.NewGuid(), FixedNow);
        var third = await ClaimAsync(session, repository, Guid.NewGuid(), FixedNow);

        // Due instant decides, never the enqueue order: the fresh row (due ten minutes ago) precedes the retry
        // (due now), and the row created in the future is not due at all.
        Assert.Equal(fresh, first!.Id);
        Assert.Equal(retry, second!.Id);
        Assert.Null(third);
        Assert.Equal((byte)NotificationStatus.Pending, (await ReadNotificationAsync(connectionString, ahead)).Status);

        output.WriteLine(
            "SQL order assertions passed: the claim followed the effective due instant (NextAttemptAt ?? CreatedAt) " +
            "then the id, not the enqueue time, and left the future notice Pending.");
    }

    [Fact]
    public async Task A_rewound_clock_defers_a_gated_and_a_fresh_notice_instead_of_treating_a_null_gate_as_due()
    {
        var target = await PrepareAsync("_Rewind");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var gated = await SeedNotificationWithGateAsync(connectionString, "booking:1", FixedNow.AddMinutes(-30), FixedNow);
        var fresh = await SeedOldProducerNotificationAsync(connectionString, "booking:2", FixedNow);

        await using var session = new DbSession(connectionString);
        var repository = new SqlOutboxRepository(session);

        // A clock twenty minutes behind: the gated row's effective due instant is its gate (ahead of the clock) and the
        // fresh row's is its CreatedAt (also ahead). Neither is due, so a rewound clock never sends early.
        Assert.Null(await ClaimAsync(session, repository, Guid.NewGuid(), FixedNow.AddMinutes(-20)));
        Assert.Equal((byte)NotificationStatus.Pending, (await ReadNotificationAsync(connectionString, gated)).Status);
        Assert.Equal((byte)NotificationStatus.Pending, (await ReadNotificationAsync(connectionString, fresh)).Status);

        // Back at the real instant both rows share one due instant, so the id is the deterministic tie-break.
        var first = await ClaimAsync(session, repository, Guid.NewGuid(), FixedNow);
        var second = await ClaimAsync(session, repository, Guid.NewGuid(), FixedNow);

        Assert.Equal(gated, first!.Id);
        Assert.Equal(fresh, second!.Id);

        output.WriteLine(
            "SQL rewind assertions passed: with the clock twenty minutes behind, the NULL retry gate was not treated " +
            "as unconditionally due and a notice created ahead of the clock waited; on the due instant the id broke the tie.");
    }

    [Fact]
    public async Task The_claim_persists_the_started_attempt_before_the_send_and_every_finalize_keeps_it()
    {
        var target = await PrepareAsync("_Attempts");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var id = await SeedOldProducerNotificationAsync(connectionString, "booking:1", FixedNow);

        await using var session = new DbSession(connectionString);
        var repository = new SqlOutboxRepository(session);
        var dispatchKey = Guid.NewGuid();

        var claimed = await ClaimAsync(session, repository, dispatchKey, FixedNow);

        Assert.NotNull(claimed);
        var attempts = claimed!.Attempts;
        Assert.Equal(1, attempts);
        // The started attempt is durable before any network call: an interrupted send already counted it.
        Assert.Equal(1, (await ReadNotificationAsync(connectionString, id)).Attempts);

        var applied = await FinalizeAsync(session, repository, r =>
            r.MarkSentAsync(id, dispatchKey, FixedNow, attempts));

        Assert.True(applied);
        var stored = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Sent, stored.Status);
        Assert.Equal(FixedNow, stored.SentAt);
        // The finalize writes the count the claim already incremented; it must never count the attempt again.
        Assert.Equal(1, stored.Attempts);
        Assert.Null(stored.LastError);
        Assert.Null(stored.NextAttemptAt);
        Assert.Null(stored.ClaimedAt);
        Assert.Null(stored.DispatchKey);

        output.WriteLine(
            "SQL attempt assertions passed: the claim persisted Attempts = 1 before the sender ran and MarkSent wrote " +
            "the same count back without incrementing it again.");
    }

    [Fact]
    public async Task A_separate_connection_observes_the_committed_claim_while_the_sender_runs()
    {
        var target = await PrepareAsync("_Isolation");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var id = await SeedOldProducerNotificationAsync(connectionString, "booking:1", FixedNow);

        var clock = new FakeClock { Now = FixedNow };
        await using var session = new DbSession(connectionString);
        var observer = new CommittedClaimObserver(session, connectionString, id);
        var service = new OutboxDispatchService(
            new SqlOutboxRepository(session), observer, session, clock, OutboxMode.Deliver);

        var sent = await service.DispatchDueAsync(batchSize: 1);

        // The claim transaction was committed and its owned transaction disposed before the sender ran, and a second
        // connection could therefore read the committed Dispatching row at that exact moment.
        Assert.Equal(1, sent);
        Assert.True(observer.SessionTransactionDetachedAtSend);
        Assert.Equal((byte)NotificationStatus.Dispatching, observer.StatusAtSend);
        Assert.Equal(1, observer.AttemptsAtSend);
        Assert.NotNull(observer.DispatchKeyAtSend);

        var stored = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Sent, stored.Status);
        Assert.Equal(FixedNow, stored.SentAt);
        Assert.Equal(1, stored.Attempts);
        Assert.Null(stored.DispatchKey);
        Assert.Null(stored.ClaimedAt);

        output.WriteLine(
            "SQL isolation assertions passed: at send time the session held no transaction and a separate connection " +
            "saw the committed Dispatching row with the claim key and Attempts = 1; the fresh completion then marked it Sent.");
    }

    [Fact]
    public async Task A_definite_retry_failure_follows_the_bounded_schedule_and_ends_terminal()
    {
        var target = await PrepareAsync("_Retry");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var id = await SeedOldProducerNotificationAsync(connectionString, "booking:1", FixedNow);

        var clock = new FakeClock { Now = FixedNow };
        var sender = new FakeEmailSender { Handler = _ => EmailSendOutcome.DefinitelyNotSentRetryable };
        await using var session = new DbSession(connectionString);
        var service = new OutboxDispatchService(
            new SqlOutboxRepository(session), sender, session, clock, OutboxMode.Deliver);

        Assert.Equal(0, await service.DispatchDueAsync(batchSize: 1));
        var first = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Pending, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(FixedNow.AddMinutes(1), first.NextAttemptAt);
        Assert.Equal(OutboxDispatchService.RetryReason, first.LastError);
        Assert.Null(first.ClaimedAt);
        Assert.Null(first.DispatchKey);
        Assert.Null(first.SentAt);

        clock.Now = FixedNow.AddMinutes(1);
        Assert.Equal(0, await service.DispatchDueAsync(batchSize: 1));
        var second = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Pending, second.Status);
        Assert.Equal(2, second.Attempts);
        Assert.Equal(FixedNow.AddMinutes(6), second.NextAttemptAt);
        Assert.Equal(OutboxDispatchService.RetryReason, second.LastError);

        clock.Now = FixedNow.AddMinutes(6);
        Assert.Equal(0, await service.DispatchDueAsync(batchSize: 1));
        var third = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Failed, third.Status);
        Assert.Equal(3, third.Attempts);
        Assert.Equal(OutboxDispatchService.RetriesExhaustedReason, third.LastError);
        Assert.Null(third.NextAttemptAt);
        Assert.Null(third.ClaimedAt);
        Assert.Null(third.DispatchKey);
        Assert.Null(third.SentAt);

        // A terminal failure is never reclaimed, however far the simulated clock advances.
        clock.Now = FixedNow.AddDays(30);
        Assert.Equal(0, await service.DispatchDueAsync(batchSize: 1));
        Assert.Equal(3, sender.CallCount);
        Assert.Equal((byte)NotificationStatus.Failed, (await ReadNotificationAsync(connectionString, id)).Status);

        output.WriteLine(
            "SQL retry assertions passed: the retry went Pending with the 1 min then 5 min gate and a safe reason, and " +
            "the third attempt became terminal Failed that a clock thirty days ahead never reclaimed.");
    }

    [Fact]
    public async Task An_unknown_outcome_becomes_review_required_with_the_claim_kept_and_is_never_reclaimed()
    {
        var target = await PrepareAsync("_Review");
        if (target is null)
            return;

        const string secret = "smtp-password=SuperSecreto";
        var bodyToken = "token-de-un-solo-uso";
        var connectionString = target.ConnectionString;
        var id = await SeedOldProducerNotificationAsync(
            connectionString, "booking:1", FixedNow, bodyHtml: $"<p>{bodyToken}</p>");

        var clock = new FakeClock { Now = FixedNow };
        // Provider text can carry a password, and the body carries a single-use token; neither may reach LastError.
        var sender = new FakeEmailSender { Throw = new InvalidOperationException($"535 auth failed {secret}") };
        await using var session = new DbSession(connectionString);
        var service = new OutboxDispatchService(
            new SqlOutboxRepository(session), sender, session, clock, OutboxMode.Deliver);

        Assert.Equal(0, await service.DispatchDueAsync(batchSize: 1));
        var stored = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.ReviewRequired, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(OutboxDispatchService.UnknownReason, stored.LastError);
        Assert.Equal(FixedNow, stored.ClaimedAt);
        Assert.NotNull(stored.DispatchKey);
        Assert.Null(stored.SentAt);
        Assert.DoesNotContain(secret, stored.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecreto", stored.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(bodyToken, stored.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(Recipient, stored.LastError, StringComparison.Ordinal);

        // The claim is retained as traceability, and ReviewRequired is never a reclaimable status.
        clock.Now = FixedNow.AddDays(30);
        Assert.Equal(0, await service.DispatchDueAsync(batchSize: 1));
        var afterMonth = await ReadNotificationAsync(connectionString, id);
        Assert.Equal(1, sender.CallCount);
        Assert.Equal((byte)NotificationStatus.ReviewRequired, afterMonth.Status);
        Assert.Equal(stored.ClaimedAt, afterMonth.ClaimedAt);
        Assert.Equal(stored.DispatchKey, afterMonth.DispatchKey);

        output.WriteLine(
            "SQL review assertions passed: an exception became ReviewRequired with the claim kept, the stored reason " +
            "was the fixed safe one without the password, the body token or the recipient, and a clock thirty days " +
            "ahead never reclaimed it.");
    }

    [Fact]
    public async Task A_stale_key_or_attempt_is_refused_without_writing_and_a_dispatching_row_is_never_reclaimed()
    {
        var target = await PrepareAsync("_Stale");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var id = await SeedOldProducerNotificationAsync(connectionString, "booking:1", FixedNow);

        await using var session = new DbSession(connectionString);
        var repository = new SqlOutboxRepository(session);
        var dispatchKey = Guid.NewGuid();
        var claimed = await ClaimAsync(session, repository, dispatchKey, FixedNow);

        Assert.NotNull(claimed);
        var attempts = claimed!.Attempts;
        var otherKey = Guid.NewGuid();

        // A stale completion is refused: a different dispatch key never overwrites the current owner.
        Assert.False(await FinalizeAsync(session, repository, r =>
            r.MarkSentAsync(id, otherKey, FixedNow, attempts)));
        Assert.False(await FinalizeAsync(session, repository, r =>
            r.MarkForRetryAsync(id, otherKey, FixedNow.AddMinutes(1), OutboxDispatchService.RetryReason, attempts)));
        Assert.False(await FinalizeAsync(session, repository, r =>
            r.MarkFailedAsync(id, otherKey, OutboxDispatchService.PermanentReason, attempts)));
        Assert.False(await FinalizeAsync(session, repository, r =>
            r.MarkForReviewAsync(id, otherKey, OutboxDispatchService.UnknownReason, attempts)));

        // The attempt count is part of the guard: a completion carrying a different count is stale too.
        Assert.False(await FinalizeAsync(session, repository, r =>
            r.MarkSentAsync(id, dispatchKey, FixedNow, attempts + 1)));

        var stored = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Dispatching, stored.Status);
        Assert.Null(stored.SentAt);
        Assert.Null(stored.LastError);
        Assert.Null(stored.NextAttemptAt);
        Assert.Equal(dispatchKey, stored.DispatchKey);
        Assert.Equal(FixedNow, stored.ClaimedAt);
        Assert.Equal(attempts, stored.Attempts);

        // A row left Dispatching is never reclaimed automatically, however far the clock advances.
        Assert.Null(await ClaimAsync(session, repository, Guid.NewGuid(), FixedNow.AddDays(30)));
        Assert.Equal(1, (await ReadNotificationAsync(connectionString, id)).Attempts);

        output.WriteLine(
            "SQL stale assertions passed: a foreign key and a wrong attempt count were refused with the row untouched, " +
            "and the row left Dispatching was still not reclaimed thirty days later.");
    }

    [Fact]
    public async Task Two_independent_concurrent_claims_have_a_single_winner()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var id = await SeedOldProducerNotificationAsync(connectionString, "booking:1", FixedNow);

        // Both claims start together on their own connection; one takes the row UPDLOCK and the other skips it through
        // READPAST (or waits for the commit and finds it Dispatching), so exactly one row leaves Pending.
        using var startGate = new Barrier(participantCount: 2);

        Task<(Guid Key, Notification? Claimed)> ClaimAsync() => Task.Run(async () =>
        {
            var key = Guid.NewGuid();
            await using var session = new DbSession(connectionString);
            var repository = new SqlOutboxRepository(session);
            await using var transaction = await session.BeginAsync();
            startGate.SignalAndWait();
            var claimed = await repository.ClaimNextDueAsync(key, FixedNow);
            await transaction.CommitAsync();
            return (key, claimed);
        });

        var results = await Task.WhenAll(ClaimAsync(), ClaimAsync());

        var winner = Assert.Single(results, result => result.Claimed is not null);
        Assert.Equal(id, winner.Claimed!.Id);
        Assert.Equal(winner.Key, winner.Claimed.DispatchKey);

        var stored = await ReadNotificationAsync(connectionString, id);
        Assert.Equal((byte)NotificationStatus.Dispatching, stored.Status);
        Assert.Equal(winner.Key, stored.DispatchKey);
        Assert.Equal(1, stored.Attempts);

        output.WriteLine(
            "SQL race assertions passed: two independent simultaneous claims of one due notice produced exactly one " +
            "winner with Attempts = 1; this is one forced overlap, not a universal no-deadlock claim.");
    }

    [Theory]
    [InlineData("smtp-password=SuperSecreto")]
    [InlineData("<p>token-de-un-solo-uso</p>")]
    [InlineData("Fallo al enviar el aviso a ana@example.com")]
    public async Task A_reason_outside_the_fixed_safe_set_is_refused_without_echoing_it(string unsafeReason)
    {
        // No WORKUP_T23_OUTBOX_SQL_CONNECTION is needed: the guard must fire before any command is created, so the
        // deliberately unopenable connection string is never used and the failure is never a SQL error.
        await using var session = new DbSession(NeverOpenableConnectionString);
        var repository = new SqlOutboxRepository(session);
        var dispatchKey = Guid.NewGuid();

        var retry = await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.MarkForRetryAsync(1, dispatchKey, FixedNow, unsafeReason, 1));
        var failed = await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.MarkFailedAsync(1, dispatchKey, unsafeReason, 1));
        var review = await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.MarkForReviewAsync(1, dispatchKey, unsafeReason, 1));

        foreach (var error in new[] { retry, failed, review })
        {
            // The refusal is generic and never echoes the rejected value, so it can never become a log of a password.
            Assert.DoesNotContain(unsafeReason, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SuperSecreto", error.Message, StringComparison.Ordinal);
            Assert.Null(error.InnerException);
        }
    }

    // --- Repository helpers ---------------------------------------------------------------------------------------

    private static async Task<Notification?> ClaimAsync(
        DbSession session, SqlOutboxRepository repository, Guid dispatchKey, DateTime now)
    {
        await using var transaction = await session.BeginAsync();
        var claimed = await repository.ClaimNextDueAsync(dispatchKey, now);
        await transaction.CommitAsync();
        return claimed;
    }

    private static async Task<bool> FinalizeAsync(
        DbSession session, SqlOutboxRepository repository, Func<SqlOutboxRepository, Task<bool>> finalize)
    {
        await using var transaction = await session.BeginAsync();
        var applied = await finalize(repository);
        await transaction.CommitAsync();
        return applied;
    }

    // --- Seeding ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Inserts a notice exactly as the seven pre-T23 producers do: only the columns of <c>001_schema.sql</c>, so the
    /// three T23 columns stay NULL and the effective due instant of the row is its <c>CreatedAt</c>.
    /// </summary>
    private static Task<int> SeedOldProducerNotificationAsync(
        string connectionString, string dedupKey, DateTime createdAt,
        string bodyHtml = BodyHtml) =>
        InsertIdAsync(connectionString,
            """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt)
            OUTPUT INSERTED.Id
            VALUES (NULL, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL)
            """,
            ("@type", (byte)NotificationType.BookingCreated),
            ("@recipient", Recipient),
            ("@subject", Subject),
            ("@bodyHtml", bodyHtml),
            ("@dedupKey", dedupKey),
            ("@status", (byte)NotificationStatus.Pending),
            ("@createdAt", createdAt));

    /// <summary>Inserts a <c>Pending</c> notice that already carries the T23 retry gate.</summary>
    private static Task<int> SeedNotificationWithGateAsync(
        string connectionString, string dedupKey, DateTime createdAt, DateTime nextAttemptAt) =>
        InsertIdAsync(connectionString,
            """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt, NextAttemptAt)
            OUTPUT INSERTED.Id
            VALUES (NULL, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL, @nextAttemptAt)
            """,
            ("@type", (byte)NotificationType.BookingCreated),
            ("@recipient", Recipient),
            ("@subject", Subject),
            ("@bodyHtml", BodyHtml),
            ("@dedupKey", dedupKey),
            ("@status", (byte)NotificationStatus.Pending),
            ("@createdAt", createdAt),
            ("@nextAttemptAt", nextAttemptAt));

    // --- Plain reads of the persisted state ---------------------------------------------------------------------

    private sealed record StoredNotification(
        int Id, byte Status, int Attempts, string? LastError, DateTime CreatedAt,
        DateTime? SentAt, DateTime? NextAttemptAt, DateTime? ClaimedAt, Guid? DispatchKey);

    private static async Task<StoredNotification> ReadNotificationAsync(string connectionString, int id)
    {
        var rows = await QueryListAsync(connectionString,
            "SELECT Id, Status, Attempts, LastError, CreatedAt, SentAt, NextAttemptAt, ClaimedAt, DispatchKey FROM Notifications WHERE Id = @id",
            reader => new StoredNotification(
                reader.GetInt32(0), reader.GetByte(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8)),
            ("@id", id));
        return Assert.Single(rows);
    }

    /// <summary>
    /// Sender that reads the notice from its own connection at send time. It proves the claim was committed and its
    /// transaction disposed <em>before</em> the network call, which a sequential repository on one session cannot show.
    /// </summary>
    private sealed class CommittedClaimObserver(DbSession session, string connectionString, int notificationId) : IEmailSender
    {
        public bool SessionTransactionDetachedAtSend { get; private set; }

        public byte StatusAtSend { get; private set; }

        public int AttemptsAtSend { get; private set; }

        public Guid? DispatchKeyAtSend { get; private set; }

        public async Task<EmailSendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(
                "SELECT Status, Attempts, DispatchKey FROM Notifications WHERE Id = @id", connection);
            command.CommandTimeout = 10;
            command.Parameters.Add("@id", SqlDbType.Int).Value = notificationId;
            await using var reader = await command.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct), "The claimed notice must be readable at send time.");
            StatusAtSend = reader.GetByte(0);
            AttemptsAtSend = reader.GetInt32(1);
            DispatchKeyAtSend = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            // The owned claim transaction must already be committed and detached when the sender runs.
            SessionTransactionDetachedAtSend = session.Transaction is null;
            return EmailSendOutcome.Sent;
        }
    }

    // --- Opt-in target ------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves this test's opt-in target. <c>null</c> means the variable is absent: the test returns N/A without
    /// opening any connection or touching any server. The created database is retained and never reused or dropped.
    /// </summary>
    private async Task<SqlTarget?> PrepareAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"N/A: {EnvVar} absent; no SQL connection opened.");
            return null;
        }

        var target = ValidateTarget(supplied, testSuffix);

        var master = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            TrustServerCertificate = true
        };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@database)", connection);
            check.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = target.DatabaseName;
            var existing = await check.ExecuteScalarAsync();
            Assert.True(existing is null or DBNull,
                $"Refusing existing database {target.DatabaseName}; the target must be new and is never reused or dropped.");
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        Assert.Contains("001_schema.sql", scripts);
        Assert.Contains("002_client_account_pending_names.sql", scripts);
        Assert.Contains("003_outbox_dispatch.sql", scripts);
        output.WriteLine(
            $"Target {target.DatabaseName}: DB_ID absent before migration; applied {scripts.Count} schema script(s); retained permanently without teardown.");
        return target;
    }

    /// <summary>
    /// Derives this test's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog,
    /// so one <c>WORKUP_T23_OUTBOX_SQL_CONNECTION</c> value serves every fact. Only the validated LocalDB instance and
    /// the derived database survive into the connection string.
    /// </summary>
    private static SqlTarget ValidateTarget(string suppliedConnectionString, string testSuffix)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{EnvVar} could not be parsed; no SQL connection was opened.");
        }

        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && databaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal)
            && SafeDatabaseName.IsMatch(databaseName);
        if (!string.Equals(supplied.DataSource, LocalDb, StringComparison.OrdinalIgnoreCase)
            || !supplied.IntegratedSecurity
            || !safeName
            || !string.IsNullOrEmpty(supplied.UserID)
            || !string.IsNullOrEmpty(supplied.Password)
            || !string.IsNullOrEmpty(supplied.AttachDBFilename)
            || supplied.UserInstance
            || !string.IsNullOrEmpty(supplied.FailoverPartner))
        {
            throw new InvalidOperationException(
                $"{EnvVar} must target {LocalDb} with Integrated Security and a safe {DatabasePrefix}* database; credentials, attached files and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T23 outbox SQL integration test",
            TrustServerCertificate = true,
            MultipleActiveResultSets = false
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    // --- Plain reads and inserts --------------------------------------------------------------------------------

    private static async Task<List<T>> QueryListAsync<T>(
        string connectionString, string sql, Func<SqlDataReader, T> map,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<T>();
        while (await reader.ReadAsync())
            items.Add(map(reader));
        return items;
    }

    private static async Task<int> InsertIdAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlCommand Create(
        SqlConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            AddParameter(command, name, value);
        return command;
    }

    private static void AddParameter(SqlCommand command, string name, object? value)
    {
        switch (value)
        {
            case DateTime dateTime:
                command.Parameters.Add(name, SqlDbType.DateTime2).Value = dateTime;
                return;
            case byte[] bytes:
                command.Parameters.Add(name, SqlDbType.Binary, bytes.Length).Value = bytes;
                return;
            case null:
                command.Parameters.AddWithValue(name, DBNull.Value);
                return;
            default:
                command.Parameters.AddWithValue(name, value);
                return;
        }
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
