using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
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

// T16 owner UI (RF-10, RF-11, RF-12, RF-22): the detail page of one generated class and its five actions — per-student
// attendance with the optional drop-in charge and observation, absence, at-arrival walk-in, cancellation of one
// annotation and suspension of the date. The controller only routes and maps: the rules, the locks and the
// transactions belong to ClassDetailService, ClassAttendanceService, ClassWalkInService and ClassDateActionsService and
// are already tested at their own level. What is pinned here is what the panel adds: the derived labels the owner
// reads, the action flags of the screen, the tenant that comes from the owner claim, the POST/redirect/GET shape, the
// Spanish message of every stable error code, the antiforgery attribute, and that a missing collaborator closes the
// action instead of running it unenforced.
public sealed class ClassDetailOwnerControllerTests
{
    private const int BusinessId = 1;
    private const int SessionId = 900;
    private const int ClientId = 11;
    private const decimal ListPrice = 5000m;

    private static readonly DateTime ClassStartsAt = new(2026, 3, 12, 18, 0, 0); // Thursday
    private static readonly DateTime ClassEndsAt = new(2026, 3, 12, 19, 0, 0);

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static MembershipPeriod Period(DateOnly coverageStart, DateOnly expiresOn) => new()
    {
        EnrollmentId = 100,
        CoverageStart = coverageStart,
        ExpiresOn = expiresOn
    };

    /// <summary>A fixed annotation of the detail read with, optionally, the period that covers the class date.</summary>
    private static ClassDetailBookingRow FixedBooking(
        int bookingId,
        string clientName,
        EnrollmentStatus enrollmentStatus,
        bool covered = false,
        BookingStatus status = BookingStatus.Pending,
        int clientId = ClientId) =>
        new(bookingId, clientId, clientName, $"{bookingId}@example.invalid", BookingModality.Fixed, 100,
            status, enrollmentStatus,
            covered ? [Period(D(2026, 3, 1), D(2026, 4, 1))] : []);

    /// <summary>A drop-in annotation: no enrollment, no periods, no fixed state.</summary>
    private static ClassDetailBookingRow DropInBooking(
        int bookingId, string clientName, BookingStatus status = BookingStatus.Pending, int clientId = ClientId) =>
        new(bookingId, clientId, clientName, $"{bookingId}@example.invalid", BookingModality.DropIn, null, status, null, []);

    /// <summary>One class annotation as the attendance lock reads it, drop-in and pending by default.</summary>
    private static ClassAttendanceBookingState AttendanceState(
        BookingModality? modality = BookingModality.DropIn,
        BookingStatus status = BookingStatus.Pending,
        EnrollmentStatus? enrollmentStatus = null,
        IReadOnlyList<MembershipPeriod>? periods = null,
        int bookingId = 501,
        int clientId = ClientId,
        DateTime? startsAt = null) =>
        new(BusinessId, bookingId, clientId, SessionId, startsAt ?? ClassStartsAt, AppointmentKind.ClassSession, status,
            modality, modality == BookingModality.Fixed ? 100 : null, enrollmentStatus, ListPrice, periods ?? []);

    [Fact]
    public async Task Detail_get_renders_the_roster_with_names_derived_states_and_the_single_capacity_rule()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        var appointmentId = SeedSession(harness.Details, serviceId, workerId, maxCapacity: 4,
            bookings:
            [
                FixedBooking(501, "Fija Vigente", EnrollmentStatus.Active, covered: true),
                FixedBooking(502, "Fija A Renovar", EnrollmentStatus.Active),
                FixedBooking(503, "Fija Sin Pagar", EnrollmentStatus.PendingPayment),
                DropInBooking(504, "Suelta Anotada"),
                DropInBooking(505, "Suelta Ausente", BookingStatus.Absent),
                DropInBooking(506, "Suelta Cancelada", BookingStatus.Cancelled)
            ]);

        var view = Assert.IsType<ViewResult>(await harness.Controller.Detail(appointmentId, CancellationToken.None));
        var model = Assert.IsType<ClassDetailViewModel>(view.Model);

        Assert.Equal("Yoga", model.Session.ServiceName);
        Assert.Equal("Ana Díaz", model.Session.TeacherName);
        Assert.Equal(D(2026, 3, 12), model.Session.Date);
        Assert.Equal(new TimeOnly(18, 0), model.Session.StartTime);
        Assert.Equal(4, model.Session.MaxCapacity);
        Assert.Equal("Programado", model.Session.StatusTag.Label);

