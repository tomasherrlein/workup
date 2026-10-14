using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Clients;
using WorkUp.Data.ClientAccounts;
using WorkUp.Domain.Entities;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Clients;

/// <summary>
/// Owner panel of the assisted invitation (RF-26, T22, slice 2). The 48 h lifetime, the single live invitation, the
/// tenant scope and the revocation are already pinned on <c>ClientInvitationService</c>; what this fixture adds is what
/// the panel contributes: the business always comes from the owner cookie and never from the form, a missing or foreign
/// ficha answers the same generic 404, the confirmation checkbox is judged before the service even starts a
/// transaction, the success flash states the invitation was queued and not delivered, the raw token and its stored
/// SHA-256 hash never reach the owner HTML or the flash, and the invitation link derives only from the validated
/// <c>WorkUp:PublicBaseUrl</c> — no request Host can retarget it.
/// </summary>
public sealed class ClientInvitationOwnerControllerTests
{
    private const int BusinessId = 1;
    private const int YogaBusinessId = 2;
    private const string HairdresserSlug = "peluqueria";
    private const string YogaSlug = "yoga";
    private const string TrustedBaseUrl = "https://reservas.example.com";
    private const string AlreadyLinkedMessage = "Esa ficha ya tiene una cuenta vinculada. No hace falta invitarla.";
    private const string InvitationFormMessage =
        "Revisá el email de destino y confirmá que lo verificaste con la persona.";

    private static readonly DateTime ClockNow = new(2026, 3, 10, 9, 0, 0);

    private static ClientInvitationFormViewModel Input(string email, bool confirmed) =>
        new() { Email = email, EmailConfirmed = confirmed };

    [Fact]
    public async Task Invite_post_queues_the_invitation_and_redirects_to_the_detail()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var clientId = SeedRealClient(harness, BusinessId, "ana@example.com");

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.Invite(
            clientId, Input("nueva@example.com", confirmed: true), CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(clientId, redirect.RouteValues!["id"]);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);

