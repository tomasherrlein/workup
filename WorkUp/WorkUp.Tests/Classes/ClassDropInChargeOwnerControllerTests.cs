using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Classes;

namespace WorkUp.Tests.Classes;

// T18 owner UI (RF-11, D-20): the late charge of a drop-in the owner already marked as present and forgot to charge.
// The controller only routes and maps: the lock, the tenant scope, the "one effective payment" rule and the annulment
// rule belong to ClassDropInChargeService and are already tested at its own level. What is pinned here is what the
// panel adds: the offer of the action on the class detail, the tenant that comes from the owner claim, the invariant
// binder and the amount guard, the POST/redirect/GET shape, the Spanish sentence of every stable error code, the
// antiforgery attribute and that a missing collaborator closes the action instead of running it unenforced.
public sealed class ClassDropInChargeOwnerControllerTests
{
    private const int BusinessId = 1;
    private const int SessionId = 900;
    private const int BookingId = 501;
    private const int ClientId = 11;
    private const decimal ListPrice = 5000m;

    private static readonly DateTime ClassStartsAt = new(2026, 3, 12, 18, 0, 0); // Thursday
    private static readonly DateTime ClockNow = new(2026, 3, 13, 9, 0, 0);

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static MembershipPeriod Period(DateOnly coverageStart, DateOnly expiresOn) => new()
    {
        EnrollmentId = 100,
        CoverageStart = coverageStart,
        ExpiresOn = expiresOn
    };

    /// <summary>One annotation of the detail read with, optionally, the period that covers the class date.</summary>
    private static ClassDetailBookingRow FixedBooking(
        int bookingId,
        string clientName,
        EnrollmentStatus enrollmentStatus,
        bool covered = false,
        BookingStatus status = BookingStatus.Pending) =>
        new(bookingId, ClientId, clientName, $"{bookingId}@example.invalid", BookingModality.Fixed, 100,
            status, enrollmentStatus,
            covered ? [Period(D(2026, 3, 1), D(2026, 4, 1))] : []);

    /// <summary>A drop-in annotation: no enrollment, no periods, no fixed state.</summary>
    private static ClassDetailBookingRow DropInBooking(
        int bookingId, string clientName, BookingStatus status = BookingStatus.Pending) =>
        new(bookingId, ClientId, clientName, $"{bookingId}@example.invalid", BookingModality.DropIn, null, status,
            null, []);

    /// <summary>
    /// One class annotation as the charge lock reads it. The default is the case the flow exists for: a drop-in of
    /// the tenant already marked as present, with no effective charge and its class still scheduled.
    /// </summary>
    private static ClassAttendanceBookingState AttendanceState(
        BookingModality? modality = BookingModality.DropIn,
        BookingStatus status = BookingStatus.Attended,
        int businessId = BusinessId,
        int bookingId = BookingId,
        int clientId = ClientId,
        decimal listPrice = ListPrice) =>
        new(businessId, bookingId, clientId, SessionId, ClassStartsAt, AppointmentKind.ClassSession, status,
            modality, modality == BookingModality.Fixed ? 100 : null, null, listPrice, []);

    /// <summary>One charge already stored for the booking of the tests.</summary>
    private static ClassAttendancePayment StoredPayment(decimal amount) =>
        new(BusinessId, ClientId, BookingId, PaymentConcept.DropInClass, amount, ListPrice, null, ClockNow);

    [Fact]
    public async Task ChargeDropIn_post_registers_the_forgotten_charge_and_redirects_to_the_class()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, 600m, "Precio acordado con la alumna", CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(SessionId, redirect.RouteValues!["id"]);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Cobro de $ 600 registrado.", Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));

