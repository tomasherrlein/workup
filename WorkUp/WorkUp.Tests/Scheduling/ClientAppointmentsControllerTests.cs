using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Controllers;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.ClientPanel;
using WorkUp.ViewModels.Shared;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// Read-only "Mis turnos y clases" of the authenticated client panel (T21.1, RF-08, RF-22). These are unit tests over
/// the real controller, the real <see cref="ClientPanelService"/> and an in-memory repository: no DI container, no
/// routing pipeline and no HTTP server. What only this layer can prove is pinned here: the identity comes from the
/// session cookie and never from the request, the panel of another business is refused before the action runs, the
/// rows are mapped to a safe view model with a deterministic order and accessible empty states, and the surface stays
/// read-only. The exact start of a booking belongs to history and offers no action.
/// </summary>
public sealed class ClientAppointmentsControllerTests
{
    private const string Slug = "peluqueria";
    private const string OtherSlug = "yoga";
    private const int BusinessId = 1;
    private const int ClientId = 7;
    private const int OtherClientId = 8;
    private const int OtherBusinessId = 2;
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    private static ClientPanelBookingRecord Row(
        int bookingId,
        DateTime startsAt,
        AppointmentKind kind = AppointmentKind.Individual,
        BookingModality? modality = null,
        BookingStatus status = BookingStatus.Pending,
        AppointmentStatus appointmentStatus = AppointmentStatus.Scheduled,
        string serviceName = "Corte",
        string workerName = "Ana Pérez",
        int durationMinutes = 30) =>
        new(bookingId, startsAt, startsAt.AddMinutes(durationMinutes), serviceName, workerName, kind, appointmentStatus, status, modality);

    // --- Identity comes only from the session cookie ------------------------------------------------------------

    [Fact]
    public async Task Index_passes_the_business_and_client_of_the_cookie_to_the_service()
    {
        var repository = new RecordingClientPanelRepository();
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        await controller.Index(CancellationToken.None);

        var call = Assert.Single(repository.Calls);
        Assert.Equal(BusinessId, call.BusinessId);
        Assert.Equal(ClientId, call.ClientId);
    }

