using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Staff;

/// <summary>One break slot of a day. Both null is an empty slot (ignored); exactly one null is an incomplete range.</summary>
public sealed record BreakInput(TimeOnly? StartTime, TimeOnly? EndTime);

/// <summary>
/// One day of the weekly schedule form. When <see cref="WorksThisDay"/> is false, the times and breaks of that day
/// are not saved: an unchecked day is a day off regardless of any leftover values.
/// </summary>
public sealed record DayScheduleInput(DayOfWeek DayOfWeek, bool WorksThisDay, TimeOnly? StartTime, TimeOnly? EndTime, List<BreakInput> Breaks);

public sealed record CreateWorkerRequest(string FirstName, string LastName, string Phone, string Cuil, List<DayScheduleInput> Days);

public sealed record UpdateWorkerRequest(string FirstName, string LastName, string Phone, string Cuil, List<DayScheduleInput> Days);

/// <summary>
/// Trabajadoras: worker profile plus its weekly schedule and breaks (RF-03). A worker is archived, never deleted, so
/// its history is kept and it stops being offered for new bookings (T04, T08). Workers never sign in (D-06).
/// </summary>
public sealed class StaffService(IWorkerRepository workers, IUnitOfWork unitOfWork)
{
    public Task<List<Worker>> ListAsync(int businessId, CancellationToken ct = default) =>
        workers.ListByBusinessAsync(businessId, ct);

    public Task<Worker?> GetWorkerAsync(int businessId, int id, CancellationToken ct = default) =>
        workers.GetByIdAsync(businessId, id, ct);

    public async Task<Result<Worker>> CreateAsync(int businessId, CreateWorkerRequest request, CancellationToken ct = default)
    {
        var firstName = request.FirstName.Trim();
        if (firstName.Length == 0)
            return Result<Worker>.Failure("worker_first_name_required", "Ingresá el nombre.", nameof(request.FirstName));

        var lastName = request.LastName.Trim();
        if (lastName.Length == 0)
            return Result<Worker>.Failure("worker_last_name_required", "Ingresá el apellido.", nameof(request.LastName));

        var phone = request.Phone.Trim();
        if (phone.Length == 0)
            return Result<Worker>.Failure("worker_phone_required", "Ingresá el teléfono.", nameof(request.Phone));

        if (!CuilFormat.TryNormalize(request.Cuil, out var cuil))
            return Result<Worker>.Failure("invalid_cuil", "Ingresá un CUIL válido de 11 dígitos.", nameof(request.Cuil));

        if (await workers.CuilExistsAsync(businessId, cuil, ct: ct))
            return Result<Worker>.Failure("cuil_taken", "Ya existe una trabajadora con ese CUIL.", nameof(request.Cuil));

        var scheduleError = ValidateAndBuildSchedule(request.Days, out var schedules, out var breaks);
        if (scheduleError is not null)
            return Result<Worker>.Failure(scheduleError);

        var worker = new Worker
        {
            BusinessId = businessId,
            FirstName = firstName,
            LastName = lastName,
            Phone = phone,
            Cuil = cuil,
            IsActive = true
        };

        await using var tx = await unitOfWork.BeginAsync(ct: ct);
        // The precheck above is only a courtesy check: between it and the insert a competing create can commit the
        // same CUIL. Losing the UQ_Workers_Cuil race is the same business outcome, and the transaction is disposed
        // without committing, so no orphan worker or schedule is written.
        var inserted = await workers.InsertAsync(worker, ct);
        if (!inserted.IsInserted)
            return Result<Worker>.Failure("cuil_taken", "Ya existe una trabajadora con ese CUIL.", nameof(request.Cuil));

        worker.Id = inserted.WorkerId;
        await workers.ReplaceSchedulesAsync(worker.Id, schedules, ct);
        await workers.ReplaceBreaksAsync(worker.Id, breaks, ct);
        await tx.CommitAsync(ct);

        worker.Schedules = schedules;
        worker.Breaks = breaks;
        return Result<Worker>.Success(worker);
    }

