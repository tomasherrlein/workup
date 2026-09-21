using Microsoft.Data.SqlClient;

namespace WorkUp.Data;

/// <summary>
/// Base for ADO.NET repositories. Every command runs on the request connection and joins the current transaction.
/// Always pass values as parameters; never concatenate them into SQL.
/// </summary>
public abstract class SqlRepositoryBase(DbSession session)
{
    protected async Task<List<T>> QueryAsync<T>(
        string sql,
        Func<SqlDataReader, T> map,
        Action<SqlParameterCollection>? parameters = null,
        CancellationToken ct = default)
    {
        await using var command = await CreateCommandAsync(sql, parameters, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var items = new List<T>();
        while (await reader.ReadAsync(ct))
            items.Add(map(reader));
        return items;
    }

    protected async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        Func<SqlDataReader, T> map,
        Action<SqlParameterCollection>? parameters = null,
        CancellationToken ct = default) where T : class
    {
        await using var command = await CreateCommandAsync(sql, parameters, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? map(reader) : null;
    }

    protected async Task<int> ExecuteAsync(string sql, Action<SqlParameterCollection>? parameters = null, CancellationToken ct = default)
    {
        await using var command = await CreateCommandAsync(sql, parameters, ct);
        return await command.ExecuteNonQueryAsync(ct);
    }

    protected async Task<T?> ScalarAsync<T>(string sql, Action<SqlParameterCollection>? parameters = null, CancellationToken ct = default)
    {
        await using var command = await CreateCommandAsync(sql, parameters, ct);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    /// <summary>Runs an INSERT that ends with <c>OUTPUT INSERTED.Id</c> and returns the new identity.</summary>
    protected async Task<int> InsertAsync(string sql, Action<SqlParameterCollection> parameters, CancellationToken ct = default)
    {
        await using var command = await CreateCommandAsync(sql, parameters, ct);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<SqlCommand> CreateCommandAsync(string sql, Action<SqlParameterCollection>? parameters, CancellationToken ct)
    {
        var connection = await session.GetOpenConnectionAsync(ct);
        var command = new SqlCommand(sql, connection, session.Transaction);
        parameters?.Invoke(command.Parameters);
        return command;
    }
}
