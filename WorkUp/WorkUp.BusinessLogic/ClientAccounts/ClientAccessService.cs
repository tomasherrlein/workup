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
/// Client sign-in and password recovery (RF-15, RF-27). The business is resolved only from the public slug, so a
/// caller cannot reach another tenant's account. Sign-in accepts solely an <em>active</em> account: its email is
/// verified and its ficha is linked (<c>ClientId is not null</c>); a pending or unlinked account is answered exactly
/// like an absent one, with one generic error and no session, so the form never reveals who has an account and
/// sign-in never links a ficha by email (that is the owner-assisted invitation of RF-26). Recovery repeats the same
/// non-enumerating contract: the request answer does not depend on whether the account exists, the link lives 30
/// minutes, works once, is stored only as its SHA-256 hash and is delivered exclusively inside the queued email;
/// completing it replaces the password hash without ever emailing a password or starting a session.
/// </summary>
public sealed class ClientAccessService(
    IBusinessRepository businesses,
    IClientAccessRepository repository,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IClientAccountUrlBuilder urlBuilder,
    IClock clock)
{
    /// <summary>Same minimum password policy as business registration (RNF Seguridad).</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>Single-use lifetime of a recovery link (RF-27).</summary>
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromMinutes(30);

    private const string BusinessNotFoundCode = "business_not_found";
    private const string BusinessNotFoundMessage = "No encontramos ese negocio.";
    private const string PasswordTooShortCode = "password_too_short";
    private const string PasswordTooShortMessage = "La contraseña debe tener al menos 8 caracteres.";

    /// <summary>
    /// One single error for a missing account, a wrong password, a pending account and an unlinked account. They are
    /// indistinguishable on purpose: any difference would tell an attacker which emails have an account.
    /// </summary>
    private static readonly BusinessError InvalidCredentials =
        new("invalid_credentials", "Email o contraseña incorrectos.");

    /// <summary>One single error for every unusable recovery link: invalid, replayed, expired, foreign or wrong purpose.</summary>
    private static readonly BusinessError ResetUnavailable = new(
        "password_reset_link_unavailable",
        "Este enlace no es válido o ya venció. Pedí uno nuevo para restablecer tu contraseña.");

    /// <summary>
    /// Stable dedup key of one recovery notice. It is derived from the stored SHA-256 hash of the token, never from
    /// the raw token and never from the issuing second alone: a genuine retry of the same notice is deduplicated,
    /// while a rotated token always gets its own notice. Keying only by account and second would let a resend inside
    /// the same second rotate the link and lose the replacement email, leaving the account with a token nobody got.
    /// </summary>
    public static string PasswordResetDedupKey(int clientAccountId, byte[] tokenHash) =>
        $"reset:{clientAccountId}:{Convert.ToHexString(tokenHash)}";

    /// <summary>
    /// Signs in a client of the given business. Only an active account can start a session, and the returned session
    /// carries the tenant and the account/ficha ids the cookie needs, never the password nor its hash.
    /// </summary>
    public async Task<Result<ClientSession>> LoginAsync(
        ClientLoginRequest request, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(request.Slug.Trim(), ct);
        if (business is null)
            return Result<ClientSession>.Failure(BusinessNotFoundCode, BusinessNotFoundMessage, nameof(request.Slug));

        var email = Text.NormalizeEmail(request.Email);
        var account = await repository.GetActiveAccountByEmailAsync(business.Id, email, ct);
        if (account is null || !passwordHasher.Verify(account.PasswordHash, request.Password))
            return Result<ClientSession>.Failure(InvalidCredentials);

        return Result<ClientSession>.Success(new ClientSession(
            business.Id,
            business.Slug,
            business.Name,
            account.ClientAccountId,
            account.ClientId,
            account.Email));
    }

    /// <summary>
    /// Queues a single-use recovery link for an active account (RF-27). The answer is the same empty success for a
    /// missing email, a pending account, an unlinked account, an account of another tenant and an active one, so the
    /// request enumerates nothing. A previous unused link stops working at the rotation, and the notice is
    /// deduplicated by the hash of its own token, so two requests in the same second still deliver both links.
    /// </summary>
    public async Task<Result> RequestPasswordResetAsync(
        ClientPasswordResetRequest request, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(request.Slug.Trim(), ct);
        if (business is null)
            return Result.Failure(BusinessNotFoundCode, BusinessNotFoundMessage, nameof(request.Slug));

        var email = Text.NormalizeEmail(request.Email);
        if (email.Length == 0)
            return Result.Failure("client_email_required", "Ingresá el email.", nameof(request.Email));
        if (!EmailFormat.IsValid(email))
            return Result.Failure("invalid_client_email", "Ingresá un email válido.", nameof(request.Email));

        var account = await repository.GetActiveAccountByEmailAsync(business.Id, email, ct);
        if (account is null)
            return Result.Success();

        var now = clock.Now;
        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var token = SecureToken.Create();
        var expiresAt = now + PasswordResetLifetime;
        var rotated = await repository.ReplacePasswordResetTokenAsync(
            business.Id,
            account.ClientAccountId,
            new AccountToken
            {
                ClientAccountId = account.ClientAccountId,
                Purpose = AccountTokenPurpose.PasswordReset,
                TokenHash = token.Hash,
                ExpiresAt = expiresAt,
                CreatedAt = now
            },
            ct);

        // The account stopped being active between the read and the rotation: the same generic answer, nothing
        // written and no token for an account that cannot sign in.
        if (!rotated)
            return Result.Success();

        await repository.InsertNotificationAsync(
            ComposePasswordResetNotice(
                business.Id, business.Name, business.Slug, account.ClientAccountId, email, token, expiresAt, now),
            ct);

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Completes a recovery from the emailed link (RF-27): the token is consumed by tenant, purpose, expiry and
    /// single use, and the password hash is replaced in the same transaction. The new password is validated before
    /// anything is consumed, is never emailed, and no session is started. Every unusable link gets one generic error.
    /// </summary>
    public async Task<Result> CompletePasswordResetAsync(
        CompleteClientPasswordResetRequest request, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(request.Slug.Trim(), ct);
        if (business is null || string.IsNullOrWhiteSpace(request.Token))
            return Result.Failure(ResetUnavailable);

        if (request.NewPassword.Length < MinimumPasswordLength)
            return Result.Failure(PasswordTooShortCode, PasswordTooShortMessage, nameof(request.NewPassword));

        var now = clock.Now;
        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var clientAccountId = await repository.ConsumePasswordResetTokenAsync(
            SecureToken.Hash(request.Token), business.Id, now, ct);
        if (clientAccountId is null)
            return Result.Failure(ResetUnavailable);

        if (!await repository.UpdatePasswordHashAsync(
                business.Id, clientAccountId.Value, passwordHasher.Hash(request.NewPassword), ct))
            return Result.Failure(ResetUnavailable);

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    private Notification ComposePasswordResetNotice(
        int businessId,
        string businessName,
        string slug,
        int clientAccountId,
        string email,
        TokenPair token,
        DateTime expiresAt,
        DateTime now)
    {
        var safeBusinessName = WebUtility.HtmlEncode(businessName);
        var safeUrl = WebUtility.HtmlEncode(urlBuilder.BuildPasswordResetUrl(slug, token.Token));
        var when = WebUtility.HtmlEncode(expiresAt.ToString("dd/MM/yyyy 'a las' HH:mm", CultureInfo.InvariantCulture));
        return new Notification
        {
            BusinessId = businessId,
            Type = NotificationType.PasswordReset,
            Recipient = email,
            Subject = $"Restablecé tu contraseña en {businessName}",
            BodyHtml =
                $"<p>Hola,</p><p>Pediste restablecer tu contraseña de {safeBusinessName}. Elegí una nueva desde este enlace:</p>" +
                $"<p><a href=\"{safeUrl}\">Restablecer mi contraseña</a></p>" +
                $"<p>El enlace vence el {when} y sirve una sola vez. " +
                "Si no fuiste vos, ignorá este mensaje: tu contraseña no cambia.</p>",
            DedupKey = PasswordResetDedupKey(clientAccountId, token.Hash),
            CreatedAt = now
        };
    }
}
