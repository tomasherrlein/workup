using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.ClientAccounts;

/// <summary>
/// Acceptance of an owner-assisted invitation (RF-26, slice 2), business rules only. It proves the contract against a
/// transactional in-memory port: the invitation link proves the <em>destination</em> email even when it differs from
/// the one stored on the ficha, so that address is the account email and the account binds to the invited existing
/// ficha without creating another one. A pending, unlinked account of the invited email can sign in <em>only</em>
/// inside this flow, and only with its own password, so a wrong password leaves the link and the account untouched.
/// <para>
/// The refusal contract is closed: a link that is missing, foreign, used, expired or revoked, an account already
/// linked to another ficha, a ficha already linked by the time the invitation is consumed, and an invited email that
/// already has an account all fail without re-reading a winner, reassigning history or leaving a partial write. The
/// expiry is revalidated after the locks, the token is consumed exactly once, and the whole acceptance is one
/// transaction: when the bind or the insert fails, the in-memory model rolls the consumed token back to live.
/// </para>
/// The old T20 verification token keeps failing by its own guard once the invitation has verified and linked the
/// account. The padlock, the SQL guards and the real rollback can only be proven by a SQL fixture, which this slice
/// does not add.
/// </summary>
public sealed class ClientInvitationAcceptanceServiceTests
{
    private const string HairdresserSlug = "peluqueria";
    private const string YogaSlug = "yoga";
    private const string FichaEmail = "ana@example.com";
    private const string InvitedEmail = "nueva@example.com";
    private const string InvitationToken = "raw-invitation-token";
    private const string Password = "clave-segura";
    private static readonly DateTime AcceptedAt = new(2026, 3, 20, 10, 0, 0);

    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeClientInvitationAcceptanceRepository _repository = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeClock _clock = new() { Now = AcceptedAt };
    private readonly FakeAcceptanceUnitOfWork _unitOfWork;
    private readonly ClientInvitationAcceptanceService _service;

    public ClientInvitationAcceptanceServiceTests()
    {
        _unitOfWork = _repository.CreateUnitOfWork();
        _service = new ClientInvitationAcceptanceService(
            _businesses, _repository, _unitOfWork, _passwordHasher, _clock);
    }

