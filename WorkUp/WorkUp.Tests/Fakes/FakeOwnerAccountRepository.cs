using WorkUp.Business.Businesses;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeOwnerAccountRepository : IOwnerAccountRepository
{
    private readonly List<OwnerAccount> _accounts = [];
    private int _nextId = 1;

    public IReadOnlyList<OwnerAccount> All => _accounts;

    public Task<OwnerAccount?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        Task.FromResult(_accounts.SingleOrDefault(a => a.Username == username));

    public Task<OwnerAccount?> GetByBusinessIdAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_accounts.SingleOrDefault(a => a.BusinessId == businessId));

    public Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default) =>
        Task.FromResult(_accounts.Any(a => a.Username == username));

    public Task<int> InsertAsync(OwnerAccount account, CancellationToken ct = default)
    {
        account.Id = _nextId++;
        _accounts.Add(account);
        return Task.FromResult(account.Id);
    }
}
