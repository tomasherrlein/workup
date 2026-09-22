using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

public sealed class FakePrivateLinkRepository : IPrivateAppointmentLinkRepository
{
    private readonly List<LinkRow> _rows = [];
    private readonly Dictionary<(string Token, PrivateAppointmentLinkAction Action), int> _transitions = [];
    private TaskCompletionSource? _ownerReadBarrier;
    private int _ownerReadCount;
    private int _nextLinkId = 1;

    public int RevokeCount { get; private set; }
    public int OwnerLinkInsertAttempts { get; private set; }
    public Action? AfterOwnerBookingLock { get; set; }
    public Action? AfterOwnerLinkRevoke { get; set; }

    public void SynchronizeNextTwoOwnerReads()
    {
        _ownerReadCount = 0;
        _ownerReadBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void AddLink(
        string token,
        int businessId,
        int bookingId,
        PrivateAppointmentLinkRecord appointment,
        DateTime? revokedAt = null)
    {
        _rows.Add(new LinkRow(
            businessId,
            bookingId,
            new AppointmentLink
            {
                Id = _nextLinkId++,
                BookingId = bookingId,
                TokenHash = SecureToken.Hash(token),
                ProtectedToken = "protected:" + token,
                ExpiresAt = appointment.ExpiresAt,
                RevokedAt = revokedAt,
                CreatedAt = DateTime.MinValue
            },
            appointment));
    }

    public Task<PrivateAppointmentLinkRecord?> GetPublicAppointmentAsync(
        byte[] tokenHash, DateTime now, CancellationToken ct = default)
    {
        var row = Find(tokenHash);
        if (row is null || !row.Link.IsUsable(now))
            return Task.FromResult<PrivateAppointmentLinkRecord?>(null);
        return Task.FromResult<PrivateAppointmentLinkRecord?>(row.Appointment with { ExpiresAt = row.Link.ExpiresAt });
    }

    public Task<bool> ApplyPublicActionAsync(
        byte[] tokenHash, PrivateAppointmentLinkAction action, DateTime now, CancellationToken ct = default)
    {
        var row = Find(tokenHash);
        if (row is null || !row.Link.IsUsable(now) || row.Appointment.AppointmentStatus != AppointmentStatus.Scheduled ||
            !HasConsistentActionState(row.Appointment))
            return Task.FromResult(false);

        var booking = row.Appointment;
        switch (action)
        {
            case PrivateAppointmentLinkAction.Confirm when now < booking.StartsAt:
                if (booking.BookingStatus == BookingStatus.Confirmed)
                    return Task.FromResult(true);
                if (booking.BookingStatus != BookingStatus.Pending)
                    return Task.FromResult(false);
                row.Appointment = booking with { BookingStatus = BookingStatus.Confirmed, ConfirmedAt = now };
                break;
            case PrivateAppointmentLinkAction.Cancel when now < booking.StartsAt:
                if (!IsPendingOrConfirmed(booking.BookingStatus))
                    return Task.FromResult(false);
                row.Appointment = booking with
                {
                    BookingStatus = BookingStatus.Cancelled,
                    CancelledBy = CancelledBy.Client,
                    CancelledAt = now
                };
                break;
            case PrivateAppointmentLinkAction.ReportAbsence when now >= booking.StartsAt:
                if (!IsPendingOrConfirmed(booking.BookingStatus))
                    return Task.FromResult(false);
                row.Appointment = booking with
                {
                    BookingStatus = BookingStatus.Absent,
                    AbsenceSource = AbsenceSource.ClientLink
                };
                break;
            default:
                return Task.FromResult(false);
        }

        var key = (Convert.ToHexString(tokenHash), action);
        _transitions[key] = _transitions.GetValueOrDefault(key) + 1;
        return Task.FromResult(true);
    }

    public Task<bool> LockOwnerBookingAsync(int businessId, int bookingId, CancellationToken ct = default)
    {
        var found = _rows.Any(row => row.BusinessId == businessId && row.BookingId == bookingId);
        if (found)
            AfterOwnerBookingLock?.Invoke();
        return Task.FromResult(found);
    }

    public async Task<IReadOnlyList<OwnerPrivateAppointmentLinkRecord>> GetActiveOwnerLinksAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default)
    {
        IReadOnlyList<OwnerPrivateAppointmentLinkRecord> links = _rows
            .Where(row => row.BusinessId == businessId && row.BookingId == bookingId && row.Link.IsUsable(now))
            .Select(row => new OwnerPrivateAppointmentLinkRecord(
                row.Link.Id,
                row.Link.TokenHash.ToArray(),
                row.Link.ProtectedToken,
                row.Link.ExpiresAt))
            .ToArray();
        var barrier = Volatile.Read(ref _ownerReadBarrier);
        if (barrier is not null)
        {
            if (Interlocked.Increment(ref _ownerReadCount) == 2)
            {
                barrier.TrySetResult();
                Interlocked.CompareExchange(ref _ownerReadBarrier, null, barrier);
            }
            await barrier.Task.WaitAsync(ct);
        }
        return links;
    }

