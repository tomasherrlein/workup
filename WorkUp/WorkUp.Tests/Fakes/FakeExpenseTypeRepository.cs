using WorkUp.BusinessLogic.Businesses;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeExpenseTypeRepository : IExpenseTypeRepository
{
    private readonly List<ExpenseType> _expenseTypes = [];
    private int _nextId = 1;

    public IReadOnlyList<ExpenseType> All => _expenseTypes;

    public Task<List<ExpenseType>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_expenseTypes.Where(e => e.BusinessId == businessId).OrderBy(e => e.Name, StringComparer.Ordinal).ToList());

    public Task<ExpenseType?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_expenseTypes.SingleOrDefault(e => e.BusinessId == businessId && e.Id == id));

    public Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(_expenseTypes.Any(e => e.BusinessId == businessId && e.Name == name && e.Id != excludingId));

    public Task<int> InsertAsync(ExpenseType expenseType, CancellationToken ct = default)
    {
        expenseType.Id = _nextId++;
        _expenseTypes.Add(expenseType);
        return Task.FromResult(expenseType.Id);
    }

    public Task UpdateAsync(ExpenseType expenseType, CancellationToken ct = default)
    {
        var index = _expenseTypes.FindIndex(e => e.Id == expenseType.Id);
        if (index < 0)
            throw new InvalidOperationException($"ExpenseType {expenseType.Id} was not inserted.");
        _expenseTypes[index] = expenseType;
        return Task.CompletedTask;
    }
}
