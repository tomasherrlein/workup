using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Expenses;

/// <summary>Owner expense persistence, always scoped to a business.</summary>
public interface IExpenseRepository
{
    Task<List<ExpenseListItem>> ListByBusinessAsync(int businessId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<int> InsertAsync(Expense expense, CancellationToken ct = default);
}

/// <summary>Expense history retains the type name even when its type is later archived.</summary>
public sealed record ExpenseListItem(Expense Expense, string ExpenseTypeName, bool ExpenseTypeIsActive);
