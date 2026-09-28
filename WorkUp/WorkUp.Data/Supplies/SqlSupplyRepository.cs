using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Supplies;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Supplies;

/// <summary>ADO.NET repository for owner-declared supply stock, isolated by BusinessId on every access.</summary>
public sealed class SqlSupplyRepository(DbSession session) : SqlRepositoryBase(session), ISupplyRepository
{
    private const string Columns = "Id, BusinessId, Name, CurrentStock, MinimumStock, LastDeclaredAt, IsActive";

    public Task<List<Supply>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        QueryAsync($"SELECT {Columns} FROM Supplies WHERE BusinessId = @businessId ORDER BY Name", Map,
            p => p.AddValue("@businessId", businessId), ct);

    public Task<Supply?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync($"SELECT {Columns} FROM Supplies WHERE BusinessId = @businessId AND Id = @id", Map,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id), ct);

    public async Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM Supplies WHERE BusinessId = @businessId AND Name = @name AND (@excludingId IS NULL OR Id <> @excludingId)",
            p => p.AddValue("@businessId", businessId).AddValue("@name", name).AddValue("@excludingId", excludingId), ct) > 0;

    public async Task<SupplyInsertResult> InsertAsync(Supply supply, CancellationToken ct = default)
    {
        try
        {
            var id = await InsertAsync(
                """
                INSERT INTO Supplies (BusinessId, Name, CurrentStock, MinimumStock, LastDeclaredAt, IsActive)
                OUTPUT INSERTED.Id
                VALUES (@businessId, @name, @currentStock, @minimumStock, @lastDeclaredAt, @isActive)
                """,
                p => p.AddValue("@businessId", supply.BusinessId).AddValue("@name", supply.Name).AddValue("@currentStock", supply.CurrentStock)
                    .AddValue("@minimumStock", supply.MinimumStock).AddValue("@lastDeclaredAt", supply.LastDeclaredAt).AddValue("@isActive", supply.IsActive), ct);
            return SupplyInsertResult.Inserted(id);
        }
        catch (SqlException exception) when (IsNameDuplicate(exception))
        {
            // The per-business unique name is the last race guard: losing the check-then-insert window is an expected
            // business outcome, never an unhandled 500 and never a second supply.
            return SupplyInsertResult.NameTaken();
        }
    }

    public async Task<SupplyUpdateResult> UpdateAsync(Supply supply, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync(
                """
                UPDATE Supplies
                   SET Name = @name, CurrentStock = @currentStock, MinimumStock = @minimumStock, LastDeclaredAt = @lastDeclaredAt
                 WHERE Id = @id AND BusinessId = @businessId
                """,
                p => p.AddValue("@id", supply.Id).AddValue("@businessId", supply.BusinessId).AddValue("@name", supply.Name)
                    .AddValue("@currentStock", supply.CurrentStock).AddValue("@minimumStock", supply.MinimumStock).AddValue("@lastDeclaredAt", supply.LastDeclaredAt), ct);
            return SupplyUpdateResult.Updated();
        }
        catch (SqlException exception) when (IsNameDuplicate(exception))
        {
            // The single statement is atomic: a rejected rename leaves the stored row unchanged.
            return SupplyUpdateResult.NameTaken();
        }
    }

    /// <summary>
    /// Only <c>UQ_Supplies_Name</c> is a benign lost race. Any other unique violation — or any other
    /// <see cref="SqlException"/> — propagates instead of being swallowed as a name conflict.
    /// </summary>
    private static bool IsNameDuplicate(SqlException exception) =>
        exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is 2601 or 2627
                && error.Message.Contains("UQ_Supplies_Name", StringComparison.Ordinal));

    public async Task<int> CountBelowMinimumAsync(int businessId, CancellationToken ct = default) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM Supplies WHERE BusinessId = @businessId AND IsActive = 1 AND CurrentStock < MinimumStock",
            p => p.AddValue("@businessId", businessId), ct);

    private static Supply Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        Name = reader.Get<string>("Name"),
        CurrentStock = reader.Get<decimal>("CurrentStock"),
        MinimumStock = reader.Get<decimal>("MinimumStock"),
        LastDeclaredAt = reader.Get<DateTime>("LastDeclaredAt"),
        IsActive = reader.Get<bool>("IsActive")
    };
}
