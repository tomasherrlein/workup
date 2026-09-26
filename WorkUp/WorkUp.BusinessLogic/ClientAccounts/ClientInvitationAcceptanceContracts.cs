using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.ClientAccounts;

/// <summary>
/// Safe outcome of accepting an owner-assisted invitation (RF-26, slice 2). It carries exactly what the client
/// session cookie needs — the account id, the linked ficha id, the verified email and the ficha name — and never a
/// token, a password, its hash or a session.
/// </summary>
public sealed record AcceptedClientAccount(
    int ClientAccountId,
    int ClientId,
    string Email,
    string FirstName,
    string LastName);

/// <summary>
/// Read-only view of an invitation for the GET of the public link (RF-26, slice 2). It never carries ficha data or
/// history: only the destination email the link proves and the deadline the page shows. The GET consumes nothing.
/// </summary>
public sealed record ClientInvitationInspection(string Email, DateTime ExpiresAt);

/// <summary>
/// Invitation resolved from its stored SHA-256 hash, scoped to one business. It is the internal target the acceptance
/// locks in order: the invited email selects the account (never a ficha lookup by email) and the ficha id comes from
/// the invitation itself.
/// </summary>
public sealed record InvitationAcceptanceTarget(int InvitationId, int ClientId, string Email, DateTime ExpiresAt);

/// <summary>
/// The account of the invited email, already locked by the acceptance. <see cref="IsPending"/> is true only for an
/// account with no ficha and no verified email: a linked or verified account can never be bound by an invitation.
/// </summary>
public sealed record InvitationAccountState(int ClientAccountId, string PasswordHash, bool IsPending);

/// <summary>
/// Result of taking the two per-business account key guards of the new-account path, before the ficha lock.
/// <see cref="EmailTaken"/> is the unique account email key; <see cref="ClientLinked"/> is the unique linked-ficha
/// key, which closes the documented issuer race by refusing an invitation whose ficha was linked by acceptance time.
/// </summary>
public sealed record InvitationAccountKeyState(bool EmailTaken, bool ClientLinked);

/// <summary>
/// Persistence boundary of the invitation acceptance (RF-26, slice 2). Every read and write is scoped to one business,
/// so a token of another tenant resolves to nothing and no row or subject data is ever returned for a missing or
/// foreign token.
/// <para>
/// The methods are the ordered steps of the documented lock order, and the caller runs them inside one transaction:
/// </para>
/// <list type="bullet">
/// <item><see cref="FindInvitationAsync"/> is a read-only probe with no lock. It resolves the invited email so the
/// account can be locked before the ficha; it decides nothing final, because usability is revalidated after the
/// locks.</item>
/// <item>The existing-account path locks the account row of the invited email first
/// (<see cref="LockAccountByEmailAsync"/>), then the ficha (<see cref="LockClientAsync"/>) and only then the
/// invitation (<see cref="ConsumeInvitationAsync"/>).</item>
/// <item>The new-account path takes the per-business padlock and the two account key guards first
/// (<see cref="LockAccountKeyAsync"/>), then the ficha and the invitation, so an account row is never acquired after
/// the ficha (the T20 register order).</item>
/// <item><see cref="ConsumeInvitationAsync"/> revalidates <c>UsedAt</c>, <c>RevokedAt</c> and <c>ExpiresAt</c> under
/// the lock and consumes exactly once; a concurrent consumer blocks and then finds the row used.</item>
/// <item><see cref="BindPendingAccountAsync"/> and <see cref="InsertVerifiedAccountAsync"/> are the only two bind
/// writes, each guarded, so a lost race returns a closed false or 0 and the caller never re-reads a winner.</item>
/// </list>
/// </summary>
public interface IClientInvitationAcceptanceRepository
{
    /// <summary>
    /// Read-only probe by stored token hash. Returns the invited target or null for a missing, foreign, used, revoked
    /// or expired invitation. It holds no lock past its own statement and consumes nothing.
    /// </summary>
    Task<InvitationAcceptanceTarget?> FindInvitationAsync(
        int businessId, byte[] tokenHash, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Existing-account path, step 1: locks the <c>ClientAccounts</c> row of the invited email inside the business and
    /// returns its state, or null when no account carries that email. It runs before the ficha lock.
    /// </summary>
    Task<InvitationAccountState?> LockAccountByEmailAsync(
        int businessId, string email, CancellationToken ct = default);

    /// <summary>
    /// New-account path, step 1: takes the per-business padlock and the two account key guards, in the T20 register
    /// order, and reports whether the invited email is already an account or the ficha is already linked. Both guards
    /// are held until the caller commits or rolls back, so an account row is never acquired after the ficha.
    /// </summary>
    Task<InvitationAccountKeyState> LockAccountKeyAsync(
        int businessId, string email, int clientId, CancellationToken ct = default);

    /// <summary>Locks the ficha of the business, or returns null when it is missing or of another tenant.</summary>
    Task<Client?> LockClientAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>
    /// Revalidates usability under the lock and marks the invitation used in one conditional update. Returns false for
    /// a used, revoked, expired or foreign invitation; two simultaneous consumers leave exactly one winner.
    /// </summary>
    Task<bool> ConsumeInvitationAsync(
        int businessId, int invitationId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Existing-account path, final step: binds a still-pending, still-unlinked account of the business to the ficha
    /// and verifies it, clearing the temporary names in the same statement. A linked, verified or foreign account
    /// matches no row, so it reports false instead of binding the wrong account.
    /// </summary>
    Task<bool> BindPendingAccountAsync(
        int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default);

    /// <summary>
    /// New-account path, final step: inserts the already-verified account bound to the invited ficha, under the
    /// per-business padlock and the account key guards. Returns the new account id, or 0 when the email key or the
    /// linked-ficha key is taken, so a lost race stays closed and no winner is re-read.
    /// </summary>
    Task<int> InsertVerifiedAccountAsync(ClientAccount account, CancellationToken ct = default);
}
