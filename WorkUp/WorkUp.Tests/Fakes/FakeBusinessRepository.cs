using WorkUp.Domain.Entities;
using WorkUp.BusinessLogic.Businesses;
// See IBusinessRepository.cs for why the entity is aliased instead of imported by its bare name.

namespace WorkUp.Tests.Fakes;

public sealed class FakeBusinessRepository : IBusinessRepository
{
    private readonly List<Business> _businesses = [];
    private int _nextId = 1;

    public IReadOnlyList<Business> All => _businesses;

    public Task<Business?> GetByIdAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(_businesses.SingleOrDefault(b => b.Id == id));

    public Task<Business?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        Task.FromResult(_businesses.SingleOrDefault(b => b.Slug == slug));

    public Task<bool> SlugExistsAsync(string slug, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(_businesses.Any(b => b.Slug == slug && b.Id != excludingId));

    public Task<int> InsertAsync(Business business, CancellationToken ct = default)
    {
        business.Id = _nextId++;
        _businesses.Add(business);
        return Task.FromResult(business.Id);
    }

    public Task UpdateAsync(Business business, CancellationToken ct = default)
    {
        var index = _businesses.FindIndex(b => b.Id == business.Id);
        if (index < 0)
            throw new InvalidOperationException($"Business {business.Id} was not inserted.");
        _businesses[index] = business;
        return Task.CompletedTask;
    }
}
