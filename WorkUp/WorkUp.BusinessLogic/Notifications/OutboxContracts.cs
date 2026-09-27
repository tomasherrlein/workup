using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// How the dispatcher treats queued notices. It comes from <c>WorkUp:Email:Mode</c>.
/// </summary>
public enum OutboxMode
{
    /// <summary>
    /// The outbox retains every notice <c>Pending</c> without sending it: nothing is delivered and nothing is marked
    /// <c>Sent</c>, so the test tools of T26 can show the queued emails.
    /// </summary>
    Retain = 1,

    /// <summary>Sends due notices through <see cref="IEmailSender"/>.</summary>
    Deliver = 2
}

/// <summary>
/// Parses the configured email mode (<c>WorkUp:Email:Mode</c>). An empty or missing value defaults to
/// <see cref="OutboxMode.Retain"/> so a fresh installation queues but never sends, and <c>Outbox</c> and <c>Smtp</c> are
/// the only accepted values. Any other non-empty value throws, because a typo such as <c>Smpt</c> must fail loudly at
/// startup instead of silently stopping every email; this parser never turns an unknown value into a sending mode.
/// </summary>
public static class OutboxModeParser
{
    public static OutboxMode Parse(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return OutboxMode.Retain;

        var value = mode.Trim();
        if (string.Equals(value, "Outbox", StringComparison.OrdinalIgnoreCase))
            return OutboxMode.Retain;
        if (string.Equals(value, "Smtp", StringComparison.OrdinalIgnoreCase))
            return OutboxMode.Deliver;

        throw new InvalidOperationException(
            $"Configuración WorkUp:Email:Mode inválida: '{value}'. Valores válidos: Outbox, Smtp.");
    }
}

/// <summary>
/// One queued email to send. It carries the subject and the HTML body, which can embed a single-use link; it is
/// transient input of a send call and must never be logged or stored as an error.
/// </summary>
public sealed record EmailMessage(string Recipient, string Subject, string BodyHtml);

/// <summary>
/// What the sender proved about a single attempt. The four outcomes exist because the dispatcher treats them
/// differently on purpose: only <see cref="Sent"/> completes the notice, only <see cref="DefinitelyNotSentRetryable"/>
/// may be retried automatically, and <see cref="OutcomeUnknown"/> is never reclaimed because a duplicate email is worse
/// than a delayed one.
/// </summary>
public enum EmailSendOutcome
{
    /// <summary>The provider confirmed that it accepted the message.</summary>
    Sent = 1,

    /// <summary>
    /// The failure happened provably <em>before</em> the message could reach the provider, so a retry cannot duplicate
    /// it. Only this outcome is eligible for the bounded automatic retry.
    /// </summary>
    DefinitelyNotSentRetryable = 2,

    /// <summary>The failure is permanent, for example an invalid destination; retrying cannot help.</summary>
    DefinitelyNotSentPermanent = 3,

    /// <summary>
    /// The dispatcher cannot know whether the provider accepted the message, for example a timeout or a socket error.
    /// A human must review it: the dispatcher never retries this outcome.
    /// </summary>
    OutcomeUnknown = 4
}

