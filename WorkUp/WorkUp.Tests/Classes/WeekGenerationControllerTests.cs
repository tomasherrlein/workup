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
using WorkUp.ViewModels.Shared;

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
        var (controller, weeks, services, workers, _) = await Create(clock);
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
        var (controller, _, _, _, _) = await Create(clock);

        var model = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);

        Assert.Equal(D(2026, 3, 9), model.CurrentWeekStart);
        Assert.Equal(D(2026, 3, 16), model.NextWeekStart);
    }

    [Fact]
    public async Task Week_get_without_active_grid_classes_points_back_to_the_grid()
    {
        var (controller, _, _, _, _) = await Create(new FakeClock());

        var model = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);

        Assert.False(model.HasActiveGridClasses);
        Assert.Empty(model.Sessions);
    }

    [Fact]
    public async Task Week_get_renders_the_derived_state_and_annotation_of_each_fixed_student()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers, _) = await Create(clock);
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

        // The view model reuses the shared status tags instead of duplicating their literals: the two derived states
        // must be the very same records, and the positive state keeps its own label, class and icon.
        var byEnrollment = students.ToDictionary(student => student.EnrollmentId);
        Assert.Equal(StatusTag.PendingPayment, byEnrollment[11].StateTag);
        Assert.Equal(StatusTag.PendingRenewal, byEnrollment[12].StateTag);
        Assert.Equal(new StatusTag("Activa", "tag-confirmed", "check"), byEnrollment[10].StateTag);
    }

    [Fact]
    public async Task Week_get_flashes_the_service_error_and_redirects_to_the_grid()
    {
        var (controller, weeks, _, _, _) = await Create(new FakeClock());
        weeks.ReportedBusinessId = 99; // snapshot of another tenant: the service rejects it

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.Week(D(2026, 3, 9), CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Contains("No pudimos preparar la semana", Assert.IsType<string>(controller.TempData["FlashMessage"]));
    }

    [Fact]
    public async Task Week_get_with_a_malformed_date_flashes_and_never_previews()
    {
        var (controller, weeks, _, _, _) = await Create(new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) });
        // `?date=abc` binds to null: without the ModelState guard the page would silently show the default week.
        controller.ModelState.AddModelError("date", "The value 'abc' is not valid for date.");

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Week(null, CancellationToken.None));

        Assert.Equal("Week", redirect.ActionName);
        Assert.Null(redirect.RouteValues);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Elegí una fecha válida.", Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, weeks.CallCount);
    }

    [Fact]
    public async Task Week_get_flags_a_week_that_already_ended_while_a_week_in_progress_stays_confirmable()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) }; // Tuesday
        var (controller, weeks, services, workers, _) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));

        var past = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 2), CancellationToken.None)).Model);
        Assert.Equal(D(2026, 3, 2), past.WeekStart);
        Assert.Equal(D(2026, 3, 8), past.WeekEnd);
        Assert.True(past.IsPastWeek); // the service rejects this week, so the page must not offer to confirm it

        var current = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);
        Assert.False(current.IsPastWeek);

        var next = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 16), CancellationToken.None)).Model);
        Assert.False(next.IsPastWeek);
    }

    [Fact]
    public async Task Week_get_does_not_flag_the_week_that_ends_today_as_past()
    {
        // The last day of the week may be today: only a week that already ended is rejected by the service.
        var clock = new FakeClock { Now = new DateTime(2026, 3, 15, 20, 0, 0) }; // Sunday
        var (controller, _, _, _, _) = await Create(clock);

        var model = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);

        Assert.Equal(D(2026, 3, 15), model.WeekEnd);
        Assert.False(model.IsPastWeek);
    }

    [Fact]
    public async Task Week_get_exposes_suspended_sessions_with_the_shared_appointment_tag()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers, _) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));
        weeks.Sessions.Add(new WeekSessionRow(
            100, 1, D(2026, 3, 10), new TimeOnly(18, 0), AppointmentStatus.Suspended, []));

        var model = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);

        var session = Assert.Single(model.Sessions);
        Assert.True(session.Suspended);
        Assert.Equal("Suspendido", session.StatusLabel);
        Assert.Equal(StatusTag.For(AppointmentStatus.Suspended), session.StatusTag);
    }

    [Fact]
    public async Task Week_get_exposes_the_annotation_branch_of_an_already_booked_and_of_a_cancelled_student()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers, _) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));
        weeks.Sessions.Add(new WeekSessionRow(
            100, 1, D(2026, 3, 10), new TimeOnly(18, 0), AppointmentStatus.Scheduled,
            [
                new WeekSessionBookingRow(500, 20, 1, "Elena", BookingStatus.Confirmed),
                new WeekSessionBookingRow(501, 21, 2, "Paula", BookingStatus.Cancelled)
            ]));
        weeks.FixedAssignments.Add(new WeekFixedAssignmentRow(
            20, 1, 1, "Elena", EnrollmentStatus.Active, D(2026, 3, 1), null,
            [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]));
        weeks.FixedAssignments.Add(new WeekFixedAssignmentRow(
            21, 1, 2, "Paula", EnrollmentStatus.Active, D(2026, 3, 1), null,
            [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]));

        var model = Assert.IsType<WeekPreviewViewModel>(
            Assert.IsType<ViewResult>(await controller.Week(D(2026, 3, 9), CancellationToken.None)).Model);

        var byEnrollment = Assert.Single(model.Sessions).FixedStudents.ToDictionary(student => student.EnrollmentId);

        var alreadyBooked = byEnrollment[20];
        Assert.True(alreadyBooked.AlreadyBooked);
        Assert.False(alreadyBooked.Cancelled);
        Assert.Equal("Ya anotada", alreadyBooked.AnnotationLabel);
        Assert.Equal(new StatusTag("Ya anotada", "tag-confirmed", "check"), alreadyBooked.AnnotationTag);

        var cancelled = byEnrollment[21];
        Assert.False(cancelled.AlreadyBooked);
        Assert.True(cancelled.Cancelled);
        Assert.Equal("Cancelada", cancelled.AnnotationLabel);
        Assert.Equal(new StatusTag("Cancelada", "tag-cancelled", "cross"), cancelled.AnnotationTag);
    }

    [Fact]
    public void Week_view_replaces_the_confirm_form_with_a_note_for_a_week_that_already_ended()
    {
        // The controller flag only matters if the view consumes it: the note must be gated on IsPastWeek and the
        // ConfirmWeek form must sit behind that gate, so a week that cannot be generated shows no confirm button.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var view = File.ReadAllText(
            Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", "Classes", "Week.cshtml"));

        var gate = view.IndexOf("Model.IsPastWeek", StringComparison.Ordinal);
        var note = view.IndexOf("ya terminó", StringComparison.Ordinal);
        var confirm = view.IndexOf("asp-action=\"ConfirmWeek\"", StringComparison.Ordinal);

        Assert.True(gate >= 0, "The view must gate the past week on Model.IsPastWeek.");
        Assert.True(note > gate, "The explanatory note must be the IsPastWeek branch body.");
        Assert.True(confirm > note, "The ConfirmWeek form must follow the past-week gate.");
        Assert.DoesNotContain("ConfirmWeek", view[gate..note]);

        // Exactly two primary buttons in the file, each on a mutually exclusive screen: the empty-grid shortcut and
        // the confirm submit. The past-week note adds none, so every rendered screen still has at most one.
        Assert.Equal(2, view.Split("btn-primary").Length - 1);
    }

    [Fact]
    public async Task ConfirmWeek_post_generates_the_same_week_flashes_the_counts_and_redirects_to_the_preview()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers, _) = await Create(clock);
        var (yogaServiceId, yogaWorkerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, yogaServiceId, yogaWorkerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));
        weeks.GridClasses.Add(new WeekGridClassRow(2, yogaServiceId, yogaWorkerId, DayOfWeek.Thursday, new TimeOnly(19, 0), 60, 8));
        weeks.Sessions.Add(new WeekSessionRow(100, 2, D(2026, 3, 12), new TimeOnly(19, 0), AppointmentStatus.Scheduled, []));
        weeks.ConfirmOutcome = new WeekGenerationWriteOutcome(2, 3);

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.ConfirmWeek(D(2026, 3, 9), CancellationToken.None));

        Assert.Equal("Week", redirect.ActionName);
        Assert.Equal("2026-03-09", redirect.RouteValues!["date"]);
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
    public async Task ConfirmWeek_post_redirects_with_the_invariant_date_of_a_midweek_selection()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers, _) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));

        // Wednesday of the following week: the date is not a Monday, so the echoed value must survive normalization.
        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.ConfirmWeek(D(2026, 3, 11), CancellationToken.None));

        // A raw DateOnly route value stringifies under the ambient culture; the invariant string is what the view
        // already emits in its links, so the redirect must carry it as well.
        Assert.Equal("2026-03-11", Assert.IsType<string>(redirect.RouteValues!["date"]));
        Assert.Equal(D(2026, 3, 9), weeks.ConfirmedPlan!.WeekStart);
    }

    [Fact]
    public async Task ConfirmWeek_post_flashes_the_error_and_still_redirects_to_the_preview()
    {
        var (controller, weeks, _, _, _) = await Create(new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) });
        weeks.ReportedBusinessId = 99;

        var redirect = Assert.IsType<RedirectToActionResult>(
            await controller.ConfirmWeek(D(2026, 3, 9), CancellationToken.None));

        Assert.Equal("Week", redirect.ActionName);
        Assert.Equal("2026-03-09", redirect.RouteValues!["date"]);
        Assert.Equal(0, weeks.ConfirmCallCount);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Contains("No pudimos preparar la semana", Assert.IsType<string>(controller.TempData["FlashMessage"]));
    }

    [Fact]
    public async Task ConfirmWeek_post_with_a_malformed_date_never_calls_the_service_and_never_commits()
    {
        var (controller, weeks, _, _, unitOfWork) = await Create(new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) });
        controller.ModelState.AddModelError("date", "The value 'abc' is not valid for date.");

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.ConfirmWeek(null, CancellationToken.None));

        Assert.Equal("Week", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("Elegí una fecha válida.", Assert.IsType<string>(controller.TempData["FlashMessage"]));
        Assert.Equal(0, weeks.CallCount);
        Assert.Equal(0, weeks.ConfirmCallCount);
        Assert.Null(weeks.ConfirmedPlan);
        Assert.Equal(0, unitOfWork.BeginCount);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task ConfirmWeek_post_without_a_date_confirms_the_default_next_week()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) }; // Tuesday
        var (controller, weeks, services, workers, _) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Monday, new TimeOnly(8, 0), 60, 8));

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.ConfirmWeek(null, CancellationToken.None));

        Assert.Equal("Week", redirect.ActionName);
        Assert.Equal(D(2026, 3, 16), weeks.ConfirmedPlan!.WeekStart);
        // No date was picked, so the redirect must not echo one and the preview falls back to its default week.
        Assert.True(redirect.RouteValues!.ContainsKey("date"));
        Assert.Null(redirect.RouteValues["date"]);
    }

    [Fact]
    public async Task ConfirmWeek_post_flashes_the_singular_counts()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers, _) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));
        weeks.Sessions.Add(new WeekSessionRow(100, 1, D(2026, 3, 10), new TimeOnly(18, 0), AppointmentStatus.Scheduled, []));
        weeks.ConfirmOutcome = new WeekGenerationWriteOutcome(1, 1);

        await controller.ConfirmWeek(D(2026, 3, 9), CancellationToken.None);

        Assert.Equal(
            "Semana generada: 1 clase nueva y 1 anotación fija nueva. 1 clase ya existía.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
    }

    [Fact]
    public async Task ConfirmWeek_post_flashes_the_zero_counts_when_nothing_was_created()
    {
        var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
        var (controller, weeks, services, workers, _) = await Create(clock);
        var (serviceId, workerId) = await SeedClass(services, workers, "Yoga");
        weeks.GridClasses.Add(new WeekGridClassRow(1, serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0), 60, 8));
        weeks.ConfirmOutcome = new WeekGenerationWriteOutcome(0, 0);

        await controller.ConfirmWeek(D(2026, 3, 9), CancellationToken.None);

        Assert.Equal(
            "Semana generada: 0 clases nuevas y 0 anotaciones fijas nuevas. No había clases ya generadas.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));
    }

    private static async Task<(int ServiceId, int WorkerId)> SeedClass(
        FakeServiceRepository services, FakeWorkerRepository workers, string name)
    {
        var serviceId = await services.InsertAsync(new Service
        {
            BusinessId = 7, Name = name, Price = 3000m, DurationMinutes = 60, MaxCapacity = 8, IsActive = true
        });
        var workerId = (await workers.InsertAsync(new Worker
        {
            BusinessId = 7, FirstName = "Ana", LastName = "Díaz", IsActive = true
        })).WorkerId;
        return (serviceId, workerId);
    }

    private static async Task<(ClassesController, FakeWeekGenerationRepository, FakeServiceRepository, FakeWorkerRepository, FakeUnitOfWork)> Create(IClock clock)
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
        return await Task.FromResult<(ClassesController, FakeWeekGenerationRepository, FakeServiceRepository, FakeWorkerRepository, FakeUnitOfWork)>(
            (controller, weeks, services, workers, unitOfWork));
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
