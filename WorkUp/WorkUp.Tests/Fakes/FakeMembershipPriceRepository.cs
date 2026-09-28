using WorkUp.BusinessLogic.Catalog;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeMembershipPriceRepository : IMembershipPriceRepository
{
    private readonly List<MembershipPrice> _prices = [];
    private int _nextId = 1;

    public IReadOnlyList<MembershipPrice> All => _prices;

    /// <summary>
    /// Deterministic model of losing the check-then-insert window (T27): while set, the frequency read is blind — it
    /// returns <c>null</c> even when a row is stored, exactly the snapshot the losing request observes just before the
    /// winner commits — and the insert reports <see cref="MembershipPriceInsertResult.AlreadyPresent"/> without
    /// writing. The reported lost insert clears the flag, so the service's re-read then observes the row the winner
    /// committed; with no stored row the re-read is missing and the set must fail safely.
    /// </summary>
    public bool LoseFrequencyRaceOnInsert { get; set; }

    /// <summary>
    /// When set, the insert throws this exception instead of writing, to prove the service never swallows an
    /// unexpected storage failure.
    /// </summary>
    public Exception? InsertException { get; set; }

    public Task<List<MembershipPrice>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_prices.Where(p => p.BusinessId == businessId).OrderBy(p => p.WeeklyFrequency).ToList());

    public Task<MembershipPrice?> GetByFrequencyAsync(int businessId, int weeklyFrequency, CancellationToken ct = default) =>
        Task.FromResult(LoseFrequencyRaceOnInsert
            ? null
            : _prices.SingleOrDefault(p => p.BusinessId == businessId && p.WeeklyFrequency == weeklyFrequency));

    public Task<MembershipPriceInsertResult> InsertAsync(MembershipPrice price, CancellationToken ct = default)
    {
        if (InsertException is not null)
            throw InsertException;

        if (LoseFrequencyRaceOnInsert)
        {
            // A competing set committed the frequency first: the loser meets UQ_MembershipPrices_Frequency, writes
            // nothing, and the flag clears so the service's re-read sees the committed row.
            LoseFrequencyRaceOnInsert = false;
            return Task.FromResult(MembershipPriceInsertResult.AlreadyPresent());
        }

        price.Id = _nextId++;
        _prices.Add(price);
        return Task.FromResult(MembershipPriceInsertResult.Inserted(price.Id));
    }

    public Task UpdateAsync(MembershipPrice price, CancellationToken ct = default)
    {
        var index = _prices.FindIndex(p => p.Id == price.Id);
        if (index < 0)
            throw new InvalidOperationException($"MembershipPrice {price.Id} was not inserted.");
        _prices[index] = price;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(int businessId, int weeklyFrequency, CancellationToken ct = default)
    {
        _prices.RemoveAll(p => p.BusinessId == businessId && p.WeeklyFrequency == weeklyFrequency);
        return Task.CompletedTask;
    }
}
