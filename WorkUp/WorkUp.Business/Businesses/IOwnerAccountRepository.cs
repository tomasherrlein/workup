using WorkUp.Domain.Entities;

namespace WorkUp.Business.Businesses;

public interface IOwnerAccountRepository
{
    Task<OwnerAccount?> GetByUsernameAsync(string username, CancellationToken ct = default);

    Task<OwnerAccount?> GetByBusinessIdAsync(int businessId, CancellationToken ct = default);

    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);

    Task<int> InsertAsync(OwnerAccount account, CancellationToken ct = default);
}
