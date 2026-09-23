namespace WorkUp.BusinessLogic.Scheduling;

public sealed record OwnerWalkInRequest(int ClientId, int ServiceId, int WorkerId,
    decimal? AgreedPrice = null, string? PriceReason = null, bool CollectPayment = false, string? NextVisitNote = null);

public sealed record OwnerWalkInOutcome(int AppointmentId, int BookingId, bool HasOverlap);

public sealed record OwnerWalkInContext(bool AllowsWalkIns, bool ClientExists, bool WorkerActive,
    bool ServiceActive, int DurationMinutes, int MaxCapacity, decimal ListPrice);

public sealed record OwnerWalkInEntry(int BusinessId, int ClientId, int ServiceId, int WorkerId,
    DateTime StartsAt, DateTime EndsAt, decimal AgreedPrice, decimal ListPrice,
    string? PriceReason, bool CollectPayment, string? NextVisitNote);

public interface IOwnerWalkInRepository
{
    Task<OwnerWalkInContext?> LockContextAsync(int businessId, int clientId, int serviceId, int workerId,
        CancellationToken ct = default);
    Task<bool> HasOverlapAsync(int businessId, int workerId, DateTime start, DateTime end,
        CancellationToken ct = default);
    Task<(int AppointmentId, int BookingId)> InsertAsync(OwnerWalkInEntry entry, CancellationToken ct = default);
}