        // The destination is the email the owner confirmed on the form, editable and possibly different from the one on
        // the ficha; the flash says the notice was queued, never that it was delivered (the outbox sender is T23).
        var flash = Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]);
        Assert.Contains("nueva@example.com", flash, StringComparison.Ordinal);
        Assert.Contains("encolada", flash, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("todavía no se envía", flash, StringComparison.Ordinal);

        var invitation = Assert.Single(harness.Invitations.Invitations);
        Assert.Equal(BusinessId, invitation.BusinessId);
        Assert.Equal(clientId, invitation.ClientId);
        Assert.Equal("nueva@example.com", invitation.Email);
        Assert.Equal(ClockNow.AddHours(48), invitation.ExpiresAt);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);

        var notification = Assert.Single(harness.Invitations.Notifications);
        Assert.Equal("nueva@example.com", notification.Recipient);
    }

    [Fact]
    public async Task Invite_post_never_publishes_the_raw_token_or_its_stored_hash()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var clientId = SeedRealClient(harness, BusinessId, "ana@example.com");

        await harness.Controller.Invite(clientId, Input("nueva@example.com", confirmed: true), CancellationToken.None);

        var flash = Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]);
        var notification = Assert.Single(harness.Invitations.Notifications);
        var rawToken = TokenFrom(notification);
        var storedHash = Convert.ToHexString(Assert.Single(harness.Invitations.Invitations).TokenHash);

        Assert.DoesNotContain(rawToken, flash, StringComparison.Ordinal);
        Assert.DoesNotContain(storedHash, flash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawToken, string.Join("|", harness.Controller.TempData.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invite_post_without_the_confirmation_checkbox_rejects_before_the_service()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var clientId = SeedRealClient(harness, BusinessId, "ana@example.com");

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.Invite(
            clientId, Input("nueva@example.com", confirmed: false), CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(clientId, redirect.RouteValues!["id"]);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal(InvitationFormMessage, Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.False(harness.Controller.ModelState.IsValid);

        // An unconfirmed email never reaches the service: no transaction is opened and nothing is stored.
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Invitations.Invitations);
        Assert.Empty(harness.Invitations.Notifications);
    }

    [Fact]
    public async Task Invite_post_without_a_destination_email_is_rejected_without_writing()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var clientId = SeedRealClient(harness, BusinessId, "ana@example.com");

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.Invite(
            clientId, Input("", confirmed: true), CancellationToken.None));

        // A direct controller call runs no ModelState binding, so the business layer judges the blank destination;
        // through the MVC pipeline the [Required] attribute of the model rejects it before that.
        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Ingresá el email de destino.", Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Invitations.Invitations);
    }

    [Fact]
    public async Task Invite_post_with_a_malformed_destination_email_writes_nothing()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var clientId = SeedRealClient(harness, BusinessId, "ana@example.com");

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.Invite(
            clientId, Input("no-es-email", confirmed: true), CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Ingresá un email válido.", Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Invitations.Invitations);
    }

    [Fact]
    public async Task Invite_post_of_a_missing_ficha_answers_a_generic_not_found()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);

        var result = await harness.Controller.Invite(
            999, Input("nueva@example.com", confirmed: true), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(harness.Controller.TempData.ContainsKey("FlashMessage"));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Invitations.Invitations);
    }

    [Fact]
    public async Task Invite_post_of_a_foreign_ficha_answers_the_same_generic_not_found()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var yogaBusinessId = SeedBusiness(harness, YogaSlug, "Estudio de Yoga");
        var foreignClientId = harness.Invitations.SeedClient(
            yogaBusinessId, "Ana", "Gómez", "ana@example.com").Id;

        var result = await harness.Controller.Invite(
            foreignClientId, Input("nueva@example.com", confirmed: true), CancellationToken.None);

        // Same generic answer as a missing ficha: the owner can never tell the two apart.
        Assert.IsType<NotFoundResult>(result);
        Assert.False(harness.Controller.TempData.ContainsKey("FlashMessage"));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Invitations.Invitations);
    }

    [Fact]
    public async Task Invite_post_scopes_every_read_to_the_business_of_the_owner_claim()
    {
        var clock = new FakeClock { Now = ClockNow };
        var panel = Create(clock);
        var yogaBusinessId = SeedBusiness(panel, YogaSlug, "Estudio de Yoga");
        var hairdresserClientId = panel.Invitations.SeedClient(
            BusinessId, "Ana", "Gómez", "ana@example.com").Id;
        var yogaClientId = panel.Invitations.SeedClient(
            yogaBusinessId, "Lucy", "Pérez", "lucy@example.com").Id;

        // The form carries no tenant: the same ficha id posted by the owner of the other business is refused.
        var foreignAttempt = await panel.Controller.Invite(
            yogaClientId, Input("nueva@example.com", confirmed: true), CancellationToken.None);
        Assert.IsType<NotFoundResult>(foreignAttempt);
        Assert.Empty(panel.Invitations.Invitations);

        // Her own ficha goes through, with the business of her cookie.
        var ownAttempt = Assert.IsType<RedirectToActionResult>(await panel.Controller.Invite(
            hairdresserClientId, Input("nueva@example.com", confirmed: true), CancellationToken.None));
        Assert.Equal("Detail", ownAttempt.ActionName);
        Assert.Equal("Success", panel.Controller.TempData["FlashType"]);
        Assert.Equal(BusinessId, Assert.Single(panel.Invitations.Invitations).BusinessId);

        // And the owner of the second business reaches her own ficha, not the neighbor's.
        var yogaPanel = Create(clock, businessId: yogaBusinessId);
        Assert.Equal(yogaBusinessId, SeedBusiness(yogaPanel, YogaSlug, "Estudio de Yoga"));
        var yogaOwnId = yogaPanel.Invitations.SeedClient(
            yogaBusinessId, "Lucy", "Pérez", "lucy@example.com").Id;

        var yogaAttempt = Assert.IsType<RedirectToActionResult>(await yogaPanel.Controller.Invite(
            yogaOwnId, Input("nueva@example.com", confirmed: true), CancellationToken.None));

        Assert.Equal("Detail", yogaAttempt.ActionName);
        Assert.Equal("Success", yogaPanel.Controller.TempData["FlashType"]);
        Assert.Equal(yogaBusinessId, Assert.Single(yogaPanel.Invitations.Invitations).BusinessId);
    }

    [Fact]
    public async Task Invite_post_of_an_already_linked_ficha_flashes_the_safe_message()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var clientId = harness.Invitations.SeedClient(
            BusinessId, "Ana", "Gómez", "ana@example.com", hasLinkedAccount: true).Id;

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.Invite(
            clientId, Input("nueva@example.com", confirmed: true), CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(clientId, redirect.RouteValues!["id"]);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal(AlreadyLinkedMessage, Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Invitations.Invitations);
    }

    [Theory]
    [InlineData("Outbox")]
    [InlineData("Smtp")]
    public async Task Invite_post_states_the_email_mode_without_promising_delivery(string mode)
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock, options: new WorkUpOptions { Email = new EmailOptions { Mode = mode } });
        var clientId = SeedRealClient(harness, BusinessId, "ana@example.com");

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.Invite(
            clientId, Input("nueva@example.com", confirmed: true), CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        var flash = Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]);
        Assert.Contains("encolada", flash, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nueva@example.com", flash, StringComparison.Ordinal);
        Assert.DoesNotContain("enviado", flash, StringComparison.OrdinalIgnoreCase);

        if (mode == "Smtp")
        {
            // Deliver only hands the notice to the scheduled dispatch: the inbox delivery is not promised.
            Assert.Contains("envío automático", flash, StringComparison.Ordinal);
            Assert.Contains("no podemos garantizar", flash, StringComparison.Ordinal);
            Assert.DoesNotContain("todavía no se envía", flash, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("todavía no se envía", flash, StringComparison.Ordinal);
            Assert.DoesNotContain("no podemos garantizar", flash, StringComparison.Ordinal);
        }

        // The copy never changes what the action stores or the raw token it keeps out of the panel.
        var notification = Assert.Single(harness.Invitations.Notifications);
        Assert.Equal("nueva@example.com", notification.Recipient);
        Assert.DoesNotContain(TokenFrom(notification), flash, StringComparison.Ordinal);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task RevokeInvitation_post_redirects_to_the_detail_and_is_idempotent()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var clientId = SeedRealClient(harness, BusinessId, "ana@example.com");
        await harness.Controller.Invite(clientId, Input("nueva@example.com", confirmed: true), CancellationToken.None);
        var invitation = Assert.Single(harness.Invitations.Invitations);

        var first = Assert.IsType<RedirectToActionResult>(await harness.Controller.RevokeInvitation(
            clientId, CancellationToken.None));

        Assert.Equal("Detail", first.ActionName);
        Assert.Equal(clientId, first.RouteValues!["id"]);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Invitación revocada.", Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(ClockNow, invitation.RevokedAt);

        // The second revoke reports success and changes nothing new: the first revocation instant is preserved.
        clock.Now = ClockNow.AddHours(5);
        var second = Assert.IsType<RedirectToActionResult>(await harness.Controller.RevokeInvitation(
            clientId, CancellationToken.None));

        Assert.Equal("Detail", second.ActionName);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        Assert.Equal(ClockNow, invitation.RevokedAt);
        Assert.Single(harness.Invitations.Invitations);
    }

    [Fact]
    public async Task RevokeInvitation_post_of_a_missing_or_foreign_ficha_answers_a_generic_not_found()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var yogaBusinessId = SeedBusiness(harness, YogaSlug, "Estudio de Yoga");
        harness.Invitations.SeedClient(yogaBusinessId, "Ana", "Gómez", "ana@example.com");

        var missing = await harness.Controller.RevokeInvitation(999, CancellationToken.None);
        var foreign = await harness.Controller.RevokeInvitation(
            harness.Invitations.Clients.Single(client => client.BusinessId == yogaBusinessId).Id,
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(missing);
        Assert.IsType<NotFoundResult>(foreign);
        Assert.False(harness.Controller.TempData.ContainsKey("FlashMessage"));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
    }

    [Fact]
    public async Task Invitation_actions_fail_closed_when_the_invitation_service_is_not_configured()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock, wireInvitationService: false);

        Assert.IsType<NotFoundResult>(await harness.Controller.Invite(
            BusinessId, Input("nueva@example.com", confirmed: true), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await harness.Controller.RevokeInvitation(BusinessId, CancellationToken.None));

        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Invitations.Invitations);
    }

    [Fact]
    public void Invitation_posts_require_antiforgery_and_never_bind_the_tenant()
    {
        var invite = typeof(ClientsController).GetMethod(nameof(ClientsController.Invite));
        Assert.NotNull(invite);
        Assert.NotNull(invite!.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(invite.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.DoesNotContain(invite.GetParameters(), parameter => parameter.Name is "businessId" or "clientId");
        Assert.Contains(invite.GetParameters(),
            parameter => parameter.ParameterType == typeof(ClientInvitationFormViewModel));

        var revoke = typeof(ClientsController).GetMethod(nameof(ClientsController.RevokeInvitation));
        Assert.NotNull(revoke);
        Assert.NotNull(revoke!.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(revoke.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.DoesNotContain(revoke.GetParameters(), parameter => parameter.Name is "businessId" or "clientId");

        // The detail is a read: it is never gated on an antiforgery token.
        var detail = typeof(ClientsController).GetMethod(nameof(ClientsController.Detail));
        Assert.NotNull(detail);
        Assert.NotNull(detail!.GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(detail.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());

        // The confirmation the action judges is part of the bound input, so a missing checkbox is a model concern.
        var property = typeof(ClientInvitationFormViewModel).GetProperty(nameof(ClientInvitationFormViewModel.EmailConfirmed));
        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property!.PropertyType);

        // The destination is mandatory: the pipeline rejects an empty email before the action runs.
        var email = typeof(ClientInvitationFormViewModel).GetProperty(nameof(ClientInvitationFormViewModel.Email));
        Assert.NotNull(email);
        Assert.Equal("Ingresá el email de destino.", email!.GetCustomAttribute<RequiredAttribute>()?.ErrorMessage);
    }

    [Fact]
    public async Task Detail_get_reads_the_account_state_of_the_own_ficha()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var clientId = SeedRealClient(harness, BusinessId, "ana@example.com");

        var without = Assert.IsType<ClientDetail>(Assert.IsType<ViewResult>(
            await harness.Controller.Detail(clientId, CancellationToken.None)).Model);
        Assert.False(without.HasAccount);

        harness.Clients.SetHasAccount(BusinessId, clientId);
        var with = Assert.IsType<ClientDetail>(Assert.IsType<ViewResult>(
            await harness.Controller.Detail(clientId, CancellationToken.None)).Model);
        Assert.True(with.HasAccount);

        // A foreign or missing ficha never reaches the detail page.
        Assert.IsType<NotFoundResult>(await harness.Controller.Detail(999, CancellationToken.None));
    }

    [Fact]
    public void Detail_view_offers_the_invitation_forms_only_while_the_ficha_has_no_account()
    {
        var view = ReadClientsView("Detail.cshtml");

        var gate = view.IndexOf("!Model.HasAccount", StringComparison.Ordinal);
        var inviteForm = view.IndexOf("asp-action=\"Invite\"", StringComparison.Ordinal);
        var revokeForm = view.IndexOf("asp-action=\"RevokeInvitation\"", StringComparison.Ordinal);
        Assert.True(gate >= 0, "La vista debe mirar !Model.HasAccount.");
        Assert.True(inviteForm > gate, "El formulario de invitación debe quedar detrás del gate !Model.HasAccount.");
        Assert.True(revokeForm > gate, "El formulario de revocación debe quedar detrás del gate !Model.HasAccount.");

        // One form each: no second invitation or revocation offer outside the gate.
        Assert.Equal(1, view.Split("asp-action=\"Invite\"").Length - 1);
        Assert.Equal(1, view.Split("asp-action=\"RevokeInvitation\"").Length - 1);

        // The destination is prefilled from the ficha and stays editable, and the confirmation is explicit and named.
        Assert.Contains("value=\"@Model.Client.Email\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"Email\"", view, StringComparison.Ordinal);
        Assert.Contains("name=\"EmailConfirmed\"", view, StringComparison.Ordinal);
        Assert.Contains("Confirmé este email con la persona", view, StringComparison.Ordinal);
        var emailInput = view.IndexOf("value=\"@Model.Client.Email\"", StringComparison.Ordinal);
        var inputTagStart = view.LastIndexOf("<input", emailInput, StringComparison.Ordinal);
        var inputTag = view[inputTagStart..view.IndexOf('>', emailInput)];
        Assert.DoesNotContain("readonly", inputTag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", inputTag, StringComparison.OrdinalIgnoreCase);

        // The owner HTML never carries the raw token nor the stored hash: neither exists in this page's data.
        Assert.DoesNotContain("Token", view, StringComparison.Ordinal);
        Assert.DoesNotContain("token", view, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", view, StringComparison.OrdinalIgnoreCase);

        // One primary action per screen, as in the rest of the owner panel.
        Assert.Equal(1, view.Split("btn-primary").Length - 1);
    }

    [Fact]
    public void Invitation_url_builder_uses_only_the_configured_trusted_base_url()
    {
        var builder = new ClientInvitationUrlBuilder(Options.Create(new WorkUpOptions { PublicBaseUrl = TrustedBaseUrl }));

        Assert.Equal(
            "https://reservas.example.com/r/peluqueria/cuenta/invitacion/opaque-token",
            builder.BuildInvitationUrl(HairdresserSlug, "opaque-token"));

        // The slug only changes the path: a hostile slug can never move the link to another host.
        Assert.Equal(
            "https://reservas.example.com/r/yoga/cuenta/invitacion/opaque-token",
            builder.BuildInvitationUrl(YogaSlug, "opaque-token"));

        // The host comes from the setting and from nothing else.
        var other = new ClientInvitationUrlBuilder(Options.Create(new WorkUpOptions { PublicBaseUrl = "https://otro.example.com" }));
        Assert.StartsWith("https://otro.example.com/r/peluqueria/", other.BuildInvitationUrl(HairdresserSlug, "opaque-token"), StringComparison.Ordinal);

        // No request input takes part: the builder cannot read a Host header even if one is hostile.
        Assert.DoesNotContain(
            typeof(ClientInvitationUrlBuilder).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(HttpContext)
                || parameter.ParameterType == typeof(IHttpContextAccessor)
                || parameter.ParameterType == typeof(Microsoft.AspNetCore.Http.HttpRequest));
    }

    [Fact]
    public void Invitation_url_builder_refuses_a_base_url_that_is_not_trusted()
    {
        Assert.Throws<ArgumentException>(() =>
            new ClientInvitationUrlBuilder(Options.Create(new WorkUpOptions { PublicBaseUrl = "http://reservas.example.com" })));
        Assert.Throws<ArgumentException>(() =>
            new ClientInvitationUrlBuilder(Options.Create(new WorkUpOptions { PublicBaseUrl = "" })));
    }

    [Fact]
    public void AddWorkUp_wires_the_invitation_builder_as_a_singleton_and_the_repository_and_service_as_scoped()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:WorkUp"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT22OwnerInvitation;Trusted_Connection=True;TrustServerCertificate=True",
            ["WorkUp:PublicBaseUrl"] = TrustedBaseUrl
        }).Build();
        var services = new ServiceCollection();
        services.AddWorkUp(configuration, new FakeHostEnvironment());

        var builder = services.Single(descriptor => descriptor.ServiceType == typeof(IClientInvitationUrlBuilder));
        Assert.Equal(ServiceLifetime.Singleton, builder.Lifetime);
        Assert.Equal(typeof(ClientInvitationUrlBuilder), builder.ImplementationType);

        var repository = services.Single(descriptor => descriptor.ServiceType == typeof(IClientInvitationRepository));
        Assert.Equal(ServiceLifetime.Scoped, repository.Lifetime);
        Assert.Equal(typeof(SqlClientInvitationRepository), repository.ImplementationType);

        var service = services.Single(descriptor => descriptor.ServiceType == typeof(ClientInvitationService));
        Assert.Equal(ServiceLifetime.Scoped, service.Lifetime);

        var acceptanceRepository = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IClientInvitationAcceptanceRepository));
        Assert.Equal(ServiceLifetime.Scoped, acceptanceRepository.Lifetime);
        Assert.Equal(typeof(SqlClientInvitationAcceptanceRepository), acceptanceRepository.ImplementationType);
        var acceptanceService = services.Single(descriptor =>
            descriptor.ServiceType == typeof(ClientInvitationAcceptanceService));
        Assert.Equal(ServiceLifetime.Scoped, acceptanceService.Lifetime);
    }

    private static int SeedBusiness(Harness harness, string slug, string name = "Peluquería Marina")
    {
        var business = new Business { Name = name, Slug = slug, CreatedAt = ClockNow };
        harness.Businesses.InsertAsync(business).GetAwaiter().GetResult();
        return business.Id;
    }

    /// <summary>A ficha the invitation service can read: seeded in the invitation port, and in the ficha port too.</summary>
    private static int SeedRealClient(Harness harness, int businessId, string email)
    {
        var stored = harness.Invitations.SeedClient(businessId, "Ana", "Gómez", email).Id;
        harness.Clients.InsertAsync(new Client
        {
            BusinessId = businessId,
            FirstName = "Ana",
            LastName = "Gómez",
            Email = email,
            CreatedAt = ClockNow
        }).GetAwaiter().GetResult();
        return stored;
    }

    private static string ReadClientsView(string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", "Clients", fileName));
    }

    private static string TokenFrom(Notification notification)
    {
        var match = Regex.Match(notification.BodyHtml, "href=\"([^\"]+)\"");
        Assert.True(match.Success, "The invitation email must contain a link.");
        var link = WebUtility.HtmlDecode(match.Groups[1].Value);
        const string marker = "/cuenta/invitacion/";
        var index = link.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The invitation email must link to the account invitation route.");
        return link[(index + marker.Length)..];
    }

    /// <summary>
    /// The controller wired like the container wires it, over in-memory collaborators. <paramref name="wireInvitationService"/>
    /// false emulates the shorter constructor: the invitation service is missing, which is what the fail-closed test
    /// exercises.
    /// </summary>
    private static Harness Create(
        IClock clock,
        int businessId = BusinessId,
        bool wireInvitationService = true,
        WorkUpOptions? options = null)
    {
        var businesses = new FakeBusinessRepository();
        var clients = new FakeClientRepository();
        var invitations = new FakeClientInvitationRepository();
        var unitOfWork = new FakeUnitOfWork();
        businesses.InsertAsync(new Business { Name = "Peluquería Marina", Slug = HairdresserSlug, CreatedAt = ClockNow })
            .GetAwaiter().GetResult();

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(WorkUpClaims.BusinessId, businessId.ToString(CultureInfo.InvariantCulture))],
                "test"))
        };
        var controller = new ClientsController(
            new ClientsService(clients, new FakeClientNoteRepository(), clock),
            wireInvitationService
                ? new ClientInvitationService(businesses, invitations, unitOfWork, new TrustedInvitationUrlBuilder(), clock)
                : null,
            options is null ? null : Options.Create(options))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };

        return new Harness(controller, businesses, clients, invitations, unitOfWork);
    }

    private sealed record Harness(
        ClientsController Controller,
        FakeBusinessRepository Businesses,
        FakeClientRepository Clients,
        FakeClientInvitationRepository Invitations,
        FakeUnitOfWork UnitOfWork);

    private sealed class TrustedInvitationUrlBuilder : IClientInvitationUrlBuilder
    {
        public string BuildInvitationUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildInvitationUrl(TrustedBaseUrl, slug, token);
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
