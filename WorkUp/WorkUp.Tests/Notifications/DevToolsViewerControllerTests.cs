using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// T26 viewer UI (plan assumptions 10 and 11): the owner-only, fail-closed page over
/// <see cref="INotificationViewerRepository"/>. The persistence contract of the port is already pinned elsewhere; what
/// is pinned here is what the panel adds: every action refuses a closed gate with the same 404 and without touching the
/// repository, the tenant always comes from the owner cookie claim and never from the request, the list is a bounded
/// newest-first keyset page with no body, the detail is reachable only for the caller's own notice, both pages are
/// never cached and carry no referrer, and the rendered view shows the body as escaped plain text with no unsafe link
/// and no hidden attribute.
/// <para>
/// The HTML-escaping proof here is structural: the Razor source must use the encoded <c>@Model.BodyHtml</c> inside a
/// <c>&lt;pre&gt;</c> and must never call <c>Html.Raw</c>, build an <c>&lt;iframe&gt;</c> or place the body in an
/// attribute or a URL. Rendering the real markup through the shipped pipeline is deferred to the opt-in host fixture,
/// because this project has no in-process Razor renderer and the host harness is the only place where the composed page
/// is observed.
/// </para>
/// </summary>
public sealed class DevToolsViewerControllerTests
{
    private const int BusinessId = 7;
    private const string OwnerUsername = "lucia.demo";

    private readonly FakeNotificationViewerRepository _viewer = new();

    // --- Fixtures -----------------------------------------------------------------------------------------------

    private static DevToolsGate Gate(bool enabled)
    {
        var options = new WorkUpOptions { Features = new FeatureOptions { DevTools = enabled } };
        var environment = new FakeHostEnvironment
        {
            EnvironmentName = enabled ? Environments.Development : Environments.Production
        };
        // Retain mode is the only email mode the tools accept, so the enabled gate stays a real gate rather than a test
        // stub; the disabled gate is a production host with the flag on, the exact refusal the task must keep.
        return new DevToolsGate(environment, Options.Create(options), OutboxMode.Retain);
    }

