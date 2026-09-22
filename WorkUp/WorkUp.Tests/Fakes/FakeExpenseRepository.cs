using WorkUp.BusinessLogic.Expenses;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeExpenseRepository : IExpenseRepository
{
    private readonly List<ExpenseListItem> _expenses = [];
    private int _nextId = 1;

    public IReadOnlyList<ExpenseListItem> All => _expenses;

    public Task<List<ExpenseListItem>> ListByBusinessAsync(int businessId, DateOnly? from, DateOnly? to, CancellationToken ct = default) =>
        Task.FromResult(_expenses.Where(item => item.Expense.BusinessId == businessId
            && (!from.HasValue || item.Expense.SpentOn >= from.Value)
            && (!to.HasValue || item.Expense.SpentOn <= to.Value))
            .OrderByDescending(item => item.Expense.SpentOn).ThenByDescending(item => item.Expense.Id).ToList());

    public Task<int> InsertAsync(Expense expense, CancellationToken ct = default)
    {
        expense.Id = _nextId++;
        _expenses.Add(new ExpenseListItem(expense, $"Tipo {expense.ExpenseTypeId}", true));
        return Task.FromResult(expense.Id);
    }
}
