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
/// </summary>
public sealed class AgendaControllerBookingTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

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
        var agendaService = new OwnerAgendaService(new FakeOwnerAgendaRepository(), clock);
        return new Fixture(businessId, publicRepository, agendaService, actionsService, clock);
    }

    private sealed record Fixture(
        int BusinessId,
        FakeBookingRepository PublicRepository,
        OwnerAgendaService AgendaService,
        OwnerBookingActionsService ActionsService,
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
            var controller = new AgendaController(AgendaService, ActionsService, null!, Clock)
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
