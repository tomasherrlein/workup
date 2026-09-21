using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Catalog;

public interface IMembershipPriceRepository
{
    /// <summary>All membership prices of the business, ordered by weekly frequency.</summary>
    Task<List<MembershipPrice>> ListByBusinessAsync(int businessId, CancellationToken ct = default);

    Task<MembershipPrice?> GetByFrequencyAsync(int businessId, int weeklyFrequency, CancellationToken ct = default);

    Task<int> InsertAsync(MembershipPrice price, CancellationToken ct = default);

    Task UpdateAsync(MembershipPrice price, CancellationToken ct = default);

    Task DeleteAsync(int businessId, int weeklyFrequency, CancellationToken ct = default);
}
