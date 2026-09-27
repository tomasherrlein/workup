using WorkUp.Domain.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Email in the outbox. <see cref="DedupKey"/> is unique, so re-enqueueing the same notice (a technical retry of the
/// business operation that queued it) never queues it twice (RF-10, RF-17). This is <b>not</b> an exactly-once delivery
/// guarantee: claiming a row and confirming the send are two separate transactions, so a crash between the claim and
/// the confirmation leaves the delivery unknown, with the row in <see cref="NotificationStatus.Dispatching"/>. The
/// dispatcher (T23) never requeues that row automatically; it is resolved manually, and only a human may choose to
/// resend it. Possible nondelivery is preferred over a duplicate automatic send.
/// </summary>
public class Notification : Entity
{
    public int? BusinessId { get; set; }
    public NotificationType Type { get; set; }
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string DedupKey { get; set; } = "";
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// Earliest moment the dispatcher may try again (T23). Null means the effective due instant is
    /// <see cref="CreatedAt"/>. It gates a retry after a failure that was provably before the send, and it is cleared
    /// whenever the notice leaves <c>Pending</c>.
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>When the current claim was taken; null unless the status is <c>Dispatching</c> or <c>ReviewRequired</c>.</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>
    /// Claim token of the current dispatch (T23). Every completion must match it, so a stale completion of a claim
    /// that was taken over writes nothing instead of overwriting the current owner.
    /// </summary>
    public Guid? DispatchKey { get; set; }
}
