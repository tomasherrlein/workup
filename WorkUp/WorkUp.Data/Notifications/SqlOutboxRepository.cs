using System.Collections.Frozen;
using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Notifications;

/// <summary>
/// ADO.NET persistence of the outbox dispatcher (T23, RF-10/RF-17). Every command runs on the request connection and
/// joins the caller's transaction, so the repository never opens a transaction of its own: the service owns the short
/// claim unit of work and the separate completion one, and it commits and disposes the claim <em>before</em> the sender
/// runs. That is what makes the started attempt durable before any network call and the completion a genuinely new
/// transaction.
/// <para>
/// <see cref="ClaimNextDueAsync"/> is one atomic statement: a single <c>UPDATE</c> over a <c>TOP (1)</c> CTE ordered by
/// <c>COALESCE(NextAttemptAt, CreatedAt), Id</c> and taken <c>UPDLOCK, ROWLOCK, READPAST</c>. The update itself is the
/// claim, so the row cannot leave <c>Pending</c> without the status, the owner token and the incremented attempt count
/// being written together, and a second dispatcher either skips the locked row or finds it already
/// <c>Dispatching</c>. The <c>OUTPUT</c> clause returns the claimed row, so the dispatcher sends the notice it claimed
/// without a second read and cannot send a different one.
/// </para>
/// <para>
/// The four completions are single guarded <c>UPDATE</c> statements that match <c>Id</c>, the claim's
/// <c>DispatchKey</c>, the <c>Dispatching</c> status and the claim's attempt count; their row count is the return
/// value, so a stale completion writes nothing and reports <c>false</c> instead of overwriting the current owner.
/// <c>Dispatching</c> and <c>ReviewRequired</c> are never reclaimed by any statement here: a notice whose send was
/// interrupted or whose outcome is unknown is resolved by a human, because a duplicate automatic send is worse than a
/// delayed one.
/// </para>
/// <para>
/// Nothing here logs. <c>LastError</c> only accepts the fixed safe reasons of <see cref="OutboxDispatchService"/>, and
/// a value outside that set is refused before any command is created and never echoed, so a direct caller cannot make
/// this layer persist raw provider text, a password or the notice body.
/// </para>
/// </summary>
public sealed class SqlOutboxRepository(DbSession session) : SqlRepositoryBase(session), IOutboxRepository
{
    /// <summary>
    /// The only <c>LastError</c> values this layer will write. They are the dispatcher's fixed safe reasons, which
    /// never embed provider text, a subject, a recipient or a token; a completion that asks for anything else is
    /// refused rather than stored.
    /// </summary>
    private static readonly FrozenSet<string> SafeReasons = new[]
    {
        OutboxDispatchService.RetryReason,
        OutboxDispatchService.PermanentReason,
        OutboxDispatchService.RetriesExhaustedReason,
        OutboxDispatchService.UnknownReason
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every column of the entity, so the claim can hand the row to the sender without a second read.</summary>
    private const string NotificationColumns = """
        Id, BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt,
        NextAttemptAt, ClaimedAt, DispatchKey
        """;

    /// <summary>
    /// The atomic claim. The CTE selects the single due row with the oldest effective due instant and the id as the
    /// tie-break under <c>UPDLOCK, ROWLOCK, READPAST</c>; updating that same row is what claims it, so the status, the
    /// owner token, the claim instant and the incremented attempt count are written in one statement. The predicate
    /// uses the effective due instant, so a row whose <c>CreatedAt</c> is ahead of a rewound simulated clock waits
    /// instead of being treated as unconditionally due, and <c>NULL</c> keeps meaning the instant of the enqueue.
    /// </summary>
    private const string ClaimSql = $"""
        WITH due AS (
            SELECT TOP (1) {NotificationColumns}
              FROM Notifications WITH (UPDLOCK, ROWLOCK, READPAST)
             WHERE Status = @pending
               AND COALESCE(NextAttemptAt, CreatedAt) <= @now
             ORDER BY COALESCE(NextAttemptAt, CreatedAt), Id
        )
        UPDATE due
           SET Status = @dispatching,
               Attempts = Attempts + 1,
               ClaimedAt = @now,
               DispatchKey = @dispatchKey
        OUTPUT INSERTED.Id, INSERTED.BusinessId, INSERTED.Type, INSERTED.Recipient, INSERTED.Subject,
               INSERTED.BodyHtml, INSERTED.DedupKey, INSERTED.Status, INSERTED.Attempts, INSERTED.LastError,
               INSERTED.CreatedAt, INSERTED.SentAt, INSERTED.NextAttemptAt, INSERTED.ClaimedAt, INSERTED.DispatchKey;
        """;

    /// <summary>
    /// Confirms the claim as <c>Sent</c> and clears the claim, the retry gate and the error. The guard matches the
    /// claim key, the <c>Dispatching</c> status and the attempted count, so a stale completion writes nothing. No
    /// <c>SET NOCOUNT ON</c>: the row count of this single statement is the return value.
    /// </summary>
    private const string MarkSentSql = """
        UPDATE Notifications
           SET Status = @sent,
               SentAt = @sentAt,
               Attempts = @attempts,
               LastError = NULL,
               NextAttemptAt = NULL,
               ClaimedAt = NULL,
               DispatchKey = NULL
         WHERE Id = @notificationId
           AND DispatchKey = @dispatchKey
           AND Status = @dispatching
           AND Attempts = @attempts;
        """;

    /// <summary>
    /// Returns a provably-not-sent claim to <c>Pending</c> with a new retry gate, clearing the claim so the row is
    /// claimable again only from that instant. The gate is why a retry is never an immediate hot loop.
    /// </summary>
    private const string MarkForRetrySql = """
        UPDATE Notifications
           SET Status = @pending,
               NextAttemptAt = @nextAttemptAt,
               LastError = @safeReason,
               Attempts = @attempts,
               ClaimedAt = NULL,
               DispatchKey = NULL
         WHERE Id = @notificationId
           AND DispatchKey = @dispatchKey
           AND Status = @dispatching
           AND Attempts = @attempts;
        """;

    /// <summary>Makes the claim terminal <c>Failed</c> and drops every trace of the claim and the retry gate.</summary>
    private const string MarkFailedSql = """
        UPDATE Notifications
           SET Status = @failed,
               LastError = @safeReason,
               Attempts = @attempts,
               SentAt = NULL,
               NextAttemptAt = NULL,
               ClaimedAt = NULL,
               DispatchKey = NULL
         WHERE Id = @notificationId
           AND DispatchKey = @dispatchKey
           AND Status = @dispatching
           AND Attempts = @attempts;
        """;

    /// <summary>
    /// Moves an uncertain claim to <c>ReviewRequired</c> and keeps <c>ClaimedAt</c> and <c>DispatchKey</c>: the row is
    /// no longer <c>Pending</c>, so nothing reclaims it and the human who decides can see which claim produced the
    /// uncertain result.
    /// </summary>
    private const string MarkForReviewSql = """
        UPDATE Notifications
           SET Status = @review,
               LastError = @safeReason,
               Attempts = @attempts
         WHERE Id = @notificationId
           AND DispatchKey = @dispatchKey
           AND Status = @dispatching
           AND Attempts = @attempts;
        """;

    public Task<Notification?> ClaimNextDueAsync(Guid dispatchKey, DateTime now, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            ClaimSql,
            MapNotification,
            p => p
                .AddValue("@pending", NotificationStatus.Pending)
                .AddValue("@dispatching", NotificationStatus.Dispatching)
                .AddValue("@dispatchKey", dispatchKey)
                .AddDateTime2("@now", now),
            ct);

    public async Task<bool> MarkSentAsync(
        int notificationId, Guid dispatchKey, DateTime sentAt, int attempts, CancellationToken ct = default) =>
        await ExecuteAsync(
            MarkSentSql,
            p => p
                .AddValue("@notificationId", notificationId)
                .AddValue("@dispatchKey", dispatchKey)
                .AddValue("@sent", NotificationStatus.Sent)
                .AddValue("@dispatching", NotificationStatus.Dispatching)
                .AddValue("@attempts", attempts)
                .AddDateTime2("@sentAt", sentAt),
            ct) == 1;

    public async Task<bool> MarkForRetryAsync(
        int notificationId, Guid dispatchKey, DateTime nextAttemptAt, string safeReason, int attempts,
        CancellationToken ct = default)
    {
        RequireSafeReason(safeReason);
        return await ExecuteAsync(
            MarkForRetrySql,
            p =>
            {
                p.AddValue("@notificationId", notificationId)
                    .AddValue("@dispatchKey", dispatchKey)
                    .AddValue("@pending", NotificationStatus.Pending)
                    .AddValue("@dispatching", NotificationStatus.Dispatching)
                    .AddValue("@attempts", attempts)
                    .AddDateTime2("@nextAttemptAt", nextAttemptAt);
                AddSafeReason(p, safeReason);
            },
            ct) == 1;
    }

    public async Task<bool> MarkFailedAsync(
        int notificationId, Guid dispatchKey, string safeReason, int attempts, CancellationToken ct = default)
    {
        RequireSafeReason(safeReason);
        return await ExecuteAsync(
            MarkFailedSql,
            p =>
            {
                p.AddValue("@notificationId", notificationId)
                    .AddValue("@dispatchKey", dispatchKey)
                    .AddValue("@failed", NotificationStatus.Failed)
                    .AddValue("@dispatching", NotificationStatus.Dispatching)
                    .AddValue("@attempts", attempts);
                AddSafeReason(p, safeReason);
            },
            ct) == 1;
    }

    public async Task<bool> MarkForReviewAsync(
        int notificationId, Guid dispatchKey, string safeReason, int attempts, CancellationToken ct = default)
    {
        RequireSafeReason(safeReason);
        return await ExecuteAsync(
            MarkForReviewSql,
            p =>
            {
                p.AddValue("@notificationId", notificationId)
                    .AddValue("@dispatchKey", dispatchKey)
                    .AddValue("@review", NotificationStatus.ReviewRequired)
                    .AddValue("@dispatching", NotificationStatus.Dispatching)
                    .AddValue("@attempts", attempts);
                AddSafeReason(p, safeReason);
            },
            ct) == 1;
    }

    /// <summary>
    /// Refuses any <c>LastError</c> text outside the fixed safe set, before a command or a connection exists. The
    /// message never embeds the rejected value, so a refusal cannot leak a password or provider text into an exception
    /// log; the caller that passed it sees a generic argument error instead of a stored secret. The allow-list also
    /// makes the <c>NVARCHAR(500)</c> column unreachable by a longer string.
    /// </summary>
    private static void RequireSafeReason(string safeReason)
    {
        if (safeReason is null || !SafeReasons.Contains(safeReason))
        {
            throw new ArgumentException(
                "El motivo del aviso no pertenece al conjunto fijo de motivos seguros del despacho; el valor recibido no se persiste ni se registra.",
                nameof(safeReason));
        }
    }

    private static void AddSafeReason(SqlParameterCollection parameters, string safeReason)
    {
        // Bound explicitly to the column width of LastError, so nothing can be silently truncated into a half-message.
        parameters.Add("@safeReason", SqlDbType.NVarChar, 500).Value = safeReason;
    }

    private static Notification MapNotification(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.GetNullable<int>("BusinessId"),
        Type = reader.GetEnum<NotificationType>("Type"),
        Recipient = reader.Get<string>("Recipient"),
        Subject = reader.Get<string>("Subject"),
        BodyHtml = reader.Get<string>("BodyHtml"),
        DedupKey = reader.Get<string>("DedupKey"),
        Status = reader.GetEnum<NotificationStatus>("Status"),
        Attempts = reader.Get<int>("Attempts"),
        LastError = reader.GetNullableString("LastError"),
        CreatedAt = reader.Get<DateTime>("CreatedAt"),
        SentAt = reader.GetNullable<DateTime>("SentAt"),
        NextAttemptAt = reader.GetNullable<DateTime>("NextAttemptAt"),
        ClaimedAt = reader.GetNullable<DateTime>("ClaimedAt"),
        DispatchKey = reader.GetNullable<Guid>("DispatchKey")
    };
}
