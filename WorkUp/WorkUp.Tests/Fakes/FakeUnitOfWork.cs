using System.Data;
using WorkUp.BusinessLogic.Abstractions;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// No-op unit of work for service tests: every transaction "commits" without touching a real database. Counts
/// commits so a test can assert a service wrote everything inside a single transaction.
/// </summary>
public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int CommitCount { get; private set; }

    public Task<ITransaction> BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default) =>
        Task.FromResult<ITransaction>(new FakeTransaction(this));

    private sealed class FakeTransaction(FakeUnitOfWork owner) : ITransaction
    {
        public Task CommitAsync(CancellationToken ct = default)
        {
            owner.CommitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
