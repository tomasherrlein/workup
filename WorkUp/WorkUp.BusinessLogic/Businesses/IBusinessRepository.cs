using WorkUp.Domain.Entities;
// The entity is aliased: this file lives under the WorkUp.BusinessLogic.* namespace tree, and the bare name "Business"
// resolves to that enclosing namespace (WorkUp.BusinessLogic) rather than the entity type (CS0118).

namespace WorkUp.BusinessLogic.Businesses;

/// <summary>
/// Outcome of inserting a business. The public slug is globally unique, so a request that passed the sequential
/// precheck can still lose the insert to a competing registration that committed in between. That lost race is a
/// business outcome, not a 500: the repository reports <see cref="BusinessInsertResultType.SlugTaken"/> instead of
/// letting the unique-key violation escape as an unhandled <c>SqlException</c>.
/// </summary>
public enum BusinessInsertResultType
{
    Inserted = 1,
    SlugTaken = 2
}

/// <summary>Result of the guarded business insert. <see cref="BusinessId"/> is only meaningful when it inserted.</summary>
public sealed record BusinessInsertResult(BusinessInsertResultType Result, int BusinessId)
{
    public static BusinessInsertResult Inserted(int businessId) => new(BusinessInsertResultType.Inserted, businessId);

    public static BusinessInsertResult SlugTaken() => new(BusinessInsertResultType.SlugTaken, 0);

    public bool IsInserted => Result == BusinessInsertResultType.Inserted;
}

/// <summary>
/// Outcome of updating a business. Renaming the slug can lose the same check-then-write window; the repository reports
/// <see cref="BusinessUpdateResultType.SlugTaken"/> so the caller maps it to the existing <c>slug_taken</c> field error.
/// </summary>
public enum BusinessUpdateResultType
{
    Updated = 1,
    SlugTaken = 2
}

/// <summary>Result of the guarded business update.</summary>
public sealed record BusinessUpdateResult(BusinessUpdateResultType Result)
{
    public static BusinessUpdateResult Updated() => new(BusinessUpdateResultType.Updated);

    public static BusinessUpdateResult SlugTaken() => new(BusinessUpdateResultType.SlugTaken);

    public bool IsUpdated => Result == BusinessUpdateResultType.Updated;
}

public interface IBusinessRepository
{
    Task<Business?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<Business?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Slug uniqueness check. Pass the business's own id as <paramref name="excludingId"/> when editing.</summary>
    Task<bool> SlugExistsAsync(string slug, int? excludingId = null, CancellationToken ct = default);

    /// <summary>
    /// Inserts the business and returns the new id, or <see cref="BusinessInsertResult.SlugTaken"/> when the global
    /// <c>UQ_Businesses_Slug</c> unique key rejects the row because a concurrent registration took the slug first.
    /// Only that constraint is reported as a business outcome; every other storage failure propagates.
    /// </summary>
    Task<BusinessInsertResult> InsertAsync(Business business, CancellationToken ct = default);

    /// <summary>
    /// Updates the business, or returns <see cref="BusinessUpdateResult.SlugTaken"/> when the global
    /// <c>UQ_Businesses_Slug</c> unique key rejects the rename. The statement is atomic: a rejected update leaves the
    /// stored row unchanged. Only that constraint is reported as a business outcome; every other storage failure
    /// propagates.
    /// </summary>
    Task<BusinessUpdateResult> UpdateAsync(Business business, CancellationToken ct = default);
}
