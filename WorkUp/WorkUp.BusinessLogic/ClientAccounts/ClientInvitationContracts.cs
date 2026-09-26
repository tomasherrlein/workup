using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.ClientAccounts;

/// <summary>
/// Safe outcome of issuing an owner-assisted invitation (RF-26). The owner needs to confirm the destination and the
/// deadline, so the receipt carries the invited email and the expiry; it never carries the raw token, which exists
/// only inside the queued email, nor the stored SHA-256 hash.
/// </summary>
public sealed record ClientInvitationReceipt(string Email, DateTime ExpiresAt);

/// <summary>
/// Builds the absolute link of an owner-assisted invitation (RF-26). It is a dedicated port on purpose: adding the
/// invitation route must not change <see cref="IClientAccountUrlBuilder"/>, so the existing verification and recovery
/// implementations stay untouched.
/// </summary>
public interface IClientInvitationUrlBuilder
{
    string BuildInvitationUrl(string slug, string token);
}

/// <summary>
/// Persistence boundary of the owner-assisted invitation (RF-26). Every read and write is scoped to one business, so a
/// ficha of another tenant can never be invited, revoked, or disclosed:
/// <list type="bullet">
/// <item>the ficha read returns null for a missing or foreign client;</item>
/// <item>the linked-account read answers whether the ficha already owns an account, and the caller runs it
/// <em>before</em> taking the Clients row lock;</item>
/// <item>the issue and the revoke take that lock and only then touch <c>AccountInvitations</c>, never
/// <c>ClientAccounts</c>, so they never request a ClientAccounts lock while holding a Clients row (the T20 order);</item>
/// <item>the issue revokes every live invitation of the ficha and inserts the replacement under that lock, so a ficha
/// never keeps two live invitations;</item>
/// <item>the write that finds no ficha of the business reports false or 0 instead of assuming success.</item>
/// </list>
/// All calls happen inside the caller's transaction. The linked-account read is a plain read outside the lock, so a
/// linkage committed between the read and the lock is not detected here: the acceptance step of RF-26 must fail closed
/// for that stale invitation.
/// </summary>
public interface IClientInvitationRepository
{
    /// <summary>Ficha of this business, or null when it does not exist or belongs to another tenant.</summary>
    Task<Client?> GetClientAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>
    /// Whether the ficha already has a linked <c>ClientAccounts</c> row in this business. The caller checks it before
    /// the Clients row lock, because locking ClientAccounts while holding a Clients row would break the T20 lock order.
    /// </summary>
    Task<bool> HasLinkedAccountAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>
    /// Takes the Clients row lock of this business, revokes every live invitation of the ficha and inserts the
    /// replacement. Returns the new invitation id, or 0 when the ficha is not of this business, in which case nothing
    /// is written. Because the revocation and the insert share the lock, two simultaneous issues leave exactly one
    /// live invitation.
    /// </summary>
    Task<int> IssueAsync(AccountInvitation invitation, CancellationToken ct = default);

    /// <summary>
    /// Takes the Clients row lock of this business and revokes every live invitation of the ficha. Returns false when
    /// the ficha is not of this business. It is idempotent: a second call on the same ficha reports success and
    /// changes nothing new.
    /// </summary>
    Task<bool> RevokeAsync(int businessId, int clientId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Queues the outbox email. Returns 0 when <see cref="Notification.DedupKey"/> is already queued, so a technical
    /// retry never duplicates a notice and the caller must not treat it as a failure.
    /// </summary>
    Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default);
}
