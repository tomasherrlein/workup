namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Safe owner projection for one recent individual booking and its current-link state.</summary>
public sealed record OwnerPrivateAppointmentSummary(
    int BookingId,
    int AppointmentId,
    DateTime StartsAt,
    string ServiceName,
    string WorkerName,
    int ActiveLinkCount,
    DateTime? ActiveLinkExpiresAt);

/// <summary>Tenant-scoped query for recent individual bookings; it never returns client or token data.</summary>
public interface IOwnerPrivateAppointmentRepository
{
    Task<IReadOnlyList<OwnerPrivateAppointmentSummary>> ListRecentIndividualBookingsAsync(
        int businessId, DateTime now, CancellationToken ct = default);
}
