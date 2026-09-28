using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Catalog;

/// <summary>
/// Outcome of inserting a membership price. The pair (business, weekly frequency) is unique in storage
/// (<c>UQ_MembershipPrices_Frequency</c>), so a set-price that passed its sequential read can still lose the insert to
/// a competing set that committed in between. That lost race is a business outcome, not a 500: the repository reports
/// <see cref="MembershipPriceInsertResultType.AlreadyPresent"/> instead of letting the unique-key violation escape as
/// an unhandled <c>SqlException</c>.
/// </summary>
public enum MembershipPriceInsertResultType
{
    Inserted = 1,
    AlreadyPresent = 2
}

/// <summary>
/// Result of the membership price insert. <see cref="MembershipPriceId"/> is only meaningful when it inserted.
/// </summary>
public sealed record MembershipPriceInsertResult(MembershipPriceInsertResultType Result, int MembershipPriceId)
{
    public static MembershipPriceInsertResult Inserted(int membershipPriceId) =>
        new(MembershipPriceInsertResultType.Inserted, membershipPriceId);

    /// <summary>
    /// No row was written: the frequency was already priced by a competing set that committed after the read. The
    /// caller re-reads that tenant's row and applies the requested price to it, so the set stays idempotent and never
    /// duplicates the frequency.
    /// </summary>
    public static MembershipPriceInsertResult AlreadyPresent() =>
        new(MembershipPriceInsertResultType.AlreadyPresent, 0);

    public bool IsInserted => Result == MembershipPriceInsertResultType.Inserted;
}

public interface IMembershipPriceRepository
{
    /// <summary>All membership prices of the business, ordered by weekly frequency.</summary>
    Task<List<MembershipPrice>> ListByBusinessAsync(int businessId, CancellationToken ct = default);

    Task<MembershipPrice?> GetByFrequencyAsync(int businessId, int weeklyFrequency, CancellationToken ct = default);

    /// <summary>
    /// Inserts the price and returns the new id, or <see cref="MembershipPriceInsertResult.AlreadyPresent"/> when the
    /// per-tenant <c>UQ_MembershipPrices_Frequency</c> unique constraint rejects the row because a concurrent set
    /// priced the same frequency first. Only that constraint is reported as a business outcome; every other storage
    /// failure — including a foreign key violation — propagates.
    /// </summary>
    Task<MembershipPriceInsertResult> InsertAsync(MembershipPrice price, CancellationToken ct = default);

    Task UpdateAsync(MembershipPrice price, CancellationToken ct = default);

    Task DeleteAsync(int businessId, int weeklyFrequency, CancellationToken ct = default);
}
