using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Controllers;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.ClientPanel;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// The authenticated client's own reservation screens (T21.5, RF-07, RF-22): the individual turn and the drop-in
/// class. These are unit tests over the real controller, the real services and in-memory repositories. What only
/// this layer can prove is pinned here: both identities (business and ficha) come from the client session cookie and
/// never from the request, the catalogue is read by the cookie slug, the individual booking calls only
/// <see cref="BookingService.BookIndividualForClientAsync"/> without any email, name or identity field, the
/// drop-in booking reuses <see cref="ClassDropInService.BookForClientAsync"/>, and both screens are POST-only
/// antiforgery forms with a safe re-render on a stale slot or a full/duplicate class.
/// </summary>
public sealed class ClientReservationsControllerTests
{
    private const string Slug = "peluqueria";
    private const string OtherSlug = "otro-negocio";
    private const int BusinessId = 1;
    private const int ClientId = 7;
    private const int OtherClientId = 8;
    private const int OtherBusinessId = 2;
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateOnly SlotDate = new(2026, 3, 11);
    private static readonly TimeOnly SlotTime = new(10, 0);

    // --- The controller is the authenticated client panel reservation page --------------------------------------

    [Fact]
    public void The_controller_is_the_authenticated_client_panel_reservation_page()
    {
        Assert.True(typeof(ClientPanelControllerBase).IsAssignableFrom(typeof(ClientReservationsController)));

        var authorize = typeof(ClientReservationsController).GetCustomAttribute<AuthorizeAttribute>(inherit: true);
        Assert.NotNull(authorize);
        Assert.Equal(AuthSchemes.Client, authorize!.AuthenticationSchemes);

        var route = typeof(ClientReservationsController).GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("r/{slug}/mis-turnos/reservar", route!.Template);
    }

    // --- Route + antiforgery reflection --------------------------------------------------------------------------

