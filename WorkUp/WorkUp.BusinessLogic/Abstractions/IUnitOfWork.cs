using System.Data;

namespace WorkUp.BusinessLogic.Abstractions;

/// <summary>
/// Scoped per request. Repositories share its connection and current transaction, so a service can make several
/// repository calls atomic:
/// <code>
/// await using var tx = await _unitOfWork.BeginAsync();
/// ...repository calls...
/// await tx.CommitAsync();
/// </code>
/// Disposing without committing rolls back.
/// </summary>
public interface IUnitOfWork
{
    Task<ITransaction> BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default);
}

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}
