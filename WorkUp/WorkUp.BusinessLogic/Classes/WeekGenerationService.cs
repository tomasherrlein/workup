using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Week generation preview (RF-06, D-16, D-23). It is read-only: the selected date is normalized to its
/// Monday-to-Sunday week, every active grid class becomes a new or already generated session, and each session lists
/// the fixed students whose enrollment still holds places (pending payment or active). "Pendiente de renovación" is
/// derived for the preview from an active enrollment whose periods do not cover the session date; it is never
/// persisted, never written back, and never announced as a recreation: cancelled bookings and suspended sessions
/// stay visible exactly as they are. Confirming the preview is a later unit of work (T13).
/// </summary>
public sealed class WeekGenerationService(IWeekGenerationRepository repository, IClock clock)
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

        // The week is only usable when all seven days exist: the incomplete last week of the calendar would need
        // Saturday and Sunday that do not exist, and generating it would duplicate sessions on 9999-12-31.
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
            return Result<WeekGenerationPreview>.Failure(
                "week_generation_week_out_of_range", "Esa semana queda fuera del calendario. Elegí otra fecha.");
        }

        if (!WeekCalendar.TryGetWeek(selected, out var weekStart, out var weekEnd))
            return Result<WeekGenerationPreview>.Failure(
                "week_generation_week_out_of_range", "Esa semana queda fuera del calendario. Elegí otra fecha.");

        var snapshot = await repository.GetWeekSnapshotAsync(businessId, weekStart, weekEnd, ct);
        if (snapshot.BusinessId != businessId)
            return Result<WeekGenerationPreview>.Failure(
                "week_generation_snapshot_mismatch", "No pudimos preparar la semana. Volvé a intentar.");

        // A grid class holds one session per date, but history can hold more than one when its start time changed
        // (T17 owns that flow). Resolve deterministically: prefer the row that still matches the grid's current start
        // time, then the earliest appointment, instead of picking an arbitrary group member.
        var existingSessions = snapshot.Sessions
            .Where(session => session.Date >= weekStart && session.Date <= weekEnd)
            .GroupBy(session => (session.GridClassId, session.Date))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(session => session.StartTime).ThenBy(session => session.AppointmentId).ToArray());

        var sessions = new List<WeekSessionPreview>();
        foreach (var gridClass in snapshot.GridClasses)
        {
            var sessionDate = WeekCalendar.DateInWeek(weekStart, gridClass.DayOfWeek);
            existingSessions.TryGetValue((gridClass.GridClassId, sessionDate), out var candidates);
            var existing = ResolveExistingSession(candidates, gridClass.StartTime);
            sessions.Add(BuildSession(gridClass, sessionDate, existing, snapshot.FixedAssignments));
        }

        var ordered = sessions
            .OrderBy(session => session.Date)
            .ThenBy(session => session.StartTime)
            .ThenBy(session => session.GridClassId)
            .ToArray();

        return Result<WeekGenerationPreview>.Success(new WeekGenerationPreview(weekStart, weekEnd, ordered));
    }

    private static WeekSessionRow? ResolveExistingSession(WeekSessionRow[]? candidates, TimeOnly gridStartTime)
    {
        if (candidates is null || candidates.Length == 0)
            return null;

        // The array is already ordered by start time then appointment id, so the fallback is the earliest session.
        return candidates.FirstOrDefault(session => session.StartTime == gridStartTime) ?? candidates[0];
    }

    private static WeekSessionPreview BuildSession(
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
            .Select(assignment => BuildStudent(assignment, sessionDate, bookings))
            .ToArray();

        return new WeekSessionPreview(
            gridClass.GridClassId,
            gridClass.ServiceId,
            gridClass.WorkerId,
            sessionDate,
            gridClass.StartTime,
            existing is null ? WeekSessionKind.New : WeekSessionKind.Existing,
            existing?.AppointmentId,
            existing?.Status,
            existing?.Status == AppointmentStatus.Suspended,
            bookings.Any(booking => booking.Status == BookingStatus.Cancelled),
            students);
    }

    private static WeekFixedStudentPreview BuildStudent(
        WeekFixedAssignmentRow assignment,
        DateOnly sessionDate,
        IReadOnlyList<WeekSessionBookingRow> bookings)
    {
        // A cancelled fixed booking stays visible; it is only replaced when an active booking for the same
        // enrollment already exists.
        var active = bookings.FirstOrDefault(booking =>
            booking.EnrollmentId == assignment.EnrollmentId && booking.Status != BookingStatus.Cancelled);
        var cancelled = active is null && bookings.Any(booking =>
            booking.EnrollmentId == assignment.EnrollmentId && booking.Status == BookingStatus.Cancelled);

        return new WeekFixedStudentPreview(
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
}
