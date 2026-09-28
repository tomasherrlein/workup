using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Controllers;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Reservations;

namespace WorkUp.Tests.Reservations;

/// <summary>
/// T14 public drop-in reservation (RF-07 cupo &gt; 1, RF-22) on <c>/r/{slug}/clases</c>. The controller only projects
/// what the verified business core and SQL repository already decided: it lists the sessions the service allows,
/// books the selected one through <see cref="ClassDropInService.BookPublicAsync"/> and maps the stable error codes
/// back onto the same form, keeping what the person typed.
/// </summary>
public sealed class DropInReservationsTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0); // Tuesday

    [Fact]
    public async Task Classes_lists_only_the_bookable_sessions_with_their_remaining_places()
    {
        var fixture = await Fixture.CreateAsync();
        var bookable = fixture.AddSession(
            new DateOnly(2026, 3, 12), new TimeOnly(10, 0), maxCapacity: 8,
            bookings: [BookingStatus.Pending, BookingStatus.Confirmed]);
        fixture.AddSession(
            new DateOnly(2026, 3, 12), new TimeOnly(11, 0), status: AppointmentStatus.Suspended);
        fixture.AddSession(new DateOnly(2026, 3, 10), new TimeOnly(8, 0), maxCapacity: 8); // already started
        fixture.AddSession(
            new DateOnly(2026, 3, 12), new TimeOnly(12, 0), maxCapacity: 2,
            bookings: [BookingStatus.Pending, BookingStatus.Confirmed]); // full
        fixture.AddSession(new DateOnly(2026, 3, 12), new TimeOnly(13, 0), kind: AppointmentKind.Individual);
        fixture.AddSession(new DateOnly(2026, 3, 12), new TimeOnly(14, 0), businessId: fixture.OtherBusinessId);

        var result = await fixture.Controller.Classes("estudio", null, default);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Classes", view.ViewName);
        var model = Assert.IsType<PublicDropInListViewModel>(view.Model);
        var session = Assert.Single(model.Sessions);
        Assert.Equal(bookable, session.AppointmentId);
        Assert.Equal(new DateOnly(2026, 3, 12), session.Date);
        Assert.Equal(new TimeOnly(10, 0), session.StartTime);
        Assert.Equal("Yoga", session.ServiceName);
        Assert.Equal("Ana Pérez", session.TeacherName);
        Assert.Equal(6, session.RemainingPlaces);
        Assert.Equal(fixture.BusinessId, fixture.DropIns.ListBusinessId);
        Assert.Equal(new DateOnly(2026, 3, 10), fixture.DropIns.RequestedFrom);
        Assert.Equal(new DateOnly(2026, 4, 9), fixture.DropIns.RequestedTo);
    }

    [Fact]
    public async Task Classes_opens_the_contact_form_only_for_a_session_it_offers()
    {
        var fixture = await Fixture.CreateAsync();
        var bookable = fixture.AddSession(new DateOnly(2026, 3, 12), new TimeOnly(10, 0), maxCapacity: 8);

        var offered = Assert.IsType<PublicDropInListViewModel>(
            Assert.IsType<ViewResult>(await fixture.Controller.Classes("estudio", bookable, default)).Model);
        var foreign = Assert.IsType<PublicDropInListViewModel>(
            Assert.IsType<ViewResult>(await fixture.Controller.Classes("estudio", 999_999, default)).Model);

        Assert.Equal(bookable, offered.OpenAppointmentId);
        Assert.Null(foreign.OpenAppointmentId);
    }

    [Fact]
    public async Task BookDropIn_books_the_selected_session_and_shows_the_confirmation()
    {
        var fixture = await Fixture.CreateAsync();
        var appointmentId = fixture.AddSession(new DateOnly(2026, 3, 12), new TimeOnly(10, 0), maxCapacity: 8);

        var result = await fixture.Controller.BookDropIn("estudio", new PublicDropInListViewModel
        {
            AppointmentId = appointmentId,
            Email = "ana@example.com",
            FirstName = "Ana",
            LastName = "Pérez",
            Phone = "11 5555 5555"
        }, default);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("DropIn", view.ViewName);
        var confirmation = Assert.IsType<PublicDropInConfirmationViewModel>(view.Model);
        Assert.Equal(new DateOnly(2026, 3, 12), confirmation.Date);
        Assert.Equal(new TimeOnly(10, 0), confirmation.StartTime);
        Assert.Equal("Yoga", confirmation.ServiceName);
        Assert.Equal("Ana Pérez", confirmation.TeacherName);
        Assert.Equal(fixture.BusinessId, fixture.DropIns.LockBusinessId);
        Assert.Equal(appointmentId, fixture.DropIns.LockedAppointmentId);
        var insert = Assert.Single(fixture.DropIns.Inserts);
        Assert.Equal(fixture.BusinessId, insert.BusinessId);
        Assert.Equal(appointmentId, insert.AppointmentId);
    }

    [Fact]
    public async Task BookDropIn_full_session_re_renders_the_list_with_the_mapped_message_and_keeps_the_typed_values()
    {
        var fixture = await Fixture.CreateAsync();
        var appointmentId = fixture.AddSession(
            new DateOnly(2026, 3, 12), new TimeOnly(10, 0), maxCapacity: 2,
            bookings: [BookingStatus.Pending, BookingStatus.Confirmed]);

        var result = await fixture.Controller.BookDropIn("estudio", new PublicDropInListViewModel
        {
            AppointmentId = appointmentId,
            Email = "ana@example.com",
            FirstName = "Ana",
            LastName = "Pérez",
            Phone = "11 5555 5555"
        }, default);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Classes", view.ViewName);
        var list = Assert.IsType<PublicDropInListViewModel>(view.Model);
        Assert.Contains(Messages(fixture.Controller), message => message.Contains("La clase está completa"));
        Assert.Equal("ana@example.com", list.Email);
        Assert.Equal("Ana", list.FirstName);
        Assert.Equal("Pérez", list.LastName);
        Assert.Equal("11 5555 5555", list.Phone);
        Assert.Equal(appointmentId, list.OpenAppointmentId);
        Assert.Equal(0, fixture.DropIns.InsertCallCount);
        Assert.Empty(fixture.DropIns.Inserts);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
        Assert.Equal(1, fixture.UnitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task BookDropIn_invalid_email_re_renders_the_list_with_the_mapped_message_and_keeps_the_typed_values()
    {
        var fixture = await Fixture.CreateAsync();
        var appointmentId = fixture.AddSession(new DateOnly(2026, 3, 12), new TimeOnly(10, 0), maxCapacity: 8);

        var result = await fixture.Controller.BookDropIn("estudio", new PublicDropInListViewModel
        {
            AppointmentId = appointmentId,
            Email = "no-es-un-email",
            FirstName = "Ana",
            LastName = "Pérez"
        }, default);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Classes", view.ViewName);
        var list = Assert.IsType<PublicDropInListViewModel>(view.Model);
        Assert.Contains(Messages(fixture.Controller), message => message.Contains("Ingresá un email válido"));
        Assert.Equal("no-es-un-email", list.Email);
        Assert.Equal("Ana", list.FirstName);
        Assert.Equal("Pérez", list.LastName);
        Assert.Equal(appointmentId, list.OpenAppointmentId);
        Assert.Contains(list.Sessions, session => session.AppointmentId == appointmentId);
        Assert.Equal(0, fixture.DropIns.InsertCallCount);
        Assert.Equal(0, fixture.DropIns.LockCallCount);
        Assert.Equal(0, fixture.UnitOfWork.BeginCount);
    }

    [Fact]
    public async Task BookDropIn_unknown_slug_is_not_found()
    {
        var fixture = await Fixture.CreateAsync();

        var result = await fixture.Controller.BookDropIn("no-existe", new PublicDropInListViewModel(), default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(fixture.DropIns.Inserts);
    }

    private static IEnumerable<string> Messages(ReservationsController controller) =>
        controller.ModelState.Values.SelectMany(entry => entry.Errors).Select(error => error.ErrorMessage);

    private sealed class Fixture
    {
        private int _nextAppointmentId = 100;

        public required int BusinessId { get; init; }
        public required int OtherBusinessId { get; init; }
        public required int ServiceId { get; init; }
        public required int WorkerId { get; init; }
        public required FakeClassDropInRepository DropIns { get; init; }
        public required FakeUnitOfWork UnitOfWork { get; init; }
        public required ReservationsController Controller { get; init; }

        public int AddSession(
            DateOnly date,
            TimeOnly time,
            int maxCapacity = 8,
            int businessId = 0,
            AppointmentStatus status = AppointmentStatus.Scheduled,
            AppointmentKind kind = AppointmentKind.ClassSession,
            IReadOnlyList<BookingStatus>? bookings = null)
        {
            var appointmentId = _nextAppointmentId++;
            var row = new ClassSessionRow(
                appointmentId,
                ServiceId,
                WorkerId,
                date.ToDateTime(time),
                maxCapacity,
                kind,
                status,
                (bookings ?? [])
                    .Select((bookingStatus, index) => new ClassSessionBooking(index + 1, bookingStatus))
                    .ToArray());
            DropIns.Sessions.Add(new FakeClassDropInSession(businessId == 0 ? BusinessId : businessId, row));
            return appointmentId;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var businesses = new FakeBusinessRepository();
            var businessId = (await businesses.InsertAsync(new Business { Name = "Estudio", Slug = "estudio" })).BusinessId;
            var otherBusinessId = (await businesses.InsertAsync(new Business { Name = "Otro", Slug = "otro" })).BusinessId;

            var serviceRepository = new FakeServiceRepository();
            var serviceId = await serviceRepository.InsertAsync(new Service
            {
                BusinessId = businessId,
                Name = "Yoga",
                Price = 3000m,
                DurationMinutes = 60,
                MaxCapacity = 8,
                IsActive = true
            });

            var workerRepository = new FakeWorkerRepository();
            var workerId = await workerRepository.InsertAsync(new Worker
            {
                BusinessId = businessId,
                FirstName = "Ana",
                LastName = "Pérez",
                IsActive = true
            });

            var dropIns = new FakeClassDropInRepository();
            var unitOfWork = new FakeUnitOfWork();
            var clock = new FakeClock { Now = Now };

            var browseRepository = new FakePublicBookingBrowseRepository();
            var publicRepository = new FakeBookingRepository();
            var browseService = new PublicBookingBrowseService(businesses, browseRepository, publicRepository, clock);
            var bookingService = new BookingService(
                publicRepository, unitOfWork, new TestLinkTokenProtector(), new TestAppointmentUrlBuilder(), clock);
            var dropInService = new ClassDropInService(dropIns, unitOfWork, clock);

            var context = new DefaultHttpContext();
            var controller = new ReservationsController(
                browseService,
                bookingService,
                dropInService,
                businesses,
                new CatalogService(serviceRepository),
                new StaffService(workerRepository, unitOfWork),
                clock)
            {
                ControllerContext = new ControllerContext { HttpContext = context },
                TempData = new TempDataDictionary(context, new FakeTempDataProvider()),
                Url = new StubUrlHelper()
            };

            return new Fixture
            {
                BusinessId = businessId,
                OtherBusinessId = otherBusinessId,
                ServiceId = serviceId,
                WorkerId = workerId,
                DropIns = dropIns,
                UnitOfWork = unitOfWork,
                Controller = controller
            };
        }
    }

    /// <summary>
    /// Minimal URL helper: <c>SetPublicLayout</c> falls back to <c>/r/{slug}</c> when <c>Url.Action</c> cannot resolve
    /// a route, which is exactly what a unit test without the MVC routing services gets. It keeps the controller under
    /// test from needing the real routing pipeline.
    /// </summary>
    private sealed class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext => new();

        public string? Action(UrlActionContext actionContext) => null;

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => url is not null && url.StartsWith('/');

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakePublicBookingBrowseRepository : IPublicBookingBrowseRepository
    {
        public Task<IReadOnlyList<Service>> ListActiveIndividualServicesAsync(int businessId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Service>>([]);

        public Task<IReadOnlyList<PublicBookingWorkerOption>> ListActiveWorkersAsync(int businessId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PublicBookingWorkerOption>>([]);

        public Task<PublicBookingWorkerOption?> GetActiveWorkerAsync(int businessId, int workerId, CancellationToken ct = default) =>
            Task.FromResult<PublicBookingWorkerOption?>(null);
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
