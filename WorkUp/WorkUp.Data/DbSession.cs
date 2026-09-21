using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Abstractions;

namespace WorkUp.Data;

/// <summary>
/// One SQL Server connection per request, opened on first use, plus the current transaction. Registered as scoped and
/// exposed to the business layer as <see cref="IUnitOfWork"/>. Repositories receive it through their constructor.
/// </summary>
public sealed class DbSession(string connectionString) : IUnitOfWork, IAsyncDisposable
{
    private SqlConnection? _connection;

    /// <summary>The transaction every repository command must join, or null outside a unit of work.</summary>
    public SqlTransaction? Transaction { get; private set; }

    public async Task<SqlConnection> GetOpenConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is null)
        {
            _connection = new SqlConnection(connectionString);
            await _connection.OpenAsync(ct);
        }

        return _connection;
    }

    /// <summary>
    /// Starts a transaction. When one is already running, the caller joins it: its commit is a no-op and only the
    /// outermost scope commits or rolls back. Services can therefore call each other inside one unit of work.
    /// </summary>
    public async Task<ITransaction> BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default)
    {
        if (Transaction is not null)
            return JoinedTransaction.Instance;

        var connection = await GetOpenConnectionAsync(ct);
        Transaction = (SqlTransaction)await connection.BeginTransactionAsync(isolationLevel, ct);
        return new OwnedTransaction(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Transaction is not null)
            await Transaction.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private sealed class OwnedTransaction(DbSession session) : ITransaction
    {
        private bool _completed;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await session.Transaction!.CommitAsync(ct);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            var transaction = session.Transaction!;
            if (!_completed)
                await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            session.Transaction = null;
        }
    }

    private sealed class JoinedTransaction : ITransaction
    {
        public static readonly JoinedTransaction Instance = new();

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
