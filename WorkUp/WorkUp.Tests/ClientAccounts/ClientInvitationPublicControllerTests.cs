using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Security;
using WorkUp.Controllers;
using WorkUp.Domain.Entities;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.ClientAccounts;

namespace WorkUp.Tests.ClientAccounts;

/// <summary>
/// Public acceptance of an owner-assisted invitation (RF-26, T22 slice 2). These are unit tests over the real
/// controller and the real acceptance service with the transactional in-memory port, so no DI container, no routing
/// pipeline and no HTTP server are involved. The behaviors a public invitation link can get wrong are pinned here:
/// the GET never spends the link and shows only the destination email and the deadline, the mode comes from a
/// server-side whitelist, a malformed form or a wrong password spends nothing, the rejection is generic (one page for
/// every unusable link, one owner-assistance page that discloses no ficha, email or history), and a successful
/// acceptance redirects to sign-in instead of opening a session. The form can only carry the token, the mode and the
/// password: the business, the ficha and the destination email are never bound from the request.
/// </summary>
public sealed class ClientInvitationPublicControllerTests
{
    private const string Slug = "peluqueria";
    private const string OtherSlug = "yoga";
    private const string FichaEmail = "ana@example.com";
    private const string InvitedEmail = "nueva@example.com";
    private const string Token = "token-invitacion";
    private const string Password = "clave-segura";
    private static readonly DateTime Now = new(2026, 3, 20, 10, 0, 0);

    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeClientRegistrationRepository _registrations = new();
    private readonly FakeClientAccessRepository _accesses = new();
    private readonly FakeUnitOfWork _accountUnitOfWork = new();
    private readonly FakeClientInvitationAcceptanceRepository _repository = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeClock _clock = new() { Now = Now };
    private readonly RecordingAuthenticationService _authentication = new();
    private readonly FakeAcceptanceUnitOfWork _acceptanceUnitOfWork;
    private readonly ClientInvitationAcceptanceService _acceptance;
    private readonly ClientAccountController _controller;

    public ClientInvitationPublicControllerTests()
    {
        SeedBusiness(Slug, "Peluquería Marina");
        SeedBusiness(OtherSlug, "Estudio de Yoga");
        _acceptanceUnitOfWork = _repository.CreateUnitOfWork();
        _acceptance = new ClientInvitationAcceptanceService(
            _businesses, _repository, _acceptanceUnitOfWork, _passwordHasher, _clock);
        _controller = CreateController();
    }

    // --- GET of the invitation link (RF-26) --------------------------------------------------------------------

