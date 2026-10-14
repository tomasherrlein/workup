using WorkUp.BusinessLogic.Businesses;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeExpenseTypeRepository : IExpenseTypeRepository
{
    private readonly List<ExpenseType> _expenseTypes = [];
    private int _nextId = 1;

    public IReadOnlyList<ExpenseType> All => _expenseTypes;

    /// <summary>
    /// Deterministic model of losing the check-then-insert window: while set, the name precheck reports the name free
    /// and the insert meets the unique key because a competing create committed after the check, so it reports
    /// <see cref="ExpenseTypeInsertResult.NameTaken"/> and writes nothing.
    /// </summary>
    public bool LoseNameRaceOnInsert { get; set; }

    /// <summary>
    /// Same lost window on the rename: while set, <see cref="NameExistsAsync"/> reports the new name free and
    /// <see cref="UpdateAsync"/> reports <see cref="ExpenseTypeUpdateResult.NameTaken"/> because a competing rename
    /// committed after the check.
    /// </summary>
    public bool LoseNameRaceOnUpdate { get; set; }

    public Task<List<ExpenseType>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_expenseTypes.Where(e => e.BusinessId == businessId).OrderBy(e => e.Name, StringComparer.Ordinal).ToList());

    public Task<ExpenseType?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_expenseTypes.SingleOrDefault(e => e.BusinessId == businessId && e.Id == id));

    public Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(!LoseNameRaceOnInsert && !LoseNameRaceOnUpdate
            && _expenseTypes.Any(e => e.BusinessId == businessId && e.Name == name && e.Id != excludingId));

    public Task<ExpenseTypeInsertResult> InsertAsync(ExpenseType expenseType, CancellationToken ct = default)
    {
        if (LoseNameRaceOnInsert)
            return Task.FromResult(ExpenseTypeInsertResult.NameTaken());

        expenseType.Id = _nextId++;
        _expenseTypes.Add(expenseType);
        return Task.FromResult(ExpenseTypeInsertResult.Inserted(expenseType.Id));
    }

    public Task<ExpenseTypeUpdateResult> UpdateAsync(ExpenseType expenseType, CancellationToken ct = default)
    {
        if (LoseNameRaceOnUpdate)
            return Task.FromResult(ExpenseTypeUpdateResult.NameTaken());

        var index = _expenseTypes.FindIndex(e => e.Id == expenseType.Id);
        if (index < 0)
            throw new InvalidOperationException($"ExpenseType {expenseType.Id} was not inserted.");
        _expenseTypes[index] = expenseType;
        return Task.FromResult(ExpenseTypeUpdateResult.Updated());
    }
}