    public Task<bool> RevokeOwnerLinkAsync(
        int businessId, int bookingId, int linkId, DateTime now, CancellationToken ct = default)
    {
        var row = _rows.SingleOrDefault(candidate => candidate.BusinessId == businessId &&
            candidate.BookingId == bookingId && candidate.Link.Id == linkId && candidate.Link.IsUsable(now));
        if (row is null)
            return Task.FromResult(false);
        row.Link.RevokedAt = now;
        RevokeCount++;
        AfterOwnerLinkRevoke?.Invoke();
        return Task.FromResult(true);
    }

    public Task<int> InsertOwnerLinkAsync(int businessId, AppointmentLink link, CancellationToken ct = default)
    {
        OwnerLinkInsertAttempts++;
        if (!_rows.Any(row => row.BusinessId == businessId && row.BookingId == link.BookingId) ||
            _rows.Any(row => row.BusinessId == businessId && row.BookingId == link.BookingId && row.Link.RevokedAt is null && row.Link.ExpiresAt > link.CreatedAt))
            return Task.FromResult(0);

        var source = _rows.First(row => row.BusinessId == businessId && row.BookingId == link.BookingId);
        link.Id = _nextLinkId++;
        _rows.Add(new LinkRow(businessId, link.BookingId, link, source.Appointment with { ExpiresAt = link.ExpiresAt }));
        return Task.FromResult(link.Id);
    }

    public PrivateAppointmentLinkRecord GetBooking(string token) => Find(SecureToken.Hash(token))!.Appointment;

    public void SetBookingStatus(
        string token,
        BookingStatus status,
        DateTime? confirmedAt = null,
        DateTime? cancelledAt = null,
        DateTime? attendanceRecordedAt = null,
        AbsenceSource? absenceSource = null)
    {
        var row = Find(SecureToken.Hash(token))!;
        row.Appointment = row.Appointment with
        {
            BookingStatus = status,
            ConfirmedAt = confirmedAt,
            CancelledAt = cancelledAt,
            AttendanceRecordedAt = attendanceRecordedAt,
            AbsenceSource = absenceSource,
            CancelledBy = status == BookingStatus.Cancelled ? CancelledBy.Owner : null
        };
    }

    public void ResetBooking(string token)
    {
        var row = Find(SecureToken.Hash(token))!;
        row.Appointment = row.Appointment with
        {
            BookingStatus = BookingStatus.Pending,
            ConfirmedAt = null,
            CancelledAt = null,
            AttendanceRecordedAt = null,
            AbsenceSource = null,
            CancelledBy = null
        };
    }

    public bool IsRevoked(string token) => Find(SecureToken.Hash(token))!.Link.RevokedAt is not null;

    public int PaymentHistoryCount(string token) => Find(SecureToken.Hash(token))!.PaymentHistoryCount;

    public IReadOnlyList<AppointmentLink> ActiveLinks(int businessId, int bookingId) => _rows
        .Where(row => row.BusinessId == businessId && row.BookingId == bookingId && row.Link.RevokedAt is null)
        .Select(row => row.Link)
        .ToArray();

    public int TransitionCount(string token, PrivateAppointmentLinkAction action) =>
        _transitions.GetValueOrDefault((Convert.ToHexString(SecureToken.Hash(token)), action));

    private LinkRow? Find(byte[] tokenHash) => _rows.SingleOrDefault(row => row.Link.TokenHash.SequenceEqual(tokenHash));

    private static bool IsPendingOrConfirmed(BookingStatus status) =>
        status is BookingStatus.Pending or BookingStatus.Confirmed;

    private static bool HasConsistentActionState(PrivateAppointmentLinkRecord booking) =>
        booking.CancelledBy is null && booking.AttendanceRecordedAt is null && booking.AbsenceSource is null &&
        booking.CancelledAt is null &&
        (booking.BookingStatus == BookingStatus.Pending
            ? booking.ConfirmedAt is null
            : booking.BookingStatus == BookingStatus.Confirmed && booking.ConfirmedAt is not null);

    private sealed class LinkRow(
        int businessId,
        int bookingId,
        AppointmentLink link,
        PrivateAppointmentLinkRecord appointment)
    {
        public int BusinessId { get; } = businessId;
        public int BookingId { get; } = bookingId;
        public AppointmentLink Link { get; } = link;
        public PrivateAppointmentLinkRecord Appointment { get; set; } = appointment;
        public int PaymentHistoryCount { get; } = 1;
    }
}
