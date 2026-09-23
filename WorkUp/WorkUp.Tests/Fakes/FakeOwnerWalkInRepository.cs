using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Tests.Fakes;

public sealed class FakeOwnerWalkInRepository : IOwnerWalkInRepository
{
    public OwnerWalkInContext? Context { get; set; } = new(true, true, true, true, 30, 1, 100m);
    public bool Overlap { get; set; }
    public bool FailOnInsert { get; set; }
    public OwnerWalkInEntry? Inserted { get; private set; }

    public Task<OwnerWalkInContext?> LockContextAsync(int businessId, int clientId, int serviceId, int workerId,
        CancellationToken ct = default) => Task.FromResult(businessId == 7 && clientId == 20 && serviceId == 30 && workerId == 40 ? Context : null);

    public Task<bool> HasOverlapAsync(int businessId, int workerId, DateTime start, DateTime end,
        CancellationToken ct = default) => Task.FromResult(Overlap);

    public Task<(int AppointmentId, int BookingId)> InsertAsync(OwnerWalkInEntry entry, CancellationToken ct = default)
    {
        if (FailOnInsert) throw new InvalidOperationException("Insert failed");
        Inserted = entry;
        return Task.FromResult((51, 52));
    }
}
