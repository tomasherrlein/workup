using System.Data;
using WorkUp.BusinessLogic.Abstractions;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// No-op unit of work for service tests: every transaction "commits" without touching a real database. Counts the
/// begins, the commits and the rollbacks (a transaction disposed before commit counts as rolled back) so a test can
/// assert a service wrote everything inside a single transaction, or began one only to reject it.
/// </summary>
public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int BeginCount { get; private set; }

    public int CommitCount { get; private set; }

    public int RolledBackCount { get; private set; }

    public Task<ITransaction> BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default)
    {
        BeginCount++;
        return Task.FromResult<ITransaction>(new FakeTransaction(this));
    }

    private sealed class FakeTransaction(FakeUnitOfWork owner) : ITransaction
    {
        private bool _committed;

        public Task CommitAsync(CancellationToken ct = default)
        {
            _committed = true;
            owner.CommitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
                owner.RolledBackCount++;

            return ValueTask.CompletedTask;
        }
    }
}