    public async Task<Result> UpdateAsync(int businessId, int id, UpdateWorkerRequest request, CancellationToken ct = default)
    {
        var worker = await workers.GetByIdAsync(businessId, id, ct);
        if (worker is null)
            return Result.Failure("worker_not_found", "No encontramos esa trabajadora.");

        var firstName = request.FirstName.Trim();
        if (firstName.Length == 0)
            return Result.Failure("worker_first_name_required", "Ingresá el nombre.", nameof(request.FirstName));

        var lastName = request.LastName.Trim();
        if (lastName.Length == 0)
            return Result.Failure("worker_last_name_required", "Ingresá el apellido.", nameof(request.LastName));

        var phone = request.Phone.Trim();
        if (phone.Length == 0)
            return Result.Failure("worker_phone_required", "Ingresá el teléfono.", nameof(request.Phone));

        if (!CuilFormat.TryNormalize(request.Cuil, out var cuil))
            return Result.Failure("invalid_cuil", "Ingresá un CUIL válido de 11 dígitos.", nameof(request.Cuil));

        if (await workers.CuilExistsAsync(businessId, cuil, excludingId: id, ct: ct))
            return Result.Failure("cuil_taken", "Ya existe una trabajadora con ese CUIL.", nameof(request.Cuil));

        var scheduleError = ValidateAndBuildSchedule(request.Days, out var schedules, out var breaks);
        if (scheduleError is not null)
            return Result.Failure(scheduleError);

        worker.FirstName = firstName;
        worker.LastName = lastName;
        worker.Phone = phone;
        worker.Cuil = cuil;

        await using var tx = await unitOfWork.BeginAsync(ct: ct);
        // Same lost window as the create: the edit must report the existing field error and leave the stored row as it
        // was, never silently repoint the request at another worker. Returning before the schedule/break replacement
        // and without committing keeps the row and its child rows untouched.
        if (!(await workers.UpdateAsync(worker, ct)).IsUpdated)
            return Result.Failure("cuil_taken", "Ya existe una trabajadora con ese CUIL.", nameof(request.Cuil));

        await workers.ReplaceSchedulesAsync(worker.Id, schedules, ct);
        await workers.ReplaceBreaksAsync(worker.Id, breaks, ct);
        await tx.CommitAsync(ct);

        return Result.Success();
    }

    public async Task<Result> SetActiveAsync(int businessId, int id, bool isActive, CancellationToken ct = default)
    {
        var worker = await workers.GetByIdAsync(businessId, id, ct);
        if (worker is null)
            return Result.Failure("worker_not_found", "No encontramos esa trabajadora.");

        worker.IsActive = isActive;
        await workers.UpdateAsync(worker, ct);
        return Result.Success();
    }

    /// <summary>
    /// Turns the 7-day form input into the schedule and break rows to save, applying request-shape checks (a
    /// working day needs both times; a break slot needs both or neither) before handing the pair to the pure
    /// <see cref="WeeklyScheduleRules"/> for the range/overlap/day-off rules.
    /// </summary>
    private static BusinessError? ValidateAndBuildSchedule(List<DayScheduleInput> days, out List<WorkerSchedule> schedules, out List<WorkerBreak> breaks)
    {
        schedules = [];
        breaks = [];

        foreach (var day in days)
        {
            if (day.WorksThisDay)
            {
                if (day.StartTime is null || day.EndTime is null)
                    return new BusinessError("worker_schedule_range_required", $"Ingresá el horario de inicio y fin del {SpanishCalendar.DayName(day.DayOfWeek)}.");

                schedules.Add(new WorkerSchedule { DayOfWeek = day.DayOfWeek, StartTime = day.StartTime.Value, EndTime = day.EndTime.Value });
            }

            foreach (var brk in day.Breaks)
            {
                var hasStart = brk.StartTime.HasValue;
                var hasEnd = brk.EndTime.HasValue;
                if (!hasStart && !hasEnd)
                    continue;

                if (hasStart != hasEnd)
                    return new BusinessError("worker_break_range_incomplete", $"Completá el inicio y el fin de la pausa del {SpanishCalendar.DayName(day.DayOfWeek)}.");

                breaks.Add(new WorkerBreak { DayOfWeek = day.DayOfWeek, StartTime = brk.StartTime!.Value, EndTime = brk.EndTime!.Value });
            }
        }

        var problems = WeeklyScheduleRules.Validate(schedules, breaks);
        return problems.Count > 0 ? MapProblem(problems[0]) : null;
    }

    private static BusinessError MapProblem(ScheduleProblem problem)
    {
        var dayName = SpanishCalendar.DayName(problem.Day);
        return problem.Kind switch
        {
            ScheduleProblemKind.InvalidRange =>
                new BusinessError("worker_schedule_invalid_range", $"El horario del {dayName} debe terminar después de empezar."),
            ScheduleProblemKind.InvalidBreakRange =>
                new BusinessError("worker_break_invalid_range", $"La pausa del {dayName} debe terminar después de empezar."),
            ScheduleProblemKind.BreakOutsideRange =>
                new BusinessError("worker_break_outside_range", $"La pausa del {dayName} debe estar dentro del horario de ese día."),
            ScheduleProblemKind.OverlappingBreaks =>
                new BusinessError("worker_breaks_overlap", $"Las pausas del {dayName} no pueden superponerse."),
            ScheduleProblemKind.BreakOnDayOff =>
                new BusinessError("worker_break_on_day_off", $"El {dayName} no tiene horario de atención: no puede tener pausas."),
            _ => throw new ArgumentOutOfRangeException(nameof(problem), problem.Kind, null)
        };
    }
}
