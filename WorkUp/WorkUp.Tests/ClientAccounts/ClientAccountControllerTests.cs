using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Controllers;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.ClientAccounts;

namespace WorkUp.Tests.ClientAccounts;

/// <summary>
/// The public client-account pages (RF-25, RF-15, RF-27). They are unit tests over the real controllers and the real
/// business services with the in-memory repositories, so no DI container, no routing pipeline and no HTTP server are
/// needed. The behaviors that a public account flow can get wrong are pinned here: a GET never spends a link, a
/// registration never opens a session, an unusable link answers generically, the cookie belongs to one business, and
/// no token or password is ever echoed back.
/// </summary>
public sealed class ClientAccountControllerTests
{
    private const string Slug = "peluqueria";
    private const string OtherSlug = "yoga";
    private const string Email = "ana@example.com";
    private const string PlainPassword = "secreto-123";
    private const string VerificationToken = "token-verificacion";
    private const string ResetToken = "token-recuperacion";
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeClientRegistrationRepository _registrations = new();
    private readonly FakeClientAccessRepository _accesses = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeClock _clock = new() { Now = Now };
    private readonly RecordingAuthenticationService _authentication = new();
    private readonly ClientAccountController _controller;

    public ClientAccountControllerTests()
    {
        SeedBusiness(Slug, "Peluquería Marina");
        SeedBusiness(OtherSlug, "Estudio de Yoga");
        _controller = CreateController();
    }

    // --- Verification link (RF-25) -----------------------------------------------------------------------------

    [Fact]
    public async Task Get_verify_shows_the_confirmation_form_without_consuming_the_link()
    {
        var account = SeedPendingAccount();
        var token = _registrations.SeedToken(
            account.Id, AccountTokenPurpose.EmailVerification, VerificationToken, Now.AddHours(48), Now);

        var result = await _controller.Verify(Slug, VerificationToken, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Verify", view.ViewName);
        Assert.Equal(VerificationToken, Assert.IsType<ClientVerifyEmailViewModel>(view.Model).Token);
        // A link scanner opening the emailed URL must find a confirmation screen and nothing else: the link is spent
        // only by the POST, and the account is still pending.
        Assert.Null(token.UsedAt);
        Assert.Null(account.EmailVerifiedAt);
        Assert.Null(account.ClientId);
        Assert.Empty(_registrations.Clients);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl);
        Assert.Equal("no-cache", _controller.Response.Headers.Pragma);
        Assert.Equal("no-referrer", _controller.Response.Headers["Referrer-Policy"]);
    }

    [Fact]
    public async Task Post_verify_consumes_the_link_creates_the_ficha_and_starts_no_session()
    {
        var account = SeedPendingAccount();
        var token = _registrations.SeedToken(
            account.Id, AccountTokenPurpose.EmailVerification, VerificationToken, Now.AddHours(48), Now);

        var result = await _controller.Verify(
            Slug, new ClientVerifyEmailViewModel { Token = VerificationToken }, CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/r/peluqueria/cuenta/ingresar", redirect.Url);
        Assert.NotNull(token.UsedAt);
        Assert.NotNull(account.EmailVerifiedAt);
        Assert.Single(_registrations.Clients);
        // Confirming the email does not sign anyone in: the session starts only with the password.
        Assert.Empty(_authentication.SignInSchemes);
        Assert.Equal("Confirmamos tu email. Ya podés ingresar con tu contraseña.", _controller.TempData["FlashMessage"]);
    }

    [Fact]
    public async Task Post_verify_answers_one_generic_page_for_an_expired_or_unknown_link()
    {
        var account = SeedPendingAccount();
        _registrations.SeedToken(
            account.Id, AccountTokenPurpose.EmailVerification, VerificationToken, Now.AddHours(48), Now);
        _clock.Now = Now.AddHours(48);

        var expired = await _controller.Verify(
            Slug, new ClientVerifyEmailViewModel { Token = VerificationToken }, CancellationToken.None);
        var unknown = await _controller.Verify(
            Slug, new ClientVerifyEmailViewModel { Token = "otro-token" }, CancellationToken.None);

        Assert.Equal("Status", Assert.IsType<ViewResult>(expired).ViewName);
        Assert.Equal("Status", Assert.IsType<ViewResult>(unknown).ViewName);
        // One single answer for every unusable link: no difference tells the visitor whether a link ever existed.
        Assert.Equal(StatusOf(expired), StatusOf(unknown));
        Assert.Equal("/r/peluqueria/cuenta/reenviar", StatusOf(expired).ActionUrl);
        Assert.Null(Assert.Single(_registrations.Tokens).UsedAt);
    }

    [Fact]
    public async Task Post_verify_sends_the_person_to_the_business_without_a_link_when_the_email_already_has_a_ficha()
    {
        var account = SeedPendingAccount();
        _registrations.SeedToken(
            account.Id, AccountTokenPurpose.EmailVerification, VerificationToken, Now.AddHours(48), Now);
        // A public booking created the ficha with the same email after the registration started: linking it
        // automatically would hand over someone else's history, so the flow fails closed (D-18, T22).
        _registrations.SeedClient(BusinessId(Slug), "Ana", "Gómez", Email);

        var result = await _controller.Verify(
            Slug, new ClientVerifyEmailViewModel { Token = VerificationToken }, CancellationToken.None);

        var status = StatusOf(result);
        Assert.Equal("Necesitamos que hables con el negocio", status.Title);
        Assert.Contains("Peluquería Marina", status.Message);
        // No link at all: the owner-assisted invitation is T22, and the screen must not point into the panel.
        Assert.Null(status.ActionLabel);
        Assert.Null(status.ActionUrl);
        Assert.Null(account.ClientId);
    }

    [Fact]
    public async Task Post_verify_answers_the_generic_page_for_a_replayed_link_and_keeps_one_ficha()
    {
        var account = SeedPendingAccount();
        _registrations.SeedToken(
            account.Id, AccountTokenPurpose.EmailVerification, VerificationToken, Now.AddHours(48), Now);
        await _controller.Verify(
            Slug, new ClientVerifyEmailViewModel { Token = VerificationToken }, CancellationToken.None);

        var replayed = await _controller.Verify(
            Slug, new ClientVerifyEmailViewModel { Token = VerificationToken }, CancellationToken.None);

        Assert.Equal("El enlace no está disponible", StatusOf(replayed).Title);
        Assert.Single(_registrations.Clients);
        Assert.Single(_registrations.Accounts);
        // The page that receives a token is never cached, so a shared machine cannot re-show it from history.
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl);
        Assert.Equal("no-referrer", _controller.Response.Headers["Referrer-Policy"]);
    }

