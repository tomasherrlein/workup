using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory outbox port (T23). It mirrors the SQL contract the dispatcher depends on: the claim is a single-writer
/// section that picks the due <c>Pending</c> row with the oldest effective due instant (<c>NextAttemptAt ?? CreatedAt</c>,
/// which must also be not later than the clock, then id), stamps it <c>Dispatching</c> with the caller's dispatch key,
/// increments <c>Attempts</c> before any network call and returns it; every completion is guarded by that key and reports <c>false</c> without writing when the claim
/// is stale (another dispatcher took the row, or the status already moved on).
/// <para>
/// LIMITATION: an in-memory fake never rolls back and holds no row lock across statements, so it cannot prove the
/// atomic claim of SQL Server nor that two concurrent dispatchers never take the same row. Only a SQL fixture can
/// prove the <c>UPDATE TOP(1) ... OUTPUT</c> claim; this fake only proves the service contract, so a test must never
/// present it as the runtime proof of the padlock.
/// </para>
/// </summary>
public sealed class FakeOutboxRepository : IOutboxRepository
{
    private readonly List<Notification> _notifications = [];
    private readonly object _gate = new();
    private int _nextId = 1;

    public int ClaimCount { get; private set; }

    public int StaleFinalizeCount { get; private set; }

    public Notification Seed(
        string dedupKey,
        DateTime createdAt,
        NotificationStatus status = NotificationStatus.Pending,
        DateTime? nextAttemptAt = null,
        int attempts = 0,
        string recipient = "ana@example.com",
        string subject = "Turno confirmado",
        string bodyHtml = "<p>Cuerpo del aviso</p>")
    {
        var notification = new Notification
        {
            Id = _nextId++,
            BusinessId = 1,
            Type = NotificationType.BookingCreated,
            Recipient = recipient,
            Subject = subject,
            BodyHtml = bodyHtml,
            DedupKey = dedupKey,
            Status = status,
            Attempts = attempts,
            CreatedAt = createdAt,
            NextAttemptAt = nextAttemptAt
        };
        if (status == NotificationStatus.Dispatching)
        {
            notification.ClaimedAt = createdAt;
            notification.DispatchKey = Guid.NewGuid();
        }

        _notifications.Add(notification);
        return notification;
    }

    /// <summary>
    /// Replaces the current claim token of a row, exactly as a second dispatcher that took the row over would. A test
    /// uses it to prove a completion whose claim is no longer current is refused instead of overwriting the winner.
    /// </summary>
    public void StealClaim(int notificationId)
    {
        lock (_gate)
        {
            _notifications.Single(notification => notification.Id == notificationId).DispatchKey = Guid.NewGuid();
        }
    }

    public Task<Notification?> ClaimNextDueAsync(Guid dispatchKey, DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var due = _notifications
                .Where(notification => notification.Status == NotificationStatus.Pending &&
                    (notification.NextAttemptAt ?? notification.CreatedAt) <= now)
                .OrderBy(notification => notification.NextAttemptAt ?? notification.CreatedAt)
                .ThenBy(notification => notification.Id)
                .FirstOrDefault();

            if (due is null)
                return Task.FromResult<Notification?>(null);

            ClaimCount++;
            // Attempts counts attempts started, so it is durable here, before the network call: a crash during the send
            // keeps the started attempt, and no finalize increments it again.
            due.Attempts++;
            due.Status = NotificationStatus.Dispatching;
            due.ClaimedAt = now;
            due.DispatchKey = dispatchKey;
            return Task.FromResult<Notification?>(due);
        }
    }

    public Task<bool> MarkSentAsync(int notificationId, Guid dispatchKey, DateTime sentAt, int attempts, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!TryTakeCurrentClaim(notificationId, dispatchKey, out var notification))
                return Task.FromResult(false);

            notification.Status = NotificationStatus.Sent;
            notification.SentAt = sentAt;
            notification.Attempts = attempts;
            notification.LastError = null;
            notification.NextAttemptAt = null;
            notification.ClaimedAt = null;
            notification.DispatchKey = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkForRetryAsync(
        int notificationId, Guid dispatchKey, DateTime nextAttemptAt, string safeReason, int attempts, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!TryTakeCurrentClaim(notificationId, dispatchKey, out var notification))
                return Task.FromResult(false);

            notification.Status = NotificationStatus.Pending;
            notification.NextAttemptAt = nextAttemptAt;
            notification.LastError = safeReason;
            notification.Attempts = attempts;
            notification.ClaimedAt = null;
            notification.DispatchKey = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkFailedAsync(int notificationId, Guid dispatchKey, string safeReason, int attempts, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!TryTakeCurrentClaim(notificationId, dispatchKey, out var notification))
                return Task.FromResult(false);

            notification.Status = NotificationStatus.Failed;
            notification.LastError = safeReason;
            notification.Attempts = attempts;
            notification.SentAt = null;
            notification.NextAttemptAt = null;
            notification.ClaimedAt = null;
            notification.DispatchKey = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkForReviewAsync(int notificationId, Guid dispatchKey, string safeReason, int attempts, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!TryTakeCurrentClaim(notificationId, dispatchKey, out var notification))
                return Task.FromResult(false);

            notification.Status = NotificationStatus.ReviewRequired;
            notification.LastError = safeReason;
            notification.Attempts = attempts;
            // ClaimedAt and DispatchKey stay: the claim that produced the uncertain result is traceable for the human
            // review, and the row is no longer Pending, so the dispatcher never reclaims it automatically.
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Single-writer guard of a completion: only the dispatcher holding the current claim of a <c>Dispatching</c> row
    /// may finish it. A mismatch is a stale completion and must never overwrite the winner.
    /// </summary>
    private bool TryTakeCurrentClaim(int notificationId, Guid dispatchKey, out Notification notification)
    {
        notification = _notifications.Single(row => row.Id == notificationId);
        if (notification.Status != NotificationStatus.Dispatching || notification.DispatchKey != dispatchKey)
        {
            StaleFinalizeCount++;
            return false;
        }

        return true;
    }
}
