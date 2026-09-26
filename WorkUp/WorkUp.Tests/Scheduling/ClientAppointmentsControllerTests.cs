using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Controllers;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.ClientPanel;
using WorkUp.ViewModels.Shared;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// The authenticated client's own panel (T21.1, T21.2, T21.3, RF-08, RF-22). These are unit tests over
/// the real controller, the real <see cref="ClientPanelService"/>, the real <see cref="ClientBookingActionsService"/>,
/// the real <see cref="ClientClassCancellationService"/> and in-memory repositories: no DI container, no routing
/// pipeline and no HTTP server. What only this layer can prove is pinned here: the identity comes from the session
/// cookie and never from the request, the panel of another business is refused before the action runs, the rows are
/// mapped to a safe view model with a deterministic order and accessible empty states, and the individual-turn and
/// drop-in actions stay POST-only, antiforgery-guarded and gated on the typed kind rather than on a display label.
/// The exact start of a booking belongs to history and offers no action.
/// </summary>
public sealed class ClientAppointmentsControllerTests
{
    private const string Slug = "peluqueria";
    private const string OtherSlug = "yoga";
    private const int BusinessId = 1;
    private const int ClientId = 7;
    private const int OtherClientId = 8;
    private const int OtherBusinessId = 2;
    private const int BookingId = 12;
    private const int WorkerId = 2;
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
        // RF-08 confirms an intention, not attendance, so the sentence names what the person will do.
        Assert.Equal("Podés confirmar que vas a asistir o cancelar.", byId[1].EligibilityLabel);