        // The occupancy is the one ClassDetail already resolved with ClassCapacity: six annotations, one cancelled,
        // so five places are taken and none is left. The page never counts anything of its own.
        Assert.Equal(5, model.Session.PlacesTaken);
        Assert.Equal(0, model.Session.RemainingPlaces);
        Assert.True(model.Session.IsFull);

        Assert.Equal(new[] { 501, 502, 503, 504, 505, 506 },
            model.Students.Select(student => student.BookingId).ToArray());

        // The derived state of every fixed student is a word plus an icon; a drop-in has no fixed state at all.
        Assert.Equal(new[] { "Vigente", "Pendiente de renovación", "Pendiente de pago", null, null, null },
            model.Students.Select(student => student.FixedStateTag?.Label).ToArray());
        Assert.Equal(new[] { "Fija", "Fija", "Fija", "Suelta", "Suelta", "Suelta" },
            model.Students.Select(student => student.ModalityLabel).ToArray());

        // «Cancelada (lugar liberado)» and «Ausente» are the labels the owner reads on the roster.
        Assert.Equal("Ausente", model.Students[4].StatusTag.Label);
        Assert.Equal("Cancelada (lugar liberado)", model.Students[5].StatusTag.Label);
        Assert.True(model.Students[5].IsCancelled);

        // Only Ausente feeds the absence count: a cancelled annotation is not an absence.
        Assert.Equal(1, model.AbsentCount);
        Assert.Equal(1, model.CancelledCount);
        Assert.True(model.HasStudents);

