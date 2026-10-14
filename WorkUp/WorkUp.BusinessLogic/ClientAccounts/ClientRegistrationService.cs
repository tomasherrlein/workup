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
/// Client self-registration, email verification and verification resend (RF-25, D-18). The business is resolved only
/// from the public slug and never from a request id. Registration stores a <em>pending</em> account with the password
/// hash and the temporary names of the future ficha, so nothing visible exists until the emailed link is consumed;
/// the account cannot start a session while its email is unverified. Only the SHA-256 hash of the token is stored and
/// the raw token travels exclusively inside the queued email. Verification fails closed: it never links a ficha that
/// already owns the email, because that would disclose another person's history; linking an existing ficha is the
/// owner-assisted invitation of T22 (RF-26).
/// </summary>
public sealed class ClientRegistrationService(
    IBusinessRepository businesses,
    IClientRegistrationRepository repository,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IClientAccountUrlBuilder urlBuilder,
    IClock clock)
{
    /// <summary>Same minimum password policy as business registration (RNF Seguridad).</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>
    /// Plan assumption for T20: the SRS does not fix a verification lifetime, so it matches the 48 h window of the
    /// account invitations (D-18). Password recovery keeps its own 30 minutes (RF-27). An expired link is never a
    /// dead end: the pending account can request a new one.
    /// </summary>
    public static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(48);

    private const string UnavailableCode = "verification_link_unavailable";
    private const string UnavailableMessage =
        "Este enlace no es válido o ya venció. Pedí uno nuevo para confirmar tu cuenta.";
    private const string OwnerAssistanceCode = "client_record_needs_owner_assistance";
    private const string OwnerAssistanceMessage =
        "Ya existe una ficha con ese email, así que no podemos crear tu cuenta desde acá. " +
        "Contactá al negocio para vincularla.";
    private const string EmailTakenCode = "email_already_registered";
    private const string EmailTakenMessage =
        "Ese email ya está registrado en este negocio. Iniciá sesión o recuperá tu contraseña.";
    private const string BusinessNotFoundCode = "business_not_found";
    private const string BusinessNotFoundMessage = "No encontramos ese negocio.";

    /// <summary>
    /// Stable dedup key of one verification notice. It is derived from the stored SHA-256 hash of the token, never
    /// from the raw token and never from the issuing second alone: a genuine retry of the same notice is deduplicated,
    /// while a rotated token always gets its own notice. Keying only by account and second would let a resend that
    /// happens inside the same second rotate the link and lose the replacement email, leaving the account with a
    /// token nobody received.
    /// </summary>
    public static string VerificationDedupKey(int clientAccountId, byte[] tokenHash) =>
        $"verify:{clientAccountId}:{Convert.ToHexString(tokenHash)}";

    /// <summary>
    /// Registers a pending account for a self-registered client. It rejects an email already used by a ficha or an
    /// account of the same business instead of duplicating or silently linking it (T22 invites the linking), and it
    /// queues the single verification email.
    /// </summary>
    public async Task<Result<ClientRegistrationReceipt>> RegisterAsync(
        RegisterClientAccountRequest request, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(request.Slug.Trim(), ct);
        if (business is null)
            return Result<ClientRegistrationReceipt>.Failure(
                BusinessNotFoundCode, BusinessNotFoundMessage, nameof(request.Slug));

        var firstName = request.FirstName.Trim();
        if (firstName.Length == 0)
            return Failure("client_first_name_required", "Ingresá el nombre.", nameof(request.FirstName));

        var lastName = request.LastName.Trim();
        if (lastName.Length == 0)
            return Failure("client_last_name_required", "Ingresá el apellido.", nameof(request.LastName));

        var email = Text.NormalizeEmail(request.Email);
        if (email.Length == 0)
            return Failure("client_email_required", "Ingresá el email.", nameof(request.Email));
        if (!EmailFormat.IsValid(email))
            return Failure("invalid_client_email", "Ingresá un email válido.", nameof(request.Email));

        if (request.Password.Length < MinimumPasswordLength)
            return Failure("password_too_short", "La contraseña debe tener al menos 8 caracteres.", nameof(request.Password));

        if ((await repository.GetEmailStateAsync(business.Id, email, ct)).IsTaken)
            return Failure(EmailTakenCode, EmailTakenMessage, nameof(request.Email));

        var passwordHash = passwordHasher.Hash(request.Password);
        var now = clock.Now;

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var accountId = await repository.InsertPendingAccountAsync(
            new ClientAccount
            {
                BusinessId = business.Id,
                ClientId = null,
                Email = email,
                PasswordHash = passwordHash,
                EmailVerifiedAt = null,
                CreatedAt = now
            },
            firstName,
            lastName,
            ct);
        if (accountId <= 0)
            return Failure(EmailTakenCode, EmailTakenMessage, nameof(request.Email));

        var token = SecureToken.Create();
        var expiresAt = now + VerificationLifetime;
        if (await repository.InsertAccountTokenAsync(
                new AccountToken
                {
                    ClientAccountId = accountId,
                    Purpose = AccountTokenPurpose.EmailVerification,
                    TokenHash = token.Hash,
                    ExpiresAt = expiresAt,
                    CreatedAt = now
                },
                ct) <= 0)
            return Failure("registration_unavailable", "No pudimos completar el registro. Probá de nuevo.", null);

        await repository.InsertNotificationAsync(
            ComposeVerificationNotice(
                business.Id, business.Name, business.Slug, accountId, email, token, expiresAt, now),
            ct);

        await transaction.CommitAsync(ct);
        return Result<ClientRegistrationReceipt>.Success(new ClientRegistrationReceipt(email, expiresAt));
    }

    /// <summary>
    /// Consumes a single-use verification token scoped to the slug and creates the ficha with the pending names and
    /// activates the account in one transaction. Every unusable link gets the same generic error. The flow fails
    /// closed when a ficha of the business already owns the email: it never attaches the account to that ficha, not
    /// even to recover from a lost insert race, because the history of the person who booked with that address is not
    /// this account's to claim. That case is resolved by the owner-assisted invitation of T22 (RF-26).
    /// </summary>
    public async Task<Result<VerifiedClientAccount>> VerifyEmailAsync(
        string slug, string? token, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(slug.Trim(), ct);
        if (business is null || string.IsNullOrWhiteSpace(token))
            return Unavailable();

        var now = clock.Now;
        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var pending = await repository.ConsumeEmailVerificationTokenAsync(SecureToken.Hash(token), business.Id, now, ct);
        if (pending is null)
            return Unavailable();

        var firstName = pending.PendingFirstName?.Trim() ?? "";
        var lastName = pending.PendingLastName?.Trim() ?? "";
        if (firstName.Length == 0 || lastName.Length == 0)
            return Unavailable();

        if (await repository.GetClientByEmailAsync(business.Id, pending.Email, ct) is not null)
        {
            // A booking made with this email created the ficha after the registration started. Attaching the account
            // to it would hand over the history of whoever booked with that address, so the verification is refused
            // and the linking is left to the owner-assisted invitation (T22, RF-26).
            return OwnerAssistance();
        }

        var client = new Client
        {
            BusinessId = business.Id,
            FirstName = firstName,
            LastName = lastName,
            Email = pending.Email,
            CreatedAt = now
        };
        var clientId = await repository.InsertClientAsync(client, ct);
        if (clientId <= 0)
        {
            // A public booking won the insert race with the same email. Re-reading and attaching would link this
            // account to a ficha the flow never verified, so the lost race also fails closed instead of re-reading.
            return OwnerAssistance();
        }

        client.Id = clientId;

        if (!await repository.ActivateVerifiedAccountAsync(
                business.Id, pending.ClientAccountId, client.Id, now, ct))
            return Unavailable();

        await transaction.CommitAsync(ct);
        return Result<VerifiedClientAccount>.Success(new VerifiedClientAccount(
            pending.ClientAccountId, client.Id, client.Email, client.FirstName, client.LastName));
    }

    /// <summary>
    /// Sends a new verification link to a pending account: the previous unused links stop working and a fresh 48 h
    /// token and notice are queued, so an expired link is never a dead end. The response is the same generic success
    /// for an unknown email and for an already verified account, so it reveals nothing about who has an account
    /// (RF-25, D-18).
    /// </summary>
    public async Task<Result> ResendVerificationAsync(
        ResendClientVerificationRequest request, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(request.Slug.Trim(), ct);
        if (business is null)
            return Result.Failure(BusinessNotFoundCode, BusinessNotFoundMessage, nameof(request.Slug));

        var email = Text.NormalizeEmail(request.Email);
        if (email.Length == 0)
            return Result.Failure("client_email_required", "Ingresá el email.", nameof(request.Email));
        if (!EmailFormat.IsValid(email))
            return Result.Failure("invalid_client_email", "Ingresá un email válido.", nameof(request.Email));

        var pending = await repository.GetPendingAccountAsync(business.Id, email, ct);
        if (pending is null)
            return Result.Success();

        var now = clock.Now;
        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var token = SecureToken.Create();
        var expiresAt = now + VerificationLifetime;
        var rotated = await repository.ReplaceVerificationTokenAsync(
            business.Id,
            pending.ClientAccountId,
            new AccountToken
            {
                ClientAccountId = pending.ClientAccountId,
                Purpose = AccountTokenPurpose.EmailVerification,
                TokenHash = token.Hash,
                ExpiresAt = expiresAt,
                CreatedAt = now
            },
            ct);

        // The account stopped being pending between the read and the rotation: the same generic answer, nothing
        // written and no token for a verified account.
        if (!rotated)
            return Result.Success();

        await repository.InsertNotificationAsync(
            ComposeVerificationNotice(
                business.Id, business.Name, business.Slug, pending.ClientAccountId, email, token,
                expiresAt, now),
            ct);

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    private Notification ComposeVerificationNotice(
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
        var safeUrl = WebUtility.HtmlEncode(urlBuilder.BuildVerificationUrl(slug, token.Token));
        var when = WebUtility.HtmlEncode(expiresAt.ToString("dd/MM/yyyy 'a las' HH:mm", CultureInfo.InvariantCulture));
        return new Notification
        {
            BusinessId = businessId,
            Type = NotificationType.EmailVerification,
            Recipient = email,
            Subject = $"Confirmá tu cuenta en {businessName}",
            BodyHtml =
                $"<p>Hola,</p><p>Confirmá tu email para crear tu cuenta y gestionar tus turnos con {safeBusinessName}.</p>" +
                $"<p><a href=\"{safeUrl}\">Confirmar mi cuenta</a></p>" +
                $"<p>El enlace vence el {when} y sirve una sola vez.</p>",
            DedupKey = VerificationDedupKey(clientAccountId, token.Hash),
            CreatedAt = now
        };
    }

    private static Result<ClientRegistrationReceipt> Failure(string code, string message, string? field) =>
        Result<ClientRegistrationReceipt>.Failure(code, message, field);

    private static Result<VerifiedClientAccount> Unavailable() =>
        Result<VerifiedClientAccount>.Failure(UnavailableCode, UnavailableMessage);

    private static Result<VerifiedClientAccount> OwnerAssistance() =>
        Result<VerifiedClientAccount>.Failure(OwnerAssistanceCode, OwnerAssistanceMessage);
}
