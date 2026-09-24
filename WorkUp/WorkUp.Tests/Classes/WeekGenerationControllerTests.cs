using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Classes;

namespace WorkUp.Tests.Classes;

// RF-06: the owner sees the week preview from Clases and confirms it with one POST. GET only previews (it resolves
// service and worker names for the owner and never writes); POST confirms the same week and redirects back to its
// preview so the regenerated week is visible immediately. The controller only routes; the read-only preview and the
// transactional write are owned and already tested at the service level (WeekGenerationServiceTests).
public sealed class WeekGenerationControllerTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Fact]
    public async Task Week_get_renders_the_preview_with_resolved_service_and_worker_names()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));

        var view = Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None));
        var model = Assert.IsType<WeekPreviewViewModel>(view.Model);

        Assert.Equal(D(2026, 3, 9), model.SelectedDate);
        Assert.Equal(D(2026, 3, 9), model.WeekStart);
        Assert.Equal(D(2026, 3, 15), model.WeekEnd);
        Assert.True(model.HasActiveGridClasses);
        var session = Assert.Single(model.Sessions);
        Assert.Equal(D(2026, 3, 10), session.Date);
        Assert.Equal(new TimeOnly(18, 0), session.StartTime);
        Assert.Equal("Yoga", session.ServiceName);
        Assert.Equal("Ana Díaz", session.WorkerName);
        Assert.True(session.IsNew);
        Assert.Equal("Nueva", session.StatusLabel);
        Assert.Equal(1, model.NewSessionCount);
        Assert.Equal(0, model.ExistingSessionCount);
    }

    [Fact]
    public async Task Week_get_offers_the_current_and_next_week_when_they_are_representable()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) }; // Tuesday
        var (controller, _, _, _) = await Create(clock);

        var model = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);

        Assert.Equal(D(2026, 3, 9), model.CurrentWeekStart);
        Assert.Equal(D(2026, 3, 16), model.NextWeekStart);
    }

    [Fact]
    public async Task Week_get_without_active_grid_classes_points_back_to_the_grid()
    {
        var (controller, _, _, _) = await Create(new FakeClock());

        var model = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);

        Assert.False(model.HasActiveGridClasses);
        Assert.Empty(model.Sessions);
    }

    [Fact]
    public async Task Week_get_renders_the_derived_state_and_annotation_of_each_fixed_student()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        var gridClassId = 1;
        weeks.GridClasses.Add(new WeekGridClassRow(gridClassId, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));
        weeks.FixedAssignments.Add(new WeekFixedAssignmentRow(
            10, gridClassId, 1, "Activa", EnrollmentStatus.Active, D(2026, 3, 1), null,
            [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]));
        weeks.FixedAssignments.Add(new WeekFixedAssignmentRow(
            11, gridClassId, 2, "Debe", EnrollmentStatus.PendingPayment, D(2026, 3, 1), null, []));
        weeks.FixedAssignments.Add(new WeekFixedAssignmentRow(
            12, gridClassId, 3, "Renovar", EnrollmentStatus.Active, D(2026, 2, 1), null,
            [new MembershipPeriod { CoverageStart = D(2026, 2, 1), ExpiresOn = D(2026, 3, 1) }]));

        var model = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);

        var students = Assert.Single(model.Sessions).FixedStudents;
        Assert.Equal(new[] { "Activa", "Pendiente de pago", "Pendiente de renovación" },
            students.OrderBy(student => student.EnrollmentId).Select(student => student.StateLabel).ToArray());
        Assert.All(students, student => Assert.Equal("Sin anotar", student.AnnotationLabel));
        Assert.Equal(3, model.FixedStudentCount);
    }

    [Fact]
    public async Task Week_get_flashes_the_service_error_and_redirects_to_the_grid()
    {
        var (controller, weeks, _, _) = await Create(new FakeClock());
        weeks.ReportedBusinessId = 99; // snapshot of another tenant: the service rejects it

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.Week(D(2026, 3, 9), CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Contains("No pudimos preparar la semana", Assert.IsType<string>(controller.TempData["FlashMessage"]));
    }

    [Fact]
    public async Task ConfirmWeek_post_generates_the_same_week_flashes_the_counts_and_redirects_to_the_preview()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers) = await Create(clock);
        var (yogaServiceId, yogaWorkerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, yogaServiceId, yogaWorkerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));
        weeks.GridClasses.Add(new WeekGridClassRow(2, yogaServiceId, yogaWorkerId, DayOfWeek.Thursday, new TimeOnly(19, 0), 60, 8));
        weeks.Sessions.Add(new WeekSessionRow(100, 2, D(2026, 3, 12), new TimeOnly(19, 0), AppointmentStatus.Scheduled, []));
        weeks.ConfirmOutcome = new WeekGenerationWriteOutcome(2, 3);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.ConfirmWeek(D(2026, 3, 9), CancellationToken.None));

        Assert.Equal("Week", redirect.ActionName);
        Assert.Equal(D(2026, 3, 9), redirect.RouteValues!["date"]);
        Assert.Equal(1, weeks.ConfirmCallCount);
        Assert.Equal(D(2026, 3, 9), weeks.ConfirmedPlan!.WeekStart);
        Assert.Equal(D(2026, 3, 15), weeks.ConfirmedPlan.WeekEnd);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        var flash = Assert.IsType<string>(controller.TempData["FlashMessage"]);
        Assert.Contains("2 clases nuevas", flash);
        Assert.Contains("3 anotaciones fijas nuevas", flash);
        Assert.Contains("1 clase ya existía", flash);
    }

    [Fact]
    public async Task ConfirmWeek_post_flashes_the_error_and_still_redirects_to_the_preview()
    {
        var (controller, weeks, _, _) = await Create(new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) });
        weeks.ReportedBusinessId = 99;

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.ConfirmWeek(D(2026, 3, 9), CancellationToken.None));

        Assert.Equal("Week", redirect.ActionName);
        Assert.Equal(D(2026, 3, 9), redirect.RouteValues!["date"]);
        Assert.Equal(0, weeks.ConfirmCallCount);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Contains("No pudimos preparar la semana", Assert.IsType<string>(controller.TempData["FlashMessage"]));
    }

    [Fact]
    public async Task ConfirmWeek_post_without_a_date_confirms_the_default_next_week()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) }; // Tuesday
        var (controller, weeks, services, workers) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Monday, new TimeOnly(8, 0), 60, 8));

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.ConfirmWeek(null, CancellationToken.None));

        Assert.Equal("Week", redirect.ActionName);
        Assert.Equal(D(2026, 3, 16), weeks.ConfirmedPlan!.WeekStart);
        Assert.True(redirect.RouteValues!.ContainsKey("date"));
    }

    private static async Task<(int ServiceId, int WorkerId)> SeedClass(
        FakeServiceRepository services, FakeWorkerRepository workers, string name)
    {
        var serviceId = await services.InsertAsync(new Service
        {
            BusinessId = 7, Name = name, Price = 3000m, DurationMinutes = 60, MaxCapacity = 8, IsActive = true
        });
        var workerId = await workers.InsertAsync(new Worker
        {
            BusinessId = 7, FirstName = "Ana", LastName = "Díaz", IsActive = true
        });
        return (serviceId, workerId);
    }

    private static Task<(ClassesController, FakeWeekGenerationRepository, FakeServiceRepository, FakeWorkerRepository)> Create(IClock clock)
    {
        var gridClasses = new FakeGridClassRepository();
        var services = new FakeServiceRepository();
        var workers = new FakeWorkerRepository();
        var weeks = new FakeWeekGenerationRepository();
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
            clock)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
        return Task.FromResult<(ClassesController, FakeWeekGenerationRepository, FakeServiceRepository, FakeWorkerRepository)>(
            (controller, weeks, services, workers));
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
