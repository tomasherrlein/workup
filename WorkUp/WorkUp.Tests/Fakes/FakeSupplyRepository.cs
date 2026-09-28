using WorkUp.BusinessLogic.Supplies;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeSupplyRepository : ISupplyRepository
{
    private readonly List<Supply> _supplies = [];
    private int _nextId = 1;

    public IReadOnlyList<Supply> All => _supplies;

    /// <summary>
    /// Deterministic model of losing the check-then-insert window: while set, the name precheck reports the name free
    /// and the insert meets the unique key because a competing create committed after the check, so it reports
    /// <see cref="SupplyInsertResult.NameTaken"/> and writes nothing.
    /// </summary>
    public bool LoseNameRaceOnInsert { get; set; }

    /// <summary>
    /// Same lost window on the rename: while set, <see cref="NameExistsAsync"/> reports the new name free and
    /// <see cref="UpdateAsync"/> reports <see cref="SupplyUpdateResult.NameTaken"/> because a competing rename committed
    /// after the check.
    /// </summary>
    public bool LoseNameRaceOnUpdate { get; set; }

    public Task<List<Supply>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_supplies.Where(s => s.BusinessId == businessId).OrderBy(s => s.Name, StringComparer.Ordinal).ToList());

    public Task<Supply?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_supplies.SingleOrDefault(s => s.BusinessId == businessId && s.Id == id));

    public Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(!LoseNameRaceOnInsert && !LoseNameRaceOnUpdate
            && _supplies.Any(s => s.BusinessId == businessId && s.Name == name && s.Id != excludingId));

    public Task<SupplyInsertResult> InsertAsync(Supply supply, CancellationToken ct = default)
    {
        if (LoseNameRaceOnInsert)
            return Task.FromResult(SupplyInsertResult.NameTaken());

        supply.Id = _nextId++;
        _supplies.Add(supply);
        return Task.FromResult(SupplyInsertResult.Inserted(supply.Id));
    }

    public Task<SupplyUpdateResult> UpdateAsync(Supply supply, CancellationToken ct = default)
    {
        if (LoseNameRaceOnUpdate)
            return Task.FromResult(SupplyUpdateResult.NameTaken());

        var index = _supplies.FindIndex(s => s.Id == supply.Id && s.BusinessId == supply.BusinessId);
        if (index < 0)
            throw new InvalidOperationException($"Supply {supply.Id} was not inserted.");
        _supplies[index] = supply;
        return Task.FromResult(SupplyUpdateResult.Updated());
    }

    public Task<int> CountBelowMinimumAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_supplies.Count(s => s.BusinessId == businessId && s.IsActive && s.CurrentStock < s.MinimumStock));
}
