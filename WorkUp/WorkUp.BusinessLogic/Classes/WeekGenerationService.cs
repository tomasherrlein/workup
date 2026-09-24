using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Week generation (RF-06, D-16, D-23). The preview is read-only: the selected date is normalized to its
/// Monday-to-Sunday week, every active grid class becomes a new or already generated session, and each session lists
/// the fixed students whose enrollment still holds places (pending payment or active). "Pendiente de renovación" is
/// derived from an active enrollment whose periods do not cover the session date; it is never persisted, never written
/// back, and never announced as a recreation: cancelled bookings and suspended sessions stay visible exactly as they
/// are. Confirming writes that same projection inside one transaction: it creates the missing sessions, adds the
/// missing fixed annotations, duplicates nothing, deletes nothing and never resolves a suspension.
/// </summary>
public sealed class WeekGenerationService(
    IWeekGenerationRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>
    /// Previews the week that contains <paramref name="date"/>, or the Monday of the next week when it is omitted, so
    /// the owner never regenerates the week already in progress by accident.
    /// </summary>
    public async Task<Result<WeekGenerationPreview>> PreviewAsync(
        int businessId, DateOnly? date = null, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Result<WeekGenerationPreview>.Failure(
                "week_generation_invalid_business", "No pudimos identificar el negocio.");

        if (!TryResolveWeek(date, out var weekStart, out var weekEnd))
            return Result<WeekGenerationPreview>.Failure(
                "week_generation_week_out_of_range", "Esa semana queda fuera del calendario. Elegí otra fecha.");

        var snapshot = await repository.GetWeekSnapshotAsync(businessId, weekStart, weekEnd, ct);
        if (snapshot.BusinessId != businessId)
            return Result<WeekGenerationPreview>.Failure(
                "week_generation_snapshot_mismatch", "No pudimos preparar la semana. Volvé a intentar.");

        var sessions = ProjectWeek(snapshot, weekStart, weekEnd).Select(ToPreview).ToArray();

        return Result<WeekGenerationPreview>.Success(new WeekGenerationPreview(weekStart, weekEnd, sessions));
    }

    /// <summary>
    /// Confirms the week that contains <paramref name="date"/>, or the Monday of the next week when it is omitted.
    /// The snapshot is read once inside the transaction, projected exactly like the preview, written by the
    /// repository and committed as a single unit. A week that already ended is rejected: regenerating it would only
    /// add classes to the past.
    /// </summary>
    public async Task<Result<WeekGenerationConfirmation>> ConfirmAsync(
        int businessId, DateOnly? date = null, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Result<WeekGenerationConfirmation>.Failure(
                "week_generation_invalid_business", "No pudimos identificar el negocio.");

        if (!TryResolveWeek(date, out var weekStart, out var weekEnd))
            return Result<WeekGenerationConfirmation>.Failure(
                "week_generation_week_out_of_range", "Esa semana queda fuera del calendario. Elegí otra fecha.");

        // One time read for the whole operation: the week bounds, the past check and plan.Now must agree.
        var now = clock.Now;
        if (weekEnd < DateOnly.FromDateTime(now))
            return Result<WeekGenerationConfirmation>.Failure(
                "week_generation_week_in_the_past", "No se puede generar una semana que ya terminó.");

        // The transaction owns the commit; returning without committing disposes it and rolls back, so a rejected
        // snapshot never leaves a half written week.
        await using var tx = await unitOfWork.BeginAsync(ct: ct);

        var snapshot = await repository.GetWeekSnapshotAsync(businessId, weekStart, weekEnd, ct);
        if (snapshot.BusinessId != businessId)
            return Result<WeekGenerationConfirmation>.Failure(
                "week_generation_snapshot_mismatch", "No pudimos preparar la semana. Volvé a intentar.");

        var projections = ProjectWeek(snapshot, weekStart, weekEnd);
        var sessions = new List<WeekSessionPlan>(projections.Count);
        var pendingRenewalStudents = 0;
        foreach (var projection in projections)
        {
            // An already booked enrollment is not annotated again, and a cancelled one is not resurrected (D-15).
            var students = projection.Students
                .Where(student => !student.AlreadyBooked && !student.Cancelled)
                .Select(student => new WeekStudentToAnnotate(student.EnrollmentId, student.ClientId))
                .ToArray();
            pendingRenewalStudents += projection.Students.Count(student =>
                !student.AlreadyBooked && !student.Cancelled &&
                student.State == WeekFixedStudentState.PendingRenewal);

            sessions.Add(new WeekSessionPlan(
                projection.GridClass.GridClassId,
                projection.GridClass.ServiceId,
                projection.GridClass.WorkerId,
                projection.Date,
                projection.GridClass.StartTime,
                projection.GridClass.DurationMinutes,
                projection.GridClass.MaxCapacity,
                projection.Existing?.AppointmentId,
                students));
        }

        var plan = new WeekGenerationPlan(
            businessId,
            weekStart,
            weekEnd,
            now,
            sessions,
            sessions.Count(session => session.AppointmentId is not null),
            pendingRenewalStudents);

        var outcome = await repository.ConfirmWeekAsync(plan, ct);
        await tx.CommitAsync(ct);

        return Result<WeekGenerationConfirmation>.Success(new WeekGenerationConfirmation(
            weekStart,
            weekEnd,
            outcome.CreatedSessions,
            outcome.CreatedFixedBookings,
            plan.ExistingSessionCount,
            plan.PendingRenewalStudentCount));
    }

    /// <summary>
    /// The week that contains <paramref name="date"/>, or the Monday of the next week when it is omitted. The week is
    /// only usable when all seven days exist: the incomplete last week of the calendar would need Saturday and Sunday
    /// that do not exist, and generating it would duplicate sessions on 9999-12-31.
    /// </summary>
    private bool TryResolveWeek(DateOnly? date, out DateOnly weekStart, out DateOnly weekEnd)
    {
        DateOnly selected;
        if (date is { } chosen)
        {
            selected = chosen;
        }
        else if (WeekCalendar.NextMonday(clock.Today) is { } nextMonday)
        {
            selected = nextMonday;
        }
        else
        {
            weekStart = default;
            weekEnd = default;
            return false;
        }

        return WeekCalendar.TryGetWeek(selected, out weekStart, out weekEnd);
    }

    /// <summary>
    /// Projects every active grid class of the week exactly once, so the preview and the confirm flow cannot diverge.
    /// A grid class holds one session per date, but history can hold more than one when its start time changed
    /// (T17 owns that flow). Resolution is deterministic: prefer the row that still matches the grid's current start
    /// time, then the earliest appointment, instead of picking an arbitrary group member.
    /// </summary>
    private static IReadOnlyList<SessionProjection> ProjectWeek(
        WeekGenerationSnapshot snapshot, DateOnly weekStart, DateOnly weekEnd)
    {
        var existingSessions = snapshot.Sessions
            .Where(session => session.Date >= weekStart && session.Date <= weekEnd)
            .GroupBy(session => (session.GridClassId, session.Date))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(session => session.StartTime).ThenBy(session => session.AppointmentId).ToArray());

        var projections = snapshot.GridClasses
            .Select(gridClass =>
            {
                var sessionDate = WeekCalendar.DateInWeek(weekStart, gridClass.DayOfWeek);
                existingSessions.TryGetValue((gridClass.GridClassId, sessionDate), out var candidates);
                var existing = ResolveExistingSession(candidates, gridClass.StartTime);
                return ProjectSession(gridClass, sessionDate, existing, snapshot.FixedAssignments);
            })
            .OrderBy(projection => projection.Date)
            .ThenBy(projection => projection.GridClass.StartTime)
            .ThenBy(projection => projection.GridClass.GridClassId)
            .ToArray();

        return projections;
    }

    private static WeekSessionRow? ResolveExistingSession(WeekSessionRow[]? candidates, TimeOnly gridStartTime)
    {
        if (candidates is null || candidates.Length == 0)
            return null;

        // The array is already ordered by start time then appointment id, so the fallback is the earliest session.
        return candidates.FirstOrDefault(session => session.StartTime == gridStartTime) ?? candidates[0];
    }

    private static SessionProjection ProjectSession(
        WeekGridClassRow gridClass,
        DateOnly sessionDate,
        WeekSessionRow? existing,
        IReadOnlyList<WeekFixedAssignmentRow> fixedAssignments)
    {
        var bookings = existing?.Bookings ?? [];
        var students = fixedAssignments
            .Where(assignment => assignment.GridClassId == gridClass.GridClassId)
            .Where(assignment => assignment.IsValidOn(sessionDate))
            .Where(assignment => assignment.EnrollmentStatus is EnrollmentStatus.PendingPayment or EnrollmentStatus.Active)
            .OrderBy(assignment => assignment.EnrollmentId)
            // A repeated assignment row would otherwise list the same student twice in one dated class.
            .DistinctBy(assignment => assignment.EnrollmentId)
            .Select(assignment => ProjectStudent(assignment, sessionDate, bookings))
            .ToArray();

        return new SessionProjection(gridClass, sessionDate, existing, students);
    }

    private static SessionStudent ProjectStudent(
        WeekFixedAssignmentRow assignment,
        DateOnly sessionDate,
        IReadOnlyList<WeekSessionBookingRow> bookings)
    {
        // A cancelled fixed booking stays visible in the preview; it is only replaced when an active booking for the
        // same enrollment already exists. The confirm flow reads the same flags to decide what it must not write.
        var active = bookings.FirstOrDefault(booking =>
            booking.EnrollmentId == assignment.EnrollmentId && booking.Status != BookingStatus.Cancelled);
        var cancelled = active is null && bookings.Any(booking =>
            booking.EnrollmentId == assignment.EnrollmentId && booking.Status == BookingStatus.Cancelled);

        return new SessionStudent(
            assignment.EnrollmentId,
            assignment.ClientId,
            assignment.ClientName,
            ResolveState(assignment, sessionDate),
            AlreadyBooked: active is not null,
            Cancelled: cancelled);
    }

    private static WeekFixedStudentState ResolveState(WeekFixedAssignmentRow assignment, DateOnly sessionDate)
    {
        if (assignment.EnrollmentStatus == EnrollmentStatus.PendingPayment)
            return WeekFixedStudentState.PendingPayment;

        // Active enrollment: it either covers the session date or keeps its place pending the owner's renewal (D-23).
        return MembershipCalendar.StateOn(assignment.Periods, sessionDate) == MembershipState.Current
            ? WeekFixedStudentState.Active
            : WeekFixedStudentState.PendingRenewal;
    }

    private static WeekSessionPreview ToPreview(SessionProjection projection) => new(
        projection.GridClass.GridClassId,
        projection.GridClass.ServiceId,
        projection.GridClass.WorkerId,
        projection.Date,
        projection.GridClass.StartTime,
        projection.Existing is null ? WeekSessionKind.New : WeekSessionKind.Existing,
        projection.Existing?.AppointmentId,
        projection.Existing?.Status,
        projection.Existing?.Status == AppointmentStatus.Suspended,
        projection.Existing?.Bookings.Any(booking => booking.Status == BookingStatus.Cancelled) ?? false,
        projection.Students
            .Select(student => new WeekFixedStudentPreview(
                student.EnrollmentId, student.ClientId, student.ClientName,
                student.State, student.AlreadyBooked, student.Cancelled))
            .ToArray());

    /// <summary>One fixed student of a projected session, with everything both flows need to classify it.</summary>
    private sealed record SessionStudent(
        int EnrollmentId,
        int ClientId,
        string ClientName,
        WeekFixedStudentState State,
        bool AlreadyBooked,
        bool Cancelled);

    /// <summary>One session of the week as resolved from the grid and the snapshot, before previewing or planning it.</summary>
    private sealed record SessionProjection(
        WeekGridClassRow GridClass,
        DateOnly Date,
        WeekSessionRow? Existing,
        IReadOnlyList<SessionStudent> Students);
}
