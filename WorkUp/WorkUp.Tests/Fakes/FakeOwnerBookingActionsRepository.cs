using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

public sealed class FakeOwnerBookingActionsRepository : IOwnerBookingActionsRepository
{
    public int BusinessId { get; set; } = 7;
    public OwnerBookingCancellationState? Booking { get; set; }
    public bool AllowCancellation { get; set; } = true;
    public List<(int BusinessId, int BookingId)> LockRequests { get; } = [];
    public List<(int BusinessId, int BookingId, DateTime Now)> CancellationRequests { get; } = [];

    public Task<OwnerBookingCancellationState?> LockOwnerBookingForCancellationAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        LockRequests.Add((businessId, bookingId));
        return Task.FromResult(businessId == BusinessId && Booking?.BookingId == bookingId ? Booking : null);
    }

    public Task<bool> CancelOwnerBookingAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default)
    {
        CancellationRequests.Add((businessId, bookingId, now));
        if (!AllowCancellation || businessId != BusinessId || Booking?.BookingId != bookingId)
            return Task.FromResult(false);

        Booking = Booking with
        {
            BookingStatus = BookingStatus.Cancelled,
            CancelledBy = CancelledBy.Owner,
            CancelledAt = now
        };
        return Task.FromResult(true);
    }
}
