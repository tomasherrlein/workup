using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Classes;

// RF-04, D-05: a grid class exists only for a class-type service (max capacity > 1, active) and an active teacher,
// fits fully inside her working schedule for that weekday (Regla 1, reusing WeeklyScheduleRules.IsAvailable) and
// never overlaps another active class of hers on the same day; different teachers may overlap freely (D-25, no
// rooms). It is archived, never deleted, and its weekday, start time and class type are locked once a student holds
// a fixed-class assignment valid today or later - only the teacher can then be swapped, and only to one who is
// actually available.
public class ClassesServiceTests
{
    private readonly FakeGridClassRepository _gridClasses = new();
    private readonly FakeServiceRepository _services = new();
    private readonly FakeWorkerRepository _workers = new();
    private readonly FakeClock _clock = new();
    private readonly ClassesService _service;

    public ClassesServiceTests()
    {
        _service = new ClassesService(_gridClasses, _services, _workers, _clock);
    }

    private async Task<int> SeedService(int businessId = 1, int durationMinutes = 60, int maxCapacity = 12, bool isActive = true)
    {
        var service = new Service { BusinessId = businessId, Name = "Yoga", Price = 3000m, DurationMinutes = durationMinutes, MaxCapacity = maxCapacity, IsActive = isActive };
        return await _services.InsertAsync(service);
    }

    private async Task<int> SeedWorker(int businessId, DayOfWeek workDay, int startHour, int endHour, bool isActive = true, string cuil = "20304050607", List<WorkerBreak>? breaks = null)
    {
        var worker = new Worker { BusinessId = businessId, FirstName = "Sofía", LastName = "Díaz", Phone = "1122334455", Cuil = cuil, IsActive = isActive };
        var id = (await _workers.InsertAsync(worker)).WorkerId;
        await _workers.ReplaceSchedulesAsync(id, [new WorkerSchedule { DayOfWeek = workDay, StartTime = new TimeOnly(startHour, 0), EndTime = new TimeOnly(endHour, 0) }]);
        await _workers.ReplaceBreaksAsync(id, breaks ?? []);
        return id;
    }

    private static CreateGridClassRequest Request(int serviceId, int workerId, DayOfWeek day, int hour, int minute = 0) =>
        new(serviceId, workerId, day, new TimeOnly(hour, minute));

    [Fact]
    public async Task Creates_a_grid_class_for_an_active_class_type_service_and_an_active_worker_inside_her_schedule()
    {
        var serviceId = await SeedService();
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        Assert.True(result.Succeeded);
        Assert.Equal(serviceId, result.Value.ServiceId);
        Assert.Equal(workerId, result.Value.WorkerId);
        Assert.Equal(DayOfWeek.Tuesday, result.Value.DayOfWeek);
        Assert.Equal(new TimeOnly(18, 0), result.Value.StartTime);
        Assert.True(result.Value.IsActive);
        Assert.Equal(1, result.Value.BusinessId);
    }

