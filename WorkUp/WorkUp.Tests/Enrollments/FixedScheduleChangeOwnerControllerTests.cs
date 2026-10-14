using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Data.Enrollments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Enrollments;

namespace WorkUp.Tests.Enrollments;

// T17 owner side (RF-21, D-19): the "Cambiar horario" entry point on the Abonos list, the guided change form with the
// enrollment's current fixed classes and the active grid classes with a free seat shown as a courtesy, and the POST
// that redirects with a flash or re-renders the form with the mapped Spanish error. The controller only resolves the
// names the owner reads and maps the service's stable codes: every rule, every date and every seat belongs to
// FixedScheduleChangeService and is already pinned at the service level (FixedScheduleChangeServiceTests). What is
// pinned here is that a stale, missing or foreign enrollment never exposes a name or an option, that the effective
// date is part of the form, and that a refusal is actionable and keeps what the owner typed.
public sealed class FixedScheduleChangeOwnerControllerTests
{
    private const int BusinessId = 7;

    private readonly FakeEnrollmentRepository _repository = new();
    private readonly FakeFixedScheduleChangeRepository _schedules;
    private readonly FakeMembershipPriceRepository _prices = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();
    private readonly FakeGridClassRepository _gridClasses = new();
    private readonly FakeServiceRepository _services = new();
    private readonly FakeWorkerRepository _workers = new();
    private readonly FakeClientRepository _clients = new();

    public FixedScheduleChangeOwnerControllerTests()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday
        _schedules = new FakeFixedScheduleChangeRepository(_repository);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // --- Fixtures -----------------------------------------------------------------------------------------------

