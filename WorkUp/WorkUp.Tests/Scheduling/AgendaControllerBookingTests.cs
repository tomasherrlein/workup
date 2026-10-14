using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.Areas.Owner.Models.Agenda;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// Covers the owner-assisted booking form wiring: the GET action must echo back everything a "try another time"
/// link carries (service, worker, day, suggested time and the client data already typed), and a slot conflict on
/// submit must reach the view as <see cref="AgendaBookingFormViewModel.ConflictAlternative"/>, not just as a
/// generic error, so the "Tus datos se conservaron" notice on NewBooking.cshtml is actually true.
/// <para>
/// T27 D1 (real HTTP): both owner forms render the selection (service, worker and day) as hidden inputs with no
/// validation span and a <c>ModelOnly</c> summary, so a binder or validator error on the hidden day produced a 200
/// with a rebuilt form, no write and no visible message. These tests pin the model-level companion that the summary
/// renders, and that a field error which already has a visible span is never duplicated into the summary.
/// </para>
/// </summary>
public sealed class AgendaControllerBookingTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);
    private const int ReschedulableBookingId = 40;
    private const int ReschedulableAppointmentId = 30;
    private static readonly DateTime ReschedulableStart = new(2026, 3, 11, 10, 0, 0);

    [Fact]
    public async Task NewBooking_get_prefills_the_suggested_time_and_the_typed_client_data_from_the_query()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController();

        var result = await controller.NewBooking(
            serviceId: 3,
            workerId: 2,
            date: new DateOnly(2026, 3, 11),
            startTime: new TimeOnly(9, 30),
            email: "sol@example.invalid",
            firstName: "Sol",
            lastName: "Díaz");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AgendaBookingFormViewModel>(view.Model);
        Assert.Equal(new TimeOnly(9, 30), model.Input.StartTime);
        Assert.Equal("sol@example.invalid", model.Input.Email);
        Assert.Equal("Sol", model.Input.FirstName);
        Assert.Equal("Díaz", model.Input.LastName);
    }

    [Fact]
    public async Task NewBooking_get_without_query_data_leaves_the_client_fields_blank()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController();

        var result = await controller.NewBooking(serviceId: 3, workerId: 2, date: new DateOnly(2026, 3, 11));

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AgendaBookingFormViewModel>(view.Model);
        Assert.Null(model.Input.StartTime);
        Assert.Equal("", model.Input.Email);
        Assert.Equal("", model.Input.FirstName);
        Assert.Equal("", model.Input.LastName);
    }

    [Fact]
    public async Task CreateBooking_conflict_carries_the_alternative_start_into_the_view_and_keeps_typed_data()
    {
        var fixture = await CreateFixtureAsync();
        var requested = new DateTime(2026, 3, 11, 10, 0, 0);
        // Occupies the requested slot so BookIndividualAsync fails with "slot_unavailable" and an AlternativeStart,
        // exactly the race the NewBooking notice warns about ("Ese horario se ocupó justo ahora").
        fixture.PublicRepository.Snapshot = fixture.PublicRepository.Snapshot with
        {
            Appointments =
            [
                new Appointment
                {
                    Id = 40, BusinessId = fixture.BusinessId, WorkerId = 2, ServiceId = 3,
                    Kind = AppointmentKind.Individual, Status = AppointmentStatus.Scheduled,
                    StartsAt = requested, EndsAt = requested.AddMinutes(30)
                }
            ]
        };
        var controller = fixture.CreateController();
        var input = new OwnerBookingCreateInput
        {
            ServiceId = 3, WorkerId = 2, Date = DateOnly.FromDateTime(requested), StartTime = TimeOnly.FromDateTime(requested),
            Email = "sol@example.invalid", FirstName = "Sol", LastName = "Díaz"
        };

        var result = await controller.CreateBooking(input);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("NewBooking", view.ViewName);
        var model = Assert.IsType<AgendaBookingFormViewModel>(view.Model);
        Assert.NotNull(model.ConflictAlternative);
        Assert.Same(input, model.Input);
        Assert.Equal("sol@example.invalid", model.Input.Email);
        Assert.Equal("Sol", model.Input.FirstName);
        Assert.Equal("Díaz", model.Input.LastName);
    }

    // --- T27 D1: an error on a hidden selector must still reach the ModelOnly summary ---------------------------

    [Fact]
    public async Task CreateBooking_with_an_omitted_hidden_date_reports_a_model_level_error_and_writes_nothing()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController();
        // What MVC's binder records for the omitted hidden Input.Date before the action runs: the same field key that
        // returned HTTP 200 with a rebuilt form, no write and no message at all.
        controller.ModelState.AddModelError("Input.Date", "Elegí un día.");
        var input = new OwnerBookingCreateInput
        {
            ServiceId = 3, WorkerId = 2, Date = null, StartTime = new TimeOnly(10, 0),
            Email = "sol@example.invalid", FirstName = "Sol", LastName = "Díaz"
        };

        var view = Assert.IsType<ViewResult>(await controller.CreateBooking(input));

        Assert.Equal("NewBooking", view.ViewName);
        // NewBooking.cshtml renders the hidden selection without a span and a ModelOnly summary, so a model-level
        // entry is the only message the owner can ever see.
        Assert.True(
            view.ViewData.ModelState.ContainsKey(string.Empty),
            "El resumen ModelOnly necesita una entrada con clave string.Empty para mostrar el error del día oculto.");
        Assert.Contains(view.ViewData.ModelState[string.Empty]!.Errors, error => error.ErrorMessage == "Elegí un día.");
        // The field entry stays for the binder and for any future span: the summary entry is added, not moved.
        Assert.Contains(view.ViewData.ModelState["Input.Date"]!.Errors, error => error.ErrorMessage == "Elegí un día.");
        Assert.Empty(fixture.PublicRepository.Bookings);
        // The data the owner already typed survives the refused save.
        var model = Assert.IsType<AgendaBookingFormViewModel>(view.Model);
        Assert.Equal("sol@example.invalid", model.Input.Email);
        Assert.Equal("Sol", model.Input.FirstName);
        Assert.Equal("Díaz", model.Input.LastName);
    }

    [Fact]
    public async Task CreateBooking_with_an_omitted_hidden_service_or_worker_reports_a_model_level_error_and_writes_nothing()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController();
        // The hidden service and worker carry the same Required validator as the hidden day and the same missing span.
        controller.ModelState.AddModelError("Input.ServiceId", "Elegí un servicio.");
        controller.ModelState.AddModelError("Input.WorkerId", "Elegí una trabajadora.");
        var input = new OwnerBookingCreateInput
        {
            ServiceId = null, WorkerId = null, Date = new DateOnly(2026, 3, 11), StartTime = new TimeOnly(10, 0),
            Email = "sol@example.invalid", FirstName = "Sol", LastName = "Díaz"
        };

        var view = Assert.IsType<ViewResult>(await controller.CreateBooking(input));

        var summary = SummaryMessages(view);
        Assert.Contains("Elegí un servicio.", summary);
        Assert.Contains("Elegí una trabajadora.", summary);
        Assert.Empty(fixture.PublicRepository.Bookings);
    }

    [Fact]
    public async Task CreateBooking_with_only_visible_field_errors_never_duplicates_them_into_the_summary()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController();
        // Every one of these fields is rendered with its own asp-validation-for span, so the summary must stay empty
        // instead of mirroring the whole ModelState (the "broad All duplication" this fix deliberately avoids).
        controller.ModelState.AddModelError("Input.StartTime", "Elegí un horario disponible.");
        controller.ModelState.AddModelError("Input.Email", "Ingresá el email.");
        controller.ModelState.AddModelError("Input.FirstName", "Ingresá el nombre.");
        var input = new OwnerBookingCreateInput
        {
            ServiceId = 3, WorkerId = 2, Date = new DateOnly(2026, 3, 11), StartTime = null,
            Email = "", FirstName = "", LastName = ""
        };

        var view = Assert.IsType<ViewResult>(await controller.CreateBooking(input));

        Assert.False(view.ViewData.ModelState.ContainsKey(string.Empty));
        Assert.Empty(fixture.PublicRepository.Bookings);
    }

    [Fact]
    public async Task Reschedule_with_an_omitted_hidden_date_reports_a_model_level_error_and_keeps_the_original_slot()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController();
        // The reprogramming form renders the chosen day as a hidden input with no span either, so the same omitted
        // binder error could rebuild the form silently instead of telling the owner what is missing.
        controller.ModelState.AddModelError("Input.Date", "Elegí un día.");
        var submitted = new OwnerRescheduleInput { Date = null, StartTime = new TimeOnly(10, 15) };

        var view = Assert.IsType<ViewResult>(await controller.Reschedule(ReschedulableBookingId, submitted));

        Assert.Equal("Reschedule", view.ViewName);
        Assert.True(
            view.ViewData.ModelState.ContainsKey(string.Empty),
            "El resumen ModelOnly necesita una entrada con clave string.Empty para mostrar el error del día oculto.");
        Assert.Contains(view.ViewData.ModelState[string.Empty]!.Errors, error => error.ErrorMessage == "Elegí un día.");
        Assert.Contains(view.ViewData.ModelState["Input.Date"]!.Errors, error => error.ErrorMessage == "Elegí un día.");
        // An incomplete choice never reaches the save: the original slot is untouched and no write is attempted.
        Assert.DoesNotContain("apply-reschedule", fixture.RescheduleRepository.Calls);
        Assert.Equal(ReschedulableStart, fixture.RescheduleRepository.Booking!.StartsAt);
    }

    [Fact]
    public async Task Reschedule_with_only_a_visible_time_error_never_duplicates_it_into_the_summary()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController();
        // Input.StartTime is a visible select with its own asp-validation-for span, and it is not one of the hidden
        // selectors: the reprogramming form must never show it twice, nor borrow the booking form's own texts.
        controller.ModelState.AddModelError("Input.StartTime", "Elegí un horario.");
        var submitted = new OwnerRescheduleInput
        {
            Date = DateOnly.FromDateTime(ReschedulableStart), StartTime = null
        };

        var view = Assert.IsType<ViewResult>(await controller.Reschedule(ReschedulableBookingId, submitted));

        Assert.False(view.ViewData.ModelState.ContainsKey(string.Empty));
        Assert.DoesNotContain("apply-reschedule", fixture.RescheduleRepository.Calls);
    }

    private static string[] SummaryMessages(ViewResult view) =>
        view.ViewData.ModelState.TryGetValue(string.Empty, out var summary)
            ? summary.Errors.Select(error => error.ErrorMessage).ToArray()
            : [];

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var businesses = new FakeBusinessRepository();
        var businessId = (await businesses.InsertAsync(new Business { Name = "Salón", Slug = "salon" })).BusinessId;
        var browseRepository = new FakeOwnerBookingBrowseRepository();
        browseRepository.Services.Add(new Service
        {
            Id = 3, BusinessId = businessId, Name = "Corte", Price = 5000m, DurationMinutes = 30, MaxCapacity = 1, IsActive = true
        });
        browseRepository.Workers.Add(new PublicBookingWorkerOption(2, "Ana Pérez"));
        var publicRepository = new FakeBookingRepository
        {
            Worker = new Worker { Id = 2, BusinessId = businessId, FirstName = "Ana", LastName = "Pérez", IsActive = true },
            Service = browseRepository.Services[0],
            Snapshot = new BookingAvailabilitySnapshot(
                Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
                {
                    WorkerId = 2, DayOfWeek = day, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0)
                }).ToArray(), [], [], [])
        };
        var clock = new FakeClock { Now = Now };
        var unitOfWork = new FakeUnitOfWork();
        var browseService = new PublicBookingBrowseService(businesses, browseRepository, publicRepository, clock);
        var bookingService = new BookingService(
            publicRepository, unitOfWork, new TestLinkTokenProtector(), new TestAppointmentUrlBuilder(), clock);
        var actionsService = new OwnerBookingActionsService(
            browseService, bookingService, new FakeOwnerBookingActionsRepository(), unitOfWork, clock);
        var agendaRepository = new FakeOwnerAgendaRepository();
        agendaRepository.BookingDetails.Add((businessId, new OwnerAgendaBookingDetail(
            ReschedulableBookingId, ReschedulableAppointmentId, ReschedulableStart, ReschedulableStart.AddMinutes(45),
            AppointmentStatus.Scheduled, AppointmentOrigin.Reserved, BookingStatus.Pending,
            "Corte", "Ana", "Sol Gómez", "sol@example.invalid", null, 12000m, AppointmentKind.Individual, 1,
            WorkerId: 2)));
        var rescheduleRepository = new FakeOwnerRescheduleRepository
        {
            WorkerBusinessId = businessId,
            Booking = new OwnerRescheduleBooking(
                BusinessId: businessId, BookingId: ReschedulableBookingId,
                AppointmentId: ReschedulableAppointmentId, WorkerId: 2,
                AppointmentKind: AppointmentKind.Individual, AppointmentStatus: AppointmentStatus.Scheduled,
                Origin: AppointmentOrigin.Reserved, MaxCapacity: 1, Modality: null, EnrollmentId: null,
                BookingStatus: BookingStatus.Pending, StartsAt: ReschedulableStart,
                EndsAt: ReschedulableStart.AddMinutes(45), ConfirmedAt: null, CancelledAt: null, CancelledBy: null,
                AttendanceRecordedAt: null, AbsenceSource: null, ServiceDurationMinutes: 45, BusinessName: "Salón",
                ClientFirstName: "Sol", ClientEmail: "sol@example.invalid", ServiceName: "Corte",
                WorkerName: "Ana"),
            Snapshot = new BookingAvailabilitySnapshot(
                Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
                {
                    WorkerId = 2, DayOfWeek = day, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0)
                }).ToArray(), [], [], [])
        };
        var agendaService = new OwnerAgendaService(agendaRepository, clock);
        var rescheduleService = new OwnerRescheduleService(rescheduleRepository, unitOfWork, clock);
        return new Fixture(
            businessId, publicRepository, agendaService, actionsService, rescheduleService, rescheduleRepository, clock);
    }

    private sealed record Fixture(
        int BusinessId,
        FakeBookingRepository PublicRepository,
        OwnerAgendaService AgendaService,
        OwnerBookingActionsService ActionsService,
        OwnerRescheduleService RescheduleService,
        FakeOwnerRescheduleRepository RescheduleRepository,
        FakeClock Clock)
    {
        public AgendaController CreateController()
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(WorkUpClaims.BusinessId, BusinessId.ToString()),
                    new Claim(WorkUpClaims.BusinessSlug, "salon")
                ], "test"));
            var controller = new AgendaController(AgendaService, ActionsService, RescheduleService, Clock)
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
            controller.TempData = new TempDataDictionary(context, new FakeTempDataProvider());
            return controller;
        }
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeOwnerBookingBrowseRepository : IPublicBookingBrowseRepository
    {
        public List<Service> Services { get; } = [];
        public List<PublicBookingWorkerOption> Workers { get; } = [];
        public int WorkerListRequests { get; private set; }

        public Task<IReadOnlyList<Service>> ListActiveIndividualServicesAsync(int businessId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Service>>(Services.Where(service => service.BusinessId == businessId).ToArray());

        public Task<IReadOnlyList<PublicBookingWorkerOption>> ListActiveWorkersAsync(int businessId, CancellationToken ct = default)
        {
            WorkerListRequests++;
            return Task.FromResult<IReadOnlyList<PublicBookingWorkerOption>>(Workers.ToArray());
        }

        public Task<PublicBookingWorkerOption?> GetActiveWorkerAsync(int businessId, int workerId, CancellationToken ct = default) =>
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
}
