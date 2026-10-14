using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace WorkUp.Data;

/// <summary>
/// Creates the database when missing and applies the embedded scripts in <c>Sql/</c> in name order, once each.
/// Add a new numbered script for every schema change; never edit a script that was already applied.
/// </summary>
public sealed partial class DatabaseMigrator(string connectionString)
{
    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseExistsAsync(ct);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await EnsureVersionTableAsync(connection, ct);

        var applied = new List<string>();
        foreach (var script in EmbeddedScripts())
        {
            if (await IsAppliedAsync(connection, script.Name, ct))
                continue;

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            foreach (var batch in SplitBatches(script.Sql))
            {
                await using var command = new SqlCommand(batch, connection, transaction);
                await command.ExecuteNonQueryAsync(ct);
            }

            await using (var record = new SqlCommand(
                "INSERT INTO SchemaVersions (ScriptName, AppliedAt) VALUES (@name, SYSDATETIME())", connection, transaction))
            {
                record.Parameters.AddValue("@name", script.Name);
                await record.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            applied.Add(script.Name);
        }

        return applied;
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "IF DB_ID(@db) IS NULL BEGIN DECLARE @sql NVARCHAR(300) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC (@sql); END",
            connection);
        command.Parameters.AddValue("@db", database);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureVersionTableAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            IF OBJECT_ID(N'SchemaVersions', N'U') IS NULL
                CREATE TABLE SchemaVersions (
                    ScriptName NVARCHAR(200) NOT NULL PRIMARY KEY,
                    AppliedAt  DATETIME2(0)  NOT NULL
                );
            """;
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsAppliedAsync(SqlConnection connection, string name, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM SchemaVersions WHERE ScriptName = @name", connection);
        command.Parameters.AddValue("@name", name);
        return (int)(await command.ExecuteScalarAsync(ct))! > 0;
    }

    private static IEnumerable<(string Name, string Sql)> EmbeddedScripts()
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        const string prefix = "WorkUp.Data.Sql.";

        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[prefix.Length..], reader.ReadToEnd());
        }
    }

    private static IEnumerable<string> SplitBatches(string sql) =>
        GoSeparator().Split(sql).Select(b => b.Trim()).Where(b => b.Length > 0);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}