        // The charge references the annotation, not an enrollment (CK_Payments_Reference), records the class list
        // price beside the agreed amount, and carries the single instant of the service's clock read. The tenant comes
        // from the owner claim and never from the form.
        var payment = Assert.Single(harness.Attendance.Payments);
        Assert.Equal(BusinessId, payment.BusinessId);
        Assert.Equal(ClientId, payment.ClientId);
        Assert.Equal(BookingId, payment.BookingId);
        Assert.Equal(PaymentConcept.DropInClass, payment.Concept);
        Assert.Equal(600m, payment.Amount);
        Assert.Equal(ListPrice, payment.ListPrice);
        Assert.Equal("Precio acordado con la alumna", payment.PriceReason);
        Assert.Equal(clock.Now, payment.PaidAt);
        Assert.Equal(BusinessId, harness.Attendance.LockedBusinessId);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);

        // The charge is the only write of the unit: the attendance it follows is not re-marked and no observation is
        // written, so the operation can never change an already recorded fact.
        Assert.Equal(["payment"], harness.Attendance.WriteOrder);
        Assert.Equal(BookingStatus.Attended, harness.Attendance.Booking!.BookingStatus);
    }

    [Fact]
    public async Task ChargeDropIn_post_without_an_amount_flashes_and_never_reaches_the_service()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, null, null, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Revisá el importe y el motivo del precio.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Equal(0, harness.Attendance.LockCallCount);
        Assert.Empty(harness.Attendance.Payments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(10000000000)]
    public async Task ChargeDropIn_post_answers_an_amount_out_of_range_without_touching_storage(decimal amount)
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, amount, null, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Revisá el importe y el motivo del precio.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Equal(0, harness.Attendance.LockCallCount);
    }

    [Fact]
    public async Task ChargeDropIn_post_rejects_a_price_reason_longer_than_the_stored_column()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, 600m, new string('x', 201), CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Revisá el importe y el motivo del precio.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Attendance.Payments);
    }

    [Theory]
    [InlineData("class_charge_not_found", "No encontramos esa anotación de la clase.")]
    [InlineData("class_charge_not_available",
        "Esa clase está suspendida: no se puede registrar el cobro.")]
    [InlineData("class_charge_not_drop_in", "Sólo se cobra la clase suelta; el abono se registra aparte.")]
    [InlineData("class_charge_cancelled", "Esa anotación está cancelada: el lugar ya fue liberado.")]
    [InlineData("class_charge_not_attended",
        "Sólo se registra el cobro de una clase marcada como presente.")]
    [InlineData("class_charge_already_charged", "Esa clase suelta ya tiene un cobro vigente.")]
    public async Task ChargeDropIn_post_flashes_the_mapped_sentence_of_every_reachable_rejection(
        string code, string message)
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        ArrangeRejection(harness, code);

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, 600m, null, CancellationToken.None));

        Assert.Equal("Detail", redirect.ActionName);
        Assert.Equal(SessionId, redirect.RouteValues!["id"]);
        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal(message, Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        // Every rejection after the lock returns before the commit, so the unit it opened is rolled back.
        Assert.Equal(0, harness.UnitOfWork.CommitCount);
        Assert.Equal(harness.UnitOfWork.BeginCount, harness.UnitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task ChargeDropIn_post_rejection_writes_no_charge_and_leaves_the_recorded_attendance_alone()
    {
        // The stable code `class_charge_invalid_charge` is not reachable from the panel: the amount and the reason are
        // judged before the service, so the request never reaches it. What is pinned here is that a reachable
        // rejection writes nothing at all and never re-marks the attendance it was asked about.
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState(status: BookingStatus.Pending);

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, 600m, null, CancellationToken.None));

        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Sólo se registra el cobro de una clase marcada como presente.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Empty(harness.Attendance.Payments);
        Assert.Empty(harness.Attendance.WriteOrder);
        Assert.Equal(BookingStatus.Pending, harness.Attendance.Booking!.BookingStatus);
        Assert.Null(harness.Attendance.LastWrittenStatus);
    }

    [Fact]
    public async Task ChargeDropIn_post_answers_a_duplicate_with_the_effective_payment_rule()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();
        harness.Attendance.Payments.Add(StoredPayment(600m));

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, 600m, null, CancellationToken.None));

        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Esa clase suelta ya tiene un cobro vigente.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        var payment = Assert.Single(harness.Attendance.Payments);
        Assert.Equal(600m, payment.Amount);
        Assert.Equal(0, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task ChargeDropIn_post_accepts_the_corrected_charge_once_the_previous_payment_is_no_longer_effective()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();
        harness.Attendance.Payments.Add(StoredPayment(400m));

        // Annulling the wrong charge in the ledger is what makes it stop counting; the fake models that by dropping
        // the effective payment, and ClassAttendanceSqlIntegrationTests proves the real PaymentAnnulments row.
        harness.Attendance.Payments.Clear();

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, 600m, "Precio corregido", CancellationToken.None));

        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
        Assert.Equal("Cobro de $ 600 registrado.", Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        var payment = Assert.Single(harness.Attendance.Payments);
        Assert.Equal(600m, payment.Amount);
        Assert.Equal("Precio corregido", payment.PriceReason);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task ChargeDropIn_post_takes_the_business_from_the_owner_claim_and_never_from_the_form()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        // The lock of another tenant's annotation resolves nothing: the same booking id is invisible.
        harness.Attendance.Booking = AttendanceState(businessId: 8);

        var redirect = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, 600m, null, CancellationToken.None));

        Assert.Equal("Error", harness.Controller.TempData["FlashType"]);
        Assert.Equal("No encontramos esa anotación de la clase.",
            Assert.IsType<string>(harness.Controller.TempData["FlashMessage"]));
        Assert.Equal(BusinessId, harness.Attendance.LockedBusinessId);
        Assert.Empty(harness.Attendance.Payments);
    }

    [Fact]
    public async Task ChargeDropIn_post_fails_closed_when_the_charge_service_is_not_configured()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock, wirePanel: false);

        Assert.IsType<NotFoundResult>(await harness.Controller.ChargeDropIn(
            BookingId, SessionId, 600m, null, CancellationToken.None));

        Assert.Equal(0, harness.UnitOfWork.BeginCount);
        Assert.Empty(harness.Attendance.Payments);
        Assert.Equal(0, harness.Attendance.LockCallCount);
    }

    [Fact]
    public async Task ChargeDropIn_post_without_the_booking_id_still_returns_to_the_class_it_was_posted_from()
    {
        // The class id only chooses the page the owner returns to; a form that arrives without it goes back to the
        // roster instead of losing the flash. The identifier the action writes always comes from the route.
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        harness.Attendance.Booking = AttendanceState();

        var withoutClass = Assert.IsType<RedirectToActionResult>(await harness.Controller.ChargeDropIn(
            BookingId, 0, 600m, null, CancellationToken.None));

        Assert.Equal("DropIns", withoutClass.ActionName);
        Assert.Equal("Success", harness.Controller.TempData["FlashType"]);
    }

    [Fact]
    public void ChargeDropIn_post_requires_antiforgery_and_binds_the_amount_invariantly()
    {
        var action = typeof(ClassesController).GetMethod(nameof(ClassesController.ChargeDropIn));
        Assert.NotNull(action);
        Assert.NotNull(action!.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());

        var amount = action.GetParameters().Single(parameter => parameter.Name == "amount");
        Assert.Equal(typeof(decimal?), amount.ParameterType);
        Assert.Equal(typeof(InvariantDecimalModelBinder),
            amount.GetCustomAttribute<ModelBinderAttribute>()?.BinderType);

        // The tenant is never a request parameter of the action: it comes from the authenticated owner.
        Assert.DoesNotContain(action.GetParameters(), parameter => parameter.Name is "businessId" or "clientId");

        var detail = typeof(ClassesController).GetMethod(nameof(ClassesController.Detail));
        Assert.NotNull(detail);
        Assert.Null(detail!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.NotNull(detail.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public async Task Detail_get_offers_the_late_charge_only_for_an_attended_drop_in_of_an_unsuspended_class()
    {
        var clock = new FakeClock { Now = ClockNow };
        var harness = Create(clock);
        var serviceId = await SeedService(harness.Services, "Yoga");
        var workerId = await SeedWorker(harness.Workers);
        var appointmentId = SeedSession(harness.Details, serviceId, workerId,
            bookings:
            [
                DropInBooking(501, "Suelta Atendida", BookingStatus.Attended),
                DropInBooking(502, "Suelta Anotada"),
                FixedBooking(503, "Fija Atendida", EnrollmentStatus.Active, covered: true,
                    status: BookingStatus.Attended),
                DropInBooking(504, "Suelta Ausente", BookingStatus.Absent),
                DropInBooking(505, "Suelta Cancelada", BookingStatus.Cancelled)
            ]);

        var model = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await harness.Controller.Detail(appointmentId, CancellationToken.None)).Model);

        // The late charge follows an attendance and belongs to the drop-in: only the annotation already marked as
        // present is offered it, because the service refuses every other one.
        Assert.Equal(new[] { true, false, false, false, false },
            model.Students.Select(student => student.CanCharge).ToArray());

        // A suspended class did not happen (RF-10): the annotation may still read `Atendido` — suspending never
        // alters the roster — and no charge is offered for it, because the server refuses it.
        var suspended = Create(clock);
        serviceId = await SeedService(suspended.Services, "Yoga");
        workerId = await SeedWorker(suspended.Workers);
        appointmentId = SeedSession(suspended.Details, serviceId, workerId,
            status: AppointmentStatus.Suspended, suspensionReason: "Feriado nacional",
            bookings: [DropInBooking(501, "Suelta Atendida", BookingStatus.Attended)]);

        var suspendedModel = Assert.IsType<ClassDetailViewModel>(
            Assert.IsType<ViewResult>(await suspended.Controller.Detail(appointmentId, CancellationToken.None)).Model);

        var annotation = Assert.Single(suspendedModel.Students);
        Assert.Equal(BookingStatus.Attended, annotation.Status);
        Assert.False(annotation.CanCharge);
    }

    [Fact]
    public void Detail_view_gates_the_late_charge_on_the_flag_and_states_the_correction_rule()
    {
        var view = ReadClassesView("Detail.cshtml");

        var gate = view.IndexOf("student.CanCharge", StringComparison.Ordinal);
        var form = view.IndexOf("asp-action=\"ChargeDropIn\"", StringComparison.Ordinal);
        Assert.True(gate >= 0, "La vista debe mirar student.CanCharge.");
        Assert.True(form > gate, "El formulario del cobro posterior debe seguir al gate student.CanCharge.");

        // The owner reads that a charge changes neither the attendance nor anything sent, and how to correct an
        // annulled one: the annulment lives in the ledger and frees the annotation for the corrected charge.
        Assert.Contains("no cambia la asistencia", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anulá", view, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cobros", view, StringComparison.Ordinal);

        // Still exactly one primary action in the whole file: the charge is a secondary action of the roster.
        Assert.Equal(1, view.Split("btn-primary").Length - 1);
    }

    private static void ArrangeRejection(Harness harness, string code)
    {
        switch (code)
        {
            case "class_charge_not_found":
                harness.Attendance.Booking = null;
                break;
            case "class_charge_not_available":
                harness.Attendance.Booking = AttendanceState();
                harness.Attendance.SessionStatus = AppointmentStatus.Suspended;
                break;
            case "class_charge_not_drop_in":
                harness.Attendance.Booking = AttendanceState(modality: BookingModality.Fixed);
                break;
            case "class_charge_cancelled":
                harness.Attendance.Booking = AttendanceState(status: BookingStatus.Cancelled);
                break;
            case "class_charge_not_attended":
                harness.Attendance.Booking = AttendanceState(status: BookingStatus.Pending);
                break;
            case "class_charge_already_charged":
                harness.Attendance.Booking = AttendanceState();
                harness.Attendance.Payments.Add(StoredPayment(600m));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(code), code, "Código sin arreglo en la prueba.");
        }
    }

    private static string ReadClassesView(string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", "Classes", fileName));
    }

    private static async Task<int> SeedService(FakeServiceRepository services, string name) =>
        await services.InsertAsync(new Service
        {
            BusinessId = BusinessId, Name = name, Price = ListPrice, DurationMinutes = 60,
            MaxCapacity = 8, IsActive = true
        });

    private static async Task<int> SeedWorker(FakeWorkerRepository workers) =>
        await workers.InsertAsync(new Worker
        {
            BusinessId = BusinessId, FirstName = "Ana", LastName = "Díaz", IsActive = true
        });

    /// <summary>One class session of the detail read, on the owner's tenant unless a test asks for a foreign one.</summary>
    private static int SeedSession(
        FakeClassDetailRepository details,
        int serviceId,
        int workerId,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        string? suspensionReason = null,
        IReadOnlyList<ClassDetailBookingRow>? bookings = null)
    {
        details.Sessions.Add(new FakeClassDetailSession(BusinessId, new ClassDetailSessionRow(
            SessionId, serviceId, workerId, ClassStartsAt, ClassStartsAt.AddMinutes(60), 8,
            AppointmentKind.ClassSession, status, suspensionReason, bookings ?? [])));
        return SessionId;
    }

    /// <summary>
    /// The controller wired like the container wires it, over in-memory collaborators. <paramref name="wirePanel"/>
    /// false emulates the shorter constructor: every panel collaborator is missing, which is what the fail-closed test
    /// exercises.
    /// </summary>
    private static Harness Create(IClock clock, bool wirePanel = true)
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
            Name = "Estudio", Slug = "estudio", AllowsWalkIns = true
        }).GetAwaiter().GetResult();

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(WorkUpClaims.BusinessId,
                    BusinessId.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                "test"))
        };
        var controller = new ClassesController(
            new ClassesService(gridClasses, services, workers, clock),
            new CatalogService(services),
            new StaffService(workers, unitOfWork),
            new WeekGenerationService(weeks, unitOfWork, clock),
            clock,
            wirePanel ? new ClassDropInService(dropIns, unitOfWork, clock) : null,
            wirePanel ? new ClientsService(clients, new FakeClientNoteRepository(), clock) : null,
            wirePanel ? new ClassDetailService(details, clock) : null,
            wirePanel ? new ClassAttendanceService(attendance, unitOfWork, clock) : null,
            wirePanel ? new ClassWalkInService(dropIns, attendance, businesses, unitOfWork, clock) : null,
            wirePanel ? new ClassDateActionsService(dateActions, unitOfWork, clock) : null,
            wirePanel ? new BusinessSettingsService(businesses) : null,
            wirePanel ? new ClassDropInChargeService(attendance, unitOfWork, clock) : null)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };

        return new Harness(controller, details, attendance, services, workers, unitOfWork);
    }

    private sealed record Harness(
        ClassesController Controller,
        FakeClassDetailRepository Details,
        FakeClassAttendanceRepository Attendance,
        FakeServiceRepository Services,
        FakeWorkerRepository Workers,
        FakeUnitOfWork UnitOfWork);

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
