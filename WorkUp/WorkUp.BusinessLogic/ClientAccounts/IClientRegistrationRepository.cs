using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.ClientAccounts;

/// <summary>
/// Persistence boundary of self-registration (RF-25). Every write reports its guarded outcome instead of assuming it
/// succeeded, because two simultaneous calls must never create two accounts or two fichas with the same email:
/// <list type="bullet">
/// <item>an insert returns 0 when the tenant/email key is already taken;</item>
/// <item>the token consume returns null when no single valid, unexpired, unused token of that business matched.</item>
/// </list>
/// All calls happen inside the caller's transaction, so a rejected step leaves nothing behind.
/// </summary>
public interface IClientRegistrationRepository
{
    /// <summary>Whether the email is already used by a ficha or an account of this business.</summary>
    Task<ClientRegistrationEmailState> GetEmailStateAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default);

    /// <summary>
    /// Inserts the pending account (<see cref="ClientAccount.ClientId"/> null, no verification instant) with the
    /// temporary names of the future ficha. Returns the new id, or 0 when this business already has an account or a
    /// ficha with that email, so a concurrent registration cannot create a second one.
    /// </summary>
    Task<int> InsertPendingAccountAsync(
        ClientAccount account, string pendingFirstName, string pendingLastName, CancellationToken ct = default);

    /// <summary>Inserts the token hash. Returns 0 when that hash already exists (unique key).</summary>
    Task<int> InsertAccountTokenAsync(AccountToken token, CancellationToken ct = default);

    /// <summary>
    /// Queues the outbox email. Returns 0 when <see cref="Notification.DedupKey"/> is already queued, so a technical
    /// retry never duplicates a notice and the caller must not treat it as a failure.
    /// </summary>
    Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// Consumes one valid, unexpired, unused <c>EmailVerification</c> token of an account of this business and marks
    /// it used in the same step. Returns null when no single row matched, which covers a replayed token, a foreign
    /// tenant, an expired token and a wrong purpose, so none of them can activate an account.
    /// </summary>
    Task<PendingClientAccount?> ConsumeEmailVerificationTokenAsync(
        byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Ficha of this business with that email, or null (Regla 7). The caller uses it to fail closed, never to attach:
    /// an existing ficha is linked only by the owner-assisted invitation (T22), because attaching it automatically
    /// would expose another person's history.
    /// </summary>
    Task<Client?> GetClientByEmailAsync(int businessId, string normalizedEmail, CancellationToken ct = default);

    /// <summary>
    /// Inserts the ficha created at verification. Returns 0 when the tenant/email key lost the race, and the caller
    /// must then fail closed without re-reading or linking the winner's ficha.
    /// </summary>
    Task<int> InsertClientAsync(Client client, CancellationToken ct = default);

    /// <summary>
    /// Attaches the ficha and the verification instant to a still-pending account and clears the temporary names.
    /// Returns false when the account is not pending in this business or is already linked to a ficha.
    /// </summary>
    Task<bool> ActivateVerifiedAccountAsync(
        int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default);

    /// <summary>Account of this business with that email whose email is not verified yet, or null.</summary>
    Task<PendingClientAccount?> GetPendingAccountAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default);

    /// <summary>
    /// Invalidates every unused verification token of the account and inserts the replacement in one step. Returns
    /// false when the account is not pending in this business, so a verified account never gets a new link.
    /// </summary>
    Task<bool> ReplaceVerificationTokenAsync(
        int businessId, int clientAccountId, AccountToken replacement, CancellationToken ct = default);
}
