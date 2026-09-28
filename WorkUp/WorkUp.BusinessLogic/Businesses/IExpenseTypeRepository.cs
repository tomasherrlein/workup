using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Businesses;

/// <summary>
/// Outcome of inserting an expense type. The name is unique per business (RF-01, RF-13), so a create that passed the
/// sequential precheck can still lose the insert to a competing create that committed in between. That lost race is a
/// business outcome, not a 500: the repository reports <see cref="ExpenseTypeInsertResultType.NameTaken"/> instead of
/// letting the unique-key violation escape as an unhandled <c>SqlException</c>.
/// </summary>
public enum ExpenseTypeInsertResultType
{
    Inserted = 1,
    NameTaken = 2
}

/// <summary>Result of the guarded expense type insert. <see cref="ExpenseTypeId"/> is only meaningful when it inserted.</summary>
public sealed record ExpenseTypeInsertResult(ExpenseTypeInsertResultType Result, int ExpenseTypeId)
{
    public static ExpenseTypeInsertResult Inserted(int expenseTypeId) => new(ExpenseTypeInsertResultType.Inserted, expenseTypeId);

    /// <summary>No row was written: the unique name was taken by a competing create that committed after the precheck.</summary>
    public static ExpenseTypeInsertResult NameTaken() => new(ExpenseTypeInsertResultType.NameTaken, 0);

    public bool IsInserted => Result == ExpenseTypeInsertResultType.Inserted;
}

/// <summary>
/// Outcome of updating an expense type. Renaming can lose the same check-then-write window; the repository reports
/// <see cref="ExpenseTypeUpdateResultType.NameTaken"/> so the caller maps it to the existing <c>expense_type_name_taken</c>
/// field error without inventing a different type or a duplicate row.
/// </summary>
public enum ExpenseTypeUpdateResultType
{
    Updated = 1,
    NameTaken = 2
}

/// <summary>Result of the guarded expense type update.</summary>
public sealed record ExpenseTypeUpdateResult(ExpenseTypeUpdateResultType Result)
{
    public static ExpenseTypeUpdateResult Updated() => new(ExpenseTypeUpdateResultType.Updated);

    public static ExpenseTypeUpdateResult NameTaken() => new(ExpenseTypeUpdateResultType.NameTaken);

    public bool IsUpdated => Result == ExpenseTypeUpdateResultType.Updated;
}

public interface IExpenseTypeRepository
{
    /// <summary>All expense types of the business, active and archived, ordered by name.</summary>
    Task<List<ExpenseType>> ListByBusinessAsync(int businessId, CancellationToken ct = default);

    Task<ExpenseType?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>Name uniqueness check scoped to the business. Pass the type's own id when renaming it.</summary>
    Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default);

    /// <summary>
    /// Inserts the expense type and returns the new id, or <see cref="ExpenseTypeInsertResult.NameTaken"/> when the
    /// per-business <c>UQ_ExpenseTypes_Name</c> unique constraint rejects the row because a concurrent create took the
    /// name first. Only that constraint is reported as a business outcome; every other storage failure — including a
    /// foreign key violation — propagates.
    /// </summary>
    Task<ExpenseTypeInsertResult> InsertAsync(ExpenseType expenseType, CancellationToken ct = default);

    /// <summary>
    /// Updates Name and IsActive, or returns <see cref="ExpenseTypeUpdateResult.NameTaken"/> when
    /// <c>UQ_ExpenseTypes_Name</c> rejects the new name. The statement is atomic: a rejected update leaves the stored
    /// row unchanged. Only that constraint is reported as a business outcome; every other storage failure propagates.
    /// </summary>
    Task<ExpenseTypeUpdateResult> UpdateAsync(ExpenseType expenseType, CancellationToken ct = default);
}