    [Fact]
    public void The_individual_screen_is_a_get_and_the_booking_is_a_post_only_antiforgery_route()
    {
        var get = typeof(ClientReservationsController).GetMethod(nameof(ClientReservationsController.Turno))!;
        var httpGet = get.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(httpGet);
        Assert.Equal("turno", httpGet!.Template);

        var post = typeof(ClientReservationsController).GetMethod(nameof(ClientReservationsController.BookTurno))!;
        var httpPost = post.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(httpPost);
        Assert.Equal("turno", httpPost!.Template);
        Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Null(post.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public void The_class_list_is_a_get_and_the_booking_is_a_post_only_antiforgery_route_with_the_appointment()
    {
        var get = typeof(ClientReservationsController).GetMethod(nameof(ClientReservationsController.Classes))!;
        var httpGet = get.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(httpGet);
        Assert.Equal("clase", httpGet!.Template);

        var post = typeof(ClientReservationsController).GetMethod(nameof(ClientReservationsController.BookClass))!;
        var httpPost = post.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(httpPost);
        Assert.Equal("clase/{appointmentId:int}", httpPost!.Template);
        Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Null(post.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public void The_individual_screen_takes_only_the_optional_selection_from_the_query()
    {
        var get = typeof(ClientReservationsController).GetMethod(nameof(ClientReservationsController.Turno))!;

        Assert.Equal(
            new[] { "serviceId", "workerId", "date", "ct" },
            get.GetParameters().Select(parameter => parameter.Name!).ToArray());
        foreach (var forbidden in new[] { "client", "business", "slug", "email", "name" })
            Assert.DoesNotContain(get.GetParameters(), parameter =>
                parameter.Name!.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_individual_booking_takes_only_the_chosen_service_worker_day_and_time()
    {
        var post = typeof(ClientReservationsController).GetMethod(nameof(ClientReservationsController.BookTurno))!;

        Assert.Equal(
            new[] { "input", "ct" },
            post.GetParameters().Select(parameter => parameter.Name!).ToArray());

        var fields = typeof(ClientIndividualReservationInput).GetProperties()
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Date", "ServiceId", "StartTime", "WorkerId" }, fields);
        foreach (var forbidden in new[] { "Email", "FirstName", "LastName", "Client", "Business", "Token" })
            Assert.DoesNotContain(fields, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_class_booking_takes_only_the_appointment_of_the_route()
    {
        var post = typeof(ClientReservationsController).GetMethod(nameof(ClientReservationsController.BookClass))!;

        Assert.Equal(
            new[] { "appointmentId", "ct" },
            post.GetParameters().Select(parameter => parameter.Name!).ToArray());
        foreach (var forbidden in new[] { "client", "business", "slug", "email", "name" })
            Assert.DoesNotContain(post.GetParameters(), parameter =>
                parameter.Name!.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    // --- The view models carry no identity or contact field ------------------------------------------------------

    [Fact]
    public void The_reservation_view_models_expose_no_identity_or_contact_field()
    {
        var types = new[]
        {
            typeof(ClientIndividualReservationViewModel),
            typeof(ClientIndividualReservationInput),
            typeof(ClientReservationServiceOption),
            typeof(ClientReservationWorkerOption),
            typeof(ClientClassReservationViewModel),
            typeof(ClientReservationClassOption)
        };

        foreach (var type in types)
        {
            var fields = type.GetProperties().Select(property => property.Name).ToArray();
            foreach (var forbidden in new[]
                     { "Email", "FirstName", "LastName", "ClientId", "BusinessId", "Token", "Health", "Note", "Phone" })
                Assert.DoesNotContain(fields, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    // --- The catalogue is read by the cookie, never by the route slug --------------------------------------------

    [Fact]
    public async Task The_individual_screen_reads_the_catalog_of_the_slug_of_the_cookie()
    {
        var fixture = await Fixture.CreateAsync();
        // The route slug is valid but different: a controller that used it would read another tenant's catalogue.
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        var controller = fixture.CreateController(routeSlug: OtherSlug);

        var view = Assert.IsType<ViewResult>(await controller.Turno(null, null, null, CancellationToken.None));

        var model = Assert.IsType<ClientIndividualReservationViewModel>(view.Model);
        Assert.Equal(Slug, model.Slug);
        Assert.Contains(Slug, fixture.BusinessSlugs.RequestedSlugs);
        Assert.DoesNotContain(OtherSlug, fixture.BusinessSlugs.RequestedSlugs);
    }

    [Theory]
    [InlineData(777, null)]
    [InlineData(0, null)]
    [InlineData(null, 777)]
    public async Task The_individual_screen_does_not_substitute_an_unknown_requested_service_or_worker(
        int? serviceId, int? workerId)
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        var controller = fixture.CreateController();

        var response = await controller.Turno(serviceId ?? fixture.IndividualServiceId, workerId, SlotDate, CancellationToken.None);

        Assert.IsType<NotFoundResult>(response);
    }

    [Fact]
    public async Task The_individual_screen_offers_the_active_service_worker_and_the_quarter_hour_slots()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        var controller = fixture.CreateController();

        var view = Assert.IsType<ViewResult>(await controller.Turno(null, null, SlotDate, CancellationToken.None));

        var model = Assert.IsType<ClientIndividualReservationViewModel>(view.Model);
        Assert.Equal("Corte", Assert.Single(model.Services).Name);
        Assert.Equal("Ana Pérez", Assert.Single(model.Workers).FullName);
        Assert.Equal(SlotDate, model.Date);
        Assert.NotEmpty(model.AvailableStartTimes);
        Assert.Contains(SlotTime, model.AvailableStartTimes);
        Assert.All(model.AvailableStartTimes, slot => Assert.Equal(0, slot.Minute % 15));
    }

    [Fact]
    public async Task The_individual_screen_offers_only_active_individual_services()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogService(400, "Clase grupal", maxCapacity: 8);
        fixture.AddCatalogService(401, "Servicio archivado", maxCapacity: 1, isActive: false);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        var controller = fixture.CreateController();

        var view = Assert.IsType<ViewResult>(await controller.Turno(null, null, SlotDate, CancellationToken.None));

        var model = Assert.IsType<ClientIndividualReservationViewModel>(view.Model);
        var service = Assert.Single(model.Services);
        Assert.Equal(fixture.IndividualServiceId, service.Id);
    }

    [Fact]
    public async Task The_class_screen_lists_only_the_future_sessions_with_a_free_place()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.GroupServiceId, "Yoga", maxCapacity: 8);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        fixture.AddSession(appointmentId: 900, startsAt: SlotDate.ToDateTime(new TimeOnly(18, 0)), maxCapacity: 8,
            serviceId: fixture.GroupServiceId);
        // A session of today that already started and a full one are never offered.
        fixture.AddSession(appointmentId: 901, startsAt: Now.Date.AddHours(8), maxCapacity: 8,
            serviceId: fixture.GroupServiceId);
        fixture.AddSession(appointmentId: 902, startsAt: SlotDate.ToDateTime(new TimeOnly(19, 0)), maxCapacity: 1,
            serviceId: fixture.GroupServiceId,
            bookings: [new ClassSessionBooking(OtherClientId, BookingStatus.Pending, 501, BookingModality.DropIn, "Otra")]);
        var controller = fixture.CreateController();

        var view = Assert.IsType<ViewResult>(await controller.Classes(CancellationToken.None));

        var model = Assert.IsType<ClientClassReservationViewModel>(view.Model);
        var session = Assert.Single(model.Sessions);
        Assert.Equal(900, session.AppointmentId);
    }

    [Fact]
    public async Task The_reservation_get_pages_never_mutate_state()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        fixture.AddSession(appointmentId: 900, startsAt: SlotDate.ToDateTime(SlotTime), maxCapacity: 8);
        var controller = fixture.CreateController();

        await controller.Turno(null, null, SlotDate, CancellationToken.None);
        await controller.Classes(CancellationToken.None);

        Assert.Empty(fixture.PublicRepository.Appointments);
        Assert.Empty(fixture.PublicRepository.Bookings);
        Assert.Empty(fixture.DropIns.Inserts);
    }

    // --- The individual booking uses only the cookie identity -----------------------------------------------------

    [Fact]
    public async Task The_individual_booking_uses_the_cookie_business_and_client_and_returns_to_the_own_agenda()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        var controller = fixture.CreateController();

        var input = new ClientIndividualReservationInput
        {
            ServiceId = fixture.IndividualServiceId, WorkerId = fixture.WorkerId, Date = SlotDate, StartTime = SlotTime
        };
        var result = await controller.BookTurno(input, CancellationToken.None);

        Assert.Equal($"/r/{Slug}/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        var booking = Assert.Single(fixture.PublicRepository.Bookings);
        Assert.Equal(ClientId, booking.ClientId);
        Assert.Equal(BusinessId, booking.BusinessId);
        var appointment = Assert.Single(fixture.PublicRepository.Appointments);
        Assert.Equal(SlotDate.ToDateTime(SlotTime), appointment.StartsAt);
        // The cookie path never creates a client and never writes a ficha from the request.
        Assert.Single(fixture.PublicRepository.Clients);
        Assert.False(fixture.PublicRepository.LookedUpClientByEmail);
    }

    [Fact]
    public async Task The_individual_booking_of_a_foreign_ficha_is_a_generic_not_found()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        // The cookie names a ficha that does not exist for this tenant.
        var controller = fixture.CreateController(clientId: 999);

        var input = new ClientIndividualReservationInput
        {
            ServiceId = fixture.IndividualServiceId, WorkerId = fixture.WorkerId, Date = SlotDate, StartTime = SlotTime
        };
        var result = await controller.BookTurno(input, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Null(controller.TempData["FlashMessage"]);
        Assert.Empty(fixture.PublicRepository.Bookings);
    }

    [Fact]
    public async Task The_individual_booking_with_an_inaccessible_catalog_is_a_generic_not_found()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        var controller = fixture.CreateController();

        var input = new ClientIndividualReservationInput
        {
            ServiceId = fixture.IndividualServiceId, WorkerId = 999, Date = SlotDate, StartTime = SlotTime
        };
        var result = await controller.BookTurno(input, CancellationToken.None);

        // The worker is not active in this tenant: the same generic 404, without leaking whether the id exists.
        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(fixture.PublicRepository.Bookings);
    }

    [Fact]
    public async Task The_individual_booking_of_a_stale_slot_rerenders_with_a_safe_message_alternative_and_fresh_options()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        // The chosen slot is taken between the read and the save.
        fixture.PublicRepository.Snapshot = fixture.PublicRepository.Snapshot with
        {
            Appointments =
            [
                new Appointment
                {
                    Id = 40, BusinessId = BusinessId, WorkerId = fixture.WorkerId, ServiceId = fixture.IndividualServiceId,
                    Kind = AppointmentKind.Individual, Origin = AppointmentOrigin.Reserved,
                    Status = AppointmentStatus.Scheduled,
                    StartsAt = SlotDate.ToDateTime(SlotTime), EndsAt = SlotDate.ToDateTime(SlotTime).AddMinutes(30)
                }
            ]
        };
        var controller = fixture.CreateController();

        var input = new ClientIndividualReservationInput
        {
            ServiceId = fixture.IndividualServiceId, WorkerId = fixture.WorkerId, Date = SlotDate, StartTime = SlotTime
        };
        var view = Assert.IsType<ViewResult>(await controller.BookTurno(input, CancellationToken.None));

        Assert.Equal("Individual", view.ViewName);
        var model = Assert.IsType<ClientIndividualReservationViewModel>(view.Model);
        var messages = view.ViewData.ModelState.Values.SelectMany(value => value.Errors)
            .Select(error => error.ErrorMessage).ToArray();
        Assert.Contains(messages, message => message.Contains("se ocupó", StringComparison.Ordinal));
        Assert.NotNull(model.NearestAlternative);
        Assert.DoesNotContain(SlotTime, model.AvailableStartTimes);
        Assert.NotEmpty(model.AvailableStartTimes);
        Assert.Empty(fixture.PublicRepository.Bookings);
    }

    [Fact]
    public async Task The_individual_booking_requires_a_day_and_a_time()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.IndividualServiceId, "Corte", maxCapacity: 1);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        var controller = fixture.CreateController();

        var view = Assert.IsType<ViewResult>(await controller.BookTurno(
            new ClientIndividualReservationInput { ServiceId = fixture.IndividualServiceId, WorkerId = fixture.WorkerId },
            CancellationToken.None));

        var messages = view.ViewData.ModelState.Values.SelectMany(value => value.Errors)
            .Select(error => error.ErrorMessage).ToArray();
        Assert.Contains("Elegí un día.", messages);
        Assert.Contains("Elegí un horario.", messages);
        Assert.Empty(fixture.PublicRepository.Bookings);
    }

    // --- The drop-in class list ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_class_screen_lists_future_drop_in_sessions_with_capacity_and_names()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.GroupServiceId, "Yoga", maxCapacity: 8);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        fixture.AddSession(
            appointmentId: 900, startsAt: SlotDate.ToDateTime(new TimeOnly(18, 0)), maxCapacity: 8,
            serviceId: fixture.GroupServiceId,
            bookings: [new ClassSessionBooking(OtherClientId, BookingStatus.Pending, 501, BookingModality.DropIn, "Otra")]);
        var controller = fixture.CreateController();

        var view = Assert.IsType<ViewResult>(await controller.Classes(CancellationToken.None));

        var model = Assert.IsType<ClientClassReservationViewModel>(view.Model);
        Assert.Equal(Slug, model.Slug);
        var session = Assert.Single(model.Sessions);
        Assert.Equal(900, session.AppointmentId);
        Assert.Equal("Yoga", session.ServiceName);
        Assert.Equal("Ana Pérez", session.TeacherName);
        Assert.Equal(7, session.RemainingPlaces);
    }

    // --- The class booking ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_class_booking_uses_the_cookie_business_and_client_and_returns_to_the_own_agenda()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.GroupServiceId, "Yoga", maxCapacity: 8);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        fixture.AddSession(appointmentId: 900, startsAt: SlotDate.ToDateTime(new TimeOnly(18, 0)), maxCapacity: 8,
            serviceId: fixture.GroupServiceId);
        fixture.DropIns.Clients.Add(new FakeClassDropInClient(
            BusinessId, new DropInClient(ClientId, "ana@example.invalid", "Ana", "Pérez")));
        var controller = fixture.CreateController();

