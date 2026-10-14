using WorkUp.Domain.Entities;
using WorkUp.BusinessLogic.Businesses;
// See IBusinessRepository.cs for why the entity is aliased instead of imported by its bare name.

namespace WorkUp.Tests.Fakes;

public sealed class FakeBusinessRepository : IBusinessRepository
{
    private readonly List<Business> _businesses = [];
    private int _nextId = 1;

    public IReadOnlyList<Business> All => _businesses;

    /// <summary>
    /// Deterministic model of losing the check-then-insert window: while set, the slug precheck reports the slug free
    /// and the insert meets the unique key because a competing request committed after the check, so it reports
    /// <see cref="BusinessInsertResult.SlugTaken"/> and writes nothing.
    /// </summary>
    public bool LoseSlugRaceOnInsert { get; set; }

    /// <summary>
    /// Same lost window on the edit: while set, <see cref="SlugExistsAsync"/> reports the new slug free and
    /// <see cref="UpdateAsync"/> reports <see cref="BusinessUpdateResult.SlugTaken"/> because a competing edit
    /// committed after the check.
    /// </summary>
    public bool LoseSlugRaceOnUpdate { get; set; }

    /// <summary>When set, <see cref="UpdateAsync"/> throws it unchanged, standing in for an unexpected storage failure.</summary>
    public Exception? UpdateFailure { get; set; }

    public Task<Business?> GetByIdAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(_businesses.SingleOrDefault(b => b.Id == id));

    public Task<Business?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        Task.FromResult(_businesses.SingleOrDefault(b => b.Slug == slug));

    public Task<bool> SlugExistsAsync(string slug, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(LoseSlugRaceOnInsert || LoseSlugRaceOnUpdate
            ? false
            : _businesses.Any(b => b.Slug == slug && b.Id != excludingId));

    public Task<BusinessInsertResult> InsertAsync(Business business, CancellationToken ct = default)
    {
        if (LoseSlugRaceOnInsert)
            return Task.FromResult(BusinessInsertResult.SlugTaken());

        business.Id = _nextId++;
        _businesses.Add(business);
        return Task.FromResult(BusinessInsertResult.Inserted(business.Id));
    }

    public Task<BusinessUpdateResult> UpdateAsync(Business business, CancellationToken ct = default)
    {
        if (UpdateFailure is not null)
            throw UpdateFailure;

        if (LoseSlugRaceOnUpdate)
            return Task.FromResult(BusinessUpdateResult.SlugTaken());

        var index = _businesses.FindIndex(b => b.Id == business.Id);
        if (index < 0)
            throw new InvalidOperationException($"Business {business.Id} was not inserted.");
        _businesses[index] = business;
        return Task.FromResult(BusinessUpdateResult.Updated());
    }
}
