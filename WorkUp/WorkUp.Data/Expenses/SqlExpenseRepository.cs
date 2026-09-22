using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Expenses;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Expenses;

/// <summary>ADO.NET expense history. The expense type join is tenant-bound to preserve BusinessId isolation.</summary>
public sealed class SqlExpenseRepository(DbSession session) : SqlRepositoryBase(session), IExpenseRepository
{
    private const string Columns = "e.Id, e.BusinessId, e.ExpenseTypeId, e.SpentOn, e.Amount, e.Description, e.CreatedAt";

    public Task<List<ExpenseListItem>> ListByBusinessAsync(int businessId, DateOnly? from, DateOnly? to, CancellationToken ct = default) =>
        QueryAsync(
            $"""
            SELECT {Columns}, et.Name AS ExpenseTypeName, et.IsActive AS ExpenseTypeIsActive
              FROM Expenses e
              JOIN ExpenseTypes et ON et.Id = e.ExpenseTypeId AND et.BusinessId = e.BusinessId
             WHERE e.BusinessId = @businessId
               AND (@from IS NULL OR e.SpentOn >= @from)
               AND (@to IS NULL OR e.SpentOn <= @to)
             ORDER BY e.SpentOn DESC, e.Id DESC
            """,
            reader => new ExpenseListItem(new Expense
            {
                Id = reader.Get<int>("Id"),
                BusinessId = reader.Get<int>("BusinessId"),
                ExpenseTypeId = reader.Get<int>("ExpenseTypeId"),
                SpentOn = reader.Get<DateOnly>("SpentOn"),
                Amount = reader.Get<decimal>("Amount"),
                Description = reader.Get<string>("Description"),
                CreatedAt = reader.Get<DateTime>("CreatedAt")
            }, reader.Get<string>("ExpenseTypeName"), reader.Get<bool>("ExpenseTypeIsActive")),
            p => p.AddValue("@businessId", businessId).AddValue("@from", from).AddValue("@to", to),
            ct);

    public Task<int> InsertAsync(Expense expense, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO Expenses (BusinessId, ExpenseTypeId, SpentOn, Amount, Description, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @expenseTypeId, @spentOn, @amount, @description, @createdAt)
            """,
            p => p.AddValue("@businessId", expense.BusinessId).AddValue("@expenseTypeId", expense.ExpenseTypeId)
                .AddValue("@spentOn", expense.SpentOn).AddValue("@amount", expense.Amount).AddValue("@description", expense.Description)
                .AddValue("@createdAt", expense.CreatedAt),
            ct);
}
