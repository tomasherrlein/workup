using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Supplies;

/// <summary>Declared supply stock persistence, always scoped to a business.</summary>
public interface ISupplyRepository
{
    Task<List<Supply>> ListByBusinessAsync(int businessId, CancellationToken ct = default);
    Task<Supply?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default);
    Task<int> InsertAsync(Supply supply, CancellationToken ct = default);
    Task UpdateAsync(Supply supply, CancellationToken ct = default);
    Task<int> CountBelowMinimumAsync(int businessId, CancellationToken ct = default);
}
