using System.Globalization;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Enrollments;

/// <summary>
/// Guided change of one fixed class of an enrollment (RF-21, D-19). The owner replaces the current weekly slot with
/// another active grid class from an effective date; the previous assignment closes the day before it and the new one
/// opens on it, so earlier dates — with their attendance, cancellations and suspensions — are never modified. Payment,
/// frequency, agreed price, reference day and paid periods are untouched: only the weekly slot moves (RF-29).
/// <para>
/// The change is refused, with the first usable date in the message, while either affected grid class already has
/// generated sessions from the effective date on. Migrating or rewriting those annotations is out of scope, so the
/// owner moves the effective date instead; an unrelated grid class never blocks the change. The new grid class must
/// belong to the business, be active and still have a free permanent seat, counted from today — never from the
/// effective date — because the place is retained from registration (Regla 23, D-21).
/// </para>
/// <para>
/// Everything runs in one transaction that commits once. The change asks for the two grid class rows in ascending id
/// order and then for the enrollment row. That request order is all this level can promise:
/// <see cref="IEnrollmentRepository.LockGridClassesAsync"/> is called with the ids in that order, but neither the
/// request nor the statement's <c>ORDER BY</c> over an <c>IN</c> list is documented to decide the order in which SQL
/// Server acquires the locks, so a two-writer deadlock is not claimed to be impossible. The guarded write closes only
/// while the previous row is still open, so the losing side of a race changes nothing and is answered with a failure
/// instead of a second open assignment for the same slot.
/// </para>
/// </summary>
public sealed class FixedScheduleChangeService(
    IEnrollmentRepository enrollments,
    IFixedScheduleChangeRepository schedules,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const string InvalidRequestCode = "fixed_schedule_change_invalid_request";
    private const string InvalidEffectiveDateCode = "fixed_schedule_change_invalid_effective_date";
    private const string EnrollmentNotFoundCode = "fixed_schedule_change_enrollment_not_found";
    private const string NotChangeableCode = "fixed_schedule_change_not_changeable";
    private const string AssignmentNotFoundCode = "fixed_schedule_change_assignment_not_found";
    private const string EffectiveDateTooEarlyCode = "fixed_schedule_change_effective_date_too_early";
    private const string DuplicateSlotCode = "fixed_schedule_change_duplicate_slot";
    private const string GridClassNotFoundCode = "fixed_schedule_change_grid_class_not_found";
    private const string GridClassInactiveCode = "fixed_schedule_change_grid_class_inactive";
    private const string NoSeatCode = "fixed_schedule_change_no_seat";
    private const string SessionsGeneratedCode = "fixed_schedule_change_sessions_generated";
    private const string PreviousAssignmentChangedCode = "fixed_schedule_change_previous_assignment_changed";

    public async Task<Result<FixedScheduleChangeOutcome>> ChangeAsync(
        int businessId, ChangeFixedScheduleRequest request, CancellationToken ct = default)
    {
        if (businessId <= 0 || request is null
            || request.EnrollmentId <= 0
            || request.CurrentGridClassId <= 0
            || request.NewGridClassId <= 0)
            return Result<FixedScheduleChangeOutcome>.Failure(
                InvalidRequestCode, "No pudimos identificar el cambio de horario. Volvé a intentar.");

        // Repeating the same grid class is not a schedule change: it would leave the enrollment with two windows on one
        // slot. It is rejected before any lock is taken.
        if (request.CurrentGridClassId == request.NewGridClassId)
            return Result<FixedScheduleChangeOutcome>.Failure(
                DuplicateSlotCode,
                "Elegí un horario distinto del actual.",
                nameof(request.NewGridClassId));

        if (request.EffectiveFrom == default)
            return Result<FixedScheduleChangeOutcome>.Failure(
                InvalidEffectiveDateCode,
                "Elegí una fecha de vigencia válida.",
                nameof(request.EffectiveFrom));

        // The single clock read of the operation: the past check and the seat reference date derive from it, so a clock
        // that advances during the call cannot split the decision across two instants.
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);

        if (request.EffectiveFrom < today)
            return Result<FixedScheduleChangeOutcome>.Failure(
                InvalidEffectiveDateCode,
                "La fecha de vigencia no puede quedar en el pasado.",
                nameof(request.EffectiveFrom));

        await using var tx = await unitOfWork.BeginAsync(ct: ct);

        // The ids go in ascending order and the enrollment row is requested after them. The request order is
        // deliberate, but it does not prove the order in which SQL Server takes the locks, so no deadlock-freedom
        // claim is made here: what is proven is the guarded write below, which serializes on the row it closes.
        var gridClassIds = new[] { request.CurrentGridClassId, request.NewGridClassId }
            .Distinct()
            .OrderBy(gridClassId => gridClassId)
            .ToArray();
        var lockedGridClasses = await enrollments.LockGridClassesAsync(businessId, gridClassIds, ct);

        var state = await enrollments.LockEnrollmentAsync(businessId, request.EnrollmentId, ct);
        if (state is null)
            return Result<FixedScheduleChangeOutcome>.Failure(
                EnrollmentNotFoundCode, "No encontramos esa inscripción.");

        if (state.Enrollment.Status is not (EnrollmentStatus.PendingPayment or EnrollmentStatus.Active))
            return Result<FixedScheduleChangeOutcome>.Failure(
                NotChangeableCode,
                "Esa inscripción está anulada o dada de baja: ya no tiene un horario fijo que cambiar.");

        var currentGridClass = lockedGridClasses
            .FirstOrDefault(row => row.GridClassId == request.CurrentGridClassId);
        var currentAssignment = state.FixedClasses
            .Where(fixedClass => fixedClass.GridClassId == request.CurrentGridClassId)
            .Where(fixedClass => fixedClass.ValidTo is null)
            .OrderBy(fixedClass => fixedClass.ValidFrom)
            .ThenBy(fixedClass => fixedClass.Id)
            .FirstOrDefault();
        if (currentGridClass is null || currentAssignment is null)
            return Result<FixedScheduleChangeOutcome>.Failure(
                AssignmentNotFoundCode, "No encontramos ese horario fijo vigente en la inscripción.");

        // The previous row closes the day before the effective date, so the effective date must be strictly after its
        // start: on or before it, the window would end before it began, which CK_EnrollmentFixedClasses_Range forbids.
        // The change is refused with the first usable date instead of a malformed range or a deleted row.
        if (request.EffectiveFrom <= currentAssignment.ValidFrom)
        {
            var firstUsable = currentAssignment.ValidFrom.AddDays(1);
            return Result<FixedScheduleChangeOutcome>.Failure(
                EffectiveDateTooEarlyCode,
                $"Elegí una fecha desde el {firstUsable.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}, porque ese horario fijo empezó antes de ese día.",
                nameof(request.EffectiveFrom));
        }

        var newGridClass = lockedGridClasses
            .FirstOrDefault(row => row.GridClassId == request.NewGridClassId);
        if (newGridClass is null)
            return Result<FixedScheduleChangeOutcome>.Failure(
                GridClassNotFoundCode, "No encontramos el nuevo horario en tu negocio.");

        if (!newGridClass.IsActive)
            return Result<FixedScheduleChangeOutcome>.Failure(
                GridClassInactiveCode, "El nuevo horario está archivado: elegí otro.");

        // One slot per weekly class: an assignment of the enrollment that still reaches the new window would leave the
        // student twice in the same class.
        if (state.FixedClasses.Any(fixedClass =>
            fixedClass.GridClassId == request.NewGridClassId
            && (fixedClass.ValidTo is null || fixedClass.ValidTo >= request.EffectiveFrom)))
            return Result<FixedScheduleChangeOutcome>.Failure(
                DuplicateSlotCode, "Ese horario fijo ya forma parte del abono. Elegí otro.");

        // The seat is judged from today, in the same transaction and under the same grid class lock, and the predicate
        // is FixedSeatCapacity: a pending-payment or active assignment already occupies its place, even when its window
        // starts in the future (Regla 23, D-21).
        var seats = await enrollments.ListFixedSeatsAsync(businessId, request.NewGridClassId, today, ct);
        if (!FixedSeatCapacity.HasSeat(newGridClass.MaxCapacity, seats, today))
            return Result<FixedScheduleChangeOutcome>.Failure(
                NoSeatCode, "No queda lugar fijo en el nuevo horario. Elegí otro.");

        // Generated sessions of either affected grid class from the effective date on are not migrated: the change is
        // refused and the owner is asked for a later date. Unrelated grid classes and earlier sessions never block it.
        var lastGeneratedSessionDate = await schedules.LastGeneratedSessionDateAsync(
            businessId,
            new[] { request.CurrentGridClassId, request.NewGridClassId },
            request.EffectiveFrom,
            ct);
        if (lastGeneratedSessionDate is { } lastDate)
        {
            var firstUsable = lastDate.AddDays(1);
            return Result<FixedScheduleChangeOutcome>.Failure(
                SessionsGeneratedCode,
                $"Ya hay clases generadas en los horarios afectados a partir de esa fecha. Elegí una fecha desde el {firstUsable.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.",
                nameof(request.EffectiveFrom));
        }

        var previousValidTo = request.EffectiveFrom.AddDays(-1);
        var newFixedClass = new EnrollmentFixedClass
        {
            EnrollmentId = state.Enrollment.Id,
            GridClassId = request.NewGridClassId,
            ValidFrom = request.EffectiveFrom,
            ValidTo = null
        };

        var write = await schedules.TryReplaceFixedClassAsync(
            businessId,
            state.Enrollment.Id,
            request.CurrentGridClassId,
            previousValidTo,
            newFixedClass,
            ct);
        switch (write.Result)
        {
            case FixedScheduleChangeWriteResultType.Replaced:
                break;
            case FixedScheduleChangeWriteResultType.PreviousAssignmentChanged:
                // Revalidated under the lock: the previous row stopped being open between the read and the write, so
                // nothing was written and the change is not reported as done.
                return Result<FixedScheduleChangeOutcome>.Failure(
                    PreviousAssignmentChangedCode,
                    "Ese horario fijo cambió recién. Volvé a revisar la inscripción.");
            default:
                throw new InvalidOperationException(
                    $"El reemplazo del horario fijo devolvió un resultado desconocido: {(int)write.Result}.");
        }

        if (write.NewFixedClassId <= 0)
            throw new InvalidOperationException(
                "El reemplazo del horario fijo informó un reemplazo sin la fila nueva.");

        await tx.CommitAsync(ct);

        return Result<FixedScheduleChangeOutcome>.Success(new FixedScheduleChangeOutcome(
            state.Enrollment.Id,
            request.CurrentGridClassId,
            request.NewGridClassId,
            request.EffectiveFrom,
            previousValidTo,
            write.NewFixedClassId));
    }
}