    private DevToolsController Create(bool gateOpen = true)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(WorkUpClaims.BusinessId, BusinessId.ToString()),
                new Claim(WorkUpClaims.BusinessName, "Estudio Lucía"),
                new Claim(ClaimTypes.Name, OwnerUsername)
            ], AuthSchemes.Owner))
        };

        return new DevToolsController(Gate(gateOpen), _viewer)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static async Task<IReadOnlyList<NotificationViewerListItem>> List(
        DevToolsController controller, int? beforeId = null) =>
        Assert.IsAssignableFrom<IReadOnlyList<NotificationViewerListItem>>(
            Assert.IsType<ViewResult>(await controller.Index(beforeId, CancellationToken.None)).Model);

    private static async Task<NotificationViewerDetail> Detail(DevToolsController controller, int id) =>
        Assert.IsType<NotificationViewerDetail>(
            Assert.IsType<ViewResult>(await controller.Detail(id, CancellationToken.None)).Model);

    // --- Closed gate: the same neutral 404 and no repository read -----------------------------------------------

    [Fact]
    public async Task Index_answers_not_found_and_never_reads_when_the_gate_is_closed()
    {
        _viewer.Seed(businessId: BusinessId);

        var result = await Create(gateOpen: false).Index(null, CancellationToken.None);

        // A flag mistakenly on in production is a refusal, and the refusal never reaches storage: the caller learns
        // nothing about whether a notice exists.
        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, _viewer.ListCallCount);
    }

    [Fact]
    public async Task Detail_answers_not_found_and_never_reads_when_the_gate_is_closed_for_a_valid_id()
    {
        var seeded = _viewer.Seed(businessId: BusinessId);

        var result = await Create(gateOpen: false).Detail(seeded.Id, CancellationToken.None);

        // The direct URL is refused exactly like the list: the gate runs first and the repository is never consulted.
        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, _viewer.DetailCallCount);
    }

    // --- Tenant scope comes from the owner claim only -----------------------------------------------------------

    [Fact]
    public async Task Index_lists_only_the_owners_business_newest_first()
    {
        _viewer.Seed(businessId: BusinessId, subject: "Propia antigua", id: 10);
        _viewer.Seed(businessId: BusinessId, subject: "Propia nueva", id: 11);
        _viewer.Seed(businessId: 8, subject: "Ajena", id: 12);
        _viewer.Seed(businessId: null, subject: "Sin negocio", id: 13);

        var items = await List(Create());

        Assert.Equal([11, 10], items.Select(item => item.Id));
        Assert.DoesNotContain(items, item => item.Subject == "Ajena" || item.Subject == "Sin negocio");
    }

    [Fact]
    public async Task Detail_returns_the_body_only_for_the_owners_own_notice()
    {
        var mine = _viewer.Seed(businessId: BusinessId, bodyHtml: "<p>Mi cuerpo</p>");
        var foreign = _viewer.Seed(businessId: 8, bodyHtml: "<p>Cuerpo ajeno</p>");

        var detail = await Detail(Create(), mine.Id);

        Assert.Equal(mine.Id, detail.Id);
        Assert.Equal("<p>Mi cuerpo</p>", detail.BodyHtml);

        // Another business's notice is answered like a missing one: the same 404, with no hint that it exists.
        var refused = await Create().Detail(foreign.Id, CancellationToken.None);
        Assert.IsType<NotFoundResult>(refused);
    }

    [Fact]
    public async Task Detail_answers_a_missing_or_non_positive_identifier_with_the_same_not_found()
    {
        var controller = Create();

        Assert.IsType<NotFoundResult>(await controller.Detail(9999, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Detail(0, CancellationToken.None));

        // A non-positive identifier is the neutral denial of the contract: no notice can carry it, so the repository
        // was never asked.
        Assert.Equal(1, _viewer.DetailCallCount);
    }

    // --- Bounded keyset page and empty state --------------------------------------------------------------------

    [Fact]
    public async Task Index_is_an_empty_page_when_the_business_has_no_notices()
    {
        _viewer.Seed(businessId: 8);

        var items = await List(Create());

        Assert.Empty(items);
    }

    [Fact]
    public async Task Index_pages_with_a_bounded_newest_first_keyset()
    {
        var seeded = Enumerable.Range(1, NotificationViewerBounds.MaxPageSize + 5)
            .Select(id => _viewer.Seed(businessId: BusinessId, id: id))
            .ToArray();
        var newest = seeded[^1].Id;

        var firstPage = await List(Create());

        // The page never exceeds the contract bound, and it starts at the newest row.
        Assert.Equal(NotificationViewerBounds.MaxPageSize, firstPage.Count);
        Assert.Equal(newest, firstPage[0].Id);
        Assert.Equal(newest - NotificationViewerBounds.MaxPageSize + 1, firstPage[^1].Id);

        var secondPage = await List(Create(), beforeId: firstPage[^1].Id);

        // The cursor is a strict keyset: the second page holds exactly the strictly older rows and repeats none.
        Assert.Equal(5, secondPage.Count);
        Assert.Equal(newest - NotificationViewerBounds.MaxPageSize, secondPage[0].Id);
        Assert.DoesNotContain(secondPage, item => firstPage.Any(first => first.Id == item.Id));
    }

    // --- Never cached, never a referrer -------------------------------------------------------------------------

    [Fact]
    public async Task Both_pages_are_marked_no_store_and_carry_no_referrer()
    {
        var seeded = _viewer.Seed(businessId: BusinessId);

        var controller = Create();
        await controller.Index(null, CancellationToken.None);
        AssertPrivateHeaders(controller);

        var detailController = Create();
        await detailController.Detail(seeded.Id, CancellationToken.None);
        AssertPrivateHeaders(detailController);
    }

    [Fact]
    public void Both_actions_ask_the_response_cache_not_to_store()
    {
        foreach (var action in new[] { nameof(DevToolsController.Index), nameof(DevToolsController.Detail) })
        {
            var attribute = Assert.Single(
                typeof(DevToolsController).GetMethod(action)!.GetCustomAttributes<ResponseCacheAttribute>());

            Assert.True(attribute.NoStore);
            Assert.Equal(ResponseCacheLocation.None, attribute.Location);
        }
    }

    // --- Owner-only surface and no tenant input -----------------------------------------------------------------

    [Fact]
    public void The_controller_is_owner_only_and_offers_no_public_access()
    {
        Assert.True(typeof(OwnerControllerBase).IsAssignableFrom(typeof(DevToolsController)));

        // The owner cookie is inherited from the base controller: an anonymous request never reaches an action.
        var authorize = Assert.Single(
            typeof(DevToolsController).GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal(AuthSchemes.Owner, authorize.AuthenticationSchemes);

        // Nothing on the controller or its actions opts a visitor out of that authorization.
        Assert.False(typeof(DevToolsController).IsDefined(typeof(AllowAnonymousAttribute), inherit: true));
        foreach (var action in new[] { nameof(DevToolsController.Index), nameof(DevToolsController.Detail) })
            Assert.False(typeof(DevToolsController).GetMethod(action)!.IsDefined(typeof(AllowAnonymousAttribute), true));
    }

    [Fact]
    public void The_actions_take_no_tenant_input_so_the_business_can_only_come_from_the_cookie()
    {
        // The only inputs are the page cursor and the notice identity. There is no business, email or business-id
        // parameter a caller could use to widen the read.
        Assert.Equal(
            ["beforeId", "ct"],
            ParameterNames(nameof(DevToolsController.Index)));
        Assert.Equal(
            ["id", "ct"],
            ParameterNames(nameof(DevToolsController.Detail)));
    }

    // --- Status and type labels ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(NotificationStatus.Pending, "Pendiente")]
    [InlineData(NotificationStatus.Sent, "Enviado")]
    [InlineData(NotificationStatus.Failed, "Falló")]
    [InlineData(NotificationStatus.Dispatching, "Enviando")]
    [InlineData(NotificationStatus.ReviewRequired, "Requiere revisión")]
    public void Status_label_is_spanish_for_every_status(NotificationStatus status, string expected) =>
        Assert.Equal(expected, DevToolsController.StatusLabel(status));

    [Theory]
    [InlineData(NotificationType.BookingCreated, "Reserva creada")]
    [InlineData(NotificationType.AppointmentReminder, "Recordatorio de turno")]
    [InlineData(NotificationType.DropInReminder, "Recordatorio de clase suelta")]
    [InlineData(NotificationType.DropInBooked, "Lugar en clase suelta")]
    [InlineData(NotificationType.ClassSuspended, "Clase suspendida")]
    [InlineData(NotificationType.Rescheduled, "Turno reprogramado")]
    [InlineData(NotificationType.MembershipExpiring, "Abono por vencer")]
    [InlineData(NotificationType.MembershipExpired, "Abono vencido")]
    [InlineData(NotificationType.DailySummary, "Resumen diario")]
    [InlineData(NotificationType.StockReview, "Revisión de insumos")]
    [InlineData(NotificationType.EmailVerification, "Verificación de email")]
    [InlineData(NotificationType.PasswordReset, "Recuperación de contraseña")]
    [InlineData(NotificationType.AccountInvitation, "Invitación de cuenta")]
    public void Type_label_is_spanish_for_every_type(NotificationType type, string expected) =>
        Assert.Equal(expected, DevToolsController.TypeLabel(type));

    // --- Static view contract -----------------------------------------------------------------------------------

    [Fact]
    public void The_list_shows_status_type_time_recipient_and_subject_and_never_a_body_or_a_private_link()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        Assert.Contains("StatusLabel", view, StringComparison.Ordinal);
        Assert.Contains("TypeLabel", view, StringComparison.Ordinal);
        Assert.Contains("item.CreatedAt", view, StringComparison.Ordinal);
        Assert.Contains("item.Recipient", view, StringComparison.Ordinal);
        Assert.Contains("item.Subject", view, StringComparison.Ordinal);

        // No body is ever part of the list: a queued body can embed a single-use private link or a reset token.
        Assert.DoesNotContain("BodyHtml", view, StringComparison.Ordinal);
        Assert.DoesNotContain("LastError", view, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatchKey", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Html.Raw", view, StringComparison.Ordinal);
        Assert.DoesNotContain("iframe", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_offers_the_next_bounded_page_through_a_keyset_cursor()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        // The next page is the same action with the last row's identity as the cursor, bound by the same bound the
        // repository enforces, so the page can only ever move strictly backwards.
        Assert.Contains("beforeId", view, StringComparison.Ordinal);
        Assert.Contains("MaxPageSize", view, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"Detail\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_shows_the_body_as_escaped_plain_text_and_warns_about_private_tokens()
    {
        var view = ReadOwnerView("DevTools", "Detail.cshtml");

        // Razor encodes @Model.BodyHtml by default; the body is rendered as preformatted text, never as markup.
        Assert.Contains("<pre", view, StringComparison.Ordinal);
        Assert.Contains("@Model.BodyHtml", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Html.Raw", view, StringComparison.Ordinal);
        Assert.DoesNotContain("iframe", view, StringComparison.Ordinal);
        Assert.DoesNotContain("srcdoc", view, StringComparison.Ordinal);

        // The body is never placed in an attribute or a URL, and no link is built from its content.
        Assert.DoesNotContain("value=\"@Model.BodyHtml\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"@Model.BodyHtml\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"@Model.BodyHtml\"", view, StringComparison.Ordinal);

        // The warning names the private tokens and single-use links a body can carry, so the owner does not share it.
        Assert.Contains("privado", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Neither_page_exposes_the_stored_error_the_claim_or_a_manual_retry()
    {
        foreach (var view in new[]
                 {
                     ReadOwnerView("DevTools", "Index.cshtml"),
                     ReadOwnerView("DevTools", "Detail.cshtml")
                 })
        {
            Assert.DoesNotContain("LastError", view, StringComparison.Ordinal);
            Assert.DoesNotContain("DispatchKey", view, StringComparison.Ordinal);
            Assert.DoesNotContain("ClaimedAt", view, StringComparison.Ordinal);

            // Manual intervention and retry are not built yet, so no page offers a button for them.
            Assert.DoesNotContain("Reintentar", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Revisión manual", view, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_viewer_link_stays_inside_the_owner_area()
    {
        var view = ReadOwnerView("DevTools", "Index.cshtml");

        Assert.Contains("asp-area=\"Owner\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", view, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", view, StringComparison.Ordinal);
    }

    // --- Gate-based layout --------------------------------------------------------------------------------------

    [Fact]
    public void The_owner_layout_links_the_viewer_only_through_the_gate()
    {
        var layout = ReadView("_OwnerLayout.cshtml");

        Assert.Contains("DevToolsGate", layout, StringComparison.Ordinal);
        Assert.Contains("IsEnabled", layout, StringComparison.Ordinal);
        // The raw flag is replaced: a production host with Features.DevTools on must not render the tools link.
        Assert.DoesNotContain("Features.DevTools", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void The_owner_layout_offers_the_viewer_only_to_a_signed_in_owner()
    {
        var layout = ReadView("_OwnerLayout.cshtml");

        // Anonymous login and register pages render this same layout, so the section is added only for an
        // authenticated principal that also carries a positive owner business claim.
        Assert.Contains("DevToolsGate.IsEnabled && isAuthenticated && hasOwnerBusiness", layout, StringComparison.Ordinal);
        Assert.Contains("IsAuthenticated", layout, StringComparison.Ordinal);
        Assert.Contains("WorkUpClaims.BusinessId", layout, StringComparison.Ordinal);
        Assert.Contains("ownerBusinessId > 0", layout, StringComparison.Ordinal);

        // Authentication is unchanged for every other section: only the tools item carries the extra guard.
        Assert.Contains("OwnerNavigation.Sections", layout, StringComparison.Ordinal);
        Assert.Contains("NavigationProvider.VisibleSectionsAsync", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void The_public_layout_and_home_use_the_same_gate_instead_of_the_raw_flag()
    {
        foreach (var view in new[] { ReadView("_Layout.cshtml"), ReadHomeView("Index.cshtml") })
        {
            Assert.Contains("DevToolsGate", view, StringComparison.Ordinal);
            Assert.Contains("IsEnabled", view, StringComparison.Ordinal);
            Assert.DoesNotContain("Features.DevTools", view, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_test_tools_section_exists_in_the_owner_navigation()
    {
        Assert.Equal("DevTools", OwnerNavigation.DevTools.Controller);
        Assert.Equal("Herramientas de prueba", OwnerNavigation.DevTools.Label);
    }

    // --- Banner copy and viewer styling -------------------------------------------------------------------------

    [Fact]
    public void The_retaining_banner_points_the_owner_to_the_viewer_without_claiming_a_send()
    {
        var text = EmailDeliveryCopy.DevToolsBanner(OutboxMode.Retain);

        Assert.Contains("retenidos sin enviarse", text, StringComparison.Ordinal);
        Assert.Contains("la dueña puede verlos en Herramientas de prueba", text, StringComparison.Ordinal);

        // The banner also renders on the public home page, so it names who can open the panel page instead of
        // promising an anonymous visitor a page she cannot reach.
        Assert.DoesNotContain("se pueden ver", text, StringComparison.Ordinal);
        // Retaining semantics stay accurate: no send is claimed and no inbox delivery is promised.
        Assert.DoesNotContain("no podemos garantizar", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_notice_body_wraps_long_private_tokens_and_uses_theme_tokens_only()
    {
        var css = ReadCss();

        var start = css.IndexOf(".notice-body {", StringComparison.Ordinal);
        Assert.True(start >= 0, "workup.css must style the .notice-body surface.");
        var block = css[start..css.IndexOf('}', start)];

        // A single-use private link or a reset token has no spaces, so the surface must wrap instead of overflowing.
        Assert.Contains("white-space: pre-wrap", block, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", block, StringComparison.Ordinal);
        Assert.Contains("max-width", block, StringComparison.Ordinal);
        Assert.Contains("padding: var(", block, StringComparison.Ordinal);

        // Design-system rule: no raw color outside :root, so the surface keeps themed, readable contrast.
        Assert.Contains("var(--", block, StringComparison.Ordinal);
        Assert.DoesNotContain("#", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_warning_uses_the_notice_component_instead_of_a_field_hint()
    {
        var view = ReadOwnerView("DevTools", "Detail.cshtml");

        Assert.Contains("notice-warning", view, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"hint\"", view, StringComparison.Ordinal);
    }

    // --- DI graph -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_viewer_repository_is_registered_scoped_in_the_composition_root()
    {
        var services = BuildServices();

        var repository = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(INotificationViewerRepository));
        Assert.Equal(ServiceLifetime.Scoped, repository.Lifetime);
        Assert.Equal(typeof(SqlNotificationViewerRepository), repository.ImplementationType);

        var gate = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DevToolsGate));
        Assert.Equal(ServiceLifetime.Singleton, gate.Lifetime);
    }

    [Fact]
    public void The_controller_constructor_closes_the_graph_over_the_gate_and_the_viewer()
    {
        // The view model is the read projection itself, so the graph has exactly two collaborators and no service that
        // could write, send or retry.
        var parameters = typeof(DevToolsController)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(DevToolsGate), typeof(INotificationViewerRepository)], parameters);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------

    private static void AssertPrivateHeaders(DevToolsController controller)
    {
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.Equal("no-cache", controller.Response.Headers.Pragma);
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"]);
    }

    private static string[] ParameterNames(string action) =>
        typeof(DevToolsController)
            .GetMethod(action)!
            .GetParameters()
            .Select(parameter => parameter.Name!)
            .ToArray();

    private static string ReadOwnerView(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "Areas", "Owner", "Views", folder, fileName));

    private static string ReadView(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "Views", "Shared", fileName));

    private static string ReadHomeView(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "Views", "Home", fileName));

    private static string ReadCss() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "wwwroot", "css", "workup.css"));

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static IServiceCollection BuildServices()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:WorkUp"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT26ViewerUi;Trusted_Connection=True;TrustServerCertificate=True",
            ["WorkUp:PublicBaseUrl"] = "https://reservas.example.com",
            ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires",
            ["WorkUp:Features:DevTools"] = "true",
            ["WorkUp:Email:Mode"] = "Outbox"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        // Registration only: no provider is built, so no key is generated and no database is touched.
        services.AddWorkUp(configuration, new FakeHostEnvironment { EnvironmentName = Environments.Development });
        return services;
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