    [Fact]
    public async Task Inspect_returns_only_the_invited_email_and_expiry_without_consuming()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);

        var result = await _service.InspectAsync(HairdresserSlug, InvitationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(InvitedEmail, result.Value.Email);
        Assert.Equal(AcceptedAt.AddHours(48), result.Value.ExpiresAt);
        // The GET never consumes the link and never opens a transaction.
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(AcceptedAt));
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Inspect_never_discloses_the_ficha_or_its_history()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);

        var result = await _service.InspectAsync(HairdresserSlug, InvitationToken);

        var properties = string.Join(',', typeof(ClientInvitationInspection).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Client", properties);
        Assert.DoesNotContain("Name", properties);
        Assert.DoesNotContain("Phone", properties);
        Assert.DoesNotContain("History", properties);
        Assert.DoesNotContain("Ana", result.Value.ToString());
        Assert.DoesNotContain("Gómez", result.Value.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("foreign")]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task Inspect_refuses_every_unusable_link_generically(string kind)
    {
        var (business, client) = SeedBusinessWithFicha();
        var token = InvitationToken;
        if (kind == "foreign")
        {
            var (yoga, yogaClient) = SeedBusinessWithFicha(YogaSlug);
            SeedInvitation(yoga.Id, yogaClient.Id);
        }
        else if (kind is not ("missing" or "blank"))
        {
            SeedInvitation(business.Id, client.Id,
                usedAt: kind == "used" ? AcceptedAt : null,
                revokedAt: kind == "revoked" ? AcceptedAt : null,
                expiresAt: kind == "expired" ? AcceptedAt : AcceptedAt.AddHours(48));
        }

        if (kind == "blank")
            token = "   ";

        var result = await _service.InspectAsync(HairdresserSlug, token);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Accept_new_creates_the_invited_account_linked_to_the_existing_ficha()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        Assert.True(result.Succeeded, result.Error?.Code);
        // The URL proves the destination email, which becomes the account email even though the ficha has another one.
        Assert.Equal(InvitedEmail, result.Value.Email);
        Assert.Equal(client.Id, result.Value.ClientId);
        Assert.Equal(client.FirstName, result.Value.FirstName);
        Assert.Equal(client.LastName, result.Value.LastName);

        var account = Assert.Single(_repository.Accounts);
        Assert.Equal(business.Id, account.BusinessId);
        Assert.Equal(InvitedEmail, account.Email);
        Assert.Equal(client.Id, account.ClientId);
        Assert.Equal(AcceptedAt, account.EmailVerifiedAt);
        Assert.Equal(_passwordHasher.Hash(Password), account.PasswordHash);
        Assert.NotEqual(FichaEmail, account.Email);

        // No new ficha is created and the existing one keeps its own email.
        var ficha = Assert.Single(_repository.Clients);
        Assert.Equal(client.Id, ficha.Id);
        Assert.Equal(FichaEmail, ficha.Email);

        Assert.Equal(AcceptedAt, Assert.Single(_repository.Invitations).UsedAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Accept_new_requires_a_password_of_at_least_eight_characters()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, "corta");

        Assert.False(result.Succeeded);
        Assert.Equal("password_too_short", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Null(Assert.Single(_repository.Invitations).UsedAt);
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task Accept_new_refuses_an_email_that_already_has_an_account_without_consuming_the_link()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password));

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("email_already_registered", result.Error!.Code);
        Assert.Single(_repository.Accounts);
        Assert.Null(Assert.Single(_repository.Invitations).UsedAt);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Accept_new_refuses_a_ficha_that_is_already_linked_when_the_invitation_is_consumed()
    {
        // Documented issuer race (slice 1): the linked-account precheck runs before the Clients row lock, so the
        // invitation can be stale. Acceptance must fail closed instead of binding a second account to the ficha.
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        _repository.SeedVerifiedAccount(business.Id, "otra@example.com", _passwordHasher.Hash(Password), client.Id, AcceptedAt);

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_owner_assistance", result.Error!.Code);
        Assert.Contains("Contactá al negocio", result.Error.Message, StringComparison.Ordinal);
        Assert.Single(_repository.Accounts);
        Assert.Null(Assert.Single(_repository.Invitations).UsedAt);
    }

    [Fact]
    public async Task Accept_existing_binds_a_pending_account_with_the_correct_password()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        var pending = _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password));

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password);

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(pending.Id, result.Value.ClientAccountId);
        Assert.Equal(client.Id, result.Value.ClientId);
        Assert.Equal(InvitedEmail, result.Value.Email);
        Assert.Equal(client.FirstName, result.Value.FirstName);

        Assert.Equal(client.Id, pending.Account.ClientId);
        Assert.Equal(AcceptedAt, pending.Account.EmailVerifiedAt);
        Assert.Null(pending.PendingFirstName);
        Assert.Null(pending.PendingLastName);
        Assert.Equal(AcceptedAt, Assert.Single(_repository.Invitations).UsedAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Accept_existing_with_a_wrong_password_leaves_the_link_and_the_account_untouched()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        var pending = _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password));

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, "otra-clave-larga");

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_credentials", result.Error!.Code);
        Assert.Null(pending.Account.ClientId);
        Assert.Null(pending.Account.EmailVerifiedAt);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(AcceptedAt));
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Accept_existing_signs_in_with_the_stored_password_without_a_new_minimum()
    {
        // The 8-character minimum is a rule for a new password; an existing account only proves its own.
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash("corta"));

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, "corta");

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(client.Id, result.Value.ClientId);
    }

    [Fact]
    public async Task Accept_existing_refuses_an_account_linked_to_another_ficha_generically()
    {
        var (business, client) = SeedBusinessWithFicha();
        var otherFicha = _repository.SeedClient(business.Id, "Beto", "Pérez", "beto@example.com");
        SeedInvitation(business.Id, client.Id);
        var linked = _repository.SeedVerifiedAccount(
            business.Id, InvitedEmail, _passwordHasher.Hash(Password), otherFicha.Id, AcceptedAt);

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_owner_assistance", result.Error!.Code);
        Assert.Contains("Contactá al negocio", result.Error.Message, StringComparison.Ordinal);
        // Nothing is reassigned and nothing about the other ficha is revealed.
        Assert.Equal(otherFicha.Id, linked.Account.ClientId);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(AcceptedAt));
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Accept_existing_does_not_reveal_a_linked_account_without_its_password()
    {
        var (business, client) = SeedBusinessWithFicha();
        var otherFicha = _repository.SeedClient(business.Id, "Beto", "Pérez", "beto@example.com");
        SeedInvitation(business.Id, client.Id);
        _repository.SeedVerifiedAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password), otherFicha.Id, AcceptedAt);

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, "clave-incorrecta");

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_credentials", result.Error!.Code);
        Assert.Null(Assert.Single(_repository.Invitations).UsedAt);
    }

    [Fact]
    public async Task Accept_existing_refuses_an_account_already_linked_to_the_invited_ficha_generically()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        var linked = _repository.SeedVerifiedAccount(
            business.Id, InvitedEmail, _passwordHasher.Hash(Password), client.Id, AcceptedAt);

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_owner_assistance", result.Error!.Code);
        Assert.Equal(client.Id, linked.Account.ClientId);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(AcceptedAt));
    }

    [Fact]
    public async Task Accept_existing_refuses_a_verified_account_that_is_not_linked_generically()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        _repository.SeedAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password), null, AcceptedAt);

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_owner_assistance", result.Error!.Code);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(AcceptedAt));
    }

    [Fact]
    public async Task Accept_existing_refuses_when_the_invited_email_has_no_account()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(AcceptedAt));
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Theory]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task Accept_refuses_a_used_expired_or_revoked_link_generically(string kind)
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id,
            usedAt: kind == "used" ? AcceptedAt : null,
            revokedAt: kind == "revoked" ? AcceptedAt : null,
            expiresAt: kind == "expired" ? AcceptedAt : AcceptedAt.AddHours(48));

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task Accept_refuses_a_foreign_business_token_without_touching_the_other_business()
    {
        var (yoga, yogaClient) = SeedBusinessWithFicha(YogaSlug);
        SeedInvitation(yoga.Id, yogaClient.Id);
        SeedBusinessWithFicha();

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(AcceptedAt));
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task Accept_revalidates_the_expiry_after_waiting_for_the_locks()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        // The clock crosses the expiry while the acceptance is waiting for the ficha lock, so the usable check has to
        // read the clock after the locks, not before them.
        _repository.WhileWaitingForClientLock = () => _clock.Now = AcceptedAt.AddHours(48);

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        Assert.Null(Assert.Single(_repository.Invitations).UsedAt);
        Assert.Empty(_repository.Accounts);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Accept_new_rolls_back_and_keeps_the_link_live_when_the_account_insert_fails()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        _repository.FailVerifiedAccountInsert = true;

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        // The transaction rolled back, so the consumed token is live again and nothing was inserted.
        Assert.Null(Assert.Single(_repository.Invitations).UsedAt);
        Assert.Empty(_repository.Accounts);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Accept_existing_rolls_back_and_keeps_the_link_live_when_the_bind_fails()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        var pending = _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password));
        _repository.FailBind = true;

        var result = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        Assert.Null(pending.Account.ClientId);
        Assert.Null(pending.Account.EmailVerifiedAt);
        Assert.Null(Assert.Single(_repository.Invitations).UsedAt);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task A_consumed_invitation_cannot_be_replayed()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        Assert.True((await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password)).Succeeded);

        var replay = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        Assert.False(replay.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", replay.Error!.Code);
        Assert.Single(_repository.Accounts);
    }

    [Fact]
    public async Task Two_simultaneous_acceptances_of_one_token_leave_exactly_one_winner()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password));
        _repository.SynchronizeNextTwoInvitationConsumes();

        var results = await Task.WhenAll(
            _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password),
            _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => !result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", results.Single(result => !result.Succeeded).Error!.Code);
        var invitation = Assert.Single(_repository.Invitations);
        Assert.Equal(AcceptedAt, invitation.UsedAt);
        Assert.Equal(client.Id, Assert.Single(_repository.Accounts).Account.ClientId);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task An_accepted_invitation_makes_the_old_verification_token_unusable_by_its_own_guard()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);
        var pending = _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password));
        _repository.SeedEmailVerificationToken(pending.Id, "t20-verify-token", AcceptedAt.AddHours(48));

        var accepted = await _service.AcceptExistingAsync(HairdresserSlug, InvitationToken, Password);

        Assert.True(accepted.Succeeded, accepted.Error?.Code);
        Assert.Equal(client.Id, accepted.Value.ClientId);
        // The T20 consume guards on a still-pending, still-unlinked account, so the old link now matches nothing.
        Assert.Null(_repository.ConsumeEmailVerificationToken(
            SecureToken.Hash("t20-verify-token"), business.Id, AcceptedAt));
    }

    [Fact]
    public async Task Accepted_receipt_never_carries_a_token_password_or_session()
    {
        var (business, client) = SeedBusinessWithFicha();
        SeedInvitation(business.Id, client.Id);

        var result = await _service.AcceptNewAsync(HairdresserSlug, InvitationToken, Password);

        var properties = string.Join(',', typeof(AcceptedClientAccount).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Token", properties);
        Assert.DoesNotContain("Password", properties);
        Assert.DoesNotContain("Hash", properties);
        Assert.DoesNotContain("Session", properties);
        Assert.DoesNotContain(InvitationToken, result.Value.ToString());
        Assert.DoesNotContain(Password, result.Value.ToString());
    }

    private (Business Business, Client Client) SeedBusinessWithFicha(string slug = HairdresserSlug)
    {
        var business = new Business
        {
            Name = slug == YogaSlug ? "Estudio de Yoga" : "Peluquería Marina",
            Slug = slug,
            ContactPhone = "1145550000",
            CreatedAt = AcceptedAt
        };
        _businesses.InsertAsync(business).GetAwaiter().GetResult();
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", FichaEmail);
        return (business, client);
    }

    private AccountInvitation SeedInvitation(
        int businessId,
        int clientId,
        string email = InvitedEmail,
        DateTime? expiresAt = null,
        DateTime? usedAt = null,
        DateTime? revokedAt = null) =>
        _repository.SeedInvitation(
            businessId, clientId, email, SecureToken.Hash(InvitationToken),
            expiresAt ?? AcceptedAt.AddHours(48), usedAt, revokedAt);
}
