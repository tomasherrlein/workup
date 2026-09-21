using WorkUp.Domain.Entities;

namespace WorkUp.Business.Businesses;

public interface IExpenseTypeRepository
{
    /// <summary>All expense types of the business, active and archived, ordered by name.</summary>
    Task<List<ExpenseType>> ListByBusinessAsync(int businessId, CancellationToken ct = default);

    Task<ExpenseType?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>Name uniqueness check scoped to the business. Pass the type's own id when renaming it.</summary>
    Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default);

    Task<int> InsertAsync(ExpenseType expenseType, CancellationToken ct = default);

    Task UpdateAsync(ExpenseType expenseType, CancellationToken ct = default);
}
