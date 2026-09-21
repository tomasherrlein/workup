using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Businesses;

public sealed class SqlExpenseTypeRepository(DbSession session) : SqlRepositoryBase(session), IExpenseTypeRepository
{
    private const string Columns = "Id, BusinessId, Name, IsActive";

    public Task<List<ExpenseType>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        QueryAsync($"SELECT {Columns} FROM ExpenseTypes WHERE BusinessId = @businessId ORDER BY Name", Map, p => p.AddValue("@businessId", businessId), ct);

    public Task<ExpenseType?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            $"SELECT {Columns} FROM ExpenseTypes WHERE BusinessId = @businessId AND Id = @id",
            Map,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct);

    public async Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM ExpenseTypes
             WHERE BusinessId = @businessId AND Name = @name AND (@excludingId IS NULL OR Id <> @excludingId)
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@name", name).AddValue("@excludingId", excludingId),
            ct) > 0;

    public Task<int> InsertAsync(ExpenseType expenseType, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO ExpenseTypes (BusinessId, Name, IsActive)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @name, @isActive)
            """,
            p => p
                .AddValue("@businessId", expenseType.BusinessId)
                .AddValue("@name", expenseType.Name)
                .AddValue("@isActive", expenseType.IsActive),
            ct);

    public Task UpdateAsync(ExpenseType expenseType, CancellationToken ct = default) =>
        ExecuteAsync(
            "UPDATE ExpenseTypes SET Name = @name, IsActive = @isActive WHERE Id = @id",
            p => p
                .AddValue("@id", expenseType.Id)
                .AddValue("@name", expenseType.Name)
                .AddValue("@isActive", expenseType.IsActive),
            ct);

    private static ExpenseType Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        Name = reader.Get<string>("Name"),
        IsActive = reader.Get<bool>("IsActive")
    };
}
