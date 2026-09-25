using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.ClientAccounts;

/// <summary>
/// Persistence boundary of client sign-in and password recovery (RF-15, RF-27). Every operation is scoped to one
/// business, so an account or a token of another tenant can never satisfy a lookup:
/// <list type="bullet">
/// <item>the account reads return null for a pending, unlinked or foreign account, so the caller cannot tell those
/// states apart (non-enumerating answer);</item>
/// <item>the token consume returns null when no single valid, unexpired, unused <c>PasswordReset</c> token of an
/// active account of that business matched, which covers a replayed token, a foreign tenant, a wrong purpose and an
/// expired token;</item>
/// <item>the guarded writes report whether they happened instead of assuming success.</item>
/// </list>
/// The caller runs them inside its own transaction. Rotating a recovery token must invalidate the previous unused
/// ones and insert the replacement under the same lock, so two simultaneous requests can never leave two live links
/// or a link that was never delivered.
/// </summary>
public interface IClientAccessRepository
{
    /// <summary>
    /// Active account of this business with that email, or null. Only an account whose email is verified and whose
    /// ficha is linked is returned; a pending or unlinked account is indistinguishable from a missing one.
    /// </summary>
    Task<ActiveClientAccount?> GetActiveAccountByEmailAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default);

    /// <summary>
    /// Invalidates every unused password-reset token of the active account and inserts the replacement in one step.
    /// Returns false when the account is not active in this business, so a pending or foreign account never gets a
    /// recovery link.
    /// </summary>
    Task<bool> ReplacePasswordResetTokenAsync(
        int businessId, int clientAccountId, AccountToken replacement, CancellationToken ct = default);

    /// <summary>
    /// Queues the outbox email. Returns 0 when <see cref="Notification.DedupKey"/> is already queued, so a technical
    /// retry never duplicates a notice and the caller must not treat it as a failure.
    /// </summary>
    Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// Consumes the single valid, unexpired, unused <c>PasswordReset</c> token of an active account of this business
    /// and marks it used in the same step. Returns null when no single row matched, so a replayed, expired, foreign
    /// or wrong-purpose token can never change a password.
    /// </summary>
    Task<int?> ConsumePasswordResetTokenAsync(
        byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Replaces the password hash of an active account of this business. Returns false when the account is not active
    /// there, so a consumed token can never write through a foreign or deactivated account.
    /// </summary>
    Task<bool> UpdatePasswordHashAsync(
        int businessId, int clientAccountId, string passwordHash, CancellationToken ct = default);
}
