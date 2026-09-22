using WorkUp.BusinessLogic.Supplies;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeSupplyRepository : ISupplyRepository
{
    private readonly List<Supply> _supplies = [];
    private int _nextId = 1;

    public IReadOnlyList<Supply> All => _supplies;

    public Task<List<Supply>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_supplies.Where(s => s.BusinessId == businessId).OrderBy(s => s.Name, StringComparer.Ordinal).ToList());

    public Task<Supply?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_supplies.SingleOrDefault(s => s.BusinessId == businessId && s.Id == id));

    public Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(_supplies.Any(s => s.BusinessId == businessId && s.Name == name && s.Id != excludingId));

    public Task<int> InsertAsync(Supply supply, CancellationToken ct = default)
    {
        supply.Id = _nextId++;
        _supplies.Add(supply);
        return Task.FromResult(supply.Id);
    }

    public Task UpdateAsync(Supply supply, CancellationToken ct = default)
    {
        var index = _supplies.FindIndex(s => s.Id == supply.Id && s.BusinessId == supply.BusinessId);
        if (index < 0)
            throw new InvalidOperationException($"Supply {supply.Id} was not inserted.");
        _supplies[index] = supply;
        return Task.CompletedTask;
    }

    public Task<int> CountBelowMinimumAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_supplies.Count(s => s.BusinessId == businessId && s.IsActive && s.CurrentStock < s.MinimumStock));
}
