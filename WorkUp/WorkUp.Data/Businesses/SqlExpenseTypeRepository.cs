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

    public async Task<ExpenseTypeInsertResult> InsertAsync(ExpenseType expenseType, CancellationToken ct = default)
    {
        try
        {
            var id = await InsertAsync(
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
            return ExpenseTypeInsertResult.Inserted(id);
        }
        catch (SqlException exception) when (IsNameDuplicate(exception))
        {
            // The per-business unique name is the last race guard: losing the check-then-insert window is an expected
            // business outcome, never an unhandled 500 and never a second type.
            return ExpenseTypeInsertResult.NameTaken();
        }
    }

    public async Task<ExpenseTypeUpdateResult> UpdateAsync(ExpenseType expenseType, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync(
                "UPDATE ExpenseTypes SET Name = @name, IsActive = @isActive WHERE Id = @id",
                p => p
                    .AddValue("@id", expenseType.Id)
                    .AddValue("@name", expenseType.Name)
                    .AddValue("@isActive", expenseType.IsActive),
                ct);
            return ExpenseTypeUpdateResult.Updated();
        }
        catch (SqlException exception) when (IsNameDuplicate(exception))
        {
            // The single statement is atomic: a rejected rename leaves the stored row unchanged.
            return ExpenseTypeUpdateResult.NameTaken();
        }
    }

    /// <summary>
    /// Only <c>UQ_ExpenseTypes_Name</c> is a benign lost race. Any other unique violation — or any other
    /// <see cref="SqlException"/> — propagates instead of being swallowed as a name conflict.
    /// </summary>
    private static bool IsNameDuplicate(SqlException exception) =>
        exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is 2601 or 2627
                && error.Message.Contains("UQ_ExpenseTypes_Name", StringComparison.Ordinal));

    private static ExpenseType Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        Name = reader.Get<string>("Name"),
        IsActive = reader.Get<bool>("IsActive")
    };
}
