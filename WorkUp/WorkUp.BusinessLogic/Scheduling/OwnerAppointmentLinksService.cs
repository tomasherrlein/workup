using WorkUp.BusinessLogic.Abstractions;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Lists recent individual bookings for the authenticated business owner.</summary>
public sealed class OwnerAppointmentLinksService(
    IOwnerPrivateAppointmentRepository repository,
    IClock clock)
{
    public Task<IReadOnlyList<OwnerPrivateAppointmentSummary>> ListRecentAsync(
        int businessId, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Task.FromResult<IReadOnlyList<OwnerPrivateAppointmentSummary>>([]);

        return repository.ListRecentIndividualBookingsAsync(businessId, clock.Now, ct);
    }
}
