using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Classes;

namespace WorkUp.Tests.Classes;

// T14 owner side (RF-22, RF-07 cupo > 1, RF-11): the assisted booking of a class session and the cancellation of a
// drop-in before it starts, from the owner panel. The controller only routes: the roster window, the eligibility
// rules and the transactional writes belong to ClassDropInService and are already tested at the service level
// (ClassDropInServiceTests). What is pinned here is what the panel adds: names resolved for the owner, the two
// POST/redirect/GET actions, the mapping of the stable error codes to Spanish messages, the courtesy filters the
// screen offers (no cancel action for a started or suspended session) and that the page itself never writes.
public sealed class ClassDropInOwnerControllerTests
{
    private static int _nextAppointmentId = 100;

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Fact]
    public async Task DropIns_get_renders_the_sessions_and_their_annotations_with_names()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, unitOfWork) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId,
            bookings:
            [
                new ClassSessionBooking(11, BookingStatus.Pending, 501, BookingModality.DropIn, "Elena Gómez"),
                new ClassSessionBooking(12, BookingStatus.Cancelled, 502, BookingModality.DropIn, "Paula Ruiz")
            ]);

        var view = Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None));
        var model = Assert.IsType<DropInOwnerIndexViewModel>(view.Model);

        var session = Assert.Single(model.Sessions);
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.Equal(D(2026, 3, 12), session.Date);
        Assert.Equal(new TimeOnly(18, 0), session.StartTime);
        Assert.Equal("Yoga", session.ServiceName);
        Assert.Equal("Ana Díaz", session.TeacherName);
        Assert.False(session.Suspended);
        Assert.True(session.CanBook);
        Assert.Equal("Programado", session.StatusTag.Label);
        Assert.True(session.HasDropIns);

        Assert.Equal(new[] { "Elena Gómez", "Paula Ruiz" }, session.DropIns.Select(entry => entry.ClientName).ToArray());
        Assert.Equal(new[] { 501, 502 }, session.DropIns.Select(entry => entry.BookingId).ToArray());

        var pending = session.DropIns[0];
        Assert.False(pending.IsCancelled);
        Assert.True(pending.CanCancel);
        Assert.Equal("Pendiente", pending.StatusTag.Label);

        // A cancelled annotation stays on the page with its status instead of disappearing: the owner has to see
        // which place was released. Only its cancel action is gone.
        var cancelled = session.DropIns[1];
        Assert.True(cancelled.IsCancelled);
        Assert.False(cancelled.CanCancel);
        Assert.Equal("Cancelado", cancelled.StatusTag.Label);

        Assert.Equal(1, model.ActiveDropInCount);
        Assert.Equal(1, model.CancelledDropInCount);

        // The roster is a read: it opens no transaction and writes nothing.
        Assert.Equal(0, unitOfWork.BeginCount);
        Assert.Equal(0, dropIns.InsertCallCount);
        Assert.Equal(0, dropIns.CancelCallCount);
    }

    [Fact]
    public async Task DropIns_get_includes_the_sessions_without_annotations()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, _) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId);

        var model = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None)).Model);

        var session = Assert.Single(model.Sessions);
        Assert.False(session.HasDropIns);
        Assert.Empty(session.DropIns);
        Assert.Equal(0, model.ActiveDropInCount);
    }

    [Fact]
    public async Task DropIns_get_labels_an_annotation_whose_client_could_not_be_resolved()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, _) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        // The roster keeps an annotation whose client row could not be resolved instead of hiding it, so the label
        // falls back and the cancel action keeps a meaning.
        SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId,
            bookings: [new ClassSessionBooking(11, BookingStatus.Pending, 501, BookingModality.DropIn)]);

        var model = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None)).Model);

        var annotation = Assert.Single(Assert.Single(model.Sessions).DropIns);
        Assert.Equal("", annotation.ClientName);
        Assert.Equal("Alumna", annotation.DisplayName);
    }

    [Fact]
    public async Task DropIns_get_bounds_the_roster_to_today_and_the_next_thirty_days()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, _) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var lastDayInsideWindow = SeedSession(dropIns, new DateTime(2026, 4, 9, 18, 0, 0), serviceId, workerId);
        SeedSession(dropIns, new DateTime(2026, 4, 10, 18, 0, 0), serviceId, workerId); // one day past the window

        var model = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None)).Model);

        Assert.Equal(lastDayInsideWindow, Assert.Single(model.Sessions).AppointmentId);
        Assert.Equal(D(2026, 3, 10), model.Today);
        Assert.Equal(D(2026, 4, 9), model.WindowEnd);
    }

    [Fact]
    public async Task DropIns_get_never_shows_another_business_session()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, _) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        dropIns.Sessions.Add(new FakeClassDropInSession(8, new ClassSessionRow(
            900, serviceId, workerId, new DateTime(2026, 3, 12, 18, 0, 0), 8,
            AppointmentKind.ClassSession, AppointmentStatus.Scheduled, [])));

        var model = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None)).Model);

        Assert.Empty(model.Sessions);
    }

    [Fact]
    public async Task DropIns_get_offers_no_action_for_a_suspended_session_but_still_shows_it()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, _) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId,
            status: AppointmentStatus.Suspended,
            bookings: [new ClassSessionBooking(11, BookingStatus.Pending, 501, BookingModality.DropIn, "Elena Gómez")]);

        var model = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None)).Model);

        // Suspending a class cancels nobody, so the session and its annotation stay visible; what disappears are the
        // two actions the service would reject anyway.
        var session = Assert.Single(model.Sessions);
        Assert.True(session.Suspended);
        Assert.False(session.CanBook);
        Assert.Equal("Suspendido", session.StatusTag.Label);
        Assert.False(Assert.Single(session.DropIns).CanCancel);

        // A query string cannot open the booking form for it either.
        var suspended = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(appointmentId, CancellationToken.None)).Model);
        Assert.Null(suspended.OpenAppointmentId);
    }

    [Fact]
    public async Task DropIns_get_never_offers_a_started_session()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, _) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        // Started an hour ago, with an active annotation: nothing on the page may offer to cancel it.
        SeedSession(dropIns, new DateTime(2026, 3, 10, 8, 0, 0), serviceId, workerId,
            bookings: [new ClassSessionBooking(11, BookingStatus.Pending, 501, BookingModality.DropIn, "Elena Gómez")]);

        var model = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None)).Model);

        Assert.Empty(model.Sessions);
    }

    [Fact]
    public async Task DropIns_get_opens_the_booking_form_only_for_a_session_that_is_offered()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, _) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId);

        var opened = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(appointmentId, CancellationToken.None)).Model);
        Assert.Equal(appointmentId, opened.OpenAppointmentId);

        var unknown = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(9999, CancellationToken.None)).Model);
        Assert.Null(unknown.OpenAppointmentId);

        var closed = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None)).Model);
        Assert.Null(closed.OpenAppointmentId);
    }

    [Fact]
    public async Task DropIns_get_lists_only_the_clients_of_the_business_in_the_picker()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, clients, _) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId);
        await SeedClient(clients, "Elena", "Gómez");
        await clients.InsertAsync(new Client
        {
            BusinessId = 8, FirstName = "Ajena", LastName = "De Otro Negocio", Email = "ajena@example.com", Phone = "1145667788"
        });

        var model = Assert.IsType<DropInOwnerIndexViewModel>(
            Assert.IsType<ViewResult>(await controller.DropIns(null, CancellationToken.None)).Model);

        var option = Assert.Single(model.Clients);
        Assert.Equal("Elena Gómez", option.Name);
        Assert.True(model.HasClients);
    }

    [Fact]
    public async Task BookDropIn_post_books_the_same_business_session_and_client_and_redirects_with_a_success_flash()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, unitOfWork) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId);
        SeedDropInClient(dropIns, 42, "Elena", "Gómez");

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.BookDropIn(appointmentId, 42, CancellationToken.None));

        Assert.Equal("DropIns", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Alumna anotada en la clase suelta.", Assert.IsType<string>(controller.TempData["FlashMessage"]));

        var insert = Assert.Single(dropIns.Inserts);
        Assert.Equal(7, insert.BusinessId);
        Assert.Equal(appointmentId, insert.AppointmentId);
        Assert.Equal(42, insert.ClientId);
        Assert.Equal(clock.Now, insert.CreatedAt);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task BookDropIn_post_flashes_the_mapped_message_of_a_full_session_and_never_writes()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, unitOfWork) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId,
            maxCapacity: 1,
            bookings: [new ClassSessionBooking(11, BookingStatus.Confirmed, 501, BookingModality.DropIn, "Paula Ruiz")]);
        SeedDropInClient(dropIns, 42, "Elena", "Gómez");

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.BookDropIn(appointmentId, 42, CancellationToken.None));

        Assert.Equal("DropIns", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        // The stable code maps to a sentence: the code itself never reaches the owner.
        Assert.Equal("La clase está completa. Probá con otro día u horario.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Empty(dropIns.Inserts);
        Assert.Equal(0, unitOfWork.CommitCount);
        Assert.Equal(1, unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task BookDropIn_post_flashes_the_mapped_message_of_a_foreign_client()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, unitOfWork) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.BookDropIn(appointmentId, 42, CancellationToken.None));

        Assert.Equal("DropIns", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No encontramos a esa alumna en tu negocio.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Empty(dropIns.Inserts);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task BookDropIn_post_without_a_selected_client_flashes_and_never_reaches_the_service()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, unitOfWork) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.BookDropIn(appointmentId, 0, CancellationToken.None));

        Assert.Equal("DropIns", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Elegí una alumna con ficha para anotar en la clase.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, unitOfWork.BeginCount);
        Assert.Empty(dropIns.Inserts);
    }

    [Fact]
    public async Task CancelDropIn_post_cancels_the_same_booking_and_redirects_with_a_success_flash()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, unitOfWork) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId);
        dropIns.DropInBookings.Add(new FakeClassDropInBooking(7, new DropInCancellationState(
            501, appointmentId, new DateTime(2026, 3, 12, 18, 0, 0), AppointmentKind.ClassSession,
            AppointmentStatus.Scheduled, BookingStatus.Pending, BookingModality.DropIn)));

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.CancelDropIn(501, CancellationToken.None));

        Assert.Equal("DropIns", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Reserva suelta cancelada.", Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal((501, clock.Now), Assert.Single(dropIns.CancelRequests));
        Assert.Equal(CancelledBy.Owner, Assert.Single(dropIns.DropInBookings).CancelledBy);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task CancelDropIn_post_of_an_already_cancelled_drop_in_flashes_the_mapped_message()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, dropIns, services, workers, _, unitOfWork) = Create(clock);
        var serviceId = await SeedService(services, "Yoga");
        var workerId = await SeedWorker(workers);
        var appointmentId = SeedSession(dropIns, new DateTime(2026, 3, 12, 18, 0, 0), serviceId, workerId);
        dropIns.DropInBookings.Add(new FakeClassDropInBooking(7, new DropInCancellationState(
            501, appointmentId, new DateTime(2026, 3, 12, 18, 0, 0), AppointmentKind.ClassSession,
            AppointmentStatus.Scheduled, BookingStatus.Cancelled, BookingModality.DropIn)));

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.CancelDropIn(501, CancellationToken.None));

        Assert.Equal("DropIns", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Esa reserva suelta ya estaba cancelada.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, dropIns.CancelCallCount);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task CancelDropIn_post_of_an_unknown_booking_flashes_the_mapped_message()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, _, _, _, _, unitOfWork) = Create(clock);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.CancelDropIn(404, CancellationToken.None));

        Assert.Equal("DropIns", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No encontramos esa reserva suelta.", Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public void DropIns_view_renders_one_booking_form_per_session_gated_on_the_open_session()
    {
        var view = ReadClassesView("DropIns.cshtml");

        // The booking form lives inside the session loop and only the session the controller opened renders it: that
        // is what keeps every field id unique and the screen down to a single primary action.
        var loop = view.IndexOf("foreach (var session in daySessions)", StringComparison.Ordinal);
        var gate = view.IndexOf("Model.OpenAppointmentId == session.AppointmentId", StringComparison.Ordinal);
        var bookingForm = view.IndexOf("asp-action=\"BookDropIn\"", StringComparison.Ordinal);
        Assert.True(loop >= 0, "La vista debe recorrer las sesiones del día.");
        Assert.True(gate > loop, "El formulario de reserva debe estar dentro del recorrido de sesiones y con su gate.");
        Assert.True(bookingForm > gate, "La acción de reserva debe seguir al gate de la sesión abierta.");

        // The cancel action is offered only for the annotations the controller marked as cancellable.
        var cancelGate = view.IndexOf("annotation.CanCancel", StringComparison.Ordinal);
        var cancelForm = view.IndexOf("asp-action=\"CancelDropIn\"", StringComparison.Ordinal);
        Assert.True(cancelGate >= 0, "La vista debe respetar el flag de cancelación del view model.");
        Assert.True(cancelForm > cancelGate, "La acción de cancelación debe seguir al gate del flag.");

        // Exactly two primary buttons in the file, each on a mutually exclusive screen: the empty-state escape and
        // the submit of the one open booking form. No screen shows two competing primary actions.
        Assert.Equal(2, view.Split("btn-primary").Length - 1);
    }

    [Fact]
    public void Classes_index_links_the_drop_in_page_behind_the_same_active_grid_gate_as_the_week()
    {
        var index = ReadClassesView("Index.cshtml");

        var gate = index.IndexOf("Model.HasAnyActive", StringComparison.Ordinal);
        var week = index.IndexOf("asp-action=\"Week\"", StringComparison.Ordinal);
        var dropIns = index.IndexOf("asp-action=\"DropIns\"", StringComparison.Ordinal);

        Assert.True(gate >= 0, "La grilla debe seguir mirando si hay clases activas.");
        Assert.True(week > gate, "Generar semana debe colgar del gate de clases activas.");
        Assert.True(dropIns > week, "Las clases sueltas deben colgar del mismo gate, después de Generar semana.");
    }

    [Fact]
    public void DropIns_view_links_every_session_to_its_generated_class_detail()
    {
        var view = ReadClassesView("DropIns.cshtml");

        // The roster is the class list the owner sees; every generated session it shows must open its detail page
        // (T16), which is where attendance, walk-in, cancellation and suspension live.
        var loop = view.IndexOf("foreach (var session in daySessions)", StringComparison.Ordinal);
        var detailLink = view.IndexOf("asp-action=\"Detail\"", StringComparison.Ordinal);
        var sessionId = view.IndexOf("asp-route-id=\"@session.AppointmentId\"", StringComparison.Ordinal);

        Assert.True(loop >= 0, "La vista debe recorrer las sesiones del día.");
        Assert.True(detailLink > loop, "El detalle de la clase debe ofrecerse desde cada sesión del listado.");
        Assert.True(sessionId > detailLink, "El enlace al detalle debe llevar el id de la sesión generada.");
    }

    private static string ReadClassesView(string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", "Classes", fileName));
    }

    private static async Task<int> SeedService(FakeServiceRepository services, string name, int maxCapacity = 8) =>
        await services.InsertAsync(new Service
        {
            BusinessId = 7, Name = name, Price = 3000m, DurationMinutes = 60, MaxCapacity = maxCapacity, IsActive = true
        });

    private static async Task<int> SeedWorker(FakeWorkerRepository workers) =>
        await workers.InsertAsync(new Worker { BusinessId = 7, FirstName = "Ana", LastName = "Díaz", IsActive = true });

    private static async Task<int> SeedClient(FakeClientRepository clients, string firstName, string lastName)
    {
        var client = new Client
        {
            BusinessId = 7,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}@example.com",
            Phone = "1145667788",
            CreatedAt = new DateTime(2026, 1, 1, 9, 0, 0)
        };
        return await clients.InsertAsync(client);
    }

    private static int SeedSession(
        FakeClassDropInRepository dropIns,
        DateTime startsAt,
        int serviceId,
        int workerId,
        int maxCapacity = 8,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        ClassSessionBooking[]? bookings = null)
    {
        var appointmentId = _nextAppointmentId++;
        dropIns.Sessions.Add(new FakeClassDropInSession(7, new ClassSessionRow(
            appointmentId, serviceId, workerId, startsAt, maxCapacity,
            AppointmentKind.ClassSession, status, bookings ?? [])));
        return appointmentId;
    }

    private static void SeedDropInClient(
        FakeClassDropInRepository dropIns, int clientId, string firstName, string lastName, string email = "elena@example.com") =>
        dropIns.Clients.Add(new FakeClassDropInClient(7, new DropInClient(clientId, email, firstName, lastName)));

    private static (ClassesController Controller, FakeClassDropInRepository DropIns, FakeServiceRepository Services,
        FakeWorkerRepository Workers, FakeClientRepository Clients, FakeUnitOfWork UnitOfWork) Create(IClock clock)
    {
        var gridClasses = new FakeGridClassRepository();
        var services = new FakeServiceRepository();
        var workers = new FakeWorkerRepository();
        var weeks = new FakeWeekGenerationRepository();
        var dropIns = new FakeClassDropInRepository();
        var clients = new FakeClientRepository();
        var unitOfWork = new FakeUnitOfWork();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkUpClaims.BusinessId, "7")], "test"))
        };
        var controller = new ClassesController(
            new ClassesService(gridClasses, services, workers, clock),
            new CatalogService(services),
            new StaffService(workers, unitOfWork),
            new WeekGenerationService(weeks, unitOfWork, clock),
            clock,
            new ClassDropInService(dropIns, unitOfWork, clock),
            new ClientsService(clients, new FakeClientNoteRepository(), clock))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
        return (controller, dropIns, services, workers, clients, unitOfWork);
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