        // The read is scoped by the owner claim and writes nothing.
        Assert.Equal(BusinessId, harness.Details.GetBusinessId);
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
    }

    [Fact]
    public async Task Detail_get_offers_attendance_only_with_current_coverage_and_never_for_a_closed_annotation()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        var appointmentId = SeedSession(harness.Details, serviceId, workerId,
            bookings:
            [
                FixedBooking(501, "Fija Vigente", EnrollmentStatus.Active, covered: true),
                FixedBooking(502, "Fija A Renovar", EnrollmentStatus.Active),
                FixedBooking(503, "Fija Sin Pagar", EnrollmentStatus.PendingPayment),
                DropInBooking(504, "Suelta Anotada"),
                DropInBooking(505, "Suelta Ausente", BookingStatus.Absent),
                DropInBooking(506, "Suelta Cancelada", BookingStatus.Cancelled)
            ]);

        var model = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await harness.Controller.Detail(appointmentId, CancellationToken.None)).Model);

        // Attendance needs the coverage in force on the class date: a fixed student pending renewal or pending
        // payment hears the server refuse it, so the screen must not offer it.
        Assert.Equal(new[] { true, false, false, true, false, false },
            model.Students.Select(student => student.CanAttend).ToArray());

        // The absence is a fact and never depends on coverage: every open annotation can be recorded absent.
        Assert.Equal(new[] { true, true, true, true, false, false },
            model.Students.Select(student => student.CanMarkAbsent).ToArray());

        // Before the class starts, any non-cancelled annotation can be cancelled, the recorded ones included.
        Assert.Equal(new[] { true, true, true, true, true, false },
            model.Students.Select(student => student.CanCancel).ToArray());
    }

    [Fact]
    public async Task Detail_get_offers_the_walk_in_form_only_inside_the_window_and_when_the_business_admits_it()
    {
        var beforeStart = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var before = Create(beforeStart);
        var serviceId = await SeedService(before.Services, "Yoga");
        var workerId = await SeedWorker(before.Workers);
        var appointmentId = SeedSession(before.Details, serviceId, workerId, startsAt: ClassStartsAt);

        var scheduled = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await before.Controller.Detail(appointmentId, CancellationToken.None)).Model);
        Assert.True(scheduled.Session.CanSuspend);
        Assert.False(scheduled.Session.CanRegisterWalkIn);

        // The arrival window is [start, end]: a client who shows up once the class started is still late, not early.
        var atStart = Create(new FakeClock { Now = ClassStartsAt });
        serviceId = await SeedService(atStart.Services, "Yoga");
        workerId = await SeedWorker(atStart.Workers);
        appointmentId = SeedSession(atStart.Details, serviceId, workerId, startsAt: ClassStartsAt);

        var inside = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await atStart.Controller.Detail(appointmentId, CancellationToken.None)).Model);
        Assert.True(inside.Session.CanRegisterWalkIn);
        Assert.False(inside.Session.CanSuspend);

        var afterEnd = Create(new FakeClock { Now = ClassEndsAt.AddMinutes(1) });
        serviceId = await SeedService(afterEnd.Services, "Yoga");
        workerId = await SeedWorker(afterEnd.Workers);
        appointmentId = SeedSession(afterEnd.Details, serviceId, workerId, startsAt: ClassStartsAt);

        var closed = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await afterEnd.Controller.Detail(appointmentId, CancellationToken.None)).Model);
        Assert.False(closed.Session.CanRegisterWalkIn);
        Assert.False(closed.Session.CanSuspend);

        // A business that does not admit attention without a turn never gets the form, whatever the clock says.
        var disabled = Create(new FakeClock { Now = ClassStartsAt }, allowsWalkIns: false);
        serviceId = await SeedService(disabled.Services, "Yoga");
        workerId = await SeedWorker(disabled.Workers);
        appointmentId = SeedSession(disabled.Details, serviceId, workerId, startsAt: ClassStartsAt);

        var notAllowed = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await disabled.Controller.Detail(appointmentId, CancellationToken.None)).Model);
        Assert.False(notAllowed.Session.CanRegisterWalkIn);
    }

    [Fact]
    public async Task Detail_get_offers_the_walk_in_form_only_with_a_free_place()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        var appointmentId = SeedSession(harness.Details, serviceId, workerId, maxCapacity: 1,
            startsAt: ClassStartsAt,
            bookings: [DropInBooking(501, "Suelta Anotada")]);

        var model = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await harness.Controller.Detail(appointmentId, CancellationToken.None)).Model);

        // A full class cannot take a walk-in (the service answers class_walk_in_full), so the form is not offered.
        Assert.Equal(0, model.Session.RemainingPlaces);
        Assert.False(model.Session.CanRegisterWalkIn);
    }

    [Fact]
    public async Task Detail_get_offers_no_action_for_a_suspended_class_but_still_shows_it()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        var appointmentId = SeedSession(harness.Details, serviceId, workerId, startsAt: ClassStartsAt,
            status: AppointmentStatus.Suspended, suspensionReason: "Feriado nacional",
            bookings: [DropInBooking(501, "Suelta Anotada")]);

        var model = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await harness.Controller.Detail(appointmentId, CancellationToken.None)).Model);

        // Suspending a class cancels nobody: the annotation stays visible; what disappears are the actions the
        // service refuses on a suspended class.
        Assert.True(model.Session.Suspended);
        Assert.Equal("Suspendido", model.Session.StatusTag.Label);
        Assert.Equal("Feriado nacional", model.Session.SuspensionReason);
        Assert.False(model.Session.CanSuspend);
        Assert.False(model.Session.CanRegisterWalkIn);

        var annotation = Assert.Single(model.Students);
        Assert.False(annotation.CanAttend);
        Assert.False(annotation.CanMarkAbsent);
        Assert.False(annotation.CanCancel);
    }

    [Fact]
    public async Task Detail_get_lists_only_the_clients_of_the_business_in_the_walk_in_picker()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        var appointmentId = SeedSession(harness.Details, serviceId, workerId, startsAt: ClassStartsAt);
        await SeedClient(harness.Clients, "Elena", "Gómez");
        await harness.Clients.InsertAsync(new Client
        {
            BusinessId = 8, FirstName = "Ajena", LastName = "De Otro Negocio",
            Email = "ajena@example.invalid", Phone = "1145667788"
        });

        var model = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await harness.Controller.Detail(appointmentId, CancellationToken.None)).Model);

        var option = Assert.Single(model.Clients);
        Assert.Equal("Elena Gómez", option.Name);
        Assert.True(model.HasClients);
    }

    [Fact]
    public async Task Detail_get_treats_a_foreign_missing_or_invalid_class_as_not_found()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        var foreign = SeedSession(harness.Details, serviceId, workerId, startsAt: ClassStartsAt, businessId: 8);

        Assert.IsType<NotFoundResult>(await harness.Controller.Detail(foreign, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await harness.Controller.Detail(9999, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await harness.Controller.Detail(0, CancellationToken.None));

        // Every attempt stayed inside the owner's tenant.
        Assert.Equal(BusinessId, harness.Details.GetBusinessId);
    }

    [Fact]
    public async Task Detail_get_fails_closed_when_its_services_are_not_configured()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock, wirePanel: false);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        var appointmentId = SeedSession(harness.Details, serviceId, workerId, startsAt: ClassStartsAt);

        // A caller that used the shorter constructor gets a closed page instead of a roster whose actions could not
        // be enforced; the read never even happens.
        Assert.IsType<NotFoundResult>(await harness.Controller.Detail(appointmentId, CancellationToken.None));
        Assert.Equal(0, harness.Details.GetCallCount);
    }

    [Fact]
    public async Task Attend_post_registers_the_attendance_with_the_optional_charge_and_redirects_to_the_class()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.Attend(501, SessionId, 1500m, "Vino con la rodilla vendada", CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(SessionId, redirect.RouteValues!["id"]);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Asistencia registrada. Cobro de $ 1.500 registrado.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));

        // The charge rides with the attendance in the same unit, as the drop-in concept, and the tenant comes from
        // the owner claim.
        var payment = Assert.Single(harness.Attendance.Payments);
        Assert.Equal(BusinessId, payment.BusinessId);
        Assert.Equal(501, payment.BookingId);
        Assert.Equal(PaymentConcept.DropInClass, payment.Concept);
        Assert.Equal(1500m, payment.Amount);
        Assert.Equal(ListPrice, payment.ListPrice);
        Assert.Equal(BookingStatus.Attended, harness.Attendance.LastWrittenStatus);
        Assert.Equal(clock.Now, harness.Attendance.LastWrittenAt);
        Assert.Equal("Vino con la rodilla vendada", Assert.Single(harness.Attendance.Notes).Text);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);
        Assert.Equal(BusinessId, harness.Attendance.LockedBusinessId);
    }

    [Fact]
    public async Task Attend_post_without_a_charge_says_so_explicitly()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.Attend(501, SessionId, null, null, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Asistencia registrada.", Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Empty(harness.Attendance.Payments);
        Assert.Equal(BookingStatus.Attended, harness.Attendance.LastWrittenStatus);
    }

    [Fact]
    public async Task Attend_post_flashes_an_explicit_message_for_an_invalid_amount_without_touching_storage()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.Attend(501, SessionId, 0m, null, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Revisá el importe y la observación (máximo 1000 caracteres).",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Equal(0, harness.Attendance.LockCallCount);
    }

    [Fact]
    public async Task Attend_post_flashes_the_mapped_message_of_a_student_without_current_coverage()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock);
        // A fixed student whose enrollment is still active but whose paid period ended before the class date.
        harness.Attendance.Booking = AttendanceState(
            modality: BookingModality.Fixed, enrollmentStatus: EnrollmentStatus.Active,
            periods: [Period(D(2026, 2, 1), D(2026, 3, 1))]);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.Attend(501, SessionId, null, null, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("La inscripción no está vigente en la fecha de la clase: renová el abono primero.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.CommitCount);
        Assert.Equal(harness.UnitOfWork.BeginCount, harness.UnitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task MarkAbsent_post_records_the_absence_even_without_current_coverage()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState(
            modality: BookingModality.Fixed, enrollmentStatus: EnrollmentStatus.Active,
            periods: [Period(D(2026, 2, 1), D(2026, 3, 1))]);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.MarkAbsent(501, SessionId, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(SessionId, redirect.RouteValues!["id"]);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Ausencia registrada.", Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(BookingStatus.Absent, harness.Attendance.LastWrittenStatus);
        Assert.Equal(AbsenceSource.Owner, harness.Attendance.LastWrittenAbsenceSource);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task RegisterWalkIn_post_registers_the_arrival_with_charge_and_never_promises_an_email()
    {
        var clock = new FakeClock { Now = ClassStartsAt.AddMinutes(10) };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        harness.DropIns.Sessions.Add(new FakeClassDropInSession(BusinessId, new ClassSessionRow(
            SessionId, serviceId, workerId, ClassStartsAt, 8, AppointmentKind.ClassSession,
            AppointmentStatus.Scheduled, [], ClassEndsAt)));
        harness.DropIns.Clients.Add(new FakeClassDropInClient(
            BusinessId, new DropInClient(42, "ana@example.invalid", "Ana", "Gómez")));
        harness.DropIns.NextBookingId = 777;
        // The attendance lock reads back the annotation the guarded insert of the walk-in just wrote.
        harness.Attendance.Booking = AttendanceState(bookingId: 777, clientId: 42);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.RegisterWalkIn(SessionId, 42, 5000m, "Pagó en efectivo", CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(SessionId, redirect.RouteValues!["id"]);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        var message = Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]);
        Assert.Equal("Llegada registrada. Cobro de $ 5.000 registrado.", message);
        Assert.Equal(BusinessId, Assert.Single(harness.DropIns.Inserts).BusinessId);
        Assert.Equal(42, Assert.Single(harness.DropIns.Inserts).ClientId);
        Assert.Equal(BookingStatus.Attended, harness.Attendance.LastWrittenStatus);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);

        // D-07: a walk-in never queues a notice, and the flash never promises one.
        Assert.Empty(harness.DropIns.Notifications);
        Assert.False(message.Contains("email", StringComparison.OrdinalIgnoreCase));
        Assert.False(message.Contains("correo", StringComparison.OrdinalIgnoreCase));
        Assert.False(message.Contains("aviso", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RegisterWalkIn_post_without_a_selected_client_flashes_and_never_reaches_the_service()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.RegisterWalkIn(SessionId, 0, null, null, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Elegí una alumna con ficha para registrar la llegada.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.DropIns.Inserts);
    }

    [Fact]
    public async Task RegisterWalkIn_post_flashes_the_mapped_message_of_an_arrival_outside_the_window()
    {
        var clock = new FakeClock { Now = ClassEndsAt.AddMinutes(30) };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        harness.DropIns.Sessions.Add(new FakeClassDropInSession(BusinessId, new ClassSessionRow(
            SessionId, serviceId, workerId, ClassStartsAt, 8, AppointmentKind.ClassSession,
            AppointmentStatus.Scheduled, [], ClassEndsAt)));
        harness.DropIns.Clients.Add(new FakeClassDropInClient(
            BusinessId, new DropInClient(42, "ana@example.invalid", "Ana", "Gómez")));

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.RegisterWalkIn(SessionId, 42, null, null, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("La atención sin turno sólo se puede registrar entre el inicio y el fin de la clase.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Empty(harness.DropIns.Inserts);
        Assert.Equal(0, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task CancelBooking_post_cancels_one_annotation_of_any_open_or_recorded_state()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var harness = Create(clock);
        harness.DateActions.Session = new FakeClassDateSession(
            BusinessId, SessionId, ClassStartsAt, AppointmentKind.ClassSession, AppointmentStatus.Scheduled);
        // Recorded ahead of time and still cancellable while the class has not started (explicit user decision).
        harness.DateActions.Bookings.Add(new FakeClassDateBooking(
            BusinessId, 501, SessionId, ClientId, "ana@example.invalid", "Ana Gómez",
            BookingModality.DropIn, BookingStatus.Attended));

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.CancelBooking(501, SessionId, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(SessionId, redirect.RouteValues!["id"]);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Anotación cancelada: el lugar quedó liberado.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));

        var booking = Assert.Single(harness.DateActions.Bookings);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(CancelledBy.Owner, booking.CancelledBy);
        Assert.Equal(clock.Now, booking.CancelledAt);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task CancelBooking_post_flashes_the_mapped_message_of_an_already_cancelled_annotation()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var harness = Create(clock);
        harness.DateActions.Session = new FakeClassDateSession(
            BusinessId, SessionId, ClassStartsAt, AppointmentKind.ClassSession, AppointmentStatus.Scheduled);
        harness.DateActions.Bookings.Add(new FakeClassDateBooking(
            BusinessId, 501, SessionId, ClientId, "ana@example.invalid", "Ana Gómez",
            BookingModality.Fixed, BookingStatus.Cancelled));

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.CancelBooking(501, SessionId, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Esa anotación ya estaba cancelada: el lugar ya fue liberado.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.DateActions.CancelWriteCallCount);
        Assert.Equal(0, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Suspend_post_suspends_the_date_and_reports_the_notices_it_queued()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var harness = Create(clock);
        harness.DateActions.Session = new FakeClassDateSession(
            BusinessId, SessionId, ClassStartsAt, AppointmentKind.ClassSession, AppointmentStatus.Scheduled);
        harness.DateActions.Bookings.Add(new FakeClassDateBooking(
            BusinessId, 501, SessionId, 11, "ana@example.invalid", "Ana Gómez",
            BookingModality.Fixed, BookingStatus.Pending));
        harness.DateActions.Bookings.Add(new FakeClassDateBooking(
            BusinessId, 502, SessionId, 12, "paula@example.invalid", "Paula Ruiz",
            BookingModality.DropIn, BookingStatus.Cancelled));

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.Suspend(SessionId, "Feriado nacional", CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(SessionId, redirect.RouteValues!["id"]);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Clase suspendida. Quedó 1 aviso para la alumna anotada.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));

        Assert.Equal(AppointmentStatus.Suspended, harness.DateActions.Session.Status);
        Assert.Equal("Feriado nacional", harness.DateActions.Session.SuspensionReason);
        Assert.Equal(clock.Now, harness.DateActions.Session.SuspendedAt);
        // One notice per non-cancelled annotation, never for the cancelled one, with the stable dedup key.
        var notice = Assert.Single(harness.DateActions.Outbox);
        Assert.Equal("suspension:appointment:900:booking:501", notice.DedupKey);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Suspend_post_without_a_reason_flashes_and_never_touches_storage()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var harness = Create(clock);
        harness.DateActions.Session = new FakeClassDateSession(
            BusinessId, SessionId, ClassStartsAt, AppointmentKind.ClassSession, AppointmentStatus.Scheduled);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await harness.Controller.Suspend(SessionId, "   ", CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Ingresá un motivo de hasta 300 caracteres.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Equal(0, harness.DateActions.SuspendWriteCallCount);
        Assert.Empty(harness.DateActions.Outbox);
    }

    [Fact]
    public async Task Every_class_action_fails_closed_when_its_service_is_not_configured()
    {
        var clock = new FakeClock { Now = ClassStartsAt };
        var harness = Create(clock, wirePanel: false);

        Assert.IsType<NotFoundResult>(
            await harness.Controller.Attend(501, SessionId, null, null, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await harness.Controller.MarkAbsent(501, SessionId, CancellationToken.None));
        Assert.IsType<NotFoundResult>(
            await harness.Controller.RegisterWalkIn(SessionId, 42, null, null, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await harness.Controller.CancelBooking(501, SessionId, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await harness.Controller.Suspend(SessionId, "Motivo", CancellationToken.None));

        // Nothing was written and no unit was opened.
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.DropIns.Inserts);
        Assert.Empty(harness.DropIns.Notifications);
        Assert.Equal(0, harness.Attendance.LockCallCount);
        Assert.Equal(0, harness.DateActions.SuspendWriteCallCount);
    }

    [Fact]
    public void Every_class_action_requires_antiforgery_and_the_detail_read_does_not()
    {
        var posts = new[] { nameof(ClassesController.Attend), nameof(ClassesController.MarkAbsent),
            nameof(ClassesController.RegisterWalkIn), nameof(ClassesController.CancelBooking),
            nameof(ClassesController.Suspend) };

        foreach (var name in posts)
        {
            var method = typeof(ClassesController).GetMethod(name);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        }

        var detail = typeof(ClassesController).GetMethod(nameof(ClassesController.Detail));
        Assert.NotNull(detail);
        Assert.Null(detail!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(detail.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public void Detail_view_gates_every_action_on_the_flags_and_keeps_a_single_primary_action()
    {
        var view = ReadClassesView("Detail.cshtml");

        // Each action is offered behind the flag the controller derived, and the form follows its gate.
        AssertGated(view, "student.CanAttend", "asp-action=\"Attend\"");
        AssertGated(view, "student.CanMarkAbsent", "asp-action=\"MarkAbsent\"");
        AssertGated(view, "student.CanCancel", "asp-action=\"CancelBooking\"");
        AssertGated(view, "session.CanRegisterWalkIn", "asp-action=\"RegisterWalkIn\"");
        AssertGated(view, "session.CanSuspend", "asp-action=\"Suspend\"");

        // The roster shows the derived labels the view model resolved, never a second count of its own.
        Assert.Contains("student.StatusTag", view, StringComparison.Ordinal);
        Assert.Contains("student.FixedStateTag", view, StringComparison.Ordinal);
        Assert.Contains("session.PlacesTaken", view, StringComparison.Ordinal);

        // A single primary action per screen: only the walk-in registration is primary.
        Assert.Equal(1, view.Split("btn-primary").Length - 1);

        // No promise of money back and no promise of an email the walk-in flow never sends.
        Assert.False(view.Contains("devolvemos", StringComparison.OrdinalIgnoreCase));
        Assert.False(view.Contains("se devuelve", StringComparison.OrdinalIgnoreCase));
        Assert.False(view.Contains("se enviará", StringComparison.OrdinalIgnoreCase));
        Assert.False(view.Contains("te enviamos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Detail_walk_in_window_and_displayed_end_follow_the_stored_end_after_the_tariff_changes()
    {
        // The session was generated with a 60-minute duration and the tariff was then changed to 30 minutes. The
        // stored end is the authority: the screen must not recompute it from the current tariff, or it would hide the
        // walk-in window the server still accepts and announce a shorter class than the one stored.
        var storedEnd = ClassStartsAt.AddMinutes(60);
        var insideStoredWindow = Create(new FakeClock { Now = ClassStartsAt.AddMinutes(45) });
        var serviceId = await SeedService(insideStoredWindow.Services, "Yoga", durationMinutes: 30);
        var workerId = await SeedWorker(insideStoredWindow.Workers);
        var appointmentId = SeedSession(insideStoredWindow.Details, serviceId, workerId,
            startsAt: ClassStartsAt, endsAt: storedEnd);

        var model = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await insideStoredWindow.Controller.Detail(appointmentId, CancellationToken.None)).Model);

        Assert.Equal(storedEnd, model.Session.EndsAt);
        Assert.Equal(new TimeOnly(19, 0), model.Session.EndTime);
        // 45 minutes in, the catalog duration (30) would already have closed the window; the stored end keeps it open.
        Assert.True(model.Session.CanRegisterWalkIn);

        // Exact boundary: the stored end is inclusive, one minute after it the window is closed.
        var atStoredEnd = Create(new FakeClock { Now = storedEnd });
        serviceId = await SeedService(atStoredEnd.Services, "Yoga", durationMinutes: 30);
        workerId = await SeedWorker(atStoredEnd.Workers);
        appointmentId = SeedSession(atStoredEnd.Details, serviceId, workerId,
            startsAt: ClassStartsAt, endsAt: storedEnd);

        var boundary = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await atStoredEnd.Controller.Detail(appointmentId, CancellationToken.None)).Model);
        Assert.True(boundary.Session.CanRegisterWalkIn);

        var afterStoredEnd = Create(new FakeClock { Now = storedEnd.AddMinutes(1) });
        serviceId = await SeedService(afterStoredEnd.Services, "Yoga", durationMinutes: 30);
        workerId = await SeedWorker(afterStoredEnd.Workers);
        appointmentId = SeedSession(afterStoredEnd.Details, serviceId, workerId,
            startsAt: ClassStartsAt, endsAt: storedEnd);

        var closed = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await afterStoredEnd.Controller.Detail(appointmentId, CancellationToken.None)).Model);
        Assert.False(closed.Session.CanRegisterWalkIn);
    }

    [Fact]
    public void Day_view_links_only_class_sessions_to_the_class_detail()
    {
        var view = ReadOwnerView("Day", "Index.cshtml");

        // The Day view renders every section, started classes included, so the class link is what makes the roster
        // and the post-start walk-in reachable; it must hang off the class discriminant and carry the session id.
        var classGate = view.IndexOf("item.Kind == AppointmentKind.ClassSession", StringComparison.Ordinal);
        var link = view.IndexOf("asp-action=\"Detail\"", StringComparison.Ordinal);
        Assert.True(classGate >= 0, "La Jornada debe distinguir la clase del turno individual.");
        Assert.True(link > classGate, "El enlace al detalle de la clase debe colgar de la condición de clase.");
        Assert.Contains("asp-controller=\"Classes\"", view, StringComparison.Ordinal);
        Assert.Contains("asp-route-id=\"@item.AppointmentId\"", view, StringComparison.Ordinal);
        Assert.Contains("Abrir clase", view, StringComparison.Ordinal);
        // A single link, scoped to the class gate: the individual turn navigation is left as it was.
        Assert.Equal(link, view.LastIndexOf("asp-action=\"Detail\"", StringComparison.Ordinal));
    }

    private static void AssertGated(string view, string gate, string form)
    {
        var gateIndex = view.IndexOf(gate, StringComparison.Ordinal);
        var formIndex = view.IndexOf(form, StringComparison.Ordinal);
        Assert.True(gateIndex >= 0, $"La vista debe mirar {gate}.");
        Assert.True(formIndex > gateIndex, $"{form} debe seguir al gate {gate}.");
    }

    private static string ReadClassesView(string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", "Classes", fileName));
    }

    private static string ReadOwnerView(string folder, string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", folder, fileName));
    }

    private static async Task<int> SeedService(FakeServiceRepository services, string name, int maxCapacity = 8,
        int durationMinutes = 60) =>
        await services.InsertAsync(new Service
        {
            BusinessId = BusinessId, Name = name, Price = ListPrice, DurationMinutes = durationMinutes,
            MaxCapacity = maxCapacity, IsActive = true
        });

    private static async Task<int> SeedWorker(FakeWorkerRepository workers) =>
        (await workers.InsertAsync(new Worker
        {
            BusinessId = BusinessId, FirstName = "Ana", LastName = "Díaz", IsActive = true
        })).WorkerId;

    private static async Task SeedClient(FakeClientRepository clients, string firstName, string lastName) =>
        await clients.InsertAsync(new Client
        {
            BusinessId = BusinessId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}@example.invalid",
            Phone = "1145667788",
            CreatedAt = new DateTime(2026, 1, 1, 9, 0, 0)
        });

    /// <summary>
    /// One class session of the detail read, on the owner's tenant unless a test asks for a foreign one.
    /// </summary>
    private static int SeedSession(
        FakeClassDetailRepository details,
        int serviceId,
        int workerId,
        DateTime? startsAt = null,
        DateTime? endsAt = null,
        int maxCapacity = 8,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        string? suspensionReason = null,
        int businessId = BusinessId,
        IReadOnlyList<ClassDetailBookingRow>? bookings = null)
    {
        var start = startsAt ?? ClassStartsAt;
        details.Sessions.Add(new FakeClassDetailSession(businessId, new ClassDetailSessionRow(
            SessionId, serviceId, workerId, start, endsAt ?? start.AddMinutes(60), maxCapacity,
            AppointmentKind.ClassSession, status, suspensionReason, bookings ?? [])));
        return SessionId;
    }

    /// <summary>
    /// The controller wired like the container wires it, over in-memory collaborators. <paramref name="wirePanel"/>
    /// false emulates the shorter constructor: every T16 collaborator is missing, which is what the fail-closed tests
    /// exercise. <paramref name="allowsWalkIns"/> seeds the business of the owner claim.
    /// </summary>
    private static Harness Create(IClock clock, bool allowsWalkIns = true, bool wirePanel = true,
        bool wireSettings = true)
    {
        var gridClasses = new FakeGridClassRepository();
        var services = new FakeServiceRepository();
        var workers = new FakeWorkerRepository();
        var weeks = new FakeWeekGenerationRepository();
        var dropIns = new FakeClassDropInRepository();
        var details = new FakeClassDetailRepository();
        var attendance = new FakeClassAttendanceRepository();
        var dateActions = new FakeClassDateActionsRepository();
        var businesses = new FakeBusinessRepository();
        var clients = new FakeClientRepository();
        var unitOfWork = new FakeUnitOfWork();

        businesses.InsertAsync(new Business
        {
            Name = "Estudio", Slug = "estudio", AllowsWalkIns = allowsWalkIns
        }).GetAwaiter().GetResult();

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(WorkUpClaims.BusinessId, BusinessId.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                "test"))
        };
        var controller = new ClassesController(
            new ClassesService(gridClasses, services, workers, unitOfWork, clock),
            new CatalogService(services, unitOfWork),
            new StaffService(workers, unitOfWork),
            new WeekGenerationService(weeks, unitOfWork, clock),
            clock,
            new ClassDropInService(dropIns, unitOfWork, clock),
            new ClientsService(clients, new FakeClientNoteRepository(), clock),
            wirePanel ? new ClassDetailService(details, clock) : null,
            wirePanel ? new ClassAttendanceService(attendance, unitOfWork, clock) : null,
            wirePanel ? new ClassWalkInService(dropIns, attendance, businesses, unitOfWork, clock) : null,
            wirePanel ? new ClassDateActionsService(dateActions, unitOfWork, clock) : null,
            wirePanel && wireSettings ? new BusinessSettingsService(businesses) : null)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };

        return new Harness(controller, details, attendance, dateActions, dropIns, services, workers, clients, unitOfWork);
    }

    private sealed record Harness(
        ClassesController Controller,
        FakeClassDetailRepository Details,
        FakeClassAttendanceRepository Attendance,
        FakeClassDateActionsRepository DateActions,
        FakeClassDropInRepository DropIns,
        FakeServiceRepository Services,
        FakeWorkerRepository Workers,
        FakeClientRepository Clients,
        FakeUnitOfWork UnitOfWork);

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
