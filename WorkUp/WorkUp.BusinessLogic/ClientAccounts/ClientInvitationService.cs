using System.Globalization;
using System.Net;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.ClientAccounts;

/// <summary>
/// Owner-assisted invitation of an existing ficha (RF-26). The owner confirms the destination address with the person,
/// so the invitation proves that email even when it differs from the one stored on the ficha; that destination is the
/// email of the account and never rewrites the ficha. The owner panel carries the business and the ficha ids, so every
/// call is tenant-scoped and a ficha of another business is refused with the same generic error as a missing one.
/// <para>
/// An invitation lives 48 hours, works once and is revocable; issuing a new one invalidates the previous, and the
/// revocation plus the insert share the Clients row lock, so a ficha never keeps two live invitations. Only the
/// SHA-256 hash is stored: the raw token exists exclusively inside the queued email and is never part of the receipt.
/// The link derives from the configured trusted base URL, never from a request header.
/// </para>
/// <para>
/// Known limitation, accepted for this slice: the linked-account check runs before the Clients row lock, because
/// locking <c>ClientAccounts</c> while holding a Clients row would invert the T20 order. A linkage committed between
/// the check and the lock can therefore yield a stale invitation. The acceptance step of RF-26 must fail closed for an
/// invitation whose ficha is already linked by the time it is consumed.
/// </para>
/// </summary>
public sealed class ClientInvitationService(
    IBusinessRepository businesses,
    IClientInvitationRepository repository,
    IUnitOfWork unitOfWork,
    IClientInvitationUrlBuilder urlBuilder,
    IClock clock)
{
    /// <summary>Single-use lifetime of an invitation (RF-26, D-18): 48 hours, the same window as email verification.</summary>
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(48);

    private const string UnavailableCode = "client_invitation_unavailable";
    private const string UnavailableMessage = "No encontramos esa ficha.";
    private const string AlreadyLinkedCode = "client_invitation_already_linked";
    private const string AlreadyLinkedMessage =
        "Esa ficha ya tiene una cuenta vinculada. No hace falta invitarla.";

    /// <summary>
    /// Stable dedup key of one invitation notice. It is keyed by the invitation id, so a technical retry of the same
    /// notice is deduplicated while a reissue always gets its own notice and its own link.
    /// </summary>
    public static string InvitationDedupKey(int invitationId) => $"invite:{invitationId}";

    /// <summary>
    /// Issues a 48 h invitation for a real ficha of the business that does not already have a linked account, queues
    /// the single email to the confirmed destination address and revokes the previous live invitation. Returns only
    /// the destination and the expiry.
    /// </summary>
    public async Task<Result<ClientInvitationReceipt>> IssueAsync(
        int businessId, int clientId, string destinationEmail, CancellationToken ct = default)
    {
        var email = Text.NormalizeEmail(destinationEmail);
        if (email.Length == 0)
            return Failure("client_invitation_email_required", "Ingresá el email de destino.", nameof(destinationEmail));
        if (email.Length > 254 || !EmailFormat.IsValid(email))
            return Failure("invalid_client_invitation_email", "Ingresá un email válido.", nameof(destinationEmail));

        var client = await repository.GetClientAsync(businessId, clientId, ct);
        if (client is null)
            return Unavailable();

        // The linked-account check happens before the Clients row lock; the T20 lock order forbids locking
        // ClientAccounts while holding a Clients row. The race window it leaves is documented on the class.
        if (await repository.HasLinkedAccountAsync(businessId, clientId, ct))
            return Result<ClientInvitationReceipt>.Failure(AlreadyLinkedCode, AlreadyLinkedMessage);

        var business = await businesses.GetByIdAsync(businessId, ct);
        if (business is null)
            return Unavailable();

        var token = SecureToken.Create();
        var now = clock.Now;
        var expiresAt = now + InvitationLifetime;

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var invitationId = await repository.IssueAsync(
            new AccountInvitation
            {
                BusinessId = businessId,
                ClientId = clientId,
                Email = email,
                TokenHash = token.Hash,
                ExpiresAt = expiresAt,
                CreatedAt = now
            },
            ct);
        if (invitationId <= 0)
            return Unavailable();

        // A deduplicated notice (0) is a technical retry, not a failure; a real outbox error throws and rolls back.
        await repository.InsertNotificationAsync(
            ComposeInvitationNotice(business, client, email, invitationId, token, expiresAt, now),
            ct);

        await transaction.CommitAsync(ct);
        return Result<ClientInvitationReceipt>.Success(new ClientInvitationReceipt(email, expiresAt));
    }

    /// <summary>
    /// Revokes every live invitation of a ficha of this business (RF-26). Missing and foreign fichas get the same
    /// generic error, and a second revoke of the same ficha succeeds without changing anything new.
    /// </summary>
    public async Task<Result> RevokeAsync(int businessId, int clientId, CancellationToken ct = default)
    {
        if (await repository.GetClientAsync(businessId, clientId, ct) is null)
            return Result.Failure(UnavailableCode, UnavailableMessage);

        var now = clock.Now;
        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        if (!await repository.RevokeAsync(businessId, clientId, now, ct))
            return Result.Failure(UnavailableCode, UnavailableMessage);

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    private Notification ComposeInvitationNotice(
        Business business,
        Client client,
        string email,
        int invitationId,
        TokenPair token,
        DateTime expiresAt,
        DateTime now)
    {
        var safeBusinessName = WebUtility.HtmlEncode(business.Name);
        var safeFirstName = WebUtility.HtmlEncode(client.FirstName);
        var safeUrl = WebUtility.HtmlEncode(urlBuilder.BuildInvitationUrl(business.Slug, token.Token));
        var when = WebUtility.HtmlEncode(expiresAt.ToString("dd/MM/yyyy 'a las' HH:mm", CultureInfo.InvariantCulture));
        return new Notification
        {
            BusinessId = business.Id,
            Type = NotificationType.AccountInvitation,
            Recipient = email,
            Subject = $"Invitación de {business.Name}",
            BodyHtml =
                $"<p>Hola {safeFirstName},</p>" +
                $"<p>{safeBusinessName} te invita a crear tu cuenta y vincularla con tu ficha de cliente.</p>" +
                $"<p><a href=\"{safeUrl}\">Aceptar la invitación</a></p>" +
                $"<p>El enlace vence el {when} y sirve una sola vez.</p>",
            DedupKey = InvitationDedupKey(invitationId),
            CreatedAt = now
        };
    }

    private static Result<ClientInvitationReceipt> Failure(string code, string message, string? field) =>
        Result<ClientInvitationReceipt>.Failure(code, message, field);

    private static Result<ClientInvitationReceipt> Unavailable() =>
        Result<ClientInvitationReceipt>.Failure(UnavailableCode, UnavailableMessage);
}
