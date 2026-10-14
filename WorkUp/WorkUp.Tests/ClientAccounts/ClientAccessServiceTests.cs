using System.Net;
using System.Text.RegularExpressions;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.ClientAccounts;

public sealed class ClientAccessServiceTests
{
    private const string HairdresserSlug = "peluqueria";
    private const string YogaSlug = "yoga";
    private const string Email = "ana@example.com";
    private const string PlainPassword = "secreto-123";
    private const string NewPassword = "nueva-clave";
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeClientAccessRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeClock _clock = new() { Now = Now };
    private readonly ClientAccessService _service;

    public ClientAccessServiceTests()
    {
        _service = new ClientAccessService(
            _businesses,
            _repository,
            _unitOfWork,
            _passwordHasher,
            new TrustedUrlBuilder(),
            _clock);
    }

    // --- Sign-in (RF-15) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_returns_the_business_and_client_ids_of_a_verified_account()
    {
        var business = SeedBusiness(HairdresserSlug, "Peluquería Marina");
        var account = _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        var result = await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, Email, PlainPassword));

        Assert.True(result.Succeeded);
        Assert.Equal(business.Id, result.Value.BusinessId);
        Assert.Equal(HairdresserSlug, result.Value.BusinessSlug);
        Assert.Equal("Peluquería Marina", result.Value.BusinessName);
        Assert.Equal(account.Id, result.Value.ClientAccountId);
        Assert.Equal(account.ClientId, result.Value.ClientId);
        Assert.Equal(Email, result.Value.Email);
        // Sign-in is a pure read: it opens no transaction and writes nothing.
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Tokens);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public void Login_result_never_carries_a_password_a_hash_or_a_token()
    {
        var properties = typeof(ClientSession).GetProperties().Select(property => property.Name).ToArray();

        Assert.Contains("BusinessId", properties);
        Assert.Contains("ClientAccountId", properties);
        Assert.Contains("ClientId", properties);
        Assert.DoesNotContain(properties, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Login_request_has_no_business_id_and_the_business_comes_only_from_the_slug()
    {
        var properties = typeof(ClientLoginRequest).GetProperties().Select(property => property.Name).ToArray();

        Assert.Contains("Slug", properties);
        Assert.DoesNotContain("Business", properties);
    }

    [Fact]
    public async Task Login_accepts_the_email_with_other_case_and_surrounding_spaces()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        var result = await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, "  Ana@Example.COM ", PlainPassword));

        Assert.True(result.Succeeded);
        Assert.Equal(Email, result.Value.Email);
    }

    [Fact]
    public async Task Login_rejects_an_unknown_slug_without_reading_or_writing()
    {
        SeedBusiness(HairdresserSlug);

        var result = await _service.LoginAsync(new ClientLoginRequest("negocio-inexistente", Email, PlainPassword));

        Assert.False(result.Succeeded);
        Assert.Equal("business_not_found", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Login_returns_the_same_generic_error_for_absent_wrong_password_pending_and_unlinked()
    {
        var business = SeedBusiness(HairdresserSlug);
        var verified = _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        var pending = _repository.SeedPendingAccount(business.Id, "pendiente@example.com", "hashed:secreto-123", Now);
        var unlinked = _repository.SeedUnlinkedAccount(business.Id, "sin-ficha@example.com", "hashed:secreto-123", Now);

        var failures = new List<Result<ClientSession>>
        {
            await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, "nadie@example.com", PlainPassword)),
            await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, Email, "contrasena-mala")),
            await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, "pendiente@example.com", PlainPassword)),
            await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, "sin-ficha@example.com", PlainPassword))
        };

        Assert.All(failures, failure => Assert.False(failure.Succeeded));
        Assert.All(failures, failure => Assert.Equal("invalid_credentials", failure.Error!.Code));
        Assert.All(failures, failure => Assert.Equal(failures[0].Error, failure.Error));
        // A failed sign-in returns no session at all: reading the value is a programming error.
        Assert.All(failures, failure => Assert.Throws<InvalidOperationException>(() => failure.Value));
        // The refused states stay exactly as they were: no activation, no automatic linking.
        Assert.Equal(verified.ClientId, Assert.Single(_repository.Accounts, account => account.Id == verified.Id).ClientId);
        Assert.Null(Assert.Single(_repository.Accounts, account => account.Id == pending.Id).ClientId);
        Assert.Null(Assert.Single(_repository.Accounts, account => account.Id == pending.Id).EmailVerifiedAt);
        // The unlinked account keeps its verified email and still owns no ficha: sign-in never links it.
        Assert.Null(Assert.Single(_repository.Accounts, account => account.Id == unlinked.Id).ClientId);
        Assert.NotNull(Assert.Single(_repository.Accounts, account => account.Id == unlinked.Id).EmailVerifiedAt);
    }

    [Fact]
    public async Task Login_never_links_an_unlinked_account_to_a_ficha_that_owns_its_email()
    {
        var business = SeedBusiness(HairdresserSlug);
        var account = _repository.SeedUnlinkedAccount(business.Id, Email, "hashed:secreto-123", Now);
        var ficha = _repository.SeedClient(business.Id, "Ana", "Gómez", Email);

        var result = await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, Email, PlainPassword));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_credentials", result.Error!.Code);
        Assert.Null(Assert.Single(_repository.Accounts, candidate => candidate.Id == account.Id).ClientId);
        Assert.Equal(ficha.Id, Assert.Single(_repository.Clients).Id);
    }

    [Fact]
    public async Task Login_rejects_an_account_of_the_same_email_in_another_business()
    {
        SeedBusiness(HairdresserSlug);
        var yoga = SeedBusiness(YogaSlug, "Estudio de Yoga");
        _repository.SeedActiveAccount(yoga.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        var result = await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, Email, PlainPassword));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_credentials", result.Error!.Code);
    }

    // --- Password recovery request (RF-27) ----------------------------------------------------------------------

    [Fact]
    public async Task Reset_request_queues_one_30_minute_recovery_notice_for_an_active_account()
    {
        var business = SeedBusiness(HairdresserSlug, "Peluquería Marina");
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        var result = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        var token = Assert.Single(_repository.Tokens);
        Assert.Equal(AccountTokenPurpose.PasswordReset, token.Purpose);
        Assert.Equal(Now.AddMinutes(30), token.ExpiresAt);
        Assert.Null(token.UsedAt);
        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal(business.Id, notification.BusinessId);
        Assert.Equal(NotificationType.PasswordReset, notification.Type);
        Assert.Equal(Email, notification.Recipient);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.StartsWith("https://reservas.example.com/r/peluqueria/cuenta/restablecer/", LinkFrom(notification));
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Reset_request_stores_only_the_sha256_hash_and_the_notice_carries_the_raw_link()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));

        var token = Assert.Single(_repository.Tokens);
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        Assert.Equal(SecureToken.Hash(rawToken), token.TokenHash);
        Assert.Equal(32, token.TokenHash.Length);
        Assert.DoesNotContain(rawToken, Convert.ToHexString(token.TokenHash));
        Assert.Contains(rawToken, _repository.Notifications.Single().BodyHtml);
    }

    [Fact]
    public void Reset_link_rules_build_only_from_the_configured_trusted_base_url()
    {
        Assert.Equal(
            "https://reservas.example.com/r/peluqueria/cuenta/restablecer/opaque-token",
            ClientAccountUrlRules.BuildPasswordResetUrl("https://reservas.example.com", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildPasswordResetUrl("http://reservas.example.com", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildPasswordResetUrl("", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildPasswordResetUrl("https://reservas.example.com", "peluqueria", ""));
    }

    [Fact]
    public async Task Reset_request_answers_the_same_generic_success_for_missing_pending_active_and_other_tenant_email()
    {
        var hairdresser = SeedBusiness(HairdresserSlug);
        var yoga = SeedBusiness(YogaSlug, "Estudio de Yoga");
        _repository.SeedActiveAccount(hairdresser.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        _repository.SeedPendingAccount(hairdresser.Id, "pendiente@example.com", "hashed:secreto-123", Now);
        _repository.SeedActiveAccount(yoga.Id, "ajena@example.com", "hashed:secreto-123", Now.AddDays(-1));

        var missing = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, "nadie@example.com"));
        var notificationsAfterMissing = _repository.Notifications.Count;
        var pending = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, "pendiente@example.com"));
        var otherTenant = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, "ajena@example.com"));
        var active = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));

        // Every answer is the same empty success, so no state can be inferred from it.
        Assert.All(new[] { missing, pending, otherTenant, active }, result => Assert.True(result.Succeeded));
        Assert.All(new[] { missing, pending, otherTenant, active }, result => Assert.Null(result.Error));
        Assert.Equal(0, notificationsAfterMissing);
        // Only the active account of this business received a link.
        Assert.Single(_repository.Notifications);
        Assert.Single(_repository.Tokens);
        Assert.Contains(Email, _repository.Notifications.Single().Recipient);
    }

    [Fact]
    public async Task Reset_request_rejects_an_unknown_slug_without_writing()
    {
        var result = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest("negocio-inexistente", Email));

        Assert.False(result.Succeeded);
        Assert.Equal("business_not_found", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Tokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ana-sin-arroba")]
    public async Task Reset_request_rejects_an_invalid_email_without_opening_a_transaction(string email)
    {
        SeedBusiness(HairdresserSlug);

        var result = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, email));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Error!.Code, new[] { "client_email_required", "invalid_client_email" });
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Reset_request_rotates_the_recovery_token_so_only_the_latest_link_works()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var firstToken = TokenFrom(Assert.Single(_repository.Notifications));
        _clock.Now = Now.AddMinutes(5);
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var secondToken = TokenFrom(_repository.Notifications[1]);

        Assert.Equal(2, _repository.Notifications.Count);
        Assert.Equal(2, _repository.Tokens.Count);
        Assert.NotEqual(firstToken, secondToken);
        Assert.NotEqual(_repository.Notifications[0].DedupKey, _repository.Notifications[1].DedupKey);
        Assert.NotNull(_repository.Tokens[0].UsedAt);

        var obsolete = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, firstToken, NewPassword));
        Assert.False(obsolete.Succeeded);
        Assert.Equal("password_reset_link_unavailable", obsolete.Error!.Code);

        var current = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, secondToken, NewPassword));
        Assert.True(current.Succeeded);
        Assert.Equal("hashed:nueva-clave", Assert.Single(_repository.Accounts).PasswordHash);
    }

    [Fact]
    public async Task Reset_request_in_the_same_second_still_delivers_the_rotated_token()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        // Both requests share the issuing second: the rotated token must still reach the outbox, because the first
        // link stopped working at the rotation.
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));

        Assert.Equal(Now, _clock.Now);
        Assert.Equal(2, _repository.Notifications.Count);
        Assert.Equal(2, _repository.Tokens.Count);
        Assert.NotEqual(_repository.Notifications[0].DedupKey, _repository.Notifications[1].DedupKey);
        var rotated = TokenFrom(_repository.Notifications[1]);
        Assert.True((await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rotated, NewPassword))).Succeeded);
    }

    [Fact]
    public async Task Reset_request_dedup_key_is_bound_to_the_token_hash_and_never_to_the_instant()
    {
        var business = SeedBusiness(HairdresserSlug);
        var account = _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));

        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal(
            ClientAccessService.PasswordResetDedupKey(account.Id, Assert.Single(_repository.Tokens).TokenHash),
            notification.DedupKey);
        Assert.DoesNotContain(TokenFrom(notification), notification.DedupKey);
    }

    [Fact]
    public async Task Reset_request_ignores_a_duplicate_notice_without_failing_the_request()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        // The retry re-enqueues the exact notice of the token the request is about to create, so the token-bound key
        // collides and the duplicate must be ignored instead of treated as a failure.
        _repository.BeforeNotificationInsert = notice => _repository.SeedQueuedNotification(notice.DedupKey);

        var result = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));

        Assert.True(result.Succeeded);
        Assert.Single(_repository.Notifications);
        Assert.Single(_repository.Tokens);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Two_simultaneous_reset_requests_leave_exactly_one_usable_link()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));

        var results = await Task.WhenAll(
            _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email)),
            _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email)));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(2, _repository.Notifications.Count);
        Assert.Equal(2, _repository.Tokens.Count);
        // The rotation invalidates the earlier link under the lock, so a person never ends up with two live links.
        Assert.Single(_repository.Tokens, token => token.UsedAt is null);
    }

    [Fact]
    public async Task Reset_request_never_writes_a_link_for_a_pending_or_unlinked_account()    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedPendingAccount(business.Id, "pendiente@example.com", "hashed:secreto-123", Now);
        _repository.SeedUnlinkedAccount(business.Id, "sin-ficha@example.com", "hashed:secreto-123", Now);

        var pending = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, "pendiente@example.com"));
        var unlinked = await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, "sin-ficha@example.com"));

        Assert.True(pending.Succeeded);
        Assert.True(unlinked.Succeeded);
        Assert.Empty(_repository.Tokens);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // --- Password recovery completion (RF-27) -------------------------------------------------------------------

    [Fact]
    public async Task Complete_reset_replaces_the_password_hash_and_consumes_the_token_without_a_session()
    {
        var business = SeedBusiness(HairdresserSlug);
        var account = _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        var notificationsBefore = _repository.Notifications.Count;
        _clock.Now = Now.AddMinutes(5);

        var result = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, NewPassword));

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        // The completion returns a plain Result: there is no session or token payload to auto-sign-in with.
        Assert.Equal(typeof(Result), result.GetType());
        Assert.Equal("hashed:nueva-clave", Assert.Single(_repository.Accounts, candidate => candidate.Id == account.Id).PasswordHash);
        Assert.NotNull(Assert.Single(_repository.Tokens).UsedAt);
        Assert.Equal(notificationsBefore, _repository.Notifications.Count);
    }

    [Fact]
    public async Task Complete_reset_uses_the_new_password_for_login_and_the_old_one_stops_working()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));

        Assert.True((await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, NewPassword))).Succeeded);

        // No auto-login: the new password signs in and the old one no longer does.
        Assert.True((await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, Email, NewPassword))).Succeeded);
        Assert.False((await _service.LoginAsync(new ClientLoginRequest(HairdresserSlug, Email, PlainPassword))).Succeeded);
    }

    [Fact]
    public async Task Complete_reset_never_emails_the_new_password()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        var notificationsBefore = _repository.Notifications.Count;

        await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, NewPassword));

        Assert.Equal(notificationsBefore, _repository.Notifications.Count);
        Assert.All(_repository.Notifications, notification =>
        {
            Assert.DoesNotContain(NewPassword, notification.Subject);
            Assert.DoesNotContain(NewPassword, notification.BodyHtml);
        });
    }

    [Fact]
    public async Task Complete_reset_validates_the_new_password_and_never_consumes_a_short_one()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        var beginsAfterRequest = _unitOfWork.BeginCount;

        var result = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, "corta"));

        Assert.False(result.Succeeded);
        Assert.Equal("password_too_short", result.Error!.Code);
        Assert.Equal(beginsAfterRequest, _unitOfWork.BeginCount);
        Assert.Null(Assert.Single(_repository.Tokens).UsedAt);
        Assert.Equal("hashed:secreto-123", Assert.Single(_repository.Accounts).PasswordHash);
    }

    [Fact]
    public async Task Complete_reset_accepts_a_password_of_exactly_eight_characters()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));

        var result = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, "ocho-car"));

        Assert.True(result.Succeeded);
        Assert.Equal("hashed:ocho-car", Assert.Single(_repository.Accounts).PasswordHash);
    }

    [Fact]
    public async Task Complete_reset_rejects_invalid_wrong_tenant_wrong_purpose_and_expired_with_the_same_generic_error()
    {
        var hairdresser = SeedBusiness(HairdresserSlug);
        var yoga = SeedBusiness(YogaSlug, "Estudio de Yoga");
        var account = _repository.SeedActiveAccount(hairdresser.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        var yogaAccount = _repository.SeedActiveAccount(yoga.Id, "ajena@example.com", "hashed:secreto-123", Now.AddDays(-1));
        _repository.SeedToken(yogaAccount.Id, AccountTokenPurpose.PasswordReset, "token-ajeno", Now.AddMinutes(30), Now);
        _repository.SeedToken(account.Id, AccountTokenPurpose.EmailVerification, "token-verificacion", Now.AddMinutes(30), Now);
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        var expiry = Assert.Single(_repository.Tokens, token => token.Purpose == AccountTokenPurpose.PasswordReset &&
            token.ClientAccountId == account.Id).ExpiresAt;
        var commitsAfterRequest = _unitOfWork.CommitCount;

        var errors = new List<BusinessError?>
        {
            (await _service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest("negocio-inexistente", rawToken, NewPassword))).Error,
            (await _service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(HairdresserSlug, null!, NewPassword))).Error,
            (await _service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(HairdresserSlug, "   ", NewPassword))).Error,
            (await _service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(HairdresserSlug, "token-inexistente", NewPassword))).Error,
            (await _service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(HairdresserSlug, "token-ajeno", NewPassword))).Error,
            (await _service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(HairdresserSlug, "token-verificacion", NewPassword))).Error
        };
        _clock.Now = expiry;
        errors.Add((await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, NewPassword))).Error);

        Assert.All(errors, error => Assert.NotNull(error));
        Assert.All(errors, error => Assert.Equal("password_reset_link_unavailable", error!.Code));
        Assert.All(errors, error => Assert.Equal(errors[0], error));
        // No failed link wrote a password: only the request committed, and the rejected token was never consumed.
        Assert.Equal(commitsAfterRequest, _unitOfWork.CommitCount);
        Assert.Equal("hashed:secreto-123", Assert.Single(_repository.Accounts, candidate => candidate.Id == account.Id).PasswordHash);
        Assert.Equal("hashed:secreto-123", Assert.Single(_repository.Accounts, candidate => candidate.Id == yogaAccount.Id).PasswordHash);
        Assert.Null(Assert.Single(_repository.Tokens, token => token.Purpose == AccountTokenPurpose.PasswordReset &&
            token.ClientAccountId == account.Id).UsedAt);
    }

    [Fact]
    public async Task Complete_reset_rejects_a_replayed_link_without_a_second_change()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        Assert.True((await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, NewPassword))).Succeeded);
        var commitsAfterFirstUse = _unitOfWork.CommitCount;

        var replayed = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, "otra-clave-9"));

        Assert.False(replayed.Succeeded);
        Assert.Equal("password_reset_link_unavailable", replayed.Error!.Code);
        Assert.Equal(commitsAfterFirstUse, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        Assert.Equal("hashed:nueva-clave", Assert.Single(_repository.Accounts).PasswordHash);
    }

    [Fact]
    public async Task Complete_reset_accepts_the_link_one_tick_before_expiry()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        _clock.Now = Now.AddMinutes(30).AddTicks(-1);

        var result = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, NewPassword));

        Assert.True(result.Succeeded);
        Assert.Equal("hashed:nueva-clave", Assert.Single(_repository.Accounts).PasswordHash);
    }

    [Fact]
    public async Task Complete_reset_rejects_the_link_at_the_exact_expiry_instant_without_consuming_it()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        var commitsAfterRequest = _unitOfWork.CommitCount;
        _clock.Now = Now.AddMinutes(30);

        var result = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, NewPassword));

        Assert.False(result.Succeeded);
        Assert.Equal("password_reset_link_unavailable", result.Error!.Code);
        Assert.Null(Assert.Single(_repository.Tokens).UsedAt);
        Assert.Equal("hashed:secreto-123", Assert.Single(_repository.Accounts).PasswordHash);
        Assert.Equal(commitsAfterRequest, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Complete_reset_rejects_a_recovery_token_of_a_pending_account()
    {
        var business = SeedBusiness(HairdresserSlug);
        var pending = _repository.SeedPendingAccount(business.Id, Email, "hashed:secreto-123", Now);
        _repository.SeedToken(pending.Id, AccountTokenPurpose.PasswordReset, "token-pendiente", Now.AddMinutes(30), Now);

        var result = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, "token-pendiente", NewPassword));

        Assert.False(result.Succeeded);
        Assert.Equal("password_reset_link_unavailable", result.Error!.Code);
        Assert.Equal("hashed:secreto-123", Assert.Single(_repository.Accounts).PasswordHash);
        Assert.Null(Assert.Single(_repository.Tokens).UsedAt);
    }

    [Fact]
    public async Task Complete_reset_does_not_commit_when_the_password_cannot_be_replaced()
    {
        var business = SeedBusiness(HairdresserSlug);
        _repository.SeedActiveAccount(business.Id, Email, "hashed:secreto-123", Now.AddDays(-1));
        await _service.RequestPasswordResetAsync(new ClientPasswordResetRequest(HairdresserSlug, Email));
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        var commitsAfterRequest = _unitOfWork.CommitCount;
        _repository.FailPasswordUpdate = true;

        var result = await _service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(HairdresserSlug, rawToken, NewPassword));

        Assert.False(result.Succeeded);
        Assert.Equal("password_reset_link_unavailable", result.Error!.Code);
        // Only the request committed: the failed completion rolled back.
        Assert.Equal(commitsAfterRequest, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        Assert.Equal("hashed:secreto-123", Assert.Single(_repository.Accounts).PasswordHash);
        // Fake limitation: an in-memory store cannot undo the consumed hash, so the token stays marked used here.
        // Only a SQL fixture proves that the rollback also returns the token to its usable state.
        Assert.NotNull(Assert.Single(_repository.Tokens).UsedAt);
    }

    private Business SeedBusiness(string slug, string name = "Peluquería Marina")
    {
        var business = new Business { Name = name, Slug = slug, ContactPhone = "1145550000", CreatedAt = Now };
        _businesses.InsertAsync(business).GetAwaiter().GetResult();
        return business;
    }

    private static string LinkFrom(Notification notification)
    {
        var match = Regex.Match(notification.BodyHtml, "href=\"([^\"]+)\"");
        Assert.True(match.Success, "The recovery email must contain a link.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string TokenFrom(Notification notification)
    {
        var link = LinkFrom(notification);
        const string marker = "/cuenta/restablecer/";
        var index = link.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The recovery email must link to the password reset route.");
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