    [Fact]
    public async Task Fails_when_the_service_does_not_exist()
    {
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(999, workerId, DayOfWeek.Tuesday, 18));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_invalid_service", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_service_is_archived()
    {
        var serviceId = await SeedService(isActive: false);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_invalid_service", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_service_is_individual()
    {
        var serviceId = await SeedService(maxCapacity: 1);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_invalid_service", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_service_belongs_to_another_business()
    {
        var serviceId = await SeedService(businessId: 2);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_invalid_service", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_worker_does_not_exist()
    {
        var serviceId = await SeedService();

        var result = await _service.CreateAsync(1, Request(serviceId, 999, DayOfWeek.Tuesday, 18));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_invalid_worker", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_worker_is_archived()
    {
        var serviceId = await SeedService();
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21, isActive: false);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_invalid_worker", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_class_starts_before_the_workers_schedule()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 16, 30));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_outside_worker_schedule", result.Error!.Code);
        Assert.Equal("La clase queda fuera del horario de Sofía los martes.", result.Error.Message);
    }

    [Fact]
    public async Task Fails_when_the_class_ends_after_the_workers_schedule()
    {
        var serviceId = await SeedService(durationMinutes: 90);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 20));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_outside_worker_schedule", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_class_overlaps_a_break()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 9, 20, breaks: [new WorkerBreak { DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(13, 0), EndTime = new TimeOnly(14, 0) }]);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 13, 30));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_outside_worker_schedule", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_worker_does_not_work_that_day()
    {
        var serviceId = await SeedService();
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Wednesday, 18));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_outside_worker_schedule", result.Error!.Code);
    }

    [Fact]
    public async Task Succeeds_when_the_class_starts_exactly_when_the_workers_schedule_starts()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 17));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Succeeds_when_the_class_ends_exactly_when_the_workers_schedule_ends()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 20));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Fails_when_it_overlaps_another_active_class_of_the_same_teacher_the_same_day()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18, 30));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_overlaps_worker", result.Error!.Code);
    }

    [Fact]
    public async Task Succeeds_when_two_classes_of_the_same_teacher_touch_at_the_edge()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 19));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Ignores_an_archived_class_of_the_same_teacher_when_checking_overlap()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        var first = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));
        await _service.SetActiveAsync(1, first.Value.Id, isActive: false);

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18, 30));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Ignores_a_class_of_the_same_teacher_on_a_different_day()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        await _workers.ReplaceSchedulesAsync(workerId, [
            new WorkerSchedule { DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(21, 0) },
            new WorkerSchedule { DayOfWeek = DayOfWeek.Wednesday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(21, 0) }
        ]);
        await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        var result = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Wednesday, 18));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Succeeds_when_two_different_teachers_have_overlapping_classes_the_same_day()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerAId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21, cuil: "20304050607");
        var workerBId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21, cuil: "20111222339");
        await _service.CreateAsync(1, Request(serviceId, workerAId, DayOfWeek.Tuesday, 18));

        var result = await _service.CreateAsync(1, Request(serviceId, workerBId, DayOfWeek.Tuesday, 18));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Fails_to_edit_when_the_grid_class_does_not_belong_to_the_business()
    {
        var serviceId = await SeedService();
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        var result = await _service.UpdateAsync(2, created.Value.Id, new UpdateGridClassRequest(serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0)));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Edits_a_grid_class_to_a_new_valid_day_and_time()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        await _workers.ReplaceSchedulesAsync(workerId, [
            new WorkerSchedule { DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(21, 0) },
            new WorkerSchedule { DayOfWeek = DayOfWeek.Thursday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(21, 0) }
        ]);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateGridClassRequest(serviceId, workerId, DayOfWeek.Thursday, new TimeOnly(19, 0)));

        Assert.True(result.Succeeded);
        var updated = await _service.GetAsync(1, created.Value.Id);
        Assert.Equal(DayOfWeek.Thursday, updated!.DayOfWeek);
        Assert.Equal(new TimeOnly(19, 0), updated.StartTime);
    }

    [Fact]
    public async Task Archiving_never_removes_the_grid_class_from_the_list()
    {
        var serviceId = await SeedService();
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        var gridClass = Assert.Single(list);
        Assert.False(gridClass.IsActive);
    }

    [Fact]
    public async Task An_archived_grid_class_can_be_reactivated()
    {
        var serviceId = await SeedService();
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));
        await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: true);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        Assert.True(list.Single().IsActive);
    }

    [Fact]
    public async Task Fails_to_archive_when_the_grid_class_does_not_belong_to_the_business()
    {
        var serviceId = await SeedService();
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));

        var result = await _service.SetActiveAsync(2, created.Value.Id, isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_to_change_the_class_type_when_it_has_an_active_fixed_assignment()
    {
        var serviceAId = await SeedService(durationMinutes: 60);
        var serviceBId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        var created = await _service.CreateAsync(1, Request(serviceAId, workerId, DayOfWeek.Tuesday, 18));
        _gridClasses.SeedFixedAssignment(created.Value.Id);

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateGridClassRequest(serviceBId, workerId, DayOfWeek.Tuesday, new TimeOnly(18, 0)));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_locked_by_fixed_students", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_to_change_the_weekday_when_it_has_an_active_fixed_assignment()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        await _workers.ReplaceSchedulesAsync(workerId, [
            new WorkerSchedule { DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(21, 0) },
            new WorkerSchedule { DayOfWeek = DayOfWeek.Wednesday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(21, 0) }
        ]);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));
        _gridClasses.SeedFixedAssignment(created.Value.Id);

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateGridClassRequest(serviceId, workerId, DayOfWeek.Wednesday, new TimeOnly(18, 0)));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_locked_by_fixed_students", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_to_change_the_start_time_when_it_has_an_active_fixed_assignment()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));
        _gridClasses.SeedFixedAssignment(created.Value.Id);

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateGridClassRequest(serviceId, workerId, DayOfWeek.Tuesday, new TimeOnly(19, 0)));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_locked_by_fixed_students", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_to_archive_when_it_has_an_active_fixed_assignment()
    {
        var serviceId = await SeedService();
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));
        _gridClasses.SeedFixedAssignment(created.Value.Id);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_locked_by_fixed_students", result.Error!.Code);
    }

    [Fact]
    public async Task Allows_changing_only_the_teacher_when_it_has_an_active_fixed_assignment_and_the_new_teacher_is_available()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerAId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21, cuil: "20304050607");
        var workerBId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21, cuil: "20111222339");
        var created = await _service.CreateAsync(1, Request(serviceId, workerAId, DayOfWeek.Tuesday, 18));
        _gridClasses.SeedFixedAssignment(created.Value.Id);

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateGridClassRequest(serviceId, workerBId, DayOfWeek.Tuesday, new TimeOnly(18, 0)));

        Assert.True(result.Succeeded);
        var updated = await _service.GetAsync(1, created.Value.Id);
        Assert.Equal(workerBId, updated!.WorkerId);
    }

    [Fact]
    public async Task Fails_changing_the_teacher_when_it_has_an_active_fixed_assignment_and_the_new_teacher_is_not_available()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerAId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21, cuil: "20304050607");
        var workerCId = await SeedWorker(1, DayOfWeek.Monday, 9, 12, cuil: "20111222339");
        var created = await _service.CreateAsync(1, Request(serviceId, workerAId, DayOfWeek.Tuesday, 18));
        _gridClasses.SeedFixedAssignment(created.Value.Id);

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateGridClassRequest(serviceId, workerCId, DayOfWeek.Tuesday, new TimeOnly(18, 0)));

        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_outside_worker_schedule", result.Error!.Code);
    }

    [Fact]
    public async Task Allows_editing_locked_fields_once_the_fixed_assignment_already_ended()
    {
        var serviceId = await SeedService(durationMinutes: 60);
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);
        await _workers.ReplaceSchedulesAsync(workerId, [
            new WorkerSchedule { DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(21, 0) },
            new WorkerSchedule { DayOfWeek = DayOfWeek.Thursday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(21, 0) }
        ]);
        var created = await _service.CreateAsync(1, Request(serviceId, workerId, DayOfWeek.Tuesday, 18));
        _gridClasses.SeedFixedAssignment(created.Value.Id, validTo: DateOnly.FromDateTime(_clock.Now).AddDays(-1));

        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateGridClassRequest(serviceId, workerId, DayOfWeek.Thursday, new TimeOnly(19, 0)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Lists_only_active_class_type_services_as_eligible()
    {
        var activeClassId = await SeedService(maxCapacity: 12);
        await SeedService(maxCapacity: 1);
        await SeedService(maxCapacity: 8, isActive: false);

        var eligible = await _service.ListEligibleServicesAsync(1);

        var eligibleService = Assert.Single(eligible);
        Assert.Equal(activeClassId, eligibleService.Id);
    }

    [Fact]
    public async Task Lists_only_active_workers_as_eligible()
    {
        var activeWorkerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21, cuil: "20304050607");
        await SeedWorker(1, DayOfWeek.Monday, 9, 12, isActive: false, cuil: "20111222339");

        var eligible = await _service.ListEligibleWorkersAsync(1);

        var eligibleWorker = Assert.Single(eligible);
        Assert.Equal(activeWorkerId, eligibleWorker.Id);
    }

    [Fact]
    public async Task Nothing_is_written_when_creation_fails()
    {
        var workerId = await SeedWorker(1, DayOfWeek.Tuesday, 17, 21);

        await _service.CreateAsync(1, Request(999, workerId, DayOfWeek.Tuesday, 18));

        Assert.Empty(_gridClasses.All);
    }
}
