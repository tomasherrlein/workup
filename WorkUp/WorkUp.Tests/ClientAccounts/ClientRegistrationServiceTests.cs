using System.Net;
using System.Text.RegularExpressions;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.ClientAccounts;

public sealed class ClientRegistrationServiceTests
{
    private const string HairdresserSlug = "peluqueria";
    private const string YogaSlug = "yoga";
    private static readonly DateTime RegisteredAt = new(2026, 3, 10, 9, 0, 0);

    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeClientRegistrationRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeClock _clock = new() { Now = RegisteredAt };
    private readonly ClientRegistrationService _service;

    public ClientRegistrationServiceTests()
    {
        _service = new ClientRegistrationService(
            _businesses,
            _repository,
            _unitOfWork,
            _passwordHasher,
            new TrustedUrlBuilder(),
            _clock);
    }

    [Fact]
    public async Task Registration_stores_a_pending_account_with_the_password_hash_and_no_ficha()
    {
        var business = SeedBusiness(HairdresserSlug);

        var result = await RegisterAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("ana@example.com", result.Value.Email);
        Assert.Equal(RegisteredAt.AddHours(48), result.Value.VerificationExpiresAt);
        var account = Assert.Single(_repository.Accounts);
        Assert.Equal(business.Id, account.BusinessId);
        Assert.Null(account.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        Assert.False(account.Account.IsActive);
        Assert.Equal("hashed:secreto-123", account.PasswordHash);
        Assert.NotEqual("secreto-123", account.PasswordHash);
        Assert.Equal("Ana", account.PendingFirstName);
        Assert.Equal("Gómez", account.PendingLastName);
        Assert.Empty(_repository.Clients);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Registration_never_exposes_the_token_or_a_session_in_its_result()
    {
        SeedBusiness(HairdresserSlug);

        var result = await RegisterAsync();

        var properties = string.Join(',', typeof(ClientRegistrationReceipt).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Token", properties);
        Assert.DoesNotContain("Session", properties);
        Assert.DoesNotContain("Claims", properties);
        Assert.DoesNotContain(TokenFrom(Assert.Single(_repository.Notifications)), result.Value.ToString());
    }

    [Fact]
    public void Registration_request_has_no_business_id_and_the_business_comes_only_from_the_slug()
    {
        var properties = string.Join(',', typeof(RegisterClientAccountRequest).GetProperties().Select(p => p.Name));

        Assert.Contains("Slug", properties);
        Assert.DoesNotContain("Business", properties);
    }

    [Fact]
    public async Task Registration_rejects_an_unknown_slug_without_writing()
    {
        var result = await RegisterAsync(Request(slug: "negocio-inexistente"));

        Assert.False(result.Succeeded);
        Assert.Equal("business_not_found", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task Registration_rejects_an_email_already_used_by_a_ficha_of_the_same_business()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        var result = await RegisterAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("email_already_registered", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Accounts);
        Assert.Single(_repository.Clients);
    }

    [Fact]
    public async Task Registration_rejects_an_email_already_used_by_an_account_of_the_same_business()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedVerifiedAccount(business.Id, "ana@example.com", "hashed:otra", RegisteredAt.AddDays(-2));

        var result = await RegisterAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("email_already_registered", result.Error!.Code);
        Assert.Single(_repository.Accounts);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task The_same_email_can_register_in_two_different_businesses_and_a_token_stays_in_its_tenant()
    {
        var hairdresser = SeedBusiness(HairdresserSlug, "Peluquería Marina");
        var yoga = SeedBusiness(YogaSlug, "Estudio de Yoga");

        Assert.True((await RegisterAsync(Request(slug: HairdresserSlug))).Succeeded);
        Assert.True((await RegisterAsync(Request(slug: YogaSlug))).Succeeded);

        Assert.Equal(2, _repository.Accounts.Count);
        Assert.Equal(
            new[] { hairdresser.Id, yoga.Id },
            _repository.Accounts.Select(account => account.BusinessId).OrderBy(id => id).ToArray());

        var yogaToken = TokenFrom(_repository.Notifications.Single(notice => notice.BusinessId == yoga.Id));
        var foreign = await _service.VerifyEmailAsync(HairdresserSlug, yogaToken);

        Assert.False(foreign.Succeeded);
        Assert.Empty(_repository.Clients);
        Assert.All(_repository.Accounts, account => Assert.Null(account.EmailVerifiedAt));
    }

    [Theory]
    [InlineData("", "Gómez", "ana@example.com", "secreto-123", "client_first_name_required")]
    [InlineData("Ana", " ", "ana@example.com", "secreto-123", "client_last_name_required")]
    [InlineData("Ana", "Gómez", "", "secreto-123", "client_email_required")]
    [InlineData("Ana", "Gómez", "ana-sin-arroba", "secreto-123", "invalid_client_email")]
    [InlineData("Ana", "Gómez", "ana@example.com", "corta", "password_too_short")]
    public async Task Registration_rejects_invalid_input_before_opening_a_transaction(
        string firstName, string lastName, string email, string password, string expectedCode)
    {
        SeedBusiness(HairdresserSlug);

        var result = await RegisterAsync(Request(
            firstName: firstName, lastName: lastName, email: email, password: password));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Accounts);
        Assert.Empty(_repository.Tokens);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Registration_accepts_a_password_of_exactly_eight_characters()
    {
        SeedBusiness(HairdresserSlug);

        var result = await RegisterAsync(Request(password: "ocho-car"));

        Assert.True(result.Succeeded);
        Assert.Single(_repository.Accounts);
    }

    [Fact]
    public async Task Registration_stores_only_the_sha256_hash_of_the_token_and_queues_the_raw_token_once()
    {
        SeedBusiness(HairdresserSlug);

        await RegisterAsync();

        var token = Assert.Single(_repository.Tokens);
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        Assert.Equal(AccountTokenPurpose.EmailVerification, token.Purpose);
        Assert.Equal(SecureToken.Hash(rawToken), token.TokenHash);
        Assert.Equal(32, token.TokenHash.Length);
        Assert.Null(token.UsedAt);
        Assert.Equal(RegisteredAt.AddHours(48), token.ExpiresAt);
        Assert.DoesNotContain(rawToken, Convert.ToHexString(token.TokenHash));
        Assert.DoesNotContain(rawToken, _repository.Accounts.Single().PasswordHash);
        Assert.Contains(rawToken, _repository.Notifications.Single().BodyHtml);
    }

    [Fact]
    public async Task Registration_queues_one_email_verification_notice_with_a_link_from_the_trusted_base_url()
    {
        var business = SeedBusiness(HairdresserSlug, "Peluquería Marina");

        await RegisterAsync();

        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal(business.Id, notification.BusinessId);
        Assert.Equal(NotificationType.EmailVerification, notification.Type);
        Assert.Equal("ana@example.com", notification.Recipient);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        // The dedup key is bound to the token hash, never to the raw token nor to the issuing second alone.
        Assert.Equal(
            ClientRegistrationService.VerificationDedupKey(1, Assert.Single(_repository.Tokens).TokenHash),
            notification.DedupKey);
        Assert.DoesNotContain(TokenFrom(notification), notification.DedupKey);
        Assert.StartsWith("https://reservas.example.com/r/peluqueria/cuenta/verificar/", LinkFrom(notification));
        Assert.Equal("Confirmá tu cuenta en Peluquería Marina", notification.Subject);
        Assert.Contains(WebUtility.HtmlEncode("Peluquería Marina"), notification.BodyHtml);
    }

    [Fact]
    public void Verification_link_rules_build_only_from_the_configured_trusted_base_url()
    {
        Assert.Equal(
            "https://reservas.example.com/r/peluqueria/cuenta/verificar/opaque-token",
            ClientAccountUrlRules.BuildVerificationUrl("https://reservas.example.com", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildVerificationUrl("http://reservas.example.com", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildVerificationUrl("", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildVerificationUrl("https://reservas.example.com", "peluqueria", ""));
    }

    [Fact]
    public async Task A_retried_notice_of_the_same_token_is_deduplicated_without_failing_the_registration()
    {
        SeedBusiness(HairdresserSlug);
        // The retry re-enqueues the exact notice of the token the registration is about to create, so the token-bound
        // key collides and the duplicate must be ignored instead of treated as a failure.
        _repository.BeforeNotificationInsert = notice => _repository.SeedQueuedNotification(notice.DedupKey);

        var result = await RegisterAsync();

        Assert.True(result.Succeeded);
        Assert.Single(_repository.Notifications);
        Assert.Single(_repository.Accounts);
        Assert.Single(_repository.Tokens);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Two_simultaneous_registrations_with_the_same_email_create_exactly_one_account()
    {
        SeedBusiness(HairdresserSlug);
        _repository.SynchronizeNextTwoEmailStateReads();

        var results = await Task.WhenAll(_service.RegisterAsync(Request()), _service.RegisterAsync(Request()));

        Assert.Single(results, result => result.Succeeded);
        Assert.Equal("email_already_registered", results.Single(result => !result.Succeeded).Error!.Code);
        Assert.Single(_repository.Accounts);
        Assert.Single(_repository.Tokens);
        Assert.Single(_repository.Notifications);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Verification_creates_the_ficha_with_the_pending_names_and_activates_the_account()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        _clock.Now = RegisteredAt.AddHours(2);

        var result = await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        Assert.True(result.Succeeded);
        var client = Assert.Single(_repository.Clients);
        Assert.Equal("Ana", client.FirstName);
        Assert.Equal("Gómez", client.LastName);
        Assert.Equal("ana@example.com", client.Email);
        Assert.Equal(client.Id, result.Value.ClientId);
        Assert.Equal(client.Id, Assert.Single(_repository.Accounts).ClientId);
        Assert.Equal(_clock.Now, Assert.Single(_repository.Accounts).EmailVerifiedAt);
        Assert.True(Assert.Single(_repository.Accounts).Account.IsActive);
        // One commit for the registration and one for the verification.
        Assert.Equal(2, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Verification_clears_the_pending_names_of_the_activated_account()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));

        await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        var account = Assert.Single(_repository.Accounts);
        Assert.Null(account.PendingFirstName);
        Assert.Null(account.PendingLastName);
    }

    [Fact]
    public async Task Verification_keeps_the_account_inactive_until_the_email_is_confirmed()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));

        var pending = Assert.Single(_repository.Accounts);
        Assert.False(pending.Account.IsActive);
        Assert.Null(pending.EmailVerifiedAt);

        Assert.True((await _service.VerifyEmailAsync(HairdresserSlug, rawToken)).Succeeded);
        Assert.True(Assert.Single(_repository.Accounts).Account.IsActive);
    }

    [Fact]
    public async Task Verification_rejects_a_replayed_token_without_a_second_ficha()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        Assert.True((await _service.VerifyEmailAsync(HairdresserSlug, rawToken)).Succeeded);

        var replayed = await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        Assert.False(replayed.Succeeded);
        Assert.Equal("verification_link_unavailable", replayed.Error!.Code);
        Assert.Single(_repository.Clients);
        Assert.Equal(2, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        Assert.NotNull(Assert.Single(_repository.Tokens).UsedAt);
    }

    [Fact]
    public async Task Verification_accepts_the_link_one_tick_before_expiry()
    {
        SeedBusiness(HairdresserSlug);
        var receipt = (await RegisterAsync()).Value;
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        _clock.Now = receipt.VerificationExpiresAt.AddTicks(-1);

        var result = await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        Assert.True(result.Succeeded);
        Assert.Single(_repository.Clients);
    }

    [Fact]
    public async Task Verification_rejects_the_link_at_the_exact_expiry_instant_without_consuming_it()
    {
        SeedBusiness(HairdresserSlug);
        var receipt = (await RegisterAsync()).Value;
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        _clock.Now = receipt.VerificationExpiresAt;

        var result = await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        Assert.False(result.Succeeded);
        Assert.Empty(_repository.Clients);
        Assert.Null(Assert.Single(_repository.Accounts).EmailVerifiedAt);
        Assert.Null(Assert.Single(_repository.Tokens).UsedAt);
        // Only the registration committed: the rejected link wrote nothing.
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Verification_returns_one_generic_error_for_every_unusable_link()
    {
        var business = SeedBusiness(HairdresserSlug);
        SeedBusiness(YogaSlug, "Estudio de Yoga");
        var receipt = (await RegisterAsync()).Value;
        var commitsAfterRegistration = _unitOfWork.CommitCount;
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));

        var errors = new List<BusinessError?>
        {
            (await _service.VerifyEmailAsync(HairdresserSlug, null)).Error,
            (await _service.VerifyEmailAsync(HairdresserSlug, "   ")).Error,
            (await _service.VerifyEmailAsync(HairdresserSlug, "token-inexistente")).Error,
            (await _service.VerifyEmailAsync(YogaSlug, rawToken)).Error,
            (await _service.VerifyEmailAsync("negocio-inexistente", rawToken)).Error
        };
        _clock.Now = receipt.VerificationExpiresAt;
        errors.Add((await _service.VerifyEmailAsync(HairdresserSlug, rawToken)).Error);

        Assert.All(errors, error => Assert.Equal(errors[0], error));
        Assert.All(errors, error => Assert.Equal("verification_link_unavailable", error!.Code));
        Assert.Empty(_repository.Clients);
        Assert.Equal(commitsAfterRegistration, _unitOfWork.CommitCount);
        Assert.All(_repository.Accounts.Where(account => account.BusinessId == business.Id),
            account => Assert.Null(account.EmailVerifiedAt));
    }

    [Fact]
    public async Task Verification_rejects_a_password_reset_token_of_the_same_account()
    {
        var business = SeedBusiness(HairdresserSlug);
        var account = _repository.SeedPendingAccount(
            business.Id, "ana@example.com", "hashed:secreto-123", "Ana", "Gómez", RegisteredAt);
        _repository.SeedToken(account.Id, AccountTokenPurpose.PasswordReset, "reset-token",
            RegisteredAt.AddMinutes(30), RegisteredAt);

        var result = await _service.VerifyEmailAsync(HairdresserSlug, "reset-token");

        Assert.False(result.Succeeded);
        Assert.Empty(_repository.Clients);
        Assert.Null(account.EmailVerifiedAt);
    }

    [Fact]
    public async Task Verification_never_creates_a_ficha_without_the_pending_names()
    {
        var business = SeedBusiness(HairdresserSlug);
        var account = _repository.SeedPendingAccount(
            business.Id, "ana@example.com", "hashed:secreto-123", null, null, RegisteredAt);
        _repository.SeedToken(account.Id, AccountTokenPurpose.EmailVerification, "sin-nombres",
            RegisteredAt.AddHours(48), RegisteredAt);

        var result = await _service.VerifyEmailAsync(HairdresserSlug, "sin-nombres");

        Assert.False(result.Succeeded);
        Assert.Empty(_repository.Clients);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Verification_refuses_a_ficha_created_after_registration_instead_of_attaching_the_account()
    {
        var business = SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        var existing = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        var result = await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        Assert.False(result.Succeeded);
        Assert.Equal("client_record_needs_owner_assistance", result.Error!.Code);
        // The refusal returns no ficha data: the history of the ficha that owns the email is never disclosed.
        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Equal(existing.Id, Assert.Single(_repository.Clients).Id);
        var account = Assert.Single(_repository.Accounts);
        Assert.Null(account.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        Assert.False(account.Account.IsActive);
        // Only the registration committed: the refused verification rolled back without linking anything.
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Verification_fails_closed_when_the_ficha_insert_loses_the_email_race()
    {
        var business = SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        Client? committedByConcurrentBooking = null;
        _repository.BeforeClientInsert = () =>
            committedByConcurrentBooking = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        var result = await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        Assert.False(result.Succeeded);
        Assert.Equal("client_record_needs_owner_assistance", result.Error!.Code);
        Assert.NotNull(committedByConcurrentBooking);
        Assert.Equal(committedByConcurrentBooking!.Id, Assert.Single(_repository.Clients).Id);
        var account = Assert.Single(_repository.Accounts);
        Assert.Null(account.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        Assert.False(account.Account.IsActive);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        // Fake limitation: an in-memory store cannot undo the consumed hash, so the token stays marked used here.
        // Only a SQL fixture proves that the rollback also returns the token to its usable state.
        Assert.NotNull(Assert.Single(_repository.Tokens).UsedAt);
    }

    [Fact]
    public async Task Verification_creates_the_ficha_when_only_another_business_has_that_email()
    {
        var hairdresser = SeedBusiness(HairdresserSlug);
        var yoga = SeedBusiness(YogaSlug, "Estudio de Yoga");
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        // Regla 7 identifies the client by email inside one business: a ficha of another tenant must not block the
        // verification, otherwise a stranger's booking elsewhere would lock this account out.
        _repository.SeedClient(yoga.Id, "Ana", "Gómez", "ana@example.com");

        var result = await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        Assert.True(result.Succeeded);
        var created = Assert.Single(_repository.Clients, client => client.BusinessId == hairdresser.Id);
        Assert.Equal(created.Id, result.Value.ClientId);
        Assert.Equal(created.Id, Assert.Single(_repository.Accounts).ClientId);
        Assert.Equal(2, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task A_refused_verification_leaves_the_pending_account_available_for_owner_assistance()
    {
        var business = SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");
        Assert.False((await _service.VerifyEmailAsync(HairdresserSlug, rawToken)).Succeeded);

        // The refusal must not destroy the pending account: the person still needs a way to reach the business and
        // the owner-assisted invitation of T22 has to find an account that is neither linked nor verified.
        var resend = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest(HairdresserSlug, "ana@example.com"));

        Assert.True(resend.Succeeded);
        Assert.Equal(2, _repository.Notifications.Count);
        Assert.Single(_repository.Clients);
        var account = Assert.Single(_repository.Accounts);
        Assert.Null(account.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        Assert.False(account.Account.IsActive);
    }

    [Fact]
    public async Task Two_simultaneous_verifications_activate_one_account_and_create_one_ficha()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        _repository.SynchronizeNextTwoTokenConsumes();

        var results = await Task.WhenAll(
            _service.VerifyEmailAsync(HairdresserSlug, rawToken),
            _service.VerifyEmailAsync(HairdresserSlug, rawToken));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(_repository.Clients);
        Assert.Single(_repository.Accounts);
        Assert.Equal(2, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Verification_does_not_commit_when_the_ficha_cannot_be_created()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        _repository.FailClientInsert = true;

        var result = await _service.VerifyEmailAsync(HairdresserSlug, rawToken);

        Assert.False(result.Succeeded);
        Assert.Empty(_repository.Clients);
        Assert.Null(Assert.Single(_repository.Accounts).EmailVerifiedAt);
        // Only the registration committed: the transaction of the failed verification rolled back.
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        // Fake limitation: an in-memory store cannot undo the consumed hash, so the token stays marked used here.
        // Only a SQL fixture proves that the rollback also returns the token to its usable state.
        Assert.NotNull(Assert.Single(_repository.Tokens).UsedAt);
    }

    [Fact]
    public async Task Resend_returns_the_same_generic_response_for_an_unknown_email_and_for_a_verified_account()
    {
        var business = SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        Assert.True((await _service.VerifyEmailAsync(HairdresserSlug, rawToken)).Succeeded);
        var notificationsAfterVerification = _repository.Notifications.Count;
        var tokensAfterVerification = _repository.Tokens.Count;

        var relatedToUnknownEmail = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest(HairdresserSlug, "nadie@example.com"));
        var relatedToVerifiedAccount = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest(HairdresserSlug, "ana@example.com"));

        Assert.True(relatedToUnknownEmail.Succeeded);
        Assert.True(relatedToVerifiedAccount.Succeeded);
        Assert.Equal(relatedToUnknownEmail.Error, relatedToVerifiedAccount.Error);
        Assert.Null(relatedToVerifiedAccount.Error);
        Assert.Equal(notificationsAfterVerification, _repository.Notifications.Count);
        Assert.Equal(tokensAfterVerification, _repository.Tokens.Count);
        Assert.Equal(1, _repository.Accounts.Count(account => account.BusinessId == business.Id));
    }

    [Fact]
    public async Task Resend_rotates_the_token_so_the_previous_link_stops_working()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var firstToken = TokenFrom(Assert.Single(_repository.Notifications));
        _clock.Now = RegisteredAt.AddHours(3);

        var resend = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest(HairdresserSlug, "ana@example.com"));

        Assert.True(resend.Succeeded);
        Assert.Equal(2, _repository.Notifications.Count);
        Assert.Equal(2, _repository.Tokens.Count);
        Assert.NotEqual(
            _repository.Notifications[0].DedupKey,
            _repository.Notifications[1].DedupKey);
        var rotatedToken = TokenFrom(_repository.Notifications[1]);
        Assert.NotEqual(firstToken, rotatedToken);

        var obsolete = await _service.VerifyEmailAsync(HairdresserSlug, firstToken);
        Assert.False(obsolete.Succeeded);
        Assert.True((await _service.VerifyEmailAsync(HairdresserSlug, rotatedToken)).Succeeded);
        Assert.Single(_repository.Clients);
    }

    [Fact]
    public async Task Resend_in_the_same_second_as_the_previous_notice_still_delivers_the_rotated_token()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var firstToken = TokenFrom(Assert.Single(_repository.Notifications));
        Assert.Equal(RegisteredAt, _clock.Now);

        // Without advancing the clock, the registration and the resend share the issuing second: the rotated token
        // must still reach the outbox, because the first link stopped working at the rotation.
        var resend = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest(HairdresserSlug, "ana@example.com"));

        Assert.True(resend.Succeeded);
        Assert.Equal(2, _repository.Notifications.Count);
        Assert.Equal(2, _repository.Tokens.Count);
        Assert.NotEqual(_repository.Notifications[0].DedupKey, _repository.Notifications[1].DedupKey);
        var rotatedToken = TokenFrom(_repository.Notifications[1]);
        Assert.NotEqual(firstToken, rotatedToken);
        Assert.True((await _service.VerifyEmailAsync(HairdresserSlug, rotatedToken)).Succeeded);
        Assert.False((await _service.VerifyEmailAsync(HairdresserSlug, firstToken)).Succeeded);
        Assert.Single(_repository.Clients);
    }

    [Fact]
    public async Task Resend_rescues_a_pending_account_whose_verification_expired()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        var expiredToken = TokenFrom(Assert.Single(_repository.Notifications));
        _clock.Now = RegisteredAt.AddHours(49);
        Assert.False((await _service.VerifyEmailAsync(HairdresserSlug, expiredToken)).Succeeded);

        var resend = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest(HairdresserSlug, "ana@example.com"));

        Assert.True(resend.Succeeded);
        var rescueToken = TokenFrom(_repository.Notifications[1]);
        _clock.Now = RegisteredAt.AddHours(50);
        var rescued = await _service.VerifyEmailAsync(HairdresserSlug, rescueToken);

        Assert.True(rescued.Succeeded);
        var client = Assert.Single(_repository.Clients);
        Assert.Equal("Ana", client.FirstName);
        Assert.True(Assert.Single(_repository.Accounts).Account.IsActive);
    }

    [Fact]
    public async Task Resend_rejects_an_unknown_slug_without_writing()
    {
        var result = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest("negocio-inexistente", "ana@example.com"));

        Assert.False(result.Succeeded);
        Assert.Equal("business_not_found", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Resend_stays_generic_and_writes_nothing_when_the_account_stops_being_pending()
    {
        SeedBusiness(HairdresserSlug);
        await RegisterAsync();
        _repository.FailTokenRotation = true;

        var result = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest(HairdresserSlug, "ana@example.com"));

        Assert.True(result.Succeeded);
        Assert.Single(_repository.Tokens);
        Assert.Single(_repository.Notifications);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Resend_never_reveals_whether_the_email_is_registered_in_another_business()
    {
        var hairdresser = SeedBusiness(HairdresserSlug);
        SeedBusiness(YogaSlug, "Estudio de Yoga");
        await RegisterAsync(Request(slug: HairdresserSlug));

        var otherTenant = await _service.ResendVerificationAsync(
            new ResendClientVerificationRequest(YogaSlug, "ana@example.com"));

        Assert.True(otherTenant.Succeeded);
        Assert.Single(_repository.Notifications);
        Assert.Single(_repository.Tokens);
        Assert.Equal(1, _repository.Accounts.Count(account => account.BusinessId == hairdresser.Id));
    }

    [Fact]
    public async Task Registration_does_not_commit_a_pending_account_without_its_token()
    {
        SeedBusiness(HairdresserSlug);
        _repository.FailTokenInsert = true;

        var result = await RegisterAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("registration_unavailable", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        Assert.Empty(_repository.Notifications);
    }

    private Business SeedBusiness(string slug, string name = "Peluquería Marina")
    {
        var business = new Business { Name = name, Slug = slug, ContactPhone = "1145550000", CreatedAt = RegisteredAt };
        _businesses.InsertAsync(business).GetAwaiter().GetResult();
        return business;
    }

    private static RegisterClientAccountRequest Request(
        string slug = HairdresserSlug,
        string firstName = "Ana",
        string lastName = "Gómez",
        string email = "ana@example.com",
        string password = "secreto-123") => new(slug, firstName, lastName, email, password);

    private Task<Result<ClientRegistrationReceipt>> RegisterAsync(RegisterClientAccountRequest request) =>
        _service.RegisterAsync(request);

    private Task<Result<ClientRegistrationReceipt>> RegisterAsync() => RegisterAsync(Request());

    private static string LinkFrom(Notification notification)
    {
        var match = Regex.Match(notification.BodyHtml, "href=\"([^\"]+)\"");
        Assert.True(match.Success, "The verification email must contain a link.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string TokenFrom(Notification notification)
    {
        var link = LinkFrom(notification);
        const string marker = "/cuenta/verificar/";
        var index = link.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The verification email must link to the account verification route.");
        return link[(index + marker.Length)..];
    }

    private sealed class TrustedUrlBuilder : IClientAccountUrlBuilder
    {
        private const string BaseUrl = "https://reservas.example.com";

        public string BuildVerificationUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildVerificationUrl(BaseUrl, slug, token);

        public string BuildPasswordResetUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildPasswordResetUrl(BaseUrl, slug, token);
    }
}
