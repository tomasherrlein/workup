using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

public sealed record CreateGridClassRequest(int ServiceId, int WorkerId, DayOfWeek DayOfWeek, TimeOnly StartTime);

public sealed record UpdateGridClassRequest(int ServiceId, int WorkerId, DayOfWeek DayOfWeek, TimeOnly StartTime);

/// <summary>
/// Grilla semanal: recurring weekly classes (RF-04, D-05). A grid class only exists for a class-type service
/// (max capacity greater than 1, active) and an active teacher; it must fit entirely inside the teacher's working
/// schedule for that weekday (reusing <see cref="Domain.Rules.WeeklyScheduleRules.IsAvailable"/>) and must not
/// overlap another active grid class of the same teacher on the same day. Different teachers may overlap freely:
/// rooms are out of scope (D-25). A grid class is archived, never deleted, so week generation (T13) can keep
/// referencing sessions already generated from it. Changing its weekday, start time or class type - or archiving it
/// - is blocked while a student holds a fixed-class assignment valid today or later, because that would silently
/// move a paying student's slot without going through the guided change (T17); only the teacher can be swapped in
/// that case, and only to one who is actually available.
/// </summary>
public sealed class ClassesService(
    IGridClassRepository gridClasses,
    IServiceRepository services,
    IWorkerRepository workers,
    IClock clock)
{
    public Task<List<GridClass>> ListAsync(int businessId, CancellationToken ct = default) =>
        gridClasses.ListByBusinessAsync(businessId, ct);

    public Task<GridClass?> GetAsync(int businessId, int id, CancellationToken ct = default) =>
        gridClasses.GetByIdAsync(businessId, id, ct);

    /// <summary>Active class-type services (max capacity greater than 1) offered when creating or editing a grid class.</summary>
    public async Task<List<Service>> ListEligibleServicesAsync(int businessId, CancellationToken ct = default) =>
        (await services.ListByBusinessAsync(businessId, ct)).Where(s => s.IsActive && s.MaxCapacity > 1).ToList();

    /// <summary>Active workers offered when creating or editing a grid class.</summary>
    public async Task<List<Worker>> ListEligibleWorkersAsync(int businessId, CancellationToken ct = default) =>
        (await workers.ListByBusinessAsync(businessId, ct)).Where(w => w.IsActive).ToList();

    /// <summary>Whether a student holds a fixed-class assignment on this grid class valid today or later (drives the T17 guard hint on the edit form).</summary>
    public Task<bool> HasActiveFixedAssignmentsAsync(int businessId, int id, CancellationToken ct = default) =>
        gridClasses.HasActiveFixedAssignmentAsync(businessId, id, clock.Today, ct);

    public async Task<Result<GridClass>> CreateAsync(int businessId, CreateGridClassRequest request, CancellationToken ct = default)
    {
        var error = await ValidateAsync(businessId, request.ServiceId, request.WorkerId, request.DayOfWeek, request.StartTime, excludingId: null, ct);
        if (error is not null)
            return Result<GridClass>.Failure(error);

        var gridClass = new GridClass
        {
            BusinessId = businessId,
            ServiceId = request.ServiceId,
            WorkerId = request.WorkerId,
            DayOfWeek = request.DayOfWeek,
            StartTime = request.StartTime,
            IsActive = true
        };
        gridClass.Id = await gridClasses.InsertAsync(gridClass, ct);
        return Result<GridClass>.Success(gridClass);
    }

    public async Task<Result> UpdateAsync(int businessId, int id, UpdateGridClassRequest request, CancellationToken ct = default)
    {
        var existing = await gridClasses.GetByIdAsync(businessId, id, ct);
        if (existing is null)
            return Result.Failure("grid_class_not_found", "No encontramos esa clase.");

        var changesLockedFields = existing.ServiceId != request.ServiceId
            || existing.DayOfWeek != request.DayOfWeek
            || existing.StartTime != request.StartTime;

        if (changesLockedFields && await gridClasses.HasActiveFixedAssignmentAsync(businessId, id, clock.Today, ct))
            return Result.Failure(
                "grid_class_locked_by_fixed_students",
                "Esta clase tiene alumnas con horario fijo vigente: para cambiar el tipo de clase, el día o el horario primero cambiá su horario fijo. Podés cambiar la profesora.");

        var error = await ValidateAsync(businessId, request.ServiceId, request.WorkerId, request.DayOfWeek, request.StartTime, excludingId: id, ct);
        if (error is not null)
            return Result.Failure(error);

        existing.ServiceId = request.ServiceId;
        existing.WorkerId = request.WorkerId;
        existing.DayOfWeek = request.DayOfWeek;
        existing.StartTime = request.StartTime;
        await gridClasses.UpdateAsync(existing, ct);
        return Result.Success();
    }

    public async Task<Result> SetActiveAsync(int businessId, int id, bool isActive, CancellationToken ct = default)
    {
        var existing = await gridClasses.GetByIdAsync(businessId, id, ct);
        if (existing is null)
            return Result.Failure("grid_class_not_found", "No encontramos esa clase.");

        if (!isActive && await gridClasses.HasActiveFixedAssignmentAsync(businessId, id, clock.Today, ct))
            return Result.Failure(
                "grid_class_locked_by_fixed_students",
                "Esta clase tiene alumnas con horario fijo vigente: para archivarla primero cambiá su horario fijo.");

        existing.IsActive = isActive;
        await gridClasses.UpdateAsync(existing, ct);
        return Result.Success();
    }

    private async Task<BusinessError?> ValidateAsync(int businessId, int serviceId, int workerId, DayOfWeek day, TimeOnly startTime, int? excludingId, CancellationToken ct)
    {
        var service = await services.GetByIdAsync(businessId, serviceId, ct);
        if (service is null || !service.IsActive || service.MaxCapacity <= 1)
            return new BusinessError("grid_class_invalid_service", "Elegí un tipo de clase activo con cupo mayor a 1.", "ServiceId");

        var worker = await workers.GetByIdAsync(businessId, workerId, ct);
        if (worker is null || !worker.IsActive)
            return new BusinessError("grid_class_invalid_worker", "Elegí una profesora activa.", "WorkerId");

        var end = startTime.AddMinutes(service.DurationMinutes, out var wrappedDays);
        if (wrappedDays != 0 || !WeeklyScheduleRules.IsAvailable(worker.Schedules, worker.Breaks, day, startTime, end))
            return new BusinessError(
                "grid_class_outside_worker_schedule",
                $"La clase queda fuera del horario de {worker.FirstName} los {SpanishCalendar.DayName(day)}.",
                "StartTime");

        var otherClasses = await gridClasses.ListActiveByWorkerAndDayAsync(businessId, workerId, day, excludingId, ct);
        foreach (var other in otherClasses)
        {
            var otherService = await services.GetByIdAsync(businessId, other.ServiceId, ct);
            if (otherService is null)
                continue;

            var otherEnd = other.StartTime.AddMinutes(otherService.DurationMinutes);
            if (Overlaps(startTime, end, other.StartTime, otherEnd))
                return new BusinessError(
                    "grid_class_overlaps_worker",
                    $"{worker.FirstName} ya tiene otra clase ese horario los {SpanishCalendar.DayName(day)}.",
                    "StartTime");
        }

        return null;
    }

    private static bool Overlaps(TimeOnly startA, TimeOnly endA, TimeOnly startB, TimeOnly endB) => startA < endB && startB < endA;
}
