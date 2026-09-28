using WorkUp.BusinessLogic.Staff;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Staff;

// RF-03: a worker (trabajadora) is data, never a user (D-06). Her CUIL is unique per business; her weekly schedule
// is at most one working range per day plus breaks inside it (Regla 1), saved atomically with her profile.
public class StaffServiceTests
{
    private readonly FakeWorkerRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly StaffService _service;

    public StaffServiceTests()
    {
        _service = new StaffService(_repository, _unitOfWork);
    }

    private static DayScheduleInput DayOff(DayOfWeek day) => new(day, false, null, null, []);

    private static DayScheduleInput WorkDay(DayOfWeek day, int startHour, int endHour, params BreakInput[] breaks) =>
        new(day, true, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0), breaks.ToList());

    private static BreakInput Brk(int startHour, int startMinute, int endHour, int endMinute) =>
        new(new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute));

    /// <summary>A full week, Sunday to Saturday, with every day off unless overridden.</summary>
    private static List<DayScheduleInput> Week(params DayScheduleInput[] workingDays)
    {
        var week = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, DayOff);
        foreach (var day in workingDays)
            week[day.DayOfWeek] = day;
        return week.Values.ToList();
    }

    private static CreateWorkerRequest ValidRequest(List<DayScheduleInput>? days = null) =>
        new("Marina", "Gómez", "1122334455", "20304050607", days ?? Week(WorkDay(DayOfWeek.Monday, 9, 20)));

    [Fact]
    public async Task Creates_a_worker_with_a_weekly_schedule_and_a_break_in_one_transaction()
    {
        var days = Week(
            WorkDay(DayOfWeek.Monday, 9, 20, Brk(13, 0, 16, 0)),
            WorkDay(DayOfWeek.Tuesday, 9, 20),
            WorkDay(DayOfWeek.Wednesday, 9, 20),
            WorkDay(DayOfWeek.Thursday, 9, 20),
            WorkDay(DayOfWeek.Friday, 9, 20));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.True(result.Succeeded);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal("Marina", result.Value.FirstName);
        Assert.Equal("Gómez", result.Value.LastName);
        Assert.Equal("1122334455", result.Value.Phone);
        Assert.Equal("20-30405060-7", result.Value.Cuil);
        Assert.True(result.Value.IsActive);
        Assert.Equal(1, result.Value.BusinessId);
        Assert.Equal(5, result.Value.Schedules.Count);
        var breakSaved = Assert.Single(result.Value.Breaks);
        Assert.Equal(DayOfWeek.Monday, breakSaved.DayOfWeek);
    }

    [Fact]
    public async Task Fails_when_the_first_name_is_blank()
    {
        var result = await _service.CreateAsync(1, ValidRequest() with { FirstName = "   " });

        Assert.False(result.Succeeded);
        Assert.Equal("worker_first_name_required", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_last_name_is_blank()
    {
        var result = await _service.CreateAsync(1, ValidRequest() with { LastName = "" });

        Assert.False(result.Succeeded);
        Assert.Equal("worker_last_name_required", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_phone_is_blank()
    {
        var result = await _service.CreateAsync(1, ValidRequest() with { Phone = "  " });

        Assert.False(result.Succeeded);
        Assert.Equal("worker_phone_required", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_cuil_does_not_have_11_digits()
    {
        var result = await _service.CreateAsync(1, ValidRequest() with { Cuil = "203040506" });

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_cuil", result.Error!.Code);
    }

    [Fact]
    public async Task Normalizes_a_cuil_entered_with_dashes()
    {
        var result = await _service.CreateAsync(1, ValidRequest() with { Cuil = "20-30405060-7" });

        Assert.True(result.Succeeded);
        Assert.Equal("20-30405060-7", result.Value.Cuil);
    }

    [Fact]
    public async Task Fails_when_the_cuil_is_already_used_by_another_worker_of_the_same_business()
    {
        await _service.CreateAsync(1, ValidRequest());

        var result = await _service.CreateAsync(1, ValidRequest() with { FirstName = "Otra" });

        Assert.False(result.Succeeded);
        Assert.Equal("cuil_taken", result.Error!.Code);
        Assert.Equal("Ya existe una trabajadora con ese CUIL.", result.Error.Message);
    }

    [Fact]
    public async Task Allows_the_same_cuil_in_a_different_business()
    {
        await _service.CreateAsync(1, ValidRequest());

        var result = await _service.CreateAsync(2, ValidRequest());

        Assert.True(result.Succeeded);
    }

    // A create can pass the sequential CUIL precheck and still lose the insert to a competing create that committed in
    // between (T27, T01 finding). The per-business UQ_Workers_Cuil is the last race guard, so that lost race is an
    // expected business outcome with the same field error as the precheck, never a 500 and never a second worker.
    [Fact]
    public async Task Fails_when_the_cuil_is_lost_to_a_concurrent_create()
    {
        _repository.LoseCuilRaceOnInsert = true;

        var result = await _service.CreateAsync(1, ValidRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("cuil_taken", result.Error!.Code);
        Assert.Equal("Ya existe una trabajadora con ese CUIL.", result.Error.Message);
        Assert.Equal(nameof(CreateWorkerRequest.Cuil), result.Error.Field);
        // The losing create wrote no worker and saved no schedule: the transaction was disposed without committing.
        Assert.Empty(_repository.All);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    // Only the known unique-CUIL collision is a business outcome. Any other storage failure must keep propagating
    // instead of being disguised as cuil_taken.
    [Fact]
    public async Task Propagates_an_unexpected_storage_failure_instead_of_reporting_a_cuil_conflict()
    {
        _repository.InsertFailure = new InvalidOperationException("connection reset");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateAsync(1, ValidRequest()));

        Assert.Equal("connection reset", exception.Message);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Fails_when_a_working_day_is_missing_its_start_or_end_time()
    {
        var days = Week(new DayScheduleInput(DayOfWeek.Monday, true, null, null, []));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.False(result.Succeeded);
        Assert.Equal("worker_schedule_range_required", result.Error!.Code);
    }

    [Fact]
    public async Task Ignores_leftover_times_when_the_day_is_marked_as_a_day_off()
    {
        var days = Week(new DayScheduleInput(DayOfWeek.Monday, false, new TimeOnly(9, 0), new TimeOnly(20, 0), []));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value.Schedules);
    }

    [Fact]
    public async Task Fails_when_a_days_range_ends_before_it_starts()
    {
        var days = Week(WorkDay(DayOfWeek.Monday, 20, 9));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.False(result.Succeeded);
        Assert.Equal("worker_schedule_invalid_range", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_a_break_is_outside_the_days_range()
    {
        var days = Week(WorkDay(DayOfWeek.Monday, 9, 20, Brk(7, 0, 8, 0)));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.False(result.Succeeded);
        Assert.Equal("worker_break_outside_range", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_two_breaks_of_the_same_day_overlap()
    {
        var days = Week(WorkDay(DayOfWeek.Monday, 8, 20, Brk(10, 0, 13, 0), Brk(12, 0, 14, 0)));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.False(result.Succeeded);
        Assert.Equal("worker_breaks_overlap", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_a_break_is_registered_on_a_day_off()
    {
        var days = Week(new DayScheduleInput(DayOfWeek.Sunday, false, null, null, [Brk(13, 0, 14, 0)]));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.False(result.Succeeded);
        Assert.Equal("worker_break_on_day_off", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_a_break_slot_only_has_its_start_time_filled()
    {
        var days = Week(new DayScheduleInput(DayOfWeek.Monday, true, new TimeOnly(9, 0), new TimeOnly(20, 0),
            [new BreakInput(new TimeOnly(13, 0), null)]));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.False(result.Succeeded);
        Assert.Equal("worker_break_range_incomplete", result.Error!.Code);
    }

    [Fact]
    public async Task Ignores_a_completely_empty_break_slot()
    {
        var days = Week(new DayScheduleInput(DayOfWeek.Monday, true, new TimeOnly(9, 0), new TimeOnly(20, 0),
            [new BreakInput(null, null)]));

        var result = await _service.CreateAsync(1, ValidRequest(days));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value.Breaks);
    }

    [Fact]
    public async Task Edits_an_existing_worker_and_replaces_its_schedule_and_breaks_atomically()
    {
        var created = await _service.CreateAsync(1, ValidRequest(Week(WorkDay(DayOfWeek.Monday, 9, 20))));

        var newDays = Week(WorkDay(DayOfWeek.Tuesday, 10, 18, Brk(13, 0, 14, 0)));
        var result = await _service.UpdateAsync(1, created.Value.Id, new UpdateWorkerRequest("Marina", "Gómez Actualizada", "1155667788", "20304050607", newDays));

        Assert.True(result.Succeeded);
        Assert.Equal(2, _unitOfWork.CommitCount);

        var updated = await _service.GetWorkerAsync(1, created.Value.Id);
        Assert.Equal("Gómez Actualizada", updated!.LastName);
        Assert.Equal("1155667788", updated.Phone);
        var schedule = Assert.Single(updated.Schedules);
        Assert.Equal(DayOfWeek.Tuesday, schedule.DayOfWeek);
        Assert.Single(updated.Breaks);
    }

    [Fact]
    public async Task Fails_to_edit_when_the_worker_does_not_belong_to_the_business()
    {
        var created = await _service.CreateAsync(1, ValidRequest());

        var result = await _service.UpdateAsync(2, created.Value.Id, new UpdateWorkerRequest("Otra", "Persona", "111", "20304050607", Week()));

        Assert.False(result.Succeeded);
        Assert.Equal("worker_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Keeping_its_own_cuil_while_editing_other_fields_does_not_fail_as_taken()
    {
        var created = await _service.CreateAsync(1, ValidRequest());

        var result = await _service.UpdateAsync(1, created.Value.Id,
            new UpdateWorkerRequest("Marina", "Gómez", "1199998888", "20304050607", Week(WorkDay(DayOfWeek.Monday, 9, 20))));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Fails_to_edit_with_a_cuil_taken_by_another_worker_of_the_same_business()
    {
        await _service.CreateAsync(1, ValidRequest());
        var second = await _service.CreateAsync(1, ValidRequest() with { FirstName = "Lucía", Cuil = "20111222339" });

        var result = await _service.UpdateAsync(1, second.Value.Id,
            new UpdateWorkerRequest("Lucía", "Gómez", "1122334455", "20304050607", Week(WorkDay(DayOfWeek.Monday, 9, 20))));

        Assert.False(result.Succeeded);
        Assert.Equal("cuil_taken", result.Error!.Code);
    }

    // The edit can pass the sequential precheck and still lose the update to a competing edit that committed in
    // between. It is reported as the same cuil_taken field error the precheck produces, and the losing edit replaces
    // neither the stored row nor its schedule: the transaction is disposed without committing.
    [Fact]
    public async Task Fails_when_the_new_cuil_is_lost_to_a_concurrent_edit()
    {
        var created = await _service.CreateAsync(1, ValidRequest(Week(WorkDay(DayOfWeek.Monday, 9, 20))));
        _repository.LoseCuilRaceOnUpdate = true;

        var result = await _service.UpdateAsync(1, created.Value.Id,
            new UpdateWorkerRequest("Marina", "Gómez", "1122334455", "20111222339", Week(WorkDay(DayOfWeek.Tuesday, 10, 18))));

        Assert.False(result.Succeeded);
        Assert.Equal("cuil_taken", result.Error!.Code);
        Assert.Equal(nameof(UpdateWorkerRequest.Cuil), result.Error.Field);
        // Only the create committed; the losing edit rolled back and left the stored row and its schedule untouched.
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        var stored = await _service.GetWorkerAsync(1, created.Value.Id);
        Assert.Equal("20-30405060-7", stored!.Cuil);
        var schedule = Assert.Single(stored.Schedules);
        Assert.Equal(DayOfWeek.Monday, schedule.DayOfWeek);
    }

    [Fact]
    public async Task Propagates_an_unexpected_storage_failure_on_edit_instead_of_reporting_a_cuil_conflict()
    {
        var created = await _service.CreateAsync(1, ValidRequest());
        _repository.UpdateFailure = new InvalidOperationException("connection reset");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateAsync(1, created.Value.Id,
                new UpdateWorkerRequest("Marina", "Gómez", "1122334455", "20111222339", Week(WorkDay(DayOfWeek.Monday, 9, 20)))));

        Assert.Equal("connection reset", exception.Message);
    }

    [Fact]
    public async Task Archiving_never_removes_the_worker_from_the_list()
    {
        var created = await _service.CreateAsync(1, ValidRequest());

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        var worker = Assert.Single(list);
        Assert.False(worker.IsActive);
    }

    [Fact]
    public async Task An_archived_worker_can_be_reactivated()
    {
        var created = await _service.CreateAsync(1, ValidRequest());
        await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: true);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        Assert.True(list.Single().IsActive);
    }

    [Fact]
    public async Task Fails_to_archive_when_the_worker_does_not_belong_to_the_business()
    {
        var created = await _service.CreateAsync(1, ValidRequest());

        var result = await _service.SetActiveAsync(2, created.Value.Id, isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal("worker_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Nothing_is_written_when_creation_fails()
    {
        await _service.CreateAsync(1, ValidRequest() with { Cuil = "123" });

        Assert.Empty(_repository.All);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }
}