    [Fact]
    public async Task Index_only_shows_the_rows_of_the_authenticated_business_and_client()
    {
        var repository = new RecordingClientPanelRepository();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));
        repository.Add(BusinessId, OtherClientId, Row(2, Now.AddHours(1)));
        repository.Add(OtherBusinessId, ClientId, Row(3, Now.AddHours(1)));
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        var model = await IndexAsync(controller);

        Assert.Equal(new[] { 1 }, model.Upcoming.Select(row => row.BookingId));
        Assert.Empty(model.History);
    }

    // --- The base tenant guard runs before the action -----------------------------------------------------------

    [Fact]
    public void A_session_of_another_business_is_redirected_to_that_business_login_before_the_action_runs()
    {
        var controller = CreateController(
            OtherSlug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository());
        var executing = ActionExecuting(controller);

        controller.OnActionExecuting(executing);

        // The cookie belongs to one business, so another tenant's route never reaches the service.
        Assert.Equal("/r/yoga/cuenta/ingresar", Assert.IsType<RedirectResult>(executing.Result).Url);
    }

    [Fact]
    public void The_panel_is_authenticated_with_the_client_scheme_and_inherits_the_base_guard()
    {
        Assert.True(typeof(ClientPanelControllerBase).IsAssignableFrom(typeof(ClientAppointmentsController)));

        var authorize = typeof(ClientAppointmentsController).GetCustomAttribute<AuthorizeAttribute>(inherit: true);
        Assert.NotNull(authorize);
        Assert.Equal(AuthSchemes.Client, authorize!.AuthenticationSchemes);
    }

    // --- No client or business id travels through the request ----------------------------------------------------

    [Fact]
    public void The_index_action_takes_no_client_or_business_identifier_from_the_request()
    {
        var index = typeof(ClientAppointmentsController).GetMethod(nameof(ClientAppointmentsController.Index))!;

        var parameter = Assert.Single(index.GetParameters());
        Assert.Equal(typeof(CancellationToken), parameter.ParameterType);
        Assert.DoesNotContain(
            "client", parameter.Name!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "business", parameter.Name!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "slug", parameter.Name!, StringComparison.OrdinalIgnoreCase);
    }

    // --- Safe mapping, order and empty state --------------------------------------------------------------------

    [Fact]
    public async Task Index_maps_the_agenda_splitting_upcoming_and_history_in_the_service_order()
    {
        var repository = new RecordingClientPanelRepository();
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(2)));
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(-1)));
        repository.Add(BusinessId, ClientId, Row(4, Now.AddHours(-2)));
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        var model = await IndexAsync(controller);

        Assert.Equal(Slug, model.Slug);
        Assert.Equal(new[] { 1, 2 }, model.Upcoming.Select(row => row.BookingId));
        Assert.Equal(new[] { 3, 4 }, model.History.Select(row => row.BookingId));
        Assert.True(model.HasUpcoming);
        Assert.True(model.HasHistory);
    }

    [Fact]
    public async Task Index_renders_an_empty_agenda_with_empty_sections_and_a_message_for_each()
    {
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository());

        var model = await IndexAsync(controller);

        Assert.Empty(model.Upcoming);
        Assert.Empty(model.History);
        Assert.False(model.HasUpcoming);
        Assert.False(model.HasHistory);
        Assert.False(string.IsNullOrWhiteSpace(ClientAppointmentsViewModel.UpcomingEmptyMessage));
        Assert.False(string.IsNullOrWhiteSpace(ClientAppointmentsViewModel.HistoryEmptyMessage));
    }

    [Fact]
    public async Task Index_labels_individual_fixed_and_drop_in_rows_and_carries_their_public_fields()
    {
        var repository = new RecordingClientPanelRepository();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1), serviceName: "Corte", workerName: "Ana Pérez"));
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(2), AppointmentKind.ClassSession, BookingModality.Fixed, serviceName: "Yoga", workerName: "Lucía Gómez", durationMinutes: 60));
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(3), AppointmentKind.ClassSession, BookingModality.DropIn, serviceName: "Yoga", workerName: "Lucía Gómez", durationMinutes: 60));
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        var model = await IndexAsync(controller);
        var byId = model.Upcoming.ToDictionary(row => row.BookingId);

        Assert.Equal("Turno individual", byId[1].KindLabel);
        Assert.Equal("Corte", byId[1].ServiceName);
        Assert.Equal("Ana Pérez", byId[1].WorkerName);
        Assert.Equal(Now.AddHours(1), byId[1].StartsAt);
        Assert.Equal(Now.AddHours(1).AddMinutes(30), byId[1].EndsAt);

        Assert.Equal("Clase fija", byId[2].KindLabel);
        Assert.Equal("Clase suelta", byId[3].KindLabel);
    }

    [Fact]
    public async Task Index_maps_the_booking_status_and_the_eligibility_of_the_row_to_labels()
    {
        var repository = new RecordingClientPanelRepository();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(2), AppointmentKind.ClassSession, BookingModality.DropIn));
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(3), status: BookingStatus.Confirmed));
        repository.Add(BusinessId, ClientId, Row(4, Now.AddHours(4), AppointmentKind.ClassSession, BookingModality.Fixed, status: BookingStatus.Confirmed));
        repository.Add(BusinessId, ClientId, Row(5, Now.AddHours(5), appointmentStatus: AppointmentStatus.Suspended));
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        var model = await IndexAsync(controller);
        var byId = model.Upcoming.ToDictionary(row => row.BookingId);

        Assert.Equal(StatusTag.For(BookingStatus.Pending), byId[1].Status);
        Assert.True(byId[1].CanConfirmIntention);
        Assert.True(byId[1].CanCancel);
        Assert.Equal("Podés confirmar tu asistencia o cancelar.", byId[1].EligibilityLabel);

        // A drop-in is cancellable but never asks for an intention; a confirmed individual turn is cancellable too.
        Assert.False(byId[2].CanConfirmIntention);
        Assert.True(byId[2].CanCancel);
        Assert.Equal("Podés cancelar.", byId[2].EligibilityLabel);
        Assert.True(byId[3].CanCancel);

        // A fixed class date keeps its place and a suspended session offers nothing (RF-21, T21 scope).
        Assert.False(byId[4].CanCancel);
        Assert.Null(byId[4].EligibilityLabel);
        Assert.False(byId[5].CanConfirmIntention);
        Assert.False(byId[5].CanCancel);
        Assert.NotNull(byId[5].SuspensionStatus);
    }

    [Fact]
    public async Task Index_maps_a_row_starting_exactly_at_the_clock_into_history_without_actions()
    {
        var repository = new RecordingClientPanelRepository();
        repository.Add(BusinessId, ClientId, Row(1, Now));
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        var model = await IndexAsync(controller);

        Assert.Empty(model.Upcoming);
        var row = Assert.Single(model.History);
        Assert.False(row.CanConfirmIntention);
        Assert.False(row.CanCancel);
        Assert.Null(row.EligibilityLabel);
    }

    [Fact]
    public void The_agenda_view_models_expose_only_safe_public_fields()
    {
        var rowFields = typeof(ClientAppointmentRowViewModel).GetProperties().Select(property => property.Name).ToArray();
        var pageFields = typeof(ClientAppointmentsViewModel).GetProperties().Select(property => property.Name).ToArray();

        foreach (var forbidden in new[] { "Health", "Note", "Token", "Link", "Email", "Phone", "Password", "ClientId", "BusinessId", "EnrollmentId" })
        {
            Assert.DoesNotContain(rowFields, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(pageFields, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // The page carries the tenant slug for its own links; the row carries no identity at all beyond its booking.
        Assert.Contains(nameof(ClientAppointmentsViewModel.Slug), pageFields);
        Assert.DoesNotContain(rowFields, name => name.Contains("Slug", StringComparison.OrdinalIgnoreCase));
    }

    // --- The surface stays read-only -----------------------------------------------------------------------------

    [Fact]
    public void The_panel_declares_only_get_actions_and_no_write_verb()
    {
        var actions = typeof(ClientAppointmentsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.DeclaringType == typeof(ClientAppointmentsController) && !method.IsSpecialName)
            .ToArray();

        var writeVerbs = new[]
        {
            typeof(HttpPostAttribute), typeof(HttpPutAttribute), typeof(HttpPatchAttribute), typeof(HttpDeleteAttribute)
        };
        Assert.DoesNotContain(actions, action => writeVerbs.Any(verb => action.GetCustomAttribute(verb) is not null));

        var index = typeof(ClientAppointmentsController).GetMethod(nameof(ClientAppointmentsController.Index))!;
        Assert.NotNull(index.GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(index.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }

    // --- The legacy /cuenta placeholder now redirects into the panel ---------------------------------------------

    [Fact]
    public void The_cuenta_placeholder_redirects_to_the_panel_of_the_authenticated_business()
    {
        var controller = LandingController(Slug, ClientPrincipal(BusinessId, ClientId, Slug));

        var result = controller.Index();

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public void The_cuenta_placeholder_builds_the_target_from_the_authenticated_slug_not_the_route_value()
    {
        // The route slug differs only by case, which the base guard accepts. The target must still be built from the
        // authenticated claim, so a request value can never retarget another tenant.
        var controller = LandingController("PELUQUERIA", ClientPrincipal(BusinessId, ClientId, "peluqueria"));

        var result = controller.Index();

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
    }

    // --- Helpers -------------------------------------------------------------------------------------------------

    private static async Task<ClientAppointmentsViewModel> IndexAsync(ClientAppointmentsController controller)
    {
        var result = await controller.Index(CancellationToken.None);
        return Assert.IsType<ClientAppointmentsViewModel>(Assert.IsType<ViewResult>(result).Model);
    }

    private static ClientAppointmentsController CreateController(
        string routeSlug, ClaimsPrincipal user, RecordingClientPanelRepository repository)
    {
        var service = new ClientPanelService(repository, new FakeClock { Now = Now });
        var routeData = new RouteData();
        routeData.Values["slug"] = routeSlug;
        return new ClientAppointmentsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user },
                RouteData = routeData,
                ActionDescriptor = new ControllerActionDescriptor()
            }
        };
    }

    private static ClientAccountHomeController LandingController(string routeSlug, ClaimsPrincipal user)
    {
        var routeData = new RouteData();
        routeData.Values["slug"] = routeSlug;
        return new ClientAccountHomeController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user },
                RouteData = routeData,
                ActionDescriptor = new ControllerActionDescriptor()
            }
        };
    }

    private static ActionExecutingContext ActionExecuting(ControllerBase controller) =>
        new(controller.ControllerContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);

    private static ClaimsPrincipal ClientPrincipal(int businessId, int clientId, string slug)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(WorkUpClaims.BusinessId, businessId.ToString()),
                new Claim(WorkUpClaims.BusinessName, "Peluquería Marina"),
                new Claim(WorkUpClaims.BusinessSlug, slug),
                new Claim(WorkUpClaims.ClientAccountId, "3"),
                new Claim(WorkUpClaims.ClientId, clientId.ToString()),
                new Claim(ClaimTypes.Email, "ana@example.com")
            ],
            AuthSchemes.Client);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Records the identity the controller hands to the service and answers with the in-memory agenda.</summary>
    private sealed class RecordingClientPanelRepository : IClientPanelRepository
    {
        private readonly FakeClientPanelRepository _inner = new();

        public List<(int BusinessId, int ClientId)> Calls { get; } = [];

        public void Add(int businessId, int clientId, ClientPanelBookingRecord row) => _inner.Add(businessId, clientId, row);

        public Task<IReadOnlyList<ClientPanelBookingRecord>> ListAgendaAsync(
            int businessId, int clientId, CancellationToken ct = default)
        {
            Calls.Add((businessId, clientId));
            return _inner.ListAgendaAsync(businessId, clientId, ct);
        }
    }
}
