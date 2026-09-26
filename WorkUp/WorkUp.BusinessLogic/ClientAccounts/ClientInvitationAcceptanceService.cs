using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.ClientAccounts;

/// <summary>
/// Acceptance of an owner-assisted invitation (RF-26, T22 slice 2). The link proves the destination email the owner
/// confirmed with the person, so that address becomes the account email even when the ficha stores another one, and
/// the account binds to the ficha the invitation names — the flow never looks up a ficha by email and never creates a
/// second ficha.
/// <para>
/// Two ways to accept share one contract. Creating an account inserts a <em>verified</em> account already bound to
/// the invited ficha, so the old pending verification of T20 is bypassed by the invitation itself and no provisional
/// ficha or panel access exists. A pre-existing pending, unlinked account of the invited email signs in with its own
/// password <em>only</em> inside this flow and is verified and bound atomically; a wrong password leaves the link and
/// the account exactly as they were. An account already linked to any ficha (the invited one included) and an
/// invitation whose ficha was linked by the time it is consumed are refused with generic owner assistance, without
/// reading, reassigning or revealing the history.
/// </para>
/// <para>
/// The token is looked up by its SHA-256 hash, tenant-scoped, and the consumable state (<c>UsedAt</c>,
/// <c>RevokedAt</c>, <c>ExpiresAt</c>) is revalidated after the locks, with the clock read at that point, so an
/// invitation that expires while the acceptance waits fails closed. The consume is a single conditional update, so a
/// replayed or simultaneous acceptance leaves exactly one winner. Read-only inspection never consumes and never
/// discloses the ficha. The lock order is the one documented on
/// <see cref="IClientInvitationAcceptanceRepository"/>: an account is always acquired before the ficha, and the
/// invitation last.
/// </para>
/// </summary>
public sealed class ClientInvitationAcceptanceService(
    IBusinessRepository businesses,
    IClientInvitationAcceptanceRepository repository,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IClock clock)
{
    /// <summary>Same minimum password policy as registration (RNF Seguridad); it applies only to a new password.</summary>
    public const int MinimumPasswordLength = 8;

    private const string UnavailableCode = "client_invitation_link_unavailable";
    private const string UnavailableMessage =
        "Este enlace no es válido o ya venció. Pedí una invitación nueva.";
    private const string EmailRegisteredCode = "email_already_registered";
    private const string EmailRegisteredMessage =
        "Ese email ya tiene una cuenta en este negocio. Iniciá sesión para vincularla a tu ficha.";
    private const string OwnerAssistanceCode = "client_invitation_owner_assistance";
    private const string OwnerAssistanceMessage =
        "No se pudo vincular esta cuenta. Contactá al negocio para que revisen tu ficha.";

    /// <summary>A wrong password does not disclose whether an account is already linked.</summary>
    private static readonly BusinessError InvalidCredentials =
        new("invalid_credentials", "Email o contraseña incorrectos.");

    /// <summary>
    /// Read-only view of an invitation for the GET of the link. It consumes nothing, opens no transaction, and returns
    /// only the invited destination and the deadline: no ficha, no history, and the same generic error for a missing,
    /// foreign, used, revoked or expired invitation.
    /// </summary>
    public async Task<Result<ClientInvitationInspection>> InspectAsync(
        string slug, string? token, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(slug.Trim(), ct);
        if (business is null || string.IsNullOrWhiteSpace(token))
            return UnavailableInspection();

        var target = await repository.FindInvitationAsync(business.Id, SecureToken.Hash(token), clock.Now, ct);
        if (target is null)
            return UnavailableInspection();

        return Result<ClientInvitationInspection>.Success(
            new ClientInvitationInspection(target.Email, target.ExpiresAt));
    }

    /// <summary>
    /// Creates a new account with the invited email, already verified and bound to the invited existing ficha, with the
    /// chosen password. No ficha is created and no other ficha is touched. The read-only guards run before the ficha
    /// lock (under the shared T20 business padlock), the token is consumed exactly once, and the insert is guarded, so a lost race
    /// returns a closed failure instead of attaching the account to a winner it never verified.
    /// </summary>
    public async Task<Result<AcceptedClientAccount>> AcceptNewAsync(
        string slug, string? token, string password, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(slug.Trim(), ct);
        if (business is null || string.IsNullOrWhiteSpace(token))
            return UnavailableAcceptance();
        if (password is null || password.Length < MinimumPasswordLength)
            return PasswordTooShort();

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var target = await repository.FindInvitationAsync(business.Id, SecureToken.Hash(token), clock.Now, ct);
        if (target is null)
            return UnavailableAcceptance();

        // New-account path: the shared business padlock and account keys precede the ficha, so account rows are not
        // acquired after it. ClientLinked closes the issuer race: an invitation linked in between is
        // refused instead of binding a second account to it.
        var keys = await repository.LockAccountKeyAsync(business.Id, target.Email, target.ClientId, ct);
        if (keys.EmailTaken)
            return EmailRegistered();
        if (keys.ClientLinked)
            return OwnerAssistance();

        var client = await repository.LockClientAsync(business.Id, target.ClientId, ct);
        if (client is null)
            return UnavailableAcceptance();

        // The clock is read after the locks, so an invitation that expired while this acceptance waited fails here.
        var now = clock.Now;
        if (!await repository.ConsumeInvitationAsync(business.Id, target.InvitationId, now, ct))
            return UnavailableAcceptance();

        var accountId = await repository.InsertVerifiedAccountAsync(
            new ClientAccount
            {
                BusinessId = business.Id,
                ClientId = client.Id,
                Email = target.Email,
                PasswordHash = passwordHasher.Hash(password),
                EmailVerifiedAt = now,
                CreatedAt = now
            },
            ct);
        if (accountId <= 0)
            return UnavailableAcceptance();

        await transaction.CommitAsync(ct);
        return Result<AcceptedClientAccount>.Success(
            new AcceptedClientAccount(accountId, client.Id, target.Email, client.FirstName, client.LastName));
    }

    /// <summary>
    /// Verifies and binds a pre-existing pending, unlinked account of the invited email to the invited ficha, after
    /// proving the password the account already holds. The account is locked first, then the ficha and the invitation;
    /// a wrong password, a non-pending account, an account linked to any other ficha and an invitation that expired
    /// while waiting all fail without consuming the link or changing the account. A non-pending account receives
    /// generic owner assistance only after its password has been validated.
    /// </summary>
    public async Task<Result<AcceptedClientAccount>> AcceptExistingAsync(
        string slug, string? token, string password, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(slug.Trim(), ct);
        if (business is null || string.IsNullOrWhiteSpace(token))
            return UnavailableAcceptance();

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var target = await repository.FindInvitationAsync(business.Id, SecureToken.Hash(token), clock.Now, ct);
        if (target is null)
            return UnavailableAcceptance();

        // Existing-account path: the shared business padlock precedes the invited account, the ficha and the
        // invitation. The invited email selects the account whose password is validated, never a ficha key.
        var account = await repository.LockAccountByEmailAsync(business.Id, target.Email, ct);
        if (account is null)
            return UnavailableAcceptance();
        if (!passwordHasher.Verify(account.PasswordHash, password))
            return Result<AcceptedClientAccount>.Failure(InvalidCredentials);
        if (!account.IsPending)
            return OwnerAssistance();

        var client = await repository.LockClientAsync(business.Id, target.ClientId, ct);
        if (client is null)
            return UnavailableAcceptance();

        var now = clock.Now;
        if (!await repository.ConsumeInvitationAsync(business.Id, target.InvitationId, now, ct))
            return UnavailableAcceptance();

        if (!await repository.BindPendingAccountAsync(business.Id, account.ClientAccountId, client.Id, now, ct))
            return OwnerAssistance();

        await transaction.CommitAsync(ct);
        return Result<AcceptedClientAccount>.Success(
            new AcceptedClientAccount(account.ClientAccountId, client.Id, target.Email, client.FirstName, client.LastName));
    }

    private static Result<ClientInvitationInspection> UnavailableInspection() =>
        Result<ClientInvitationInspection>.Failure(UnavailableCode, UnavailableMessage);

    private static Result<AcceptedClientAccount> UnavailableAcceptance() =>
        Result<AcceptedClientAccount>.Failure(UnavailableCode, UnavailableMessage);

    private static Result<AcceptedClientAccount> EmailRegistered() =>
        Result<AcceptedClientAccount>.Failure(EmailRegisteredCode, EmailRegisteredMessage);

    private static Result<AcceptedClientAccount> OwnerAssistance() =>
        Result<AcceptedClientAccount>.Failure(OwnerAssistanceCode, OwnerAssistanceMessage);

    private static Result<AcceptedClientAccount> PasswordTooShort() =>
        Result<AcceptedClientAccount>.Failure(
            "password_too_short",
            "La contraseña debe tener al menos 8 caracteres.",
            "password");
}