        var result = await controller.BookClass(900, CancellationToken.None);

        Assert.Equal($"/r/{Slug}/mis-turnos", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        var insert = Assert.Single(fixture.DropIns.Inserts);
        Assert.Equal(BusinessId, insert.BusinessId);
        Assert.Equal(ClientId, insert.ClientId);
        Assert.Equal(900, insert.AppointmentId);
    }

    [Fact]
    public async Task The_class_booking_of_a_foreign_or_missing_session_is_a_generic_not_found()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.GroupServiceId, "Yoga", maxCapacity: 8);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        fixture.AddSession(appointmentId: 900, startsAt: SlotDate.ToDateTime(new TimeOnly(18, 0)), maxCapacity: 8,
            serviceId: fixture.GroupServiceId);
        fixture.AddSession(appointmentId: 901, startsAt: SlotDate.ToDateTime(new TimeOnly(19, 0)), maxCapacity: 8,
            serviceId: fixture.GroupServiceId, businessId: OtherBusinessId);
        fixture.DropIns.Clients.Add(new FakeClassDropInClient(
            BusinessId, new DropInClient(ClientId, "ana@example.invalid", "Ana", "Pérez")));
        var controller = fixture.CreateController();

        Assert.IsType<NotFoundResult>(await controller.BookClass(901, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.BookClass(999, CancellationToken.None));
        Assert.Null(controller.TempData["FlashMessage"]);
        Assert.Empty(fixture.DropIns.Inserts);

        // A ficha that is not of this tenant is refused by the same generic 404.
        var foreignClient = fixture.CreateController(clientId: 999);
        Assert.IsType<NotFoundResult>(await foreignClient.BookClass(900, CancellationToken.None));
        Assert.Empty(fixture.DropIns.Inserts);
    }

    [Fact]
    public async Task The_class_booking_of_a_full_class_rerenders_with_safe_feedback_and_no_insert()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.GroupServiceId, "Yoga", maxCapacity: 8);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        fixture.AddSession(appointmentId: 900, startsAt: SlotDate.ToDateTime(new TimeOnly(18, 0)), maxCapacity: 1,
            serviceId: fixture.GroupServiceId,
            bookings: [new ClassSessionBooking(OtherClientId, BookingStatus.Pending, 501, BookingModality.DropIn, "Otra")]);
        fixture.DropIns.Clients.Add(new FakeClassDropInClient(
            BusinessId, new DropInClient(ClientId, "ana@example.invalid", "Ana", "Pérez")));
        var controller = fixture.CreateController();

        var view = Assert.IsType<ViewResult>(await controller.BookClass(900, CancellationToken.None));

        Assert.Equal("Classes", view.ViewName);
        var messages = view.ViewData.ModelState.Values.SelectMany(value => value.Errors)
            .Select(error => error.ErrorMessage).ToArray();
        Assert.Contains(messages, message => message.Contains("completa", StringComparison.Ordinal));
        Assert.Empty(fixture.DropIns.Inserts);
    }

    [Fact]
    public async Task The_class_booking_of_a_duplicate_rerenders_with_safe_feedback_and_no_insert()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.AddCatalogService(fixture.GroupServiceId, "Yoga", maxCapacity: 8);
        fixture.AddCatalogWorker(fixture.WorkerId, "Ana Pérez");
        // The same ficha already holds a place in the class.
        fixture.AddSession(appointmentId: 900, startsAt: SlotDate.ToDateTime(new TimeOnly(18, 0)), maxCapacity: 8,
            serviceId: fixture.GroupServiceId,
            bookings: [new ClassSessionBooking(ClientId, BookingStatus.Pending, 501, BookingModality.DropIn, "Ana")]);
        fixture.DropIns.Clients.Add(new FakeClassDropInClient(
            BusinessId, new DropInClient(ClientId, "ana@example.invalid", "Ana", "Pérez")));
        var controller = fixture.CreateController();

        var view = Assert.IsType<ViewResult>(await controller.BookClass(900, CancellationToken.None));

        Assert.Equal("Classes", view.ViewName);
        var messages = view.ViewData.ModelState.Values.SelectMany(value => value.Errors)
            .Select(error => error.ErrorMessage).ToArray();
        Assert.Contains(messages, message => message.Contains("Ya estás anotada", StringComparison.Ordinal));
        Assert.Empty(fixture.DropIns.Inserts);
    }

    // --- No public email method is ever used ---------------------------------------------------------------------

    [Fact]
    public void The_controller_never_uses_the_public_email_booking_paths()
    {
        var source = ReadSource("WorkUp", "Controllers", "ClientReservationsController.cs");

        Assert.Contains(nameof(BookingService.BookIndividualForClientAsync), source);
        Assert.DoesNotContain(nameof(BookingService.BookIndividualAsync), source);
        Assert.Contains(nameof(ClassDropInService.BookForClientAsync), source);
        Assert.DoesNotContain(nameof(ClassDropInService.BookPublicAsync), source);
    }

    // --- The views wire only antiforgery POST forms and never a private token or identity input -------------------

    [Fact]
    public void The_individual_view_posts_with_antiforgery_and_offers_no_contact_or_identity_input()
    {
        var view = ReadView("Individual.cshtml");

        Assert.Contains("method=\"post\"", view);
        Assert.Contains("@Html.AntiForgeryToken()", view);
        Assert.Contains("asp-for=\"Input.ServiceId\"", view);
        Assert.Contains("asp-for=\"Input.WorkerId\"", view);
        Assert.Contains("asp-for=\"Input.Date\"", view);
        Assert.Contains("asp-for=\"Input.StartTime\"", view);
        Assert.DoesNotContain("type=\"email\"", view);
        Assert.DoesNotContain("Input.Email", view);
        Assert.DoesNotContain("Input.FirstName", view);
        Assert.DoesNotContain("Input.LastName", view);
    }

    [Fact]
    public void The_classes_view_posts_each_session_with_antiforgery_and_offers_no_contact_input()
    {
        var view = ReadView("Classes.cshtml");

        Assert.Contains("method=\"post\"", view);
        Assert.Contains("@Html.AntiForgeryToken()", view);
        Assert.Contains("/mis-turnos/reservar/clase/@session.AppointmentId", view);
        Assert.Contains("session.RemainingPlaces", view);
        Assert.DoesNotContain("type=\"email\"", view);
        Assert.DoesNotContain("Email", view);
        Assert.DoesNotContain("FirstName", view);
        Assert.DoesNotContain("LastName", view);
    }

    [Fact]
    public void The_reservation_views_never_render_a_private_token_note_or_health_data()
    {
        foreach (var fileName in new[] { "Individual.cshtml", "Classes.cshtml" })
        {
            var view = ReadView(fileName);

            Assert.DoesNotContain("/t/", view);
            Assert.DoesNotContain("BookingToken", view);
            Assert.DoesNotContain("LinkToken", view);
            Assert.DoesNotContain("HealthNotes", view);
            Assert.DoesNotContain("Note", view);
        }
    }

    // --- The agenda offers two visible entry links, even when it is empty ----------------------------------------

    [Fact]
    public void The_agenda_offers_two_visible_entry_links_to_both_reservation_screens()
    {
        var view = ReadSource("WorkUp", "Views", "ClientAppointments", "Index.cshtml");

        var individualLink = view.IndexOf(
            "href=\"/r/@Model.Slug/mis-turnos/reservar/turno\"", StringComparison.Ordinal);
        var classLink = view.IndexOf(
            "href=\"/r/@Model.Slug/mis-turnos/reservar/clase\"", StringComparison.Ordinal);
        Assert.True(individualLink >= 0, "El panel debe ofrecer el enlace para reservar un turno.");
        Assert.True(classLink >= 0, "El panel debe ofrecer el enlace para anotarse a una clase suelta.");

        // Both links live before the first conditional section, so an empty agenda still shows them.
        var firstConditional = view.IndexOf("!Model.HasUpcoming", StringComparison.Ordinal);
        Assert.True(firstConditional > individualLink, "El enlace del turno debe verse aunque la agenda esté vacía.");
        Assert.True(firstConditional > classLink, "El enlace de la clase debe verse aunque la agenda esté vacía.");
        Assert.Contains("Reservar un turno", view);
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static string ReadSource(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine([root, .. segments]));
    }

    private static string ReadView(string fileName) => ReadSource("WorkUp", "Views", "ClientReservations", fileName);

    private sealed class Fixture
    {
        private Fixture() { }

        public FakeBusinessRepository Businesses { get; } = new();

        public RecordingBusinessRepository BusinessSlugs { get; private set; } = null!;

        public RecordingBrowseRepository Browse { get; } = new();

        public FakeBookingRepository PublicRepository { get; } = new();

        public FakeClassDropInRepository DropIns { get; } = new();

        public FakeServiceRepository Services { get; } = new();

        public FakeWorkerRepository Workers { get; } = new();

        public FakeClock Clock { get; } = new() { Now = Now };

        public int BusinessId { get; private set; }

        public int IndividualServiceId { get; private set; }

        public int GroupServiceId { get; private set; }

        public int WorkerId { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.BusinessSlugs = new RecordingBusinessRepository(fixture.Businesses);
            fixture.BusinessId = (await fixture.Businesses.InsertAsync(
                new Business { Name = "Peluquería Marina", Slug = Slug })).BusinessId;
            fixture.IndividualServiceId = await fixture.Services.InsertAsync(new Service
            {
                BusinessId = fixture.BusinessId, Name = "Corte", Price = 5000m, DurationMinutes = 30,
                MaxCapacity = 1, IsActive = true
            });
            fixture.GroupServiceId = await fixture.Services.InsertAsync(new Service
            {
                BusinessId = fixture.BusinessId, Name = "Yoga", Price = 3000m, DurationMinutes = 60,
                MaxCapacity = 8, IsActive = true
            });
            fixture.WorkerId = (await fixture.Workers.InsertAsync(new Worker
            {
                BusinessId = fixture.BusinessId, FirstName = "Ana", LastName = "Pérez", Phone = "1155001234",
                Cuil = "27312345674", IsActive = true
            })).WorkerId;
            fixture.PublicRepository.Worker = new Worker
            {
                Id = fixture.WorkerId, BusinessId = fixture.BusinessId, FirstName = "Ana", LastName = "Pérez",
                IsActive = true
            };
            fixture.PublicRepository.Service = new Service
            {
                Id = fixture.IndividualServiceId, BusinessId = fixture.BusinessId, Name = "Corte", Price = 5000m,
                DurationMinutes = 30, MaxCapacity = 1, IsActive = true
            };
            fixture.PublicRepository.BusinessSettings = new PublicBookingBusinessSettings("Peluquería Marina", 24);
            fixture.PublicRepository.Snapshot = new BookingAvailabilitySnapshot(
                Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
                {
                    WorkerId = fixture.WorkerId, DayOfWeek = day,
                    StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(20, 0)
                }).ToArray(),
                [], [], []);
            fixture.PublicRepository.Clients.Add(new Client
            {
                Id = ClientId, BusinessId = fixture.BusinessId, FirstName = "Ana", LastName = "Pérez",
                Email = "ana@example.invalid", HealthNotes = "private health note"
            });
            return fixture;
        }

        public void AddCatalogService(int serviceId, string name, int maxCapacity, bool isActive = true) =>
            Browse.Services.Add(new Service
            {
                Id = serviceId, BusinessId = BusinessId, Name = name, Price = 5000m, DurationMinutes = 30,
                MaxCapacity = maxCapacity, IsActive = isActive
            });

        public void AddCatalogWorker(int workerId, string fullName) =>
            Browse.Workers.Add(new PublicBookingWorkerOption(workerId, fullName));

        public void AddSession(
            int appointmentId,
            DateTime startsAt,
            int maxCapacity,
            int? serviceId = null,
            int? businessId = null,
            params ClassSessionBooking[] bookings) =>
            DropIns.Sessions.Add(new FakeClassDropInSession(
                businessId ?? BusinessId,
                new ClassSessionRow(
                    appointmentId, serviceId ?? GroupServiceId, WorkerId, startsAt, maxCapacity,
                    AppointmentKind.ClassSession, AppointmentStatus.Scheduled, bookings)));

        public ClientReservationsController CreateController(
            string routeSlug = Slug, int clientId = ClientId, string cookieSlug = Slug,
            int businessId = ClientReservationsControllerTests.BusinessId)
        {
            var browseService = new PublicBookingBrowseService(BusinessSlugs, Browse, PublicRepository, Clock);
            var bookingService = new BookingService(
                PublicRepository, new FakeUnitOfWork(), new TestLinkTokenProtector(),
                new TestAppointmentUrlBuilder(), Clock);
            var dropInService = new ClassDropInService(DropIns, new FakeUnitOfWork(), Clock);
            var catalogService = new CatalogService(Services, new FakeUnitOfWork());
            var staffService = new StaffService(Workers, new FakeUnitOfWork());

            var routeData = new RouteData();
            routeData.Values["slug"] = routeSlug;
            var httpContext = new DefaultHttpContext { User = Principal(businessId, clientId, cookieSlug) };
            return new ClientReservationsController(
                browseService, bookingService, dropInService, catalogService, staffService, Clock)
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
    }

    private static ClaimsPrincipal Principal(int businessId, int clientId, string slug)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(WorkUpClaims.BusinessId, businessId.ToString()),
                new Claim(WorkUpClaims.BusinessName, "Peluquería Marina"),
                new Claim(WorkUpClaims.BusinessSlug, slug),
                new Claim(WorkUpClaims.ClientAccountId, "3"),
                new Claim(WorkUpClaims.ClientId, clientId.ToString()),
                new Claim(ClaimTypes.Email, "ana@example.invalid")
            ],
            AuthSchemes.Client);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Records the slug of every catalogue read, so a test can prove the cookie slug was used.</summary>
    private sealed class RecordingBusinessRepository(IBusinessRepository inner) : IBusinessRepository
    {
        public List<string> RequestedSlugs { get; } = [];

        public Task<Business?> GetByIdAsync(int id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<Business?> GetBySlugAsync(string slug, CancellationToken ct = default)
        {
            RequestedSlugs.Add(slug);
            return inner.GetBySlugAsync(slug, ct);
        }

        public Task<bool> SlugExistsAsync(string slug, int? excludingId = null, CancellationToken ct = default) =>
            inner.SlugExistsAsync(slug, excludingId, ct);

        public Task<BusinessInsertResult> InsertAsync(Business business, CancellationToken ct = default) =>
            inner.InsertAsync(business, ct);

        public Task<BusinessUpdateResult> UpdateAsync(Business business, CancellationToken ct = default) =>
            inner.UpdateAsync(business, ct);
    }

    /// <summary>The public catalogue of the fixture, with the tenant of every read recorded.</summary>
    private sealed class RecordingBrowseRepository : IPublicBookingBrowseRepository
    {
        public List<Service> Services { get; } = [];

        public List<PublicBookingWorkerOption> Workers { get; } = [];

        public List<int> ServiceListBusinessIds { get; } = [];

        public List<int> WorkerListBusinessIds { get; } = [];

        public Task<IReadOnlyList<Service>> ListActiveIndividualServicesAsync(
            int businessId, CancellationToken ct = default)
        {
            ServiceListBusinessIds.Add(businessId);
            return Task.FromResult<IReadOnlyList<Service>>(
                Services.Where(service => service.BusinessId == businessId).ToArray());
        }

        public Task<IReadOnlyList<PublicBookingWorkerOption>> ListActiveWorkersAsync(
            int businessId, CancellationToken ct = default)
        {
            WorkerListBusinessIds.Add(businessId);
            return Task.FromResult<IReadOnlyList<PublicBookingWorkerOption>>(Workers.ToArray());
        }

        public Task<PublicBookingWorkerOption?> GetActiveWorkerAsync(
            int businessId, int workerId, CancellationToken ct = default) =>
            Task.FromResult(Workers.SingleOrDefault(worker => worker.Id == workerId));
    }

    private sealed class TestLinkTokenProtector : ILinkTokenProtector
    {
        public string Protect(string token) => "protected:" + token;
        public string Unprotect(string protectedToken) => protectedToken["protected:".Length..];
    }

    private sealed class TestAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://workup.test/t/" + token;
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