    private async Task<int> SeedClient(string firstName, string lastName, int businessId = BusinessId) =>
        (await _clients.InsertAsync(new Client
        {
            BusinessId = businessId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}@example.com",
            Phone = "1145667788",
            CreatedAt = _clock.Now
        })).ClientId;

    private async Task<EnrollmentGridClassRow> SeedGridClass(
        string serviceName,
        int maxCapacity = 3,
        bool gridActive = true,
        bool serviceActive = true,
        DayOfWeek dayOfWeek = DayOfWeek.Thursday,
        int hour = 18,
        int businessId = BusinessId)
    {
        var serviceId = await _services.InsertAsync(new Service
        {
            BusinessId = businessId,
            Name = serviceName,
            Price = 3000m,
            DurationMinutes = 60,
            MaxCapacity = maxCapacity,
            IsActive = serviceActive
        });
        var workerId = (await _workers.InsertAsync(new Worker
        {
            BusinessId = businessId,
            FirstName = "Ana",
            LastName = "Díaz",
            IsActive = true
        })).WorkerId;
        var startTime = new TimeOnly(hour, 0);
        var gridClassId = await _gridClasses.InsertAsync(new GridClass
        {
            BusinessId = businessId,
            ServiceId = serviceId,
            WorkerId = workerId,
            DayOfWeek = dayOfWeek,
            StartTime = startTime,
            IsActive = gridActive
        });

        var row = new EnrollmentGridClassRow(
            gridClassId, serviceId, workerId, dayOfWeek, startTime, maxCapacity, gridActive);
        _repository.GridClasses.Add(new FakeEnrollmentGridClass(businessId, row));
        return row;
    }

    private Enrollment StoreEnrollment(
        int clientId = 100,
        EnrollmentStatus status = EnrollmentStatus.Active,
        int businessId = BusinessId)
    {
        var enrollment = new Enrollment
        {
            Id = _repository.NextEnrollmentId++,
            BusinessId = businessId,
            ClientId = clientId,
            WeeklyFrequency = 1,
            Status = status,
            AgreedPrice = 5000m,
            ReferenceDay = 1,
            CreatedAt = _clock.Now,
            ActivatedAt = status == EnrollmentStatus.Active ? _clock.Now : null
        };
        _repository.Enrollments.Add(enrollment);
        return enrollment;
    }

    private EnrollmentFixedClass StoreFixedClass(
        Enrollment enrollment, int gridClassId, DateOnly validFrom, DateOnly? validTo = null)
    {
        var fixedClass = new EnrollmentFixedClass
        {
            Id = _schedules.NextFixedClassId++,
            EnrollmentId = enrollment.Id,
            GridClassId = gridClassId,
            ValidFrom = validFrom,
            ValidTo = validTo
        };
        _repository.FixedClasses.Add(fixedClass);
        return fixedClass;
    }

    private async Task<ChangeScheduleFormViewModel> GetForm(int enrollmentId)
    {
        var controller = Create();
        return Assert.IsType<ChangeScheduleFormViewModel>(
            Assert.IsType<ViewResult>(await controller.ChangeSchedule(enrollmentId, CancellationToken.None)).Model);
    }

    // --- Index entry point (RF-21) ------------------------------------------------------------------------------

    [Fact]
    public void Index_row_offers_the_change_only_while_the_membership_holds_places()
    {
        static bool CanChange(EnrollmentStatus status) =>
            new EnrollmentRowViewModel(new EnrollmentListItem(
                1, 100, "Ana", 1, status, null, MembershipState.None, false, false)).CanChangeSchedule;

        Assert.True(CanChange(EnrollmentStatus.PendingPayment));
        Assert.True(CanChange(EnrollmentStatus.Active));
        Assert.False(CanChange(EnrollmentStatus.Annulled));
        Assert.False(CanChange(EnrollmentStatus.Terminated));
    }

    // --- Form (RF-21) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Change_schedule_form_shows_the_current_open_classes_with_day_time_and_name()
    {
        var current = await SeedGridClass("Hatha", dayOfWeek: DayOfWeek.Thursday, hour: 18);
        var closed = await SeedGridClass("Corte", dayOfWeek: DayOfWeek.Friday, hour: 10);
        await SeedGridClass("Vinyasa", dayOfWeek: DayOfWeek.Monday, hour: 9);
        var clientId = await SeedClient("Ana", "Pérez");
        var enrollment = StoreEnrollment(clientId: clientId);
        StoreFixedClass(enrollment, current.GridClassId, D(2026, 1, 5));
        StoreFixedClass(enrollment, closed.GridClassId, D(2025, 1, 1), D(2025, 12, 31));

        var model = await GetForm(enrollment.Id);

        Assert.Equal("Ana Pérez", model.ClientName);
        Assert.Equal(enrollment.Id, model.EnrollmentId);
        Assert.True(model.CanChange);

        // Only the still-open fixed class is offered, with the day, the time and the name resolved for the owner.
        var option = Assert.Single(model.CurrentClasses);
        Assert.Equal(current.GridClassId, option.GridClassId);
        Assert.Contains("jueves", option.Label);
        Assert.Contains("18:00", option.Label);
        Assert.Contains("Hatha", option.Label);
        Assert.Contains("Ana Díaz", option.Label);
        Assert.Contains("desde el 05/01/2026", option.Label);
        Assert.DoesNotContain(model.CurrentClasses, candidate => candidate.GridClassId == closed.GridClassId);
    }

    [Fact]
    public async Task Change_schedule_form_offers_only_active_tenant_grid_classes_with_a_free_seat()
    {
        var current = await SeedGridClass("Hatha");
        var free = await SeedGridClass("Vinyasa", maxCapacity: 4, dayOfWeek: DayOfWeek.Monday, hour: 9);
        var full = await SeedGridClass("Pilates", maxCapacity: 2, dayOfWeek: DayOfWeek.Wednesday, hour: 11);
        await SeedGridClass("Yoga archivada", gridActive: false, dayOfWeek: DayOfWeek.Friday, hour: 8);
        await SeedGridClass("Reformer", maxCapacity: 1, dayOfWeek: DayOfWeek.Saturday, hour: 8);
        await SeedGridClass("Ajena", dayOfWeek: DayOfWeek.Sunday, hour: 8, businessId: 8);

        // Two active memberships fill the capacity-2 grid class: no permanent seat is left.
        var first = StoreEnrollment(clientId: 901);
        StoreFixedClass(first, full.GridClassId, D(2026, 1, 1));
        var second = StoreEnrollment(clientId: 902);
        StoreFixedClass(second, full.GridClassId, D(2026, 1, 1));

        var clientId = await SeedClient("Ana", "Pérez");
        var enrollment = StoreEnrollment(clientId: clientId);
        StoreFixedClass(enrollment, current.GridClassId, D(2026, 1, 5));

        var model = await GetForm(enrollment.Id);

        // The current slot is not offered again, the full and the archived ones neither, and the foreign class is invisible.
        var option = Assert.Single(model.GridClasses);
        Assert.Equal(free.GridClassId, option.GridClassId);
        Assert.Equal(4, option.FreeSeats);
        Assert.Contains("Vinyasa", option.Label);
        Assert.DoesNotContain(model.GridClasses, candidate => candidate.GridClassId == current.GridClassId);
        Assert.DoesNotContain(model.GridClasses, candidate => candidate.GridClassId == full.GridClassId);

        // A required effective date, defaulted to the business day and never in the past.
        Assert.Equal(D(2026, 3, 10), model.Today);
        Assert.Equal(D(2026, 3, 10), model.EffectiveFrom);
    }

    [Fact]
    public async Task Change_schedule_form_without_another_free_slot_still_shows_the_current_class_but_cannot_change()
    {
        var current = await SeedGridClass("Hatha");
        var clientId = await SeedClient("Ana", "Pérez");
        var enrollment = StoreEnrollment(clientId: clientId);
        StoreFixedClass(enrollment, current.GridClassId, D(2026, 1, 5));

        var model = await GetForm(enrollment.Id);

        Assert.True(model.HasCurrentClasses);
        Assert.False(model.HasGridClasses);
        Assert.False(model.CanChange);
    }

    [Fact]
    public async Task Change_schedule_form_never_exposes_a_name_or_an_option_for_a_missing_foreign_or_ended_enrollment()
    {
        await SeedClient("Ana", "Pérez");
        var gridClass = await SeedGridClass("Hatha");
        var foreign = StoreEnrollment(clientId: 100, businessId: 8);
        StoreFixedClass(foreign, gridClass.GridClassId, D(2026, 1, 5));
        var ended = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Terminated);
        StoreFixedClass(ended, gridClass.GridClassId, D(2026, 1, 5));

        var controller = Create();

        var missing = Assert.IsType<RedirectToActionResult>(
            await controller.ChangeSchedule(9999, CancellationToken.None));
        Assert.Equal("Index", missing.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No encontramos una inscripción con horario fijo para cambiar.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        var other = Assert.IsType<RedirectToActionResult>(
            await controller.ChangeSchedule(foreign.Id, CancellationToken.None));
        Assert.Equal("Index", other.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);

        var terminated = Assert.IsType<RedirectToActionResult>(
            await controller.ChangeSchedule(ended.Id, CancellationToken.None));
        Assert.Equal("Index", terminated.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);

        // The read is a read: no transaction and no write.
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _schedules.ReplaceCallCount);
    }

    // --- POST (RF-21) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Change_schedule_post_calls_the_service_with_the_business_from_the_claim_and_redirects_with_a_flash()
    {
        var current = await SeedGridClass("Hatha", dayOfWeek: DayOfWeek.Tuesday, hour: 18);
        var next = await SeedGridClass("Vinyasa", dayOfWeek: DayOfWeek.Monday, hour: 9);
        var clientId = await SeedClient("Ana", "Pérez");
        var enrollment = StoreEnrollment(clientId: clientId, status: EnrollmentStatus.PendingPayment);
        StoreFixedClass(enrollment, current.GridClassId, D(2026, 1, 5));

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.ChangeSchedule(
            new ChangeScheduleFormViewModel
            {
                EnrollmentId = enrollment.Id,
                CurrentGridClassId = current.GridClassId,
                NewGridClassId = next.GridClassId,
                EffectiveFrom = D(2026, 4, 1)
            },
            CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Success", controller.TempData["FlashType"]);
        Assert.Equal("Horario cambiado desde el 01/04/2026. El horario anterior queda vigente hasta el 31/03/2026.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        // The claim business scopes the write: the previous window closed and the new one opened in this tenant.
        var previous = Assert.Single(_repository.FixedClasses, row => row.GridClassId == current.GridClassId);
        Assert.Equal(D(2026, 3, 31), previous.ValidTo);
        var opened = Assert.Single(_repository.FixedClasses, row => row.GridClassId == next.GridClassId);
        Assert.Equal(D(2026, 4, 1), opened.ValidFrom);
        Assert.Null(opened.ValidTo);
        Assert.Equal(enrollment.Id, opened.EnrollmentId);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Change_schedule_post_re_renders_the_form_keeping_the_typed_values_when_the_new_slot_has_no_seat()
    {
        var current = await SeedGridClass("Hatha");
        var full = await SeedGridClass("Vinyasa", maxCapacity: 2, dayOfWeek: DayOfWeek.Monday, hour: 9);
        var first = StoreEnrollment(clientId: 901);
        StoreFixedClass(first, full.GridClassId, D(2026, 1, 1));
        var second = StoreEnrollment(clientId: 902);
        StoreFixedClass(second, full.GridClassId, D(2026, 1, 1));
        var clientId = await SeedClient("Ana", "Pérez");
        var enrollment = StoreEnrollment(clientId: clientId);
        StoreFixedClass(enrollment, current.GridClassId, D(2026, 1, 5));

        var controller = Create();
        var view = Assert.IsType<ViewResult>(await controller.ChangeSchedule(
            new ChangeScheduleFormViewModel
            {
                EnrollmentId = enrollment.Id,
                CurrentGridClassId = current.GridClassId,
                NewGridClassId = full.GridClassId,
                EffectiveFrom = D(2026, 4, 1)
            },
            CancellationToken.None));

        Assert.Equal("ChangeSchedule", view.ViewName);
        var model = Assert.IsType<ChangeScheduleFormViewModel>(view.Model);
        Assert.Equal(enrollment.Id, model.EnrollmentId);
        Assert.Equal(current.GridClassId, model.CurrentGridClassId);
        Assert.Equal(full.GridClassId, model.NewGridClassId);
        Assert.Equal(D(2026, 4, 1), model.EffectiveFrom);

        var error = Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage;
        Assert.Equal("No queda lugar fijo en el nuevo horario. Elegí otro.", error);
        Assert.DoesNotContain("fixed_schedule_change_", error);

        // The rejection rolls back: nothing is written.
        Assert.Equal(0, _schedules.ReplaceCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Change_schedule_post_shows_the_earliest_valid_date_from_the_service_refusal()
    {
        var current = await SeedGridClass("Hatha", dayOfWeek: DayOfWeek.Tuesday, hour: 18);
        var next = await SeedGridClass("Vinyasa", dayOfWeek: DayOfWeek.Monday, hour: 9);
        var clientId = await SeedClient("Ana", "Pérez");
        var enrollment = StoreEnrollment(clientId: clientId);
        StoreFixedClass(enrollment, current.GridClassId, D(2026, 1, 5));
        // A future session on the new grid class already exists on the chosen date, so the change is refused.
        _schedules.Sessions.Add(new FakeGeneratedClassSession(BusinessId, next.GridClassId, D(2026, 4, 1)));

        var controller = Create();
        var view = Assert.IsType<ViewResult>(await controller.ChangeSchedule(
            new ChangeScheduleFormViewModel
            {
                EnrollmentId = enrollment.Id,
                CurrentGridClassId = current.GridClassId,
                NewGridClassId = next.GridClassId,
                EffectiveFrom = D(2026, 4, 1)
            },
            CancellationToken.None));

        var model = Assert.IsType<ChangeScheduleFormViewModel>(view.Model);
        Assert.Equal(D(2026, 4, 1), model.EffectiveFrom); // what the owner typed is kept
        Assert.True(model.CanChange); // the rebuilt form still offers the current classes and the free slots

        var error = Assert.Single(controller.ModelState["EffectiveFrom"]!.Errors).ErrorMessage;
        Assert.Contains("desde el 02/04/2026", error); // the earliest usable date, not one day too late
        Assert.DoesNotContain("posterior al", error);
        Assert.Equal(0, _schedules.ReplaceCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Change_schedule_post_without_the_new_class_or_the_effective_date_never_reaches_the_service()
    {
        var current = await SeedGridClass("Hatha");
        var clientId = await SeedClient("Ana", "Pérez");
        var enrollment = StoreEnrollment(clientId: clientId);
        StoreFixedClass(enrollment, current.GridClassId, D(2026, 1, 5));

        var controller = Create();
        var incomplete = new ChangeScheduleFormViewModel
        {
            EnrollmentId = enrollment.Id,
            CurrentGridClassId = current.GridClassId,
            NewGridClassId = 0,
            EffectiveFrom = null
        };

        // The DataAnnotations contract of the form: the new class and the effective date are both required.
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(
            incomplete, new ValidationContext(incomplete), results, validateAllProperties: true));
        Assert.Contains(results, result => result.ErrorMessage == "Elegí el nuevo horario de la grilla.");
        Assert.Contains(results, result => result.ErrorMessage == "Elegí la fecha de vigencia.");

        // The MVC pipeline feeds those results into ModelState before the action runs; a direct call does the same.
        foreach (var result in results)
            controller.ModelState.AddModelError(result.MemberNames.FirstOrDefault() ?? string.Empty, result.ErrorMessage!);

        var view = Assert.IsType<ViewResult>(await controller.ChangeSchedule(incomplete, CancellationToken.None));

        var model = Assert.IsType<ChangeScheduleFormViewModel>(view.Model);
        Assert.Equal(current.GridClassId, model.CurrentGridClassId);
        Assert.Null(model.EffectiveFrom);

        Assert.Equal("Elegí el nuevo horario de la grilla.",
            Assert.Single(controller.ModelState["NewGridClassId"]!.Errors).ErrorMessage);
        Assert.Equal("Elegí la fecha de vigencia.",
            Assert.Single(controller.ModelState["EffectiveFrom"]!.Errors).ErrorMessage);

        // The form was rejected before the service: no transaction, no write.
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _schedules.ReplaceCallCount);
    }

    [Fact]
    public async Task Change_schedule_post_never_reaches_an_enrollment_of_another_business()
    {
        var current = await SeedGridClass("Hatha", dayOfWeek: DayOfWeek.Tuesday, hour: 18);
        var next = await SeedGridClass("Vinyasa", dayOfWeek: DayOfWeek.Monday, hour: 9);
        var foreign = StoreEnrollment(clientId: 100, businessId: 8);
        StoreFixedClass(foreign, current.GridClassId, D(2026, 1, 5));

        var controller = Create();
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.ChangeSchedule(
            new ChangeScheduleFormViewModel
            {
                EnrollmentId = foreign.Id,
                CurrentGridClassId = current.GridClassId,
                NewGridClassId = next.GridClassId,
                EffectiveFrom = D(2026, 4, 1)
            },
            CancellationToken.None));

        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Error", controller.TempData["FlashType"]);
        Assert.Equal("No encontramos una inscripción con horario fijo para cambiar.",
            Assert.IsType<string>(controller.TempData["FlashMessage"]));

        // The foreign enrollment is untouched and nothing commits.
        Assert.Null(Assert.Single(_repository.FixedClasses).ValidTo);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public void Every_schedule_change_post_requires_antiforgery_and_the_form_read_does_not()
    {
        var post = typeof(EnrollmentsController)
            .GetMethod(nameof(EnrollmentsController.ChangeSchedule), [typeof(ChangeScheduleFormViewModel), typeof(CancellationToken)]);
        Assert.NotNull(post);
        Assert.NotNull(post!.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());

        var get = typeof(EnrollmentsController)
            .GetMethod(nameof(EnrollmentsController.ChangeSchedule), [typeof(int), typeof(CancellationToken)]);
        Assert.NotNull(get);
        Assert.NotNull(get!.GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(get.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    // --- Views and composition (RF-21) --------------------------------------------------------------------------

    [Fact]
    public void Index_view_offers_the_change_link_behind_the_row_gate_and_keeps_a_single_primary_action()
    {
        var index = ReadEnrollmentsView("Index.cshtml");

        // Exactly one primary action on the whole screen: "Nueva inscripción".
        Assert.Equal(1, index.Split("btn-primary").Length - 1);
        Assert.True(index.IndexOf("item.CanChangeSchedule", StringComparison.Ordinal) <
            index.IndexOf("asp-action=\"ChangeSchedule\"", StringComparison.Ordinal));
        Assert.Contains("Cambiar horario", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Change_schedule_view_keeps_one_primary_action_and_labels_every_field()
    {
        var view = ReadEnrollmentsView("ChangeSchedule.cshtml");

        Assert.Equal(1, view.Split("btn-primary").Length - 1);
        Assert.Contains("asp-for=\"CurrentGridClassId\"", view, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"NewGridClassId\"", view, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"EffectiveFrom\"", view, StringComparison.Ordinal);
        Assert.Contains("asp-validation-summary", view, StringComparison.Ordinal);
        Assert.Contains("Cambiar horario", view, StringComparison.Ordinal);

        // The seat the picker shows is a courtesy and the screen never promises to rewrite generated classes.
        Assert.Contains("orientativos", view, StringComparison.Ordinal);
        Assert.DoesNotContain("regener", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("se actualizarán las clases", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schedule_change_repository_and_service_resolve_as_scoped_from_the_container()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:WorkUp"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT17Di;Trusted_Connection=True;TrustServerCertificate=True",
            ["WorkUp:PublicBaseUrl"] = "https://reservas.example.com",
            ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkUp(configuration, new FakeHostEnvironment { EnvironmentName = Environments.Development });

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var repository = scope.ServiceProvider.GetRequiredService<IFixedScheduleChangeRepository>();
        var service = scope.ServiceProvider.GetRequiredService<FixedScheduleChangeService>();

        Assert.IsType<SqlFixedScheduleChangeRepository>(repository);
        Assert.IsType<FixedScheduleChangeService>(service);

        await using var other = provider.CreateAsyncScope();
        Assert.NotSame(repository, other.ServiceProvider.GetRequiredService<IFixedScheduleChangeRepository>());
        Assert.NotSame(service, other.ServiceProvider.GetRequiredService<FixedScheduleChangeService>());
    }

    // --- Harness ------------------------------------------------------------------------------------------------

    private EnrollmentsController Create()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkUpClaims.BusinessId, BusinessId.ToString())], "test"))
        };

        return new EnrollmentsController(
            new EnrollmentService(_repository, _prices, _unitOfWork, _clock),
            _clock,
            _repository,
            new ClassesService(_gridClasses, _services, _workers, _unitOfWork, _clock),
            new CatalogService(_services, _unitOfWork),
            new StaffService(_workers, _unitOfWork),
            new ClientsService(_clients, new FakeClientNoteRepository(), _clock),
            _prices,
            new FixedScheduleChangeService(_repository, _schedules, _unitOfWork, _clock))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
    }

    private static string ReadEnrollmentsView(string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(root, "WorkUp", "Areas", "Owner", "Views", "Enrollments", fileName));
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