/// <summary>
/// Sends exactly one queued email. It returns a typed outcome and must not throw to report a definite result; an
/// exception is treated as <see cref="EmailSendOutcome.OutcomeUnknown"/>. It must not return or log raw provider text:
/// an SMTP error can contain a password, and the notice body can contain a single-use token.
/// </summary>
public interface IEmailSender
{
    Task<EmailSendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// Persistence boundary of the outbox dispatcher (T23). Every call happens inside the caller's transaction.
/// <para>
/// The claim and the completion are two separate transactions with the sender call in between, because no storage can
/// promise exactly-once SMTP delivery across a crash between the send and the confirmation. The contract therefore
/// prioritises avoiding a duplicate automatic send over guaranteeing immediate delivery: it never reclaims a row it
/// does not own, and an uncertain outcome goes to manual review instead of an automatic resend:
/// <list type="bullet">
/// <item><see cref="ClaimNextDueAsync"/> is one atomic statement, typically <c>UPDATE TOP(1) ... SET Status = 4,
/// ClaimedAt = @now, DispatchKey = @key, Attempts = Attempts + 1 OUTPUT inserted.*</c>, that selects the oldest
/// <em>effective due instant</em>, <c>NextAttemptAt ?? CreatedAt</c>, then the id, and treats a row as due only when
/// that instant is not later than the clock. Two dispatchers therefore never claim the same row, a fresh enqueue and a
/// retry are ordered by when they became due rather than by when they were queued, a rewound simulated clock (T26)
/// makes a row created ahead of it wait, and the row leaves <c>Pending</c> so a second tick cannot pick it up while it
/// is being sent.</item>
/// <item>Every completion is guarded by the claim's <see cref="Notification.DispatchKey"/> and the <c>Dispatching</c>
/// status, so a stale completion writes nothing and reports <c>false</c> instead of overwriting the current owner.</item>
/// <item>A retry is only a re-<c>Pending</c> of a row that the dispatcher proved was not sent, with a
/// <see cref="Notification.NextAttemptAt"/> gate; an unknown outcome becomes <c>ReviewRequired</c> and keeps its
/// claim, so nothing reclaims it automatically and a human decides.</item>
/// </list>
/// </para>
/// </summary>
public interface IOutboxRepository
{
    /// <summary>
    /// Atomically claims the next due <c>Pending</c> row, or returns null when none is due. A row is due when its
    /// effective due instant <c>NextAttemptAt ?? CreatedAt</c> is not later than <paramref name="now"/>, and the claim
    /// order is that same instant ascending, then the id: a fresh enqueue and a retry are ordered by when they became
    /// due, not by when they were queued. The predicate uses the same instant, so a row whose
    /// <see cref="Notification.CreatedAt"/> is ahead of a rewound simulated clock (T26) waits instead of being treated
    /// as unconditionally due. On a claim the row becomes <c>Dispatching</c> with
    /// <see cref="Notification.ClaimedAt"/> set to <paramref name="now"/>,
    /// <see cref="Notification.DispatchKey"/> set to <paramref name="dispatchKey"/> and
    /// <see cref="Notification.Attempts"/> incremented in that same statement, so the started attempt is durable before
    /// any network call. The returned instance carries the incremented value; the caller must use it (and the key) for
    /// every completion of this claim, and a completion must never increment the count again.
    /// </summary>
    Task<Notification?> ClaimNextDueAsync(Guid dispatchKey, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Completes the claim as <c>Sent</c>: status <c>Sent</c>, <paramref name="sentAt"/>, and
    /// <paramref name="attempts"/> written as the count the claim already incremented. Clears the claim, the retry gate
    /// and the error. Returns <c>false</c> without writing when the claim is stale (a different dispatch key, or the row
    /// is no longer <c>Dispatching</c>).
    /// </summary>
    Task<bool> MarkSentAsync(int notificationId, Guid dispatchKey, DateTime sentAt, int attempts, CancellationToken ct = default);

    /// <summary>
    /// Returns the claim to <c>Pending</c> with a new due time after a failure that was provably before the send, so it
    /// may be retried. Writes <paramref name="attempts"/> as it is (the claim already counted the started attempt) and
    /// stores only <paramref name="safeReason"/>, never provider text. Returns <c>false</c> when the claim is stale.
    /// </summary>
    Task<bool> MarkForRetryAsync(
        int notificationId, Guid dispatchKey, DateTime nextAttemptAt, string safeReason, int attempts, CancellationToken ct = default);

    /// <summary>
    /// Completes the claim as terminal <c>Failed</c>, writing <paramref name="attempts"/> as it is and storing only
    /// <paramref name="safeReason"/>. Returns <c>false</c> when the claim is stale.
    /// </summary>
    Task<bool> MarkFailedAsync(int notificationId, Guid dispatchKey, string safeReason, int attempts, CancellationToken ct = default);

    /// <summary>
    /// Completes the claim as <c>ReviewRequired</c>: the outcome is unsure and a human must decide. Keeps
    /// <see cref="Notification.ClaimedAt"/> and <see cref="Notification.DispatchKey"/> for traceability, stores only
    /// <paramref name="safeReason"/>, and returns <c>false</c> when the claim is stale.
    /// </summary>
    Task<bool> MarkForReviewAsync(int notificationId, Guid dispatchKey, string safeReason, int attempts, CancellationToken ct = default);
}