    // --- Registration (RF-25) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Post_register_creates_a_pending_account_and_never_signs_in()
    {
        var result = await _controller.Register(Slug, RegisterModel(), CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/r/peluqueria/cuenta/reenviar", redirect.Url);
        var account = Assert.Single(_registrations.Accounts);
        Assert.Null(account.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        Assert.Empty(_registrations.Clients);
        Assert.Single(_registrations.Notifications);
        Assert.Equal(1, _unitOfWork.CommitCount);
        // No session until the emailed link is consumed.
        Assert.Empty(_authentication.SignInSchemes);
    }

    [Fact]
    public async Task Post_register_with_different_passwords_reexamines_the_form_without_writing()
    {
        var model = RegisterModel();
        model.ConfirmPassword = "otra-clave-9";

        var result = await _controller.Register(Slug, model, CancellationToken.None);

        Assert.Equal("Register", Assert.IsType<ViewResult>(result).ViewName);
        Assert.Empty(_registrations.Accounts);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_authentication.SignInSchemes);
    }

    [Fact]
    public async Task Post_register_reports_a_ficha_that_owns_the_email_without_duplicating_it()
    {
        _registrations.SeedClient(BusinessId(Slug), "Ana", "Gómez", Email);

        var result = await _controller.Register(Slug, RegisterModel(), CancellationToken.None);

        Assert.Equal("Register", Assert.IsType<ViewResult>(result).ViewName);
        Assert.Empty(_registrations.Accounts);
        Assert.Single(_registrations.Clients);
        Assert.Contains(
            _controller.ModelState["Email"]!.Errors,
            error => error.ErrorMessage.Contains("ya está registrado", StringComparison.Ordinal));
    }

    // --- Sign-in (RF-15) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Post_login_with_a_verified_account_sets_the_client_cookie_of_its_business()
    {
        var account = _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));

