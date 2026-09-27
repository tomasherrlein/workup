using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory viewer port (T26) for the owner controller and view tests. It mirrors the persistence contract instead of
/// approximating it: the same bounded keyset page (newest first by <c>Id</c>, strictly older than the cursor, clamped
/// by <see cref="NotificationViewerBounds"/>), the same strict tenant filter — a notice of another business and a
/// notice with no business at all are both invisible, however their address reads — and the same neutral answer for an
/// invalid identifier, answered before anything is read.
/// <para>
/// The stored row is the <see cref="Notification"/> entity, so a test can seed the fields the viewer must never expose
/// (body, stored error, claim) and observe that the projections simply cannot carry them.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake never touches SQL Server, so it cannot prove the tenant predicate, the keyset cursor
/// or the bounded <c>TOP</c> of the real adapter. It proves the controller contract only; the runtime proof is the
/// opt-in SQL fixture.
/// </para>
/// </summary>
public sealed class FakeNotificationViewerRepository : INotificationViewerRepository
{
    /// <summary>The creation instant of a seeded row when the test does not care about it.</summary>
    public static readonly DateTime DefaultCreatedAt = new(2026, 3, 10, 9, 0, 0);

    private const string DefaultRecipient = "avisos@duena.example";
    private const string DefaultSubject = "Turno confirmado";
    private const string DefaultBodyHtml = "<p>Cuerpo del aviso</p>";

    private readonly List<Notification> _notifications = [];
    private readonly object _gate = new();
    private int _nextId = 1;

    /// <summary>List reads that actually ran, so a test can prove an invalid identifier never reached storage.</summary>
    public int ListCallCount { get; private set; }

    /// <summary>Detail reads that actually ran, so a test can prove an invalid identifier never reached storage.</summary>
    public int DetailCallCount { get; private set; }

    /// <summary>
    /// Adds a notice and returns it, so the caller can use its identity. An omitted <paramref name="id"/> mimics the
    /// SQL identity: it is assigned in insertion order.
    /// </summary>
    public Notification Seed(
        int? businessId = 1,
        NotificationStatus status = NotificationStatus.Pending,
        NotificationType type = NotificationType.BookingCreated,
        string recipient = DefaultRecipient,
        string subject = DefaultSubject,
        string bodyHtml = DefaultBodyHtml,
        int attempts = 0,
        DateTime? createdAt = null,
        DateTime? sentAt = null,
        int? id = null)
    {
        lock (_gate)
        {
            var assignedId = id ?? _nextId;
            if (assignedId >= _nextId)
                _nextId = assignedId + 1;

            var notification = new Notification
            {
                Id = assignedId,
                BusinessId = businessId,
                Type = type,
                Recipient = recipient,
                Subject = subject,
                BodyHtml = bodyHtml,
                DedupKey = $"viewer:{assignedId}",
                Status = status,
                Attempts = attempts,
                CreatedAt = createdAt ?? DefaultCreatedAt,
                SentAt = sentAt
            };
            _notifications.Add(notification);
            return notification;
        }
    }

    public Task<IReadOnlyList<NotificationViewerListItem>> ListRecentAsync(
        int businessId, int? beforeId, int pageSize, CancellationToken ct = default)
    {
        // The neutral answer of a non-positive tenant is produced before anything is read, exactly as the adapter does.
        if (businessId <= 0)
            return Task.FromResult<IReadOnlyList<NotificationViewerListItem>>([]);

        var boundedPageSize = NotificationViewerBounds.ClampPageSize(pageSize);

        lock (_gate)
        {
            ListCallCount++;

            // BusinessId == businessId is also what excludes every row with no business at all.
            var items = _notifications
                .Where(notification => notification.BusinessId == businessId
                    && (beforeId is null || notification.Id < beforeId))
                .OrderByDescending(notification => notification.Id)
                .Take(boundedPageSize)
                .Select(stored => ToList(stored))
                .ToArray();

            return Task.FromResult<IReadOnlyList<NotificationViewerListItem>>(items);
        }
    }

    public Task<NotificationViewerDetail?> GetDetailAsync(
        int businessId, int notificationId, CancellationToken ct = default)
    {
        if (businessId <= 0 || notificationId <= 0)
            return Task.FromResult<NotificationViewerDetail?>(null);

        lock (_gate)
        {
            DetailCallCount++;

            var stored = _notifications.FirstOrDefault(notification =>
                notification.Id == notificationId && notification.BusinessId == businessId);
            if (stored is null)
                return Task.FromResult<NotificationViewerDetail?>(null);

            return Task.FromResult<NotificationViewerDetail?>(new NotificationViewerDetail(
                stored.Id,
                stored.Type,
                stored.Status,
                stored.Recipient,
                stored.Subject,
                stored.Attempts,
                stored.CreatedAt,
                stored.SentAt,
                stored.BodyHtml));
        }
    }

    /// <summary>
    /// Projects a stored row into the list row. The body, the stored error and the claim stay behind: the list
    /// projection has no member able to carry them.
    /// </summary>
    private static NotificationViewerListItem ToList(Notification stored) => new(
        stored.Id,
        stored.Type,
        stored.Status,
        stored.Recipient,
        stored.Subject,
        stored.Attempts,
        stored.CreatedAt,
        stored.SentAt);
}
