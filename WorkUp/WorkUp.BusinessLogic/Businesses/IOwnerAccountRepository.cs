using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Businesses;

/// <summary>
/// Outcome of inserting an owner account. The username is globally unique, so a registration that passed the
/// sequential precheck can still lose the insert to a competing registration that committed in between. That lost race
/// is a business outcome, not a 500: the repository reports <see cref="OwnerAccountInsertResultType.UsernameTaken"/>
/// instead of letting the unique-key violation escape as an unhandled <c>SqlException</c>.
/// </summary>
public enum OwnerAccountInsertResultType
{
    Inserted = 1,
    UsernameTaken = 2
}

/// <summary>Result of the guarded owner-account insert. <see cref="OwnerAccountId"/> is only meaningful when it inserted.</summary>
public sealed record OwnerAccountInsertResult(OwnerAccountInsertResultType Result, int OwnerAccountId)
{
    public static OwnerAccountInsertResult Inserted(int ownerAccountId) =>
        new(OwnerAccountInsertResultType.Inserted, ownerAccountId);

    public static OwnerAccountInsertResult UsernameTaken() => new(OwnerAccountInsertResultType.UsernameTaken, 0);

    public bool IsInserted => Result == OwnerAccountInsertResultType.Inserted;
}

public interface IOwnerAccountRepository
{
    Task<OwnerAccount?> GetByUsernameAsync(string username, CancellationToken ct = default);

    Task<OwnerAccount?> GetByBusinessIdAsync(int businessId, CancellationToken ct = default);

    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Inserts the account and returns the new id, or <see cref="OwnerAccountInsertResult.UsernameTaken"/> when the
    /// global <c>UQ_OwnerAccounts_Username</c> unique key rejects the row because a concurrent registration took the
    /// username first. Only that constraint is reported as a business outcome; every other storage failure — including
    /// the one-account-per-business key — propagates.
    /// </summary>
    Task<OwnerAccountInsertResult> InsertAsync(OwnerAccount account, CancellationToken ct = default);
}