        var result = await _controller.Login(Slug, LoginModel(), CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/r/peluqueria/cuenta", redirect.Url);
        Assert.Equal(new[] { AuthSchemes.Client }, _authentication.SignInSchemes);
        var principal = Assert.Single(_authentication.Principals);
        Assert.Equal(BusinessId(Slug).ToString(), principal.FindFirstValue(WorkUpClaims.BusinessId));
        Assert.Equal(Slug, principal.FindFirstValue(WorkUpClaims.BusinessSlug));
        Assert.Equal("Peluquería Marina", principal.FindFirstValue(WorkUpClaims.BusinessName));
        Assert.Equal(account.Id.ToString(), principal.FindFirstValue(WorkUpClaims.ClientAccountId));
        Assert.Equal(account.ClientId!.Value.ToString(), principal.FindFirstValue(WorkUpClaims.ClientId));
        Assert.Equal(Email, principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal(account.Id.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task Post_login_with_a_pending_account_shows_the_same_generic_error_as_an_unknown_email()
    {
        _accesses.SeedPendingAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now);

        var pending = await _controller.Login(Slug, LoginModel(), CancellationToken.None);
        var unknown = await Rebuild().Login(Slug, LoginModel("nadie@example.com"), CancellationToken.None);

        Assert.Equal("Login", Assert.IsType<ViewResult>(pending).ViewName);
        Assert.Equal("Login", Assert.IsType<ViewResult>(unknown).ViewName);
        Assert.Equal(Messages(pending), Messages(unknown));
        Assert.Equal(new[] { "Email o contraseña incorrectos." }, Messages(pending));
        // Neither an unverified account nor an absent email gets a session, and the pending account stays pending.
        Assert.Empty(_authentication.SignInSchemes);
        Assert.Null(Assert.Single(_accesses.Accounts).ClientId);
    }

    [Fact]
    public async Task Post_login_rejects_an_account_of_another_business()
    {
        _accesses.SeedActiveAccount(BusinessId(OtherSlug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));

        var result = await _controller.Login(Slug, LoginModel(), CancellationToken.None);

        Assert.Equal("Login", Assert.IsType<ViewResult>(result).ViewName);
        Assert.Empty(_authentication.SignInSchemes);
        Assert.Contains("Email o contraseña incorrectos.", Messages(result));
    }

    [Theory]
    [InlineData("/r/yoga/cuenta/ingresar")]
    [InlineData("/r/peluqueria/../yoga/cuenta/ingresar")]
    [InlineData("//evil.example/phish")]
    [InlineData("https://evil.example/phish")]
    [InlineData("/panel/Day")]
    [InlineData("/r/peluqueria")]
    public async Task Post_login_rejects_a_return_url_outside_this_business_and_lands_on_the_placeholder(string returnUrl)
    {
        _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        var model = LoginModel();
        model.ReturnUrl = returnUrl;

        var result = await _controller.Login(Slug, model, CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/r/peluqueria/cuenta", redirect.Url);
    }

    [Fact]
    public async Task Post_login_accepts_a_return_url_of_this_same_business()
    {
        _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        var model = LoginModel();
        model.ReturnUrl = "/r/peluqueria/mis-turnos?page=2";

        var result = await _controller.Login(Slug, model, CancellationToken.None);

        Assert.Equal("/r/peluqueria/mis-turnos?page=2", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task Get_login_never_echoes_an_unsafe_return_url_back_into_the_form()
    {
        var result = await _controller.Login(Slug, "https://evil.example/phish", CancellationToken.None);

        var model = Assert.IsType<ClientLoginViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(model.ReturnUrl);
        Assert.Null(RenderedReturnUrl(_controller, model));
    }

    [Theory]
    [InlineData("https://evil.example.invalid/robo")]
    [InlineData("//evil.example/phish")]
    [InlineData("/r/yoga/cuenta/ingresar")]
    [InlineData("/r/peluqueria/../yoga/cuenta/ingresar")]
    [InlineData("/panel/Day")]
    public async Task Get_login_never_renders_a_return_url_outside_this_business_the_binder_left_in_model_state(
        string offered)
    {
        // The real request is /r/peluqueria/cuenta/ingresar?returnUrl=https://evil.example.invalid/robo. The binder
        // records the raw offer in ModelState under the parameter name, and the hidden field of the form resolves
        // ReturnUrl from there before falling back to the model, so sanitizing the model alone is not enough.
        SeedBoundReturnUrl(_controller, offered);

        var result = await _controller.Login(Slug, offered, CancellationToken.None);

        var model = Assert.IsType<ClientLoginViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(model.ReturnUrl);
        Assert.Null(RenderedReturnUrl(_controller, model));
    }

    [Fact]
    public async Task Get_login_keeps_a_return_url_of_this_same_business_in_the_form()
    {
        const string offered = "/r/peluqueria/mis-turnos?page=2";
        SeedBoundReturnUrl(_controller, offered);

        var result = await _controller.Login(Slug, offered, CancellationToken.None);

        var model = Assert.IsType<ClientLoginViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(offered, model.ReturnUrl);
        // Dropping the ModelState entry is not the same as dropping the destination: the form must still carry the
        // same-business offer, or a failed attempt would lose the place the person was going to.
        Assert.Equal(offered, RenderedReturnUrl(_controller, model));
    }

    [Fact]
    public async Task Post_login_re_examines_the_form_without_echoing_a_hostile_return_url()
    {
        var model = LoginModel();
        model.ReturnUrl = HostileReturnUrl;
        // The binder already rejected the form, and the hostile destination reached ModelState under the field name.
        _controller.ModelState.AddModelError(nameof(ClientLoginViewModel.Email), "El email no es válido.");
        SeedBoundReturnUrl(_controller, HostileReturnUrl, nameof(ClientLoginViewModel.ReturnUrl));

        var result = await _controller.Login(Slug, model, CancellationToken.None);

        Assert.Equal("Login", Assert.IsType<ViewResult>(result).ViewName);
        var redisplayed = Assert.IsType<ClientLoginViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(redisplayed.ReturnUrl);
        Assert.Null(RenderedReturnUrl(_controller, redisplayed));
    }

    [Fact]
    public async Task Post_login_with_wrong_credentials_does_not_echo_a_hostile_return_url()
    {
        _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        var model = LoginModel();
        model.Password = "otra-clave-9";
        model.ReturnUrl = HostileReturnUrl;
        SeedBoundReturnUrl(_controller, HostileReturnUrl, nameof(ClientLoginViewModel.ReturnUrl));

        var result = await _controller.Login(Slug, model, CancellationToken.None);

        Assert.Contains("Email o contraseña incorrectos.", Messages(result));
        var redisplayed = Assert.IsType<ClientLoginViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(redisplayed.ReturnUrl);
        Assert.Null(RenderedReturnUrl(_controller, redisplayed));
    }

    [Fact]
    public async Task Post_login_re_examines_the_form_with_the_return_url_of_this_same_business()
    {
        _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        const string offered = "/r/peluqueria/mis-turnos?page=2";
        var model = LoginModel();
        model.Password = "otra-clave-9";
        model.ReturnUrl = offered;
        SeedBoundReturnUrl(_controller, offered, nameof(ClientLoginViewModel.ReturnUrl));

        var result = await _controller.Login(Slug, model, CancellationToken.None);

        var redisplayed = Assert.IsType<ClientLoginViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(offered, redisplayed.ReturnUrl);
        Assert.Equal(offered, RenderedReturnUrl(_controller, redisplayed));
    }

    [Fact]
    public async Task Post_logout_clears_the_client_cookie_and_is_the_only_action_that_does()
    {
        var result = await _controller.Logout(Slug, CancellationToken.None);

        Assert.Equal(new[] { AuthSchemes.Client }, _authentication.SignOutSchemes);
        Assert.Equal("/r/peluqueria", Assert.IsType<RedirectResult>(result).Url);
        // No GET clears a session, so a mere link or a prefetch can never sign anyone out.
        var logout = typeof(ClientAccountController).GetMethod(nameof(ClientAccountController.Logout))!;
        Assert.NotNull(logout.GetCustomAttribute<HttpPostAttribute>());
        Assert.Null(logout.GetCustomAttribute<HttpGetAttribute>());
        Assert.Empty(_authentication.SignInSchemes);
    }

    // --- Password recovery (RF-27) -----------------------------------------------------------------------------

    [Fact]
    public async Task Post_recover_answers_the_same_generic_message_for_absent_pending_and_active_email()
    {
        _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        _accesses.SeedPendingAccount(BusinessId(Slug), "pendiente@example.com", "hashed:" + PlainPassword, Now);

        var messages = new List<string>();
        foreach (var email in new[] { "nadie@example.com", "pendiente@example.com", Email })
        {
            var result = await _controller.Recover(
                Slug, new ClientRecoverPasswordViewModel { Email = email }, CancellationToken.None);

            var redirect = Assert.IsType<RedirectResult>(result);
            Assert.Equal("/r/peluqueria/cuenta/recuperar", redirect.Url);
            messages.Add((string)_controller.TempData["FlashMessage"]!);
        }

        // One single answer for every state: the request never reveals who has an account (RF-27).
        Assert.Single(messages.Distinct());
        Assert.DoesNotContain(Email, messages[0]);
    }

    [Fact]
    public async Task Get_reset_shows_the_form_without_consuming_the_link()
    {
        var account = _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        var token = _accesses.SeedToken(
            account.Id, AccountTokenPurpose.PasswordReset, ResetToken, Now.AddMinutes(30), Now);

        var result = await _controller.Reset(Slug, ResetToken, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Reset", view.ViewName);
        Assert.Equal(ResetToken, Assert.IsType<ClientResetPasswordViewModel>(view.Model).Token);
        Assert.Null(token.UsedAt);
        Assert.Equal("hashed:" + PlainPassword, account.PasswordHash);
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl);
        Assert.Equal("no-referrer", _controller.Response.Headers["Referrer-Policy"]);
    }

    [Fact]
    public async Task Post_reset_consumes_the_link_replaces_the_password_and_starts_no_session()
    {
        var account = _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        var token = _accesses.SeedToken(
            account.Id, AccountTokenPurpose.PasswordReset, ResetToken, Now.AddMinutes(30), Now);

        var result = await _controller.Reset(Slug, ResetForm(), CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/r/peluqueria/cuenta/ingresar", redirect.Url);
        Assert.NotNull(token.UsedAt);
        Assert.Equal("hashed:nueva-clave", account.PasswordHash);
        // Changing a password never signs anyone in: the session still starts only with the new password.
        Assert.Empty(_authentication.SignInSchemes);
    }

    [Fact]
    public async Task Post_reset_with_a_short_password_reexamines_the_form_without_consuming_the_link()
    {
        var account = _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        var token = _accesses.SeedToken(
            account.Id, AccountTokenPurpose.PasswordReset, ResetToken, Now.AddMinutes(30), Now);
        var form = ResetForm();
        form.NewPassword = "corta";
        form.ConfirmPassword = "corta";

        var result = await _controller.Reset(Slug, form, CancellationToken.None);

        Assert.Equal("Reset", Assert.IsType<ViewResult>(result).ViewName);
        Assert.Null(token.UsedAt);
        Assert.Equal("hashed:" + PlainPassword, account.PasswordHash);
    }

    [Fact]
    public async Task Post_reset_with_an_unusable_link_answers_the_generic_page()
    {
        var form = ResetForm();
        form.Token = "token-inexistente";

        var result = await _controller.Reset(Slug, form, CancellationToken.None);

        var status = StatusOf(result);
        Assert.Equal("El enlace no está disponible", status.Title);
        Assert.Equal("/r/peluqueria/cuenta/recuperar", status.ActionUrl);
        Assert.Empty(_authentication.SignInSchemes);
    }

    [Fact]
    public async Task Post_reenviar_answers_generically_and_never_reveals_whether_the_account_exists()
    {
        var unknown = await _controller.Resend(
            Slug, new ClientResendVerificationViewModel { Email = "nadie@example.com" }, CancellationToken.None);
        var unknownFlash = (string)_controller.TempData["FlashMessage"]!;

        var account = SeedPendingAccount();
        var pending = await _controller.Resend(
            Slug, new ClientResendVerificationViewModel { Email = Email }, CancellationToken.None);

        Assert.Equal("/r/peluqueria/cuenta/reenviar", Assert.IsType<RedirectResult>(unknown).Url);
        Assert.Equal("/r/peluqueria/cuenta/reenviar", Assert.IsType<RedirectResult>(pending).Url);
        Assert.Equal(unknownFlash, (string)_controller.TempData["FlashMessage"]!);
        Assert.DoesNotContain(Email, unknownFlash);
        // The pending account got a fresh link and a notice; the unknown email got nothing and revealed nothing.
        var token = Assert.Single(_registrations.Tokens);
        Assert.Equal(account.Id, token.ClientAccountId);
        Assert.Null(token.UsedAt);
        Assert.Single(_registrations.Notifications);
    }

    // --- Email mode copy (T23) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Register_post_without_an_options_dependency_reads_the_mode_as_outbox()
    {
        var withoutOptions = await _controller.Register(Slug, RegisterModel(), CancellationToken.None);
        var explicitOutbox = CreateController(new WorkUpOptions());
        var explicitOutboxResult = await explicitOutbox.Register(
            Slug, RegisterModel("otra@example.com"), CancellationToken.None);

        Assert.Equal("/r/peluqueria/cuenta/reenviar", Assert.IsType<RedirectResult>(withoutOptions).Url);
        Assert.Equal("/r/peluqueria/cuenta/reenviar", Assert.IsType<RedirectResult>(explicitOutboxResult).Url);
        // The missing dependency is the retained default, so both flashes say the email is not sent yet.
        Assert.Equal(
            Assert.IsType<string>(_controller.TempData["FlashMessage"]),
            Assert.IsType<string>(explicitOutbox.TempData["FlashMessage"]));
        Assert.Contains("no está activo", Assert.IsType<string>(_controller.TempData["FlashMessage"]), StringComparison.Ordinal);
        Assert.DoesNotContain("Te enviamos", Assert.IsType<string>(_controller.TempData["FlashMessage"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Outbox")]
    [InlineData("Smtp")]
    public async Task Register_post_states_the_email_mode_without_claiming_the_email_was_sent(string mode)
    {
        var controller = CreateController(new WorkUpOptions { Email = new EmailOptions { Mode = mode } });

        var result = await controller.Register(Slug, RegisterModel(), CancellationToken.None);

        Assert.Equal("/r/peluqueria/cuenta/reenviar", Assert.IsType<RedirectResult>(result).Url);
        Assert.Single(_registrations.Notifications);
        // The copy is the only thing the mode changes: the account is still pending and no session starts.
        Assert.Single(_registrations.Accounts);
        Assert.Null(Assert.Single(_registrations.Accounts).ClientId);
        Assert.Empty(_authentication.SignInSchemes);
        var flash = Assert.IsType<string>(controller.TempData["FlashMessage"]);
        Assert.DoesNotContain("Te enviamos", flash, StringComparison.Ordinal);
        Assert.Contains("48 horas", flash, StringComparison.Ordinal);

        if (mode == "Smtp")
        {
            // Deliver only queues the notice for the scheduled dispatch: no inbox delivery is promised.
            Assert.Contains("envío automático", flash, StringComparison.Ordinal);
            Assert.Contains("no podemos garantizar", flash, StringComparison.Ordinal);
            Assert.DoesNotContain("no está activo", flash, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("no está activo", flash, StringComparison.Ordinal);
            Assert.DoesNotContain("no podemos garantizar", flash, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Outbox")]
    [InlineData("Smtp")]
    public async Task Resend_post_keeps_one_conditional_message_per_mode_and_never_confirms_an_account(string mode)
    {
        var controller = CreateController(new WorkUpOptions { Email = new EmailOptions { Mode = mode } });

        var unknown = await controller.Resend(
            Slug, new ClientResendVerificationViewModel { Email = "nadie@example.com" }, CancellationToken.None);
        var unknownFlash = Assert.IsType<string>(controller.TempData["FlashMessage"]);

        var account = SeedPendingAccount();
        var pending = await controller.Resend(
            Slug, new ClientResendVerificationViewModel { Email = Email }, CancellationToken.None);

        Assert.Equal("/r/peluqueria/cuenta/reenviar", Assert.IsType<RedirectResult>(unknown).Url);
        Assert.Equal("/r/peluqueria/cuenta/reenviar", Assert.IsType<RedirectResult>(pending).Url);
        // The answer stays exactly conditional, so an existent pending account and an absent one are indistinguishable.
        Assert.Equal(unknownFlash, Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.StartsWith("Si esa cuenta está pendiente de confirmar,", unknownFlash, StringComparison.Ordinal);
        Assert.DoesNotContain(Email, unknownFlash, StringComparison.Ordinal);
        Assert.DoesNotContain("Te enviamos", unknownFlash, StringComparison.Ordinal);
        Assert.Null(account.EmailVerifiedAt);

        if (mode == "Smtp")
        {
            Assert.Contains("no podemos garantizar", unknownFlash, StringComparison.Ordinal);
            Assert.DoesNotContain("no está activo", unknownFlash, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("no está activo", unknownFlash, StringComparison.Ordinal);
            Assert.DoesNotContain("no podemos garantizar", unknownFlash, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Outbox")]
    [InlineData("Smtp")]
    public async Task Recover_post_keeps_one_conditional_message_per_mode_for_absent_pending_and_active_email(string mode)
    {
        var controller = CreateController(new WorkUpOptions { Email = new EmailOptions { Mode = mode } });
        _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        _accesses.SeedPendingAccount(BusinessId(Slug), "pendiente@example.com", "hashed:" + PlainPassword, Now);

        var messages = new List<string>();
        foreach (var email in new[] { "nadie@example.com", "pendiente@example.com", Email })
        {
            var result = await controller.Recover(
                Slug, new ClientRecoverPasswordViewModel { Email = email }, CancellationToken.None);

            Assert.Equal("/r/peluqueria/cuenta/recuperar", Assert.IsType<RedirectResult>(result).Url);
            messages.Add(Assert.IsType<string>(controller.TempData["FlashMessage"]));
        }

        // Every state of the email gets the same conditional answer, revealing who has an account to no one (RF-27).
        Assert.Single(messages.Distinct());
        Assert.StartsWith("Si ese email tiene una cuenta,", messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Email, messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Te enviamos", messages[0], StringComparison.Ordinal);
        Assert.Contains("30 minutos", messages[0], StringComparison.Ordinal);

        if (mode == "Smtp")
        {
            Assert.Contains("no podemos garantizar", messages[0], StringComparison.Ordinal);
            Assert.DoesNotContain("no está activo", messages[0], StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("no está activo", messages[0], StringComparison.Ordinal);
            Assert.DoesNotContain("no podemos garantizar", messages[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unknown_email_mode_fails_fast_without_echoing_the_configuration_or_writing()
    {
        // The controller delegates to the same fail-fast parser as the startup, so a typo can never be read as Outbox.
        var error = Assert.Throws<InvalidOperationException>(() =>
            CreateController(new WorkUpOptions { Email = new EmailOptions { Mode = "Smpt" } }));

        Assert.Contains("WorkUp:Email:Mode", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Smpt", error.Message, StringComparison.Ordinal);
        Assert.Empty(_registrations.Accounts);
        Assert.Empty(_registrations.Notifications);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // --- Public surface ----------------------------------------------------------------------------------------

    [Fact]
    public void Every_post_action_declares_antiforgery_and_no_action_that_writes_is_a_get()
    {
        var actions = typeof(ClientAccountController).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var posts = actions.Where(action => action.GetCustomAttribute<HttpPostAttribute>() is not null).ToArray();
        Assert.Equal(8, posts.Length);
        Assert.All(posts, post => Assert.True(
            post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() is not null,
            $"The POST action {post.Name} must declare ValidateAntiForgeryToken."));

        foreach (var name in new[] { "Verify", "Reset" })
        {
            var get = actions.Single(candidate => candidate.Name == name &&
                candidate.GetCustomAttribute<HttpGetAttribute>() is not null);
            Assert.False(string.IsNullOrWhiteSpace(get.GetCustomAttribute<HttpGetAttribute>()!.Template));
        }
    }

    [Fact]
    public void No_account_view_model_carries_a_business_id_or_a_business()
    {
        var viewModels = new[]
        {
            typeof(ClientRegisterViewModel),
            typeof(ClientLoginViewModel),
            typeof(ClientRecoverPasswordViewModel),
            typeof(ClientResetPasswordViewModel),
            typeof(ClientResendVerificationViewModel),
            typeof(ClientVerifyEmailViewModel)
        };

        Assert.All(viewModels, viewModel => Assert.DoesNotContain(
            viewModel.GetProperties().Select(property => property.Name),
            name => name.Contains("Business", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task The_pages_resolve_the_business_from_the_slug_and_report_not_found_for_an_unknown_one()
    {
        var unknown = await _controller.Register("negocio-inexistente", CancellationToken.None);
        var unknownLogin = await _controller.Login("negocio-inexistente", (string?)null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(unknown);
        Assert.IsType<NotFoundResult>(unknownLogin);
    }

    [Fact]
    public void The_authenticated_landing_refuses_a_session_of_another_business_before_the_action_runs()
    {
        var controller = LandingController(OtherSlug, ClientPrincipal(Slug));
        var executing = ActionExecuting(controller);

        controller.OnActionExecuting(executing);

        // The cookie belongs to one business, so another tenant's route is refused before the action runs.
        Assert.Equal("/r/yoga/cuenta/ingresar", Assert.IsType<RedirectResult>(executing.Result).Url);
    }

    [Fact]
    public void The_authenticated_landing_accepts_the_session_of_its_own_business_and_is_require_authenticated()
    {
        var controller = LandingController(Slug, ClientPrincipal(Slug));
        var executing = ActionExecuting(controller);

        controller.OnActionExecuting(executing);
        var result = controller.Index();

        Assert.Null(executing.Result);
        // T21 replaced the placeholder with the real panel, so /cuenta now hands the client over to the safe agenda of
        // the authenticated business. The target is the claim-scoped slug, never the route value.
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/r/peluqueria/mis-turnos", redirect.Url);
        var authorize = typeof(ClientAccountHomeController).GetCustomAttribute<AuthorizeAttribute>(inherit: true);
        Assert.Equal(AuthSchemes.Client, authorize!.AuthenticationSchemes);
    }

    [Fact]
    public async Task No_token_or_password_is_ever_echoed_back_to_a_message_or_a_flash()
    {
        var unknownLink = await _controller.Verify(
            Slug, new ClientVerifyEmailViewModel { Token = "token-secreto" }, CancellationToken.None);
        var status = StatusOf(unknownLink);
        Assert.DoesNotContain("token-secreto", status.Title);
        Assert.DoesNotContain("token-secreto", status.Message);
        Assert.DoesNotContain("token-secreto", status.ActionUrl ?? "");

        var account = _accesses.SeedActiveAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, Now.AddDays(-1));
        _accesses.SeedToken(account.Id, AccountTokenPurpose.PasswordReset, ResetToken, Now.AddMinutes(30), Now);
        await _controller.Reset(Slug, ResetForm(), CancellationToken.None);

        var flash = (string)_controller.TempData["FlashMessage"]!;
        Assert.DoesNotContain("nueva-clave", flash);
        Assert.DoesNotContain(ResetToken, flash);
    }

    // --- Helpers -----------------------------------------------------------------------------------------------

    private ClientAccountController CreateController(WorkUpOptions? options = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = new SingleServiceProvider(_authentication) };
        return new ClientAccountController(
            new ClientRegistrationService(
                _businesses, _registrations, _unitOfWork, _passwordHasher, new TrustedUrlBuilder(), _clock),
            new ClientAccessService(
                _businesses, _accesses, _unitOfWork, _passwordHasher, new TrustedUrlBuilder(), _clock),
            _businesses,
            invitationAcceptanceService: null,
            options is null ? null : Options.Create(options!))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
                RouteData = new RouteData()
            },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider()),
            Url = new UrlHelper(new ActionContext { HttpContext = httpContext, RouteData = new RouteData() })
        };
    }

    /// <summary>A second controller over the same stores, so a request never inherits the previous ModelState.</summary>
    private ClientAccountController Rebuild() => CreateController();

    private static ClientAccountHomeController LandingController(string slug, ClaimsPrincipal user)
    {
        var routeData = new RouteData();
        routeData.Values["slug"] = slug;
        var controller = new ClientAccountHomeController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user },
                RouteData = routeData,
                ActionDescriptor = new ControllerActionDescriptor()
            }
        };
        return controller;
    }

    private static ActionExecutingContext ActionExecuting(ControllerBase controller) =>
        new(controller.ControllerContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);

    private int BusinessId(string slug) => _businesses.All.Single(business => business.Slug == slug).Id;

    private const string HostileReturnUrl = "https://evil.example.invalid/robo";

    /// <summary>
    /// Reproduces what the model binder leaves in ModelState for the request: a simple action parameter bound from
    /// the query string on the GET, and the hidden field of the form on the POST. The entry keeps the raw values the
    /// request carried, and ModelState matches names case-insensitively, so "returnUrl" also answers a lookup of
    /// "ReturnUrl".
    /// </summary>
    private static void SeedBoundReturnUrl(
        ClientAccountController controller, string returnUrl, string key = "returnUrl")
    {
        controller.ModelState.SetModelValue(key, new[] { returnUrl }, returnUrl);
        // A field the binder accepted ends the request as Valid, and ModelState.IsValid counts an Unvalidated entry
        // as not valid, so the seeded entry is marked the same way a real request leaves it.
        controller.ModelState.MarkFieldValid(key);
    }

    /// <summary>
    /// The destination the sign-in form really renders in its hidden field. The input tag helper asks the HTML
    /// generator for the field, and the generator prefers the raw value the binder left in ModelState, falling back
    /// to the model only when that entry or its raw value is absent. Mirroring both steps is what turns a regression
    /// here into a rendering defect instead of only a controller-state one.
    /// </summary>
    private static string? RenderedReturnUrl(ClientAccountController controller, ClientLoginViewModel model)
    {
        if (controller.ModelState.TryGetValue(nameof(ClientLoginViewModel.ReturnUrl), out var entry)
            && entry.RawValue is not null)
        {
            var raw = entry.RawValue is string[] values ? values.FirstOrDefault() : entry.RawValue;
            return Convert.ToString(raw, CultureInfo.InvariantCulture);
        }

        return model.ReturnUrl;
    }

    private void SeedBusiness(string slug, string name) =>
        _businesses.InsertAsync(new Business
        {
            Name = name,
            Slug = slug,
            ContactPhone = "1145550000",
            CreatedAt = Now
        }).GetAwaiter().GetResult();

    private FakeClientAccount SeedPendingAccount() =>
        _registrations.SeedPendingAccount(BusinessId(Slug), Email, "hashed:" + PlainPassword, "Ana", "Gómez", Now);

    private static ClientRegisterViewModel RegisterModel(string email = Email) => new()
    {
        FirstName = "Ana",
        LastName = "Gómez",
        Email = email,
        Password = PlainPassword,
        ConfirmPassword = PlainPassword
    };

    private static ClientLoginViewModel LoginModel(string email = Email) => new()
    {
        Email = email,
        Password = PlainPassword
    };

    private static ClientResetPasswordViewModel ResetForm() => new()
    {
        Token = ResetToken,
        NewPassword = "nueva-clave",
        ConfirmPassword = "nueva-clave"
    };

    private static IReadOnlyList<string> Messages(IActionResult result) =>
        Assert.IsType<ViewResult>(result).ViewData.ModelState[string.Empty]!.Errors
            .Select(error => error.ErrorMessage)
            .ToArray();

    private static ClientAccountStatusViewModel StatusOf(IActionResult result)
    {
        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Status", view.ViewName);
        return Assert.IsType<ClientAccountStatusViewModel>(view.Model);
    }

    private static ClaimsPrincipal ClientPrincipal(string slug)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(WorkUpClaims.BusinessId, "1"),
                new Claim(WorkUpClaims.BusinessName, "Peluquería Marina"),
                new Claim(WorkUpClaims.BusinessSlug, slug),
                new Claim(WorkUpClaims.ClientAccountId, "7"),
                new Claim(WorkUpClaims.ClientId, "3"),
                new Claim(ClaimTypes.Email, Email)
            ],
            AuthSchemes.Client);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Records the cookie operations the controller asks for, so a test proves who signs in and who does not.</summary>
    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public List<string> SignInSchemes { get; } = [];

        public List<ClaimsPrincipal> Principals { get; } = [];

        public List<string> SignOutSchemes { get; } = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignInSchemes.Add(scheme!);
            Principals.Add(principal);
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignOutSchemes.Add(scheme!);
            return Task.CompletedTask;
        }
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
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
