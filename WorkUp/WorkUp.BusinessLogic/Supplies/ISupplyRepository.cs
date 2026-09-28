using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Supplies;

/// <summary>
/// Outcome of inserting a supply. The name is unique per business (RF-14), so a create that passed the sequential
/// precheck can still lose the insert to a competing create that committed in between. That lost race is a business
/// outcome, not a 500: the repository reports <see cref="SupplyInsertResultType.NameTaken"/> instead of letting the
/// unique-key violation escape as an unhandled <c>SqlException</c>.
/// </summary>
public enum SupplyInsertResultType
{
    Inserted = 1,
    NameTaken = 2
}

/// <summary>Result of the guarded supply insert. <see cref="SupplyId"/> is only meaningful when it inserted.</summary>
public sealed record SupplyInsertResult(SupplyInsertResultType Result, int SupplyId)
{
    public static SupplyInsertResult Inserted(int supplyId) => new(SupplyInsertResultType.Inserted, supplyId);

    /// <summary>No row was written: the unique name was taken by a competing create that committed after the precheck.</summary>
    public static SupplyInsertResult NameTaken() => new(SupplyInsertResultType.NameTaken, 0);

    public bool IsInserted => Result == SupplyInsertResultType.Inserted;
}

/// <summary>
/// Outcome of updating a supply. Renaming can lose the same check-then-write window; the repository reports
/// <see cref="SupplyUpdateResultType.NameTaken"/> so the caller maps it to the existing <c>supply_name_taken</c> field
/// error without inventing a different type or a duplicate row.
/// </summary>
public enum SupplyUpdateResultType
{
    Updated = 1,
    NameTaken = 2
}

/// <summary>Result of the guarded supply update.</summary>
public sealed record SupplyUpdateResult(SupplyUpdateResultType Result)
{
    public static SupplyUpdateResult Updated() => new(SupplyUpdateResultType.Updated);

    public static SupplyUpdateResult NameTaken() => new(SupplyUpdateResultType.NameTaken);

    public bool IsUpdated => Result == SupplyUpdateResultType.Updated;
}

/// <summary>Declared supply stock persistence, always scoped to a business.</summary>
public interface ISupplyRepository
{
    Task<List<Supply>> ListByBusinessAsync(int businessId, CancellationToken ct = default);
    Task<Supply?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default);

    /// <summary>
    /// Inserts the supply and returns the new id, or <see cref="SupplyInsertResult.NameTaken"/> when the per-business
    /// <c>UQ_Supplies_Name</c> unique constraint rejects the row because a concurrent create took the name first. Only
    /// that constraint is reported as a business outcome; every other storage failure — including a foreign key
    /// violation — propagates.
    /// </summary>
    Task<SupplyInsertResult> InsertAsync(Supply supply, CancellationToken ct = default);

    /// <summary>
    /// Updates the declared stock and name, or returns <see cref="SupplyUpdateResult.NameTaken"/> when
    /// <c>UQ_Supplies_Name</c> rejects the new name. The statement is atomic and scoped to the owning business: a
    /// rejected update leaves the stored row unchanged. Only that constraint is reported as a business outcome; every
    /// other storage failure propagates.
    /// </summary>
    Task<SupplyUpdateResult> UpdateAsync(Supply supply, CancellationToken ct = default);
    Task<int> CountBelowMinimumAsync(int businessId, CancellationToken ct = default);
}