    [Fact]
    public async Task Get_invitation_shows_only_the_destination_and_expiry_without_consuming_the_link()
    {
        SeedFichaWithInvitation();

        var result = await _controller.Invitation(Slug, Token, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Invitation", view.ViewName);
        var model = Assert.IsType<ClientInvitationViewModel>(view.Model);
        Assert.Equal(Token, model.Token);
        // The link proves the invited destination, which differs from the email of the ficha: only that address and
        // the deadline are shown, never a ficha, a note or a history.
        Assert.Equal(InvitedEmail, model.Email);
        Assert.NotEqual(FichaEmail, model.Email);
        Assert.Equal(Now.AddHours(48), model.ExpiresAt);
        // A link scanner opening the emailed URL finds a form and nothing else: the single use is still live and no
        // acceptance transaction was opened.
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(Now));
        Assert.Equal(0, _acceptanceUnitOfWork.BeginCount);
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl);
        Assert.Equal("no-cache", _controller.Response.Headers.Pragma);
        Assert.Equal("no-referrer", _controller.Response.Headers["Referrer-Policy"]);
    }

    [Fact]
    public void The_invitation_acceptance_view_model_never_carries_ficha_or_history_data()
    {
        var names = string.Join(',', typeof(ClientInvitationViewModel).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Client", names);
        Assert.DoesNotContain("History", names);
        Assert.DoesNotContain("Note", names);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("foreign")]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task Get_invitation_answers_one_generic_page_with_token_headers_for_every_unusable_link(string kind)
    {
        SeedFichaWithInvitation(kind);
        var token = kind == "blank" ? "   " : Token;

        var result = await _controller.Invitation(Slug, token, CancellationToken.None);

        var status = StatusOf(result);
        Assert.Equal("El enlace no está disponible", status.Title);
        Assert.DoesNotContain(Token, status.Title);
        Assert.DoesNotContain(Token, status.Message);
        // Even an unusable link is uncached and referrer-free, so the raw token never leaks through a shared cache or
        // a referrer header.
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl);
        Assert.Equal("no-referrer", _controller.Response.Headers["Referrer-Policy"]);
        Assert.Equal(0, _acceptanceUnitOfWork.BeginCount);
    }

    // --- POST of the acceptance (RF-26) ------------------------------------------------------------------------

    [Fact]
    public async Task Post_invitation_creates_the_account_redirects_to_login_and_starts_no_session()
    {
        var (business, client) = SeedFichaWithInvitation();

        var result = await _controller.Invitation(Slug, NewAccountForm(), CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/r/peluqueria/cuenta/ingresar", redirect.Url);
        var account = Assert.Single(_repository.Accounts);
        Assert.Equal(business.Id, account.BusinessId);
        Assert.Equal(InvitedEmail, account.Email);
        Assert.Equal(client.Id, account.ClientId);
        Assert.Equal(Now, account.EmailVerifiedAt);
        Assert.Equal(_passwordHasher.Hash(Password), account.PasswordHash);
        Assert.Equal(Now, Assert.Single(_repository.Invitations).UsedAt);
        Assert.Equal(1, _acceptanceUnitOfWork.CommitCount);
        // Accepting links the account but never signs anyone in: the session starts only with the password.
        Assert.Empty(_authentication.SignInSchemes);
        Assert.Equal("Listo: vinculamos tu cuenta. Ingresá con tu email y contraseña.", _controller.TempData["FlashMessage"]);
    }

    [Fact]
    public async Task Post_invitation_binds_a_pending_account_with_the_correct_password()
    {
        var (business, client) = SeedFichaWithInvitation();
        var pending = _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password));

        var result = await _controller.Invitation(Slug, ExistingAccountForm(), CancellationToken.None);

        Assert.Equal("/r/peluqueria/cuenta/ingresar", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(client.Id, pending.Account.ClientId);
        Assert.Equal(Now, pending.Account.EmailVerifiedAt);
        Assert.Equal(Now, Assert.Single(_repository.Invitations).UsedAt);
        Assert.Empty(_authentication.SignInSchemes);
    }

    [Fact]
    public async Task Post_invitation_existing_account_accepts_the_stored_password_without_the_new_minimum()
    {
        // The 8-character minimum is a rule for a new password; an existing account only proves its own.
        var (business, client) = SeedFichaWithInvitation();
        _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash("corta"));
        var form = ExistingAccountForm();
        form.Password = "corta";

        var result = await _controller.Invitation(Slug, form, CancellationToken.None);

        Assert.Equal("/r/peluqueria/cuenta/ingresar", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task Post_invitation_with_a_wrong_password_keeps_the_link_and_reexamines_the_form()
    {
        var (business, client) = SeedFichaWithInvitation();
        var pending = _repository.SeedPendingAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password));
        var form = ExistingAccountForm();
        form.Password = "otra-clave-larga";

        var result = await _controller.Invitation(Slug, form, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Invitation", view.ViewName);
        Assert.Equal(new[] { "Email o contraseña incorrectos." }, Messages(result));
        // The submitted password is never written back into the form nor left in the validation state.
        Assert.Equal("", Assert.IsType<ClientInvitationViewModel>(view.Model).Password);
        Assert.Null(view.ViewData.ModelState[nameof(ClientInvitationViewModel.Password)]?.AttemptedValue);
        // The wrong password spends nothing: the link is live and the account is still pending.
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(Now));
        Assert.Null(pending.Account.ClientId);
        Assert.Null(pending.Account.EmailVerifiedAt);
        Assert.Empty(_authentication.SignInSchemes);
    }

    [Fact]
    public async Task Post_invitation_with_a_mismatched_confirmation_reexamines_the_form_without_consuming_the_link()
    {
        SeedFichaWithInvitation();
        var form = NewAccountForm();
        form.ConfirmPassword = "otra-clave-segura";

        var result = await _controller.Invitation(Slug, form, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Invitation", view.ViewName);
        Assert.Contains("Las contraseñas no coinciden.", AllMessages(result));
        // The re-render refreshes the destination from inspection, never from the request.
        Assert.Equal(InvitedEmail, Assert.IsType<ClientInvitationViewModel>(view.Model).Email);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(Now));
        Assert.Equal(0, _acceptanceUnitOfWork.BeginCount);
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task Post_invitation_new_account_with_a_short_password_reexamines_the_form_without_consuming_the_link()
    {
        SeedFichaWithInvitation();
        var form = NewAccountForm();
        form.Password = "corta";
        form.ConfirmPassword = "corta";

        var result = await _controller.Invitation(Slug, form, CancellationToken.None);

        Assert.Equal("Invitation", Assert.IsType<ViewResult>(result).ViewName);
        Assert.Contains("La contraseña debe tener al menos 8 caracteres.", AllMessages(result));
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(Now));
        Assert.Equal(0, _acceptanceUnitOfWork.BeginCount);
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task Post_invitation_never_writes_the_submitted_password_back_into_the_form()
    {
        SeedFichaWithInvitation();
        var form = NewAccountForm();
        form.Password = "secreto-clave-nueva";
        form.ConfirmPassword = "otra-clave-segura";

        var result = await _controller.Invitation(Slug, form, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ClientInvitationViewModel>(view.Model);
        // The error of the rejected confirmation survives, but neither the model nor the validation state carries the
        // submitted secret back into the re-rendered page.
        Assert.Equal("", model.Password);
        Assert.Null(model.ConfirmPassword);
        var confirmation = view.ViewData.ModelState[nameof(ClientInvitationViewModel.ConfirmPassword)]!;
        Assert.Null(confirmation.AttemptedValue);
        Assert.NotEmpty(confirmation.Errors);
        Assert.Contains("Las contraseñas no coinciden.", AllMessages(result));
        Assert.DoesNotContain("secreto-clave-nueva", string.Join('|', AllMessages(result)));
    }

    [Theory]
    [InlineData("otro")]
    [InlineData("")]
    public async Task Post_invitation_with_an_unknown_mode_is_refused_without_consuming_the_link(string mode)
    {
        SeedFichaWithInvitation();
        var form = NewAccountForm();
        form.Mode = mode;

        var result = await _controller.Invitation(Slug, form, CancellationToken.None);

        Assert.Equal("Invitation", Assert.IsType<ViewResult>(result).ViewName);
        Assert.Contains("Elegí cómo querés continuar.", AllMessages(result));
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(Now));
        Assert.Equal(0, _acceptanceUnitOfWork.BeginCount);
        Assert.Empty(_repository.Accounts);
    }

    [Fact]
    public async Task Post_invitation_sends_an_already_linked_account_to_the_business_without_disclosing_it()
    {
        var (business, client) = SeedFichaWithInvitation();
        var otherFicha = _repository.SeedClient(business.Id, "Beto", "Pérez", "beto@example.com");
        _repository.SeedVerifiedAccount(business.Id, InvitedEmail, _passwordHasher.Hash(Password), otherFicha.Id, Now);

        var result = await _controller.Invitation(Slug, ExistingAccountForm(), CancellationToken.None);

        var status = StatusOf(result);
        Assert.Equal("Necesitamos que hables con el negocio", status.Title);
        Assert.Equal("No se pudo vincular esta cuenta. Contactá al negocio.", status.Message);
        // No ficha, no email and no history is echoed, and no link into the panel is offered.
        Assert.DoesNotContain("Beto", status.Message);
        Assert.DoesNotContain("beto@example.com", status.Message);
        Assert.DoesNotContain(InvitedEmail, status.Message);
        Assert.DoesNotContain("Ana", status.Message);
        // Even the business name stays out: the page only sends the person to the business, generically.
        Assert.DoesNotContain("Peluquería Marina", status.Message);
        Assert.Null(status.ActionUrl);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(Now));
        Assert.Empty(_authentication.SignInSchemes);
    }

    [Fact]
    public async Task Post_invitation_new_account_on_an_already_linked_ficha_is_sent_to_the_business_generically()
    {
        var (business, client) = SeedFichaWithInvitation();
        _repository.SeedVerifiedAccount(business.Id, "otra@example.com", _passwordHasher.Hash(Password), client.Id, Now);

        var result = await _controller.Invitation(Slug, NewAccountForm(), CancellationToken.None);

        var status = StatusOf(result);
        Assert.Equal("No se pudo vincular esta cuenta. Contactá al negocio.", status.Message);
        Assert.DoesNotContain("otra@example.com", status.Message);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(Now));
    }

    [Fact]
    public async Task Post_invitation_with_a_blank_token_answers_the_generic_page()
    {
        SeedFichaWithInvitation();
        var form = ExistingAccountForm();
        form.Token = "";

        var result = await _controller.Invitation(Slug, form, CancellationToken.None);

        Assert.Equal("El enlace no está disponible", StatusOf(result).Title);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(Now));
        Assert.Empty(_authentication.SignInSchemes);
    }

    [Fact]
    public async Task Post_invitation_replayed_link_answers_the_generic_page()
    {
        SeedFichaWithInvitation();
        await _controller.Invitation(Slug, NewAccountForm(), CancellationToken.None);

        var replay = await Rebuild().Invitation(Slug, NewAccountForm(), CancellationToken.None);

        Assert.Equal("El enlace no está disponible", StatusOf(replay).Title);
        Assert.Single(_repository.Accounts);
        Assert.Empty(_authentication.SignInSchemes);
    }

    // --- Public surface ----------------------------------------------------------------------------------------

    [Fact]
    public void The_invitation_routes_are_declared_under_the_tenant_slug_and_the_post_is_antiforgery_protected()
    {
        var controller = typeof(ClientAccountController);
        Assert.Equal("r/{slug}/cuenta", controller.GetCustomAttribute<RouteAttribute>()!.Template);

        var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == nameof(ClientAccountController.Invitation)).ToArray();
        var get = methods.Single(method => method.GetCustomAttribute<HttpGetAttribute>() is not null);
        var post = methods.Single(method => method.GetCustomAttribute<HttpPostAttribute>() is not null);

        Assert.Equal("invitacion/{token}", get.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("invitacion", post.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public void The_invitation_form_binds_only_the_token_mode_and_password_never_a_business_client_or_email()
    {
        var model = typeof(ClientInvitationViewModel);
        Assert.All(model.GetProperties(), property => Assert.DoesNotContain(
            "Business", property.Name, StringComparison.OrdinalIgnoreCase));
        Assert.All(model.GetProperties(), property => Assert.DoesNotContain(
            "Client", property.Name, StringComparison.OrdinalIgnoreCase));

        // The destination and the deadline are display-only: the binder can never fill them from the request.
        Assert.NotNull(model.GetProperty(nameof(ClientInvitationViewModel.Email))!.GetCustomAttribute<BindNeverAttribute>());
        Assert.NotNull(model.GetProperty(nameof(ClientInvitationViewModel.ExpiresAt))!.GetCustomAttribute<BindNeverAttribute>());
        // The values the controller must read from the body stay bindable.
        Assert.Null(model.GetProperty(nameof(ClientInvitationViewModel.Token))!.GetCustomAttribute<BindNeverAttribute>());
        Assert.Null(model.GetProperty(nameof(ClientInvitationViewModel.Mode))!.GetCustomAttribute<BindNeverAttribute>());
        Assert.Null(model.GetProperty(nameof(ClientInvitationViewModel.Password))!.GetCustomAttribute<BindNeverAttribute>());
    }

    [Theory]
    [InlineData(ClientInvitationModes.NewAccount, true)]
    [InlineData(ClientInvitationModes.ExistingAccount, true)]
    [InlineData("otro", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_two_known_modes_pass_the_whitelist(string? mode, bool known) =>
        Assert.Equal(known, ClientInvitationModes.IsKnown(mode));

    [Fact]
    public void The_invitation_view_offers_the_two_modes_and_renders_no_ficha_history()
    {
        var view = ReadInvitationView();
        Assert.Contains("/cuenta/invitacion", view);
        Assert.Contains("ClientInvitationModes.NewAccount", view);
        Assert.Contains("ClientInvitationModes.ExistingAccount", view);
        Assert.Contains("AntiForgeryToken", view);
        Assert.Contains("@Model.Email", view);
        Assert.Contains("Model.ExpiresAt", view);
        Assert.Contains("ConfirmPassword", view);
        Assert.DoesNotContain("historial", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientNotes", view);
    }

    [Fact]
    public async Task No_rejection_message_or_validation_summary_ever_carries_the_raw_token()
    {
        SeedFichaWithInvitation();
        var mismatch = NewAccountForm();
        mismatch.ConfirmPassword = "otra-clave-segura";

        var form = await _controller.Invitation(Slug, mismatch, CancellationToken.None);
        Assert.All(AllMessages(form), message => Assert.DoesNotContain(Token, message));

        var unknown = NewAccountForm();
        unknown.Token = "token-secreto";
        var status = StatusOf(await Rebuild().Invitation(Slug, unknown, CancellationToken.None));
        Assert.DoesNotContain("token-secreto", status.Title);
        Assert.DoesNotContain("token-secreto", status.Message);
    }

    [Fact]
    public async Task The_invitation_pages_report_not_found_for_an_unknown_slug()
    {
        var get = await _controller.Invitation("negocio-inexistente", Token, CancellationToken.None);
        var post = await _controller.Invitation("negocio-inexistente", NewAccountForm(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(get);
        Assert.IsType<NotFoundResult>(post);
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task The_invitation_pages_fail_closed_without_the_acceptance_service()
    {
        var controller = CreateController(withAcceptance: false);

        var get = await controller.Invitation(Slug, Token, CancellationToken.None);
        var post = await controller.Invitation(Slug, NewAccountForm(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(get);
        Assert.IsType<NotFoundResult>(post);
    }

    // --- Helpers -----------------------------------------------------------------------------------------------

    private ClientAccountController CreateController(bool withAcceptance = true)
    {
        var httpContext = new DefaultHttpContext { RequestServices = new SingleServiceProvider(_authentication) };
        return new ClientAccountController(
            new ClientRegistrationService(
                _businesses, _registrations, _accountUnitOfWork, _passwordHasher, new TrustedUrlBuilder(), _clock),
            new ClientAccessService(
                _businesses, _accesses, _accountUnitOfWork, _passwordHasher, new TrustedUrlBuilder(), _clock),
            _businesses,
            withAcceptance ? _acceptance : null)
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

    private Business Business(string slug) => _businesses.All.Single(business => business.Slug == slug);

    private int BusinessId(string slug) => Business(slug).Id;

    private void SeedBusiness(string slug, string name) =>
        _businesses.InsertAsync(new Business
        {
            Name = name,
            Slug = slug,
            ContactPhone = "1145550000",
            CreatedAt = Now
        }).GetAwaiter().GetResult();

    /// <summary>
    /// A ficha of the invited business plus the invitation the owner issued. <paramref name="kind"/> selects the
    /// unusable state the GET must answer generically: a missing, blank, foreign, used, expired or revoked link.
    /// </summary>
    private (Business Business, Client Client) SeedFichaWithInvitation(string kind = "live")
    {
        var business = Business(Slug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", FichaEmail);
        switch (kind)
        {
            case "missing":
            case "blank":
                break;
            case "foreign":
                var yogaId = BusinessId(OtherSlug);
                var yogaClient = _repository.SeedClient(yogaId, "Beto", "Pérez", "beto@example.com");
                _repository.SeedInvitation(yogaId, yogaClient.Id, InvitedEmail, SecureToken.Hash(Token), Now.AddHours(48));
                break;
            default:
                _repository.SeedInvitation(
                    business.Id, client.Id, InvitedEmail, SecureToken.Hash(Token),
                    expiresAt: kind == "expired" ? Now : Now.AddHours(48),
                    usedAt: kind == "used" ? Now : null,
                    revokedAt: kind == "revoked" ? Now : null);
                break;
        }

        return (business, client);
    }

    private static ClientInvitationViewModel NewAccountForm() => new()
    {
        Token = Token,
        Mode = ClientInvitationModes.NewAccount,
        Password = Password,
        ConfirmPassword = Password
    };

    private static ClientInvitationViewModel ExistingAccountForm() => new()
    {
        Token = Token,
        Mode = ClientInvitationModes.ExistingAccount,
        Password = Password
    };

    private static IReadOnlyList<string> Messages(IActionResult result) =>
        Assert.IsType<ViewResult>(result).ViewData.ModelState[string.Empty]!.Errors
            .Select(error => error.ErrorMessage)
            .ToArray();

    private static IReadOnlyList<string> AllMessages(IActionResult result) =>
        Assert.IsType<ViewResult>(result).ViewData.ModelState.Values
            .SelectMany(entry => entry.Errors)
            .Select(error => error.ErrorMessage)
            .ToArray();

    private static ClientAccountStatusViewModel StatusOf(IActionResult result)
    {
        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Status", view.ViewName);
        return Assert.IsType<ClientAccountStatusViewModel>(view.Model);
    }

    private static string ReadInvitationView()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Views", "ClientAccount", "Invitation.cshtml"));
    }

    /// <summary>Records the cookie operations the controller asks for, so a test proves who signs in and who does not.</summary>
    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public List<string> SignInSchemes { get; } = [];

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
