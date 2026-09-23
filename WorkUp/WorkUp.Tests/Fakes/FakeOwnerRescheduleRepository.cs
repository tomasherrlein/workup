using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeOwnerRescheduleRepository : IOwnerRescheduleRepository
{
    public OwnerRescheduleBooking? Booking { get; set; }
    public int WorkerBusinessId { get; set; } = 7;
    public bool AllowWorkerLock { get; set; } = true;
    public bool AllowReschedule { get; set; } = true;
    public bool AllowLinkExpiryUpdate { get; set; } = true;
    public bool ThrowOnNotificationInsert { get; set; }
    public BookingAvailabilitySnapshot Snapshot { get; set; } = new([], [], [], []);
    public List<AppointmentLink> Links { get; } = [];
    public List<Notification> Notifications { get; } = [];
    public List<string> Calls { get; } = [];
    public int NotificationInsertCount { get; private set; }

    private OwnerRescheduleBooking? _pendingBooking;
    private readonly Dictionary<int, DateTime> _pendingLinkExpiries = [];
    private readonly List<Notification> _pendingNotifications = [];

    public Task<int?> GetWorkerIdAsync(int businessId, int bookingId, CancellationToken ct = default)
    {
        Calls.Add("lookup-worker");
        return Task.FromResult<int?>(Booking?.BusinessId == businessId && Booking.BookingId == bookingId
            ? Booking.WorkerId
            : null);
    }

    public Task<bool> LockWorkerAsync(int businessId, int workerId, CancellationToken ct = default)
    {
        Calls.Add("lock-worker");
        return Task.FromResult(AllowWorkerLock && businessId == WorkerBusinessId && Booking?.WorkerId == workerId);
    }

    public Task<OwnerRescheduleBooking?> LockOwnerBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        Calls.Add("lock-booking");
        return Task.FromResult(Booking?.BusinessId == businessId && Booking.BookingId == bookingId ? Booking : null);
    }

    public Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly date, CancellationToken ct = default)
    {
        Calls.Add("availability");
        return Task.FromResult(Snapshot);
    }

    public Task<OwnerRescheduleBooking?> GetOwnerBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        Calls.Add("read-booking");
        return Task.FromResult(Booking?.BusinessId == businessId && Booking.BookingId == bookingId ? Booking : null);
    }

    public Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly startDate, DateOnly endDate, CancellationToken ct = default)
    {
        Calls.Add("availability-range");
        return Task.FromResult(Snapshot);
    }

    public Task<IReadOnlyList<OwnerRescheduleCurrentLink>> GetCurrentLinksAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default)
    {
        Calls.Add("active-links");
        IReadOnlyList<OwnerRescheduleCurrentLink> current = businessId == Booking?.BusinessId && bookingId == Booking.BookingId
            ? Links.Where(link => link.BookingId == bookingId && link.IsUsable(now))
                .Take(2).Select(link => new OwnerRescheduleCurrentLink(link.Id, link.ExpiresAt)).ToArray()
            : [];
        return Task.FromResult(current);
    }

    public Task<bool> TryApplyRescheduleAsync(
        int businessId,
        OwnerRescheduleBooking booking,
        DateTime newStartsAt,
        DateTime newEndsAt,
        DateTime now,
        CancellationToken ct = default)
    {
        Calls.Add("apply-reschedule");
        if (!AllowReschedule || Booking is null || businessId != Booking.BusinessId || booking.BookingId != Booking.BookingId ||
            booking.StartsAt != Booking.StartsAt || booking.WorkerId != Booking.WorkerId)
            return Task.FromResult(false);

        _pendingBooking = Booking with
        {
            StartsAt = newStartsAt,
            EndsAt = newEndsAt,
            BookingStatus = WorkUp.Domain.Enums.BookingStatus.Confirmed,
            ConfirmedAt = now
        };
        return Task.FromResult(true);
    }

    public Task<bool> UpdateCurrentLinkExpiryAsync(
        int businessId,
        int bookingId,
        int linkId,
        DateTime expectedExpiresAt,
        DateTime newExpiresAt,
        DateTime now,
        CancellationToken ct = default)
    {
        Calls.Add("update-link-expiry");
        var link = Links.SingleOrDefault(candidate => candidate.Id == linkId && candidate.BookingId == bookingId);
        if (!AllowLinkExpiryUpdate || businessId != Booking?.BusinessId || link is null ||
            link.RevokedAt is not null || link.ExpiresAt <= now || link.ExpiresAt != expectedExpiresAt)
            return Task.FromResult(false);

        _pendingLinkExpiries[linkId] = newExpiresAt;
        return Task.FromResult(true);
    }

    public Task InsertNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        Calls.Add("insert-notification");
        NotificationInsertCount++;
        if (ThrowOnNotificationInsert)
            throw new InvalidOperationException("Simulated outbox failure.");

        _pendingNotifications.Add(notification);
        return Task.CompletedTask;
    }

    public void CommitPendingChanges()
    {
        if (_pendingBooking is not null)
            Booking = _pendingBooking;
        foreach (var (linkId, expiresAt) in _pendingLinkExpiries)
        {
            var link = Links.Single(candidate => candidate.Id == linkId);
            link.ExpiresAt = expiresAt;
        }
        Notifications.AddRange(_pendingNotifications);
        DiscardPendingChanges();
    }

    public void DiscardPendingChanges()
    {
        _pendingBooking = null;
        _pendingLinkExpiries.Clear();
        _pendingNotifications.Clear();
    }
}