        // T21.3 wires the drop-in cancel: the row both offers the button (typed IsDropInClass && CanCancel) and
        // announces it, so the sentence and the form stay in parity.
        Assert.False(byId[2].CanConfirmIntention);
        Assert.True(byId[2].CanCancel);
        Assert.True(byId[2].IsDropInClass);
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
    public async Task Index_derives_the_individual_turn_flag_from_the_appointment_kind_and_not_the_display_label()
    {
        var repository = new RecordingClientPanelRepository();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(2), AppointmentKind.ClassSession, BookingModality.Fixed));
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(3), AppointmentKind.ClassSession, BookingModality.DropIn));
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        var model = await IndexAsync(controller);
        var byId = model.Upcoming.ToDictionary(row => row.BookingId);

        // The label is display text only: every action gate uses the typed shape derived from the appointment kind.
        Assert.True(byId[1].IsIndividualTurn);
        Assert.False(byId[2].IsIndividualTurn);
        Assert.False(byId[3].IsIndividualTurn);
    }

    [Fact]
    public async Task Index_derives_the_drop_in_class_flag_from_the_appointment_kind_and_modality()
    {
        var repository = new RecordingClientPanelRepository();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(2), AppointmentKind.ClassSession, BookingModality.Fixed));
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(3), AppointmentKind.ClassSession, BookingModality.DropIn));
        repository.Add(BusinessId, ClientId, Row(4, Now.AddHours(4), AppointmentKind.ClassSession));
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        var model = await IndexAsync(controller);
        var byId = model.Upcoming.ToDictionary(row => row.BookingId);

        // A drop-in class is exactly a class session with the drop-in modality: an individual turn, a fixed class and
        // a class without a modality are not, and the display label never decides it.
        Assert.False(byId[1].IsDropInClass);
        Assert.False(byId[2].IsDropInClass);
        Assert.True(byId[3].IsDropInClass);
        Assert.False(byId[4].IsDropInClass);
    }

    [Fact]
    public async Task Index_announces_the_drop_in_cancel_the_panel_now_wires()
    {
        var repository = new RecordingClientPanelRepository();
        repository.Add(
            BusinessId, ClientId, Row(1, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.DropIn));
        var controller = CreateController(Slug, ClientPrincipal(BusinessId, ClientId, Slug), repository);

        var model = await IndexAsync(controller);
        var row = Assert.Single(model.Upcoming);

        // T21.3 wires the drop-in cancel, so the row offers the button (typed kind plus availability) and announces
        // it: the sentence counts exactly the actions the view has a form for.
        Assert.True(row.CanCancel);
        Assert.False(row.IsIndividualTurn);
        Assert.True(row.IsDropInClass);
        Assert.Equal("Podés cancelar.", row.EligibilityLabel);
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

    // --- The agenda read stays a GET and the mutations are POST-only antiforgery routes -------------------------

    [Fact]
    public void The_index_action_stays_a_get_and_never_mutates_state()
    {
        var index = typeof(ClientAppointmentsController).GetMethod(nameof(ClientAppointmentsController.Index))!;
        Assert.NotNull(index.GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(index.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }

    [Fact]
    public void The_controller_route_keeps_the_mutations_under_the_own_agenda_of_the_authenticated_slug()
    {
        var route = typeof(ClientAppointmentsController).GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("r/{slug}/mis-turnos", route!.Template);
    }

    [Theory]
    [InlineData(nameof(ClientAppointmentsController.Confirm), "confirmar")]
    [InlineData(nameof(ClientAppointmentsController.Cancel), "cancelar")]
    [InlineData(nameof(ClientAppointmentsController.CancelClass), "cancelar-clase")]
    public void The_mutations_are_post_only_antiforgery_endpoints_with_the_booking_id_in_the_route(
        string methodName, string tail)
    {
        var method = typeof(ClientAppointmentsController).GetMethod(methodName)!;

        var post = method.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(post);
        Assert.Equal($"{{bookingId:int}}/{tail}", post!.Template);
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Null(method.GetCustomAttribute<HttpGetAttribute>());
    }

    [Theory]
    [InlineData(nameof(ClientAppointmentsController.Confirm))]
    [InlineData(nameof(ClientAppointmentsController.Cancel))]
    [InlineData(nameof(ClientAppointmentsController.CancelClass))]
    public void The_mutations_take_only_the_booking_id_from_the_request(string methodName)
    {
        var method = typeof(ClientAppointmentsController).GetMethod(methodName)!;

        var routeParameter = Assert.Single(
            method.GetParameters(), parameter => parameter.ParameterType != typeof(CancellationToken));
        Assert.Equal("bookingId", routeParameter.Name);
        Assert.Equal(typeof(int), routeParameter.ParameterType);
        foreach (var forbidden in new[] { "client", "business", "slug", "email", "token" })
            Assert.DoesNotContain(forbidden, routeParameter.Name!, StringComparison.OrdinalIgnoreCase);
    }

    // --- Identity of a mutation comes only from the session cookie ----------------------------------------------

    [Theory]
    [InlineData(nameof(ClientAppointmentsController.Confirm))]
    [InlineData(nameof(ClientAppointmentsController.Cancel))]
    public async Task A_booking_of_another_ficha_or_another_tenant_is_a_generic_not_found(string methodName)
    {
        var actions = new RecordingClientBookingActionsRepository();
        actions.Add(BusinessId, OtherClientId, ActionState());
        actions.Add(OtherBusinessId, ClientId, ActionState());
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(), actions);

        var result = await Invoke(controller, methodName, BookingId);

        // A foreign row is indistinguishable from a row that does not exist: the response never leaks existence.
        Assert.IsType<NotFoundResult>(result);
        Assert.Null(controller.TempData["FlashMessage"]);
        Assert.Equal(0, actions.Inner.ConfirmWriteCount);
        Assert.Equal(0, actions.Inner.CancelWriteCount);
    }

    [Fact]
    public async Task Confirm_passes_the_business_and_client_of_the_cookie_to_the_service()
    {
        var actions = new RecordingClientBookingActionsRepository();
        actions.Add(BusinessId, ClientId, ActionState());
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(), actions);

        await controller.Confirm(BookingId, CancellationToken.None);

        Assert.Equal((BusinessId, ClientId, BookingId), Assert.Single(actions.Calls));
    }

    // --- Valid future flows mutate and come back to the own agenda ----------------------------------------------

    [Fact]
    public async Task Confirm_of_an_own_pending_future_turn_confirms_it_and_returns_with_a_success_flash()
    {
        var actions = new RecordingClientBookingActionsRepository();
        actions.Add(BusinessId, ClientId, ActionState());
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(), actions);

        var result = await controller.Confirm(BookingId, CancellationToken.None);

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Confirmaste que vas a asistir.", controller.TempData["FlashMessage"]);
        Assert.Equal(BookingStatus.Confirmed, actions.Inner.Get(BusinessId, ClientId, BookingId)!.BookingStatus);
        Assert.Equal(1, actions.Inner.ConfirmWriteCount);
    }

    [Fact]
    public async Task Confirm_of_an_already_confirmed_own_turn_is_a_noop_success_that_returns_to_the_agenda()
    {
        var actions = new RecordingClientBookingActionsRepository();
        actions.Add(BusinessId, ClientId, ActionState(BookingStatus.Confirmed, Now.AddMinutes(-30)));
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(), actions);

        var result = await controller.Confirm(BookingId, CancellationToken.None);

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal(0, actions.Inner.ConfirmWriteCount);
    }

    [Fact]
    public async Task Cancel_of_an_own_confirmed_future_turn_cancels_it_and_returns_with_a_success_flash()
    {
        var actions = new RecordingClientBookingActionsRepository();
        actions.Add(BusinessId, ClientId, ActionState(BookingStatus.Confirmed, Now.AddMinutes(-30)));
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(), actions);

        var result = await controller.Cancel(BookingId, CancellationToken.None);

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Cancelaste tu turno.", controller.TempData["FlashMessage"]);
        var stored = actions.Inner.Get(BusinessId, ClientId, BookingId)!;
        Assert.Equal(BookingStatus.Cancelled, stored.BookingStatus);
        Assert.Equal(CancelledBy.Client, stored.CancelledBy);
        Assert.Equal(1, actions.Inner.CancelWriteCount);
    }

    // --- The exact start and the invalid states are refused without writing ---------------------------------------

    [Fact]
    public async Task Confirm_of_an_own_turn_starting_exactly_now_is_refused_with_a_safe_flash_and_no_write()
    {
        var actions = new RecordingClientBookingActionsRepository();
        actions.Add(BusinessId, ClientId, ActionState(startsAt: Now));
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(), actions,
            new FakeClock { Now = Now });

        var result = await controller.Confirm(BookingId, CancellationToken.None);

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No se puede confirmar este turno.", controller.TempData["FlashMessage"]);
        Assert.Equal(0, actions.Inner.ConfirmWriteCount);
    }

    [Fact]
    public async Task Cancel_of_an_own_turn_that_already_started_is_refused_with_a_safe_flash_and_no_write()
    {
        var actions = new RecordingClientBookingActionsRepository();
        actions.Add(BusinessId, ClientId, ActionState(startsAt: Now.AddHours(-1)));
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(), actions);

        var result = await controller.Cancel(BookingId, CancellationToken.None);

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No se puede cancelar este turno.", controller.TempData["FlashMessage"]);
        Assert.Equal(0, actions.Inner.CancelWriteCount);
    }

    // --- T21.3: the own drop-in class cancel from the panel -------------------------------------------------------

    [Fact]
    public async Task CancelClass_passes_the_business_and_client_of_the_cookie_to_the_service()
    {
        var dropIns = new FakeClassDropInRepository();
        AddDropIn(dropIns, BusinessId, ClientId, BookingId, Now.AddHours(2));
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(),
            classDropIns: dropIns);

        await controller.CancelClass(BookingId, CancellationToken.None);

        // The identity is the ficha of the cookie and the booking is the route value: the request never carries a
        // client or business id, so one session cannot cancel another ficha's class.
        Assert.Equal(BusinessId, dropIns.FindOwnProbeBusinessId);
        Assert.Equal(ClientId, dropIns.FindOwnProbeClientId);
        Assert.Equal(BookingId, dropIns.FindOwnProbeBookingId);
    }

    [Fact]
    public async Task CancelClass_of_an_own_future_drop_in_cancels_it_by_the_client_and_returns_with_a_success_flash()
    {
        var dropIns = new FakeClassDropInRepository();
        AddDropIn(dropIns, BusinessId, ClientId, BookingId, Now.AddHours(2));
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(),
            classDropIns: dropIns);

        var result = await controller.CancelClass(BookingId, CancellationToken.None);

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Cancelaste tu clase suelta.", controller.TempData["FlashMessage"]);
        var stored = Assert.Single(dropIns.DropInBookings);
        Assert.Equal(BookingStatus.Cancelled, stored.State.Status);
        Assert.Equal(CancelledBy.Client, stored.CancelledBy);
        Assert.Equal(Now, stored.CancelledAt);
    }

    [Fact]
    public async Task CancelClass_of_another_ficha_another_tenant_or_a_missing_booking_is_a_generic_not_found()
    {
        var dropIns = new FakeClassDropInRepository();
        AddDropIn(dropIns, BusinessId, OtherClientId, BookingId, Now.AddHours(2));
        AddDropIn(dropIns, OtherBusinessId, ClientId, BookingId + 1, Now.AddHours(2), appointmentId: 901);
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(),
            classDropIns: dropIns);

        var foreignClient = await controller.CancelClass(BookingId, CancellationToken.None);
        var foreignTenant = await controller.CancelClass(BookingId + 1, CancellationToken.None);
        var missing = await controller.CancelClass(BookingId + 2, CancellationToken.None);

        // A foreign row and a row that does not exist answer identically, so the panel never learns existence.
        Assert.IsType<NotFoundResult>(foreignClient);
        Assert.IsType<NotFoundResult>(foreignTenant);
        Assert.IsType<NotFoundResult>(missing);
        Assert.Null(controller.TempData["FlashMessage"]);
        Assert.Equal(0, dropIns.CancelClientCallCount);
    }

    [Fact]
    public async Task CancelClass_refuses_a_fixed_annotation_and_an_individual_turn_as_a_generic_not_found()
    {
        var dropIns = new FakeClassDropInRepository();
        AddDropIn(
            dropIns, BusinessId, ClientId, BookingId, Now.AddHours(2), modality: BookingModality.Fixed);
        AddDropIn(
            dropIns, BusinessId, ClientId, BookingId + 1, Now.AddHours(2),
            kind: AppointmentKind.Individual, modality: null, appointmentId: 901);
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(),
            classDropIns: dropIns);

        var fixedAnnotation = await controller.CancelClass(BookingId, CancellationToken.None);
        var individualTurn = await controller.CancelClass(BookingId + 1, CancellationToken.None);

        // A fixed class date keeps its place (RF-21) and an individual turn uses its own route: neither is cancelled
        // here, and both are hidden behind the same generic 404 as a foreign booking.
        Assert.IsType<NotFoundResult>(fixedAnnotation);
        Assert.IsType<NotFoundResult>(individualTurn);
        Assert.Equal(0, dropIns.CancelClientCallCount);
    }

    [Fact]
    public async Task CancelClass_of_an_own_class_starting_exactly_now_is_refused_with_a_safe_flash_and_no_write()
    {
        var dropIns = new FakeClassDropInRepository();
        AddDropIn(dropIns, BusinessId, ClientId, BookingId, Now);
        var controller = CreateController(
            Slug, ClientPrincipal(BusinessId, ClientId, Slug), new RecordingClientPanelRepository(),
            clock: new FakeClock { Now = Now }, classDropIns: dropIns);

        var result = await controller.CancelClass(BookingId, CancellationToken.None);

        Assert.Equal("/r/peluqueria/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No se puede cancelar esa reserva ahora.", controller.TempData["FlashMessage"]);
        Assert.Equal(0, dropIns.CancelClientCallCount);
        Assert.Equal(BookingStatus.Pending, Assert.Single(dropIns.DropInBookings).State.Status);
    }

    // --- The view wires the actions only for the individual-turn flags and exposes no private link ---------------

    [Fact]
    public void The_agenda_view_gates_the_post_forms_on_the_typed_kind_and_the_token()
    {
        var view = ReadClientAppointmentsView("Index.cshtml");

        // The gate is the typed shape derived from AppointmentKind, never the localized display label: renaming
        // "Turno individual" must not be able to switch the cancel action off.
        Assert.Contains("item.IsIndividualTurn", view);
        Assert.Contains("item.IsDropInClass", view);
        Assert.DoesNotContain("KindLabel ==", view);
        Assert.DoesNotContain("Turno individual\"", view);

        var confirmGate = view.IndexOf("item.CanConfirmIntention", StringComparison.Ordinal);
        var confirmMarker = view.IndexOf("/mis-turnos/@item.BookingId/confirmar", StringComparison.Ordinal);
        Assert.True(confirmGate >= 0, "La vista debe respetar el flag de confirmación del view model.");
        Assert.True(confirmMarker > confirmGate, "El formulario de confirmación debe seguir al gate CanConfirmIntention.");
        Assert.Contains("method=\"post\"", FormTagContaining(view, "/mis-turnos/@item.BookingId/confirmar"));

        var cancelGate = view.IndexOf("item.CanCancel", StringComparison.Ordinal);
        var individualGate = view.IndexOf("item.IsIndividualTurn", StringComparison.Ordinal);
        var individualMarker = view.IndexOf("/mis-turnos/@item.BookingId/cancelar\"", StringComparison.Ordinal);
        Assert.True(cancelGate >= 0, "La vista debe respetar el flag de cancelación del view model.");
        Assert.True(individualGate > cancelGate, "Cancelar turno es solo para el turno individual, nunca para la clase suelta.");
        Assert.True(individualMarker > individualGate, "El formulario de cancelación debe seguir al gate del turno individual.");
        Assert.Contains("method=\"post\"", FormTagContaining(view, "/mis-turnos/@item.BookingId/cancelar\""));

        // The drop-in class cancel is its own typed action: it is gated on IsDropInClass (not on CanCancel alone) and
        // uses the class route, so it can never be the individual-turn form repurposed.
        var dropInGate = view.IndexOf("item.IsDropInClass", StringComparison.Ordinal);
        var dropInMarker = view.IndexOf("/mis-turnos/@item.BookingId/cancelar-clase", StringComparison.Ordinal);
        Assert.True(dropInGate > cancelGate, "Cancelar clase es solo para la clase suelta: la anotación fija no se cancela.");
        Assert.True(dropInMarker > dropInGate, "El formulario de la clase suelta debe seguir al gate IsDropInClass.");
        Assert.Contains("method=\"post\"", FormTagContaining(view, "/mis-turnos/@item.BookingId/cancelar-clase"));
        Assert.Contains("Cancelar clase", view);

        // RF-08 confirms an intention, not attendance.
        Assert.Contains("Confirmar que voy a asistir", view);
        Assert.DoesNotContain("Confirmar asistencia", view);

        // All three mutations are POST forms with the antiforgery token, never a GET link that could fire on a prefetch.
        Assert.Equal(3, Count(view, "@Html.AntiForgeryToken()"));
        Assert.DoesNotContain("href=\"/r/@Model.Slug/mis-turnos/@item.BookingId/confirmar\"", view);
        Assert.DoesNotContain("href=\"/r/@Model.Slug/mis-turnos/@item.BookingId/cancelar\"", view);
        Assert.DoesNotContain("href=\"/r/@Model.Slug/mis-turnos/@item.BookingId/cancelar-clase\"", view);

        // The panel never renders a private-link token or any token in the URL.
        Assert.DoesNotContain("/t/", view);
        Assert.DoesNotContain("BookingToken", view);
        Assert.DoesNotContain("LinkToken", view);
    }

    [Fact]
    public void The_agenda_view_never_offers_the_class_cancel_for_a_fixed_or_a_past_row()
    {
        var view = ReadClientAppointmentsView("Index.cshtml");

        // The drop-in form is guarded by both the typed flag and the availability flag, so a fixed annotation (whose
        // IsDropInClass is false) or a non-cancellable row can never render it.
        Assert.Contains("item.CanCancel && item.IsDropInClass", view);

        // History is a pure read: no form and no antiforgery token are rendered for a past row.
        var historyStart = view.IndexOf("client-appointments-history", StringComparison.Ordinal);
        Assert.True(historyStart >= 0, "La vista debe tener la sección de historial.");
        Assert.DoesNotContain("<form", view[historyStart..]);
        Assert.DoesNotContain("@Html.AntiForgeryToken()", view[historyStart..]);
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

    private static ClientBookingActionState ActionState(
        BookingStatus status = BookingStatus.Pending,
        DateTime? confirmedAt = null,
        DateTime? startsAt = null,
        DateTime? cancelledAt = null,
        CancelledBy? cancelledBy = null,
        AppointmentKind kind = AppointmentKind.Individual,
        int maxCapacity = 1,
        int workerId = WorkerId)
    {
        var start = startsAt ?? Now.AddHours(2);
        return new ClientBookingActionState(
            BusinessId, BookingId, BookingId + 100, workerId, start, start.AddMinutes(30),
            kind, AppointmentStatus.Scheduled, AppointmentOrigin.Reserved, maxCapacity,
            Modality: null, EnrollmentId: null,
            status, confirmedAt, cancelledAt, cancelledBy, AttendanceRecordedAt: null, AbsenceSource: null);
    }

    private static Task<IActionResult> Invoke(ClientAppointmentsController controller, string methodName, int bookingId) =>
        methodName switch
        {
            nameof(ClientAppointmentsController.Confirm) => controller.Confirm(bookingId, CancellationToken.None),
            nameof(ClientAppointmentsController.Cancel) => controller.Cancel(bookingId, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName))
        };

    private static ClientAppointmentsController CreateController(
        string routeSlug,
        ClaimsPrincipal user,
        RecordingClientPanelRepository repository,
        RecordingClientBookingActionsRepository? bookingActions = null,
        FakeClock? clock = null,
        FakeClassDropInRepository? classDropIns = null)
    {
        var sharedClock = clock ?? new FakeClock { Now = Now };
        var panelService = new ClientPanelService(repository, sharedClock);
        var actionsService = new ClientBookingActionsService(
            bookingActions ?? new RecordingClientBookingActionsRepository(), new FakeUnitOfWork(), sharedClock);
        var classCancellationService = new ClientClassCancellationService(
            classDropIns ?? new FakeClassDropInRepository(), new FakeUnitOfWork(), sharedClock);
        var routeData = new RouteData();
        routeData.Values["slug"] = routeSlug;
        var httpContext = new DefaultHttpContext { User = user };
        return new ClientAppointmentsController(panelService, actionsService, classCancellationService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
                RouteData = routeData,
                ActionDescriptor = new ControllerActionDescriptor()
            },
            TempData = new TempDataDictionary(httpContext, new DictionaryTempDataProvider())
        };
    }

    /// <summary>
    /// Registers one drop-in annotation under a class session of the fake, so the lock-free probe resolves the owning
    /// ficha exactly like the scoped reads do. The tenant, the ficha, the kind and the modality are all parameters so
    /// a test can register a foreign, fixed or individual row and prove it is refused.
    /// </summary>
    private static void AddDropIn(
        FakeClassDropInRepository repository,
        int businessId,
        int clientId,
        int bookingId,
        DateTime startsAt,
        BookingStatus status = BookingStatus.Pending,
        AppointmentStatus appointmentStatus = AppointmentStatus.Scheduled,
        AppointmentKind kind = AppointmentKind.ClassSession,
        BookingModality? modality = BookingModality.DropIn,
        int appointmentId = 900)
    {
        repository.Sessions.Add(new FakeClassDropInSession(
            businessId,
            new ClassSessionRow(
                appointmentId, 7, 3, startsAt, 8, kind, appointmentStatus,
                [new ClassSessionBooking(clientId, status, bookingId, modality, "Ana Pérez")])));
        repository.DropInBookings.Add(new FakeClassDropInBooking(
            businessId,
            new DropInCancellationState(
                bookingId, appointmentId, startsAt, kind, appointmentStatus, status, modality)));
    }

    private static string ReadClientAppointmentsView(string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Views", "ClientAppointments", fileName));
    }

    private static string FormTagContaining(string view, string marker)
    {
        var index = view.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"La vista debe contener {marker}.");
        var formStart = view.LastIndexOf("<form", index, StringComparison.Ordinal);
        Assert.True(formStart >= 0, $"El marcador {marker} debe estar dentro de un formulario.");
        var formEnd = view.IndexOf('>', formStart);
        return view[formStart..(formEnd + 1)];
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
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

    /// <summary>
    /// Records the trusted identity the controller hands to the actions service while delegating the guarded work to
    /// the in-memory fake, so a test can prove the controller used the cookie's (businessId, clientId) and the route
    /// booking, and still observe the guarded writes.
    /// </summary>
    private sealed class RecordingClientBookingActionsRepository : IClientBookingActionsRepository
    {
        public FakeClientBookingActionsRepository Inner { get; } = new();

        public List<(int BusinessId, int ClientId, int BookingId)> Calls { get; } = [];

        public void Add(int businessId, int clientId, ClientBookingActionState state) =>
            Inner.Add(businessId, clientId, state);

        public Task<int?> GetWorkerIdAsync(int businessId, int clientId, int bookingId, CancellationToken ct = default)
        {
            Calls.Add((businessId, clientId, bookingId));
            return Inner.GetWorkerIdAsync(businessId, clientId, bookingId, ct);
        }

        public Task<bool> LockWorkerAsync(int businessId, int workerId, CancellationToken ct = default) =>
            Inner.LockWorkerAsync(businessId, workerId, ct);

        public Task<ClientBookingActionState?> LockClientBookingAsync(
            int businessId, int clientId, int bookingId, CancellationToken ct = default) =>
            Inner.LockClientBookingAsync(businessId, clientId, bookingId, ct);

        public Task<bool> ConfirmClientBookingAsync(
            int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default) =>
            Inner.ConfirmClientBookingAsync(businessId, clientId, bookingId, now, ct);

        public Task<bool> CancelClientBookingAsync(
            int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default) =>
            Inner.CancelClientBookingAsync(businessId, clientId, bookingId, now, ct);
    }

    /// <summary>Keeps the one-time flash messages of a redirect inside the test instance.</summary>
    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
