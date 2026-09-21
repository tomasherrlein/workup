using WorkUp.BusinessLogic.Catalog;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeMembershipPriceRepository : IMembershipPriceRepository
{
    private readonly List<MembershipPrice> _prices = [];
    private int _nextId = 1;

    public IReadOnlyList<MembershipPrice> All => _prices;

    public Task<List<MembershipPrice>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_prices.Where(p => p.BusinessId == businessId).OrderBy(p => p.WeeklyFrequency).ToList());

    public Task<MembershipPrice?> GetByFrequencyAsync(int businessId, int weeklyFrequency, CancellationToken ct = default) =>
        Task.FromResult(_prices.SingleOrDefault(p => p.BusinessId == businessId && p.WeeklyFrequency == weeklyFrequency));

    public Task<int> InsertAsync(MembershipPrice price, CancellationToken ct = default)
    {
        price.Id = _nextId++;
        _prices.Add(price);
        return Task.FromResult(price.Id);
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
