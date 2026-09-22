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

    public Task<int> InsertAsync(Supply supply, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO Supplies (BusinessId, Name, CurrentStock, MinimumStock, LastDeclaredAt, IsActive)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @name, @currentStock, @minimumStock, @lastDeclaredAt, @isActive)
            """,
            p => p.AddValue("@businessId", supply.BusinessId).AddValue("@name", supply.Name).AddValue("@currentStock", supply.CurrentStock)
                .AddValue("@minimumStock", supply.MinimumStock).AddValue("@lastDeclaredAt", supply.LastDeclaredAt).AddValue("@isActive", supply.IsActive), ct);

    public Task UpdateAsync(Supply supply, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            UPDATE Supplies
               SET Name = @name, CurrentStock = @currentStock, MinimumStock = @minimumStock, LastDeclaredAt = @lastDeclaredAt
             WHERE Id = @id AND BusinessId = @businessId
            """,
            p => p.AddValue("@id", supply.Id).AddValue("@businessId", supply.BusinessId).AddValue("@name", supply.Name)
                .AddValue("@currentStock", supply.CurrentStock).AddValue("@minimumStock", supply.MinimumStock).AddValue("@lastDeclaredAt", supply.LastDeclaredAt), ct);

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
