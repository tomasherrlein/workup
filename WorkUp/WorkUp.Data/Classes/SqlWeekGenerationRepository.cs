using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Classes;

/// <summary>
/// SQL Server persistence for week generation (RF-06, D-16, D-23). The snapshot is read-only and every statement is
/// scoped by <c>BusinessId</c>; the confirm flow joins the caller's ambient transaction, never commits or rolls back,
/// never modifies an existing session and creates only what the unique session identity
/// (UX_Appointments_GridSession) and the unique fixed annotation (UX_Bookings_FixedPerSession) allow.
/// </summary>
public sealed class SqlWeekGenerationRepository(DbSession session)
    : SqlRepositoryBase(session), IWeekGenerationRepository
{
    /// <summary>Active grid rows with the duration and capacity their service defines, never the other way round.</summary>
    private const string GridClassesSql = """
        SELECT g.Id AS GridClassId, g.ServiceId, g.WorkerId, g.DayOfWeek, g.StartTime,
               s.DurationMinutes, s.MaxCapacity
          FROM GridClasses AS g
          JOIN Services AS s ON s.Id = g.ServiceId AND s.BusinessId = g.BusinessId
         WHERE g.BusinessId = @businessId AND g.IsActive = 1
         ORDER BY g.DayOfWeek, g.StartTime, g.Id
        """;

    /// <summary>
    /// Sessions of the week, suspended ones included. The range is half open (<c>StartsAt &gt;= @weekStart</c> and
    /// <c>StartsAt &lt; @dayAfterWeekEnd</c>) so the filter never has to reason about the last second of a day and
    /// never uses <c>BETWEEN</c> on a date.
    /// </summary>
    private const string SessionsSql = """
        SELECT a.Id AS AppointmentId, a.GridClassId,
               CAST(a.StartsAt AS date) AS [Date], CAST(a.StartsAt AS time) AS StartTime,
               a.Status
          FROM Appointments AS a
         WHERE a.BusinessId = @businessId
           AND a.GridClassId IS NOT NULL
           AND a.StartsAt >= @weekStart
           AND a.StartsAt < @dayAfterWeekEnd
         ORDER BY a.StartsAt, a.Id
        """;

    /// <summary>
    /// Bookings of those same sessions, cancelled ones included so the preview keeps them visible. A drop-in booking
    /// has no enrollment; it is projected as enrollment 0, which never matches a real identity value, because the
    /// fixed annotation rows carry a non-nullable enrollment.
    /// </summary>
    private const string SessionBookingsSql = """
        SELECT b.Id AS BookingId, b.AppointmentId, COALESCE(b.EnrollmentId, 0) AS EnrollmentId,
               b.ClientId, CONCAT(c.FirstName, N' ', c.LastName) AS ClientName, b.Status
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
          JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND a.GridClassId IS NOT NULL
           AND a.StartsAt >= @weekStart
           AND a.StartsAt < @dayAfterWeekEnd
         ORDER BY b.AppointmentId, b.Id
        """;

    /// <summary>
    /// Fixed assignments of pending-payment or active enrollments whose validity overlaps the week. The grid class and
    /// the client are joined on the same business as a tenant guard on top of the enrollment filter.
    /// </summary>
    private const string FixedAssignmentsSql = """
        SELECT efc.EnrollmentId, efc.GridClassId, e.ClientId,
               CONCAT(c.FirstName, N' ', c.LastName) AS ClientName,
               e.Status AS EnrollmentStatus, efc.ValidFrom, efc.ValidTo
          FROM EnrollmentFixedClasses AS efc
          JOIN Enrollments AS e ON e.Id = efc.EnrollmentId AND e.BusinessId = @businessId
          JOIN GridClasses AS g ON g.Id = efc.GridClassId AND g.BusinessId = @businessId
          JOIN Clients AS c ON c.Id = e.ClientId AND c.BusinessId = @businessId
         WHERE e.BusinessId = @businessId
           AND e.Status IN (@pendingPayment, @active)
           AND efc.ValidFrom <= @weekEnd
           AND (efc.ValidTo IS NULL OR efc.ValidTo >= @weekStart)
         ORDER BY efc.EnrollmentId, efc.GridClassId, efc.ValidFrom, efc.Id
        """;

    /// <summary>
    /// Every period paid for those same enrollments, since membership coverage is resolved date by date by the caller.
    /// The <c>EXISTS</c> repeats the assignment filter so only enrollments of the week are read.
    /// </summary>
    private const string MembershipPeriodsSql = """
        SELECT mp.EnrollmentId, mp.CoverageStart, mp.ExpiresOn, mp.PaymentId, mp.CreatedAt
          FROM MembershipPeriods AS mp
          JOIN Enrollments AS e ON e.Id = mp.EnrollmentId AND e.BusinessId = @businessId
         WHERE e.BusinessId = @businessId
           AND e.Status IN (@pendingPayment, @active)
           AND EXISTS
               (
                   SELECT 1
                     FROM EnrollmentFixedClasses AS efc
                     JOIN GridClasses AS g ON g.Id = efc.GridClassId AND g.BusinessId = @businessId
                    WHERE efc.EnrollmentId = e.Id
                      AND efc.ValidFrom <= @weekEnd
                      AND (efc.ValidTo IS NULL OR efc.ValidTo >= @weekStart)
               )
         ORDER BY mp.EnrollmentId, mp.CoverageStart, mp.Id
        """;

    /// <summary>
    /// Inserts the dated session only when the unique session identity is still free, and reports it through
    /// <c>OUTPUT</c>. The <c>UPDLOCK, HOLDLOCK</c> probe over the filtered index takes the key range lock that
    /// serializes two concurrent confirms of the same week, so a competing insert waits instead of failing on
    /// UX_Appointments_GridSession.
    /// </summary>
    private const string InsertSessionSql = """
        INSERT INTO Appointments
                    (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity,
                     Origin, Status, CreatedAt)
        OUTPUT INSERTED.Id
        SELECT @businessId, @kind, @serviceId, @workerId, @gridClassId, @startsAt, @endsAt, @maxCapacity,
               NULL, @status, @createdAt
         WHERE NOT EXISTS
               (
                   SELECT 1
                     FROM Appointments WITH (UPDLOCK, HOLDLOCK)
                    WHERE GridClassId = @gridClassId AND StartsAt = @startsAt
               )
        """;

    /// <summary>
    /// Reuses the session a competing confirm created while this one was waiting for the lock. GridClassId identifies
    /// exactly one grid class, which belongs to exactly one business, so this pair can only resolve to the current
    /// tenant's own session.
    /// </summary>
    private const string SelectSessionIdBySlotSql =
        "SELECT Id FROM Appointments WHERE GridClassId = @gridClassId AND StartsAt = @startsAt";

    /// <summary>
    /// Inserts the fixed annotation only when neither unique index is already satisfied: the fixed annotation per
    /// enrollment (which keeps a cancelled booking from being resurrected) and the single active booking per client
    /// in the session (which would otherwise reject a drop-in client of the same session).
    /// </summary>
    private const string InsertFixedBookingSql = """
        INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt)
        OUTPUT INSERTED.Id
        SELECT @businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, @createdAt
         WHERE NOT EXISTS
               (
                   SELECT 1
                     FROM Bookings WITH (UPDLOCK, HOLDLOCK)
                    WHERE AppointmentId = @appointmentId AND EnrollmentId = @enrollmentId
               )
           AND NOT EXISTS
               (
                   SELECT 1
                     FROM Bookings WITH (UPDLOCK, HOLDLOCK)
                    WHERE AppointmentId = @appointmentId AND ClientId = @clientId AND Status <> @cancelled
               )
        """;

    public async Task<WeekGenerationSnapshot> GetWeekSnapshotAsync(
        int businessId, DateOnly weekStart, DateOnly weekEnd, CancellationToken ct = default)
    {
        // The week bounds are compared against DATETIME2 columns as instants and against DATE columns as dates, so the
        // half-open range keeps both comparisons exact.
        var weekStartAt = weekStart.ToDateTime(TimeOnly.MinValue);
        var dayAfterWeekEnd = weekEnd == DateOnly.MaxValue
            ? DateTime.MaxValue
            : weekEnd.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var gridClasses = await QueryAsync(
            GridClassesSql,
            MapGridClass,
            p => p.AddValue("@businessId", businessId),
            ct);

        var sessions = await QueryAsync(
            SessionsSql,
            MapSession,
            p => AddWeekRangeParameters(p, businessId, weekStartAt, dayAfterWeekEnd),
            ct);

        var bookings = await QueryAsync(
            SessionBookingsSql,
            MapBooking,
            p => AddWeekRangeParameters(p, businessId, weekStartAt, dayAfterWeekEnd),
            ct);

        Action<SqlParameterCollection> enrollmentWeek = p => p
            .AddValue("@businessId", businessId)
            .AddValue("@pendingPayment", EnrollmentStatus.PendingPayment)
            .AddValue("@active", EnrollmentStatus.Active)
            .AddValue("@weekStart", weekStart)
            .AddValue("@weekEnd", weekEnd);

        var assignments = await QueryAsync(FixedAssignmentsSql, MapAssignment, enrollmentWeek, ct);
        var periods = await QueryAsync(MembershipPeriodsSql, MapMembershipPeriod, enrollmentWeek, ct);

        var bookingsByAppointment = bookings
            .GroupBy(booking => booking.AppointmentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<WeekSessionBookingRow>)group.Select(booking => booking.Booking).ToArray());

        var periodsByEnrollment = periods
            .GroupBy(period => period.EnrollmentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MembershipPeriod>)group.ToArray());

        var sessionRows = sessions
            .Select(session => new WeekSessionRow(
                session.AppointmentId,
                session.GridClassId,
                session.Date,
                session.StartTime,
                session.Status,
                bookingsByAppointment.TryGetValue(session.AppointmentId, out var attached) ? attached : []))
            .ToArray();

        var fixedAssignments = assignments
            .Select(assignment => new WeekFixedAssignmentRow(
                assignment.EnrollmentId,
                assignment.GridClassId,
                assignment.ClientId,
                assignment.ClientName,
                assignment.EnrollmentStatus,
                assignment.ValidFrom,
                assignment.ValidTo,
                periodsByEnrollment.TryGetValue(assignment.EnrollmentId, out var attached) ? attached : []))
            .ToArray();

        // The snapshot echoes the requested tenant so the service can reject a leaked projection before writing.
        return new WeekGenerationSnapshot(businessId, gridClasses, sessionRows, fixedAssignments);
    }

    public async Task<WeekGenerationWriteOutcome> ConfirmWeekAsync(
        WeekGenerationPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var createdSessions = 0;
        var createdFixedBookings = 0;

        foreach (var session in plan.Sessions)
        {
            var appointmentId = session.AppointmentId;
            if (appointmentId is null)
            {
                var startsAt = session.Date.ToDateTime(session.StartTime);
                var endsAt = startsAt.AddMinutes(session.DurationMinutes);

                // ExecuteReader, not ExecuteScalar: OUTPUT is a result set, and an empty one means "already existed".
                var inserted = await QueryAsync(
                    InsertSessionSql,
                    reader => reader.GetInt32(0),
                    p => AddSessionParameters(p, plan, session, startsAt, endsAt),
                    ct);

                if (inserted.Count > 0)
                {
                    appointmentId = inserted[0];
                    createdSessions++;
                }
                else
                {
                    var reused = await ScalarAsync<int>(
                        SelectSessionIdBySlotSql,
                        p => p
                            .AddValue("@gridClassId", session.GridClassId)
                            .AddDateTime2("@startsAt", startsAt),
                        ct);

                    if (reused <= 0)
                        throw new InvalidOperationException(
                            "Week generation found the session slot locked but could not reuse it.");

                    appointmentId = reused;
                }
            }

            // An existing session is reused exactly as it is: its status, suspension reason and capacity are never
            // rewritten, and only the fixed annotations still missing are added.
            foreach (var student in session.Students)
            {
                var inserted = await QueryAsync(
                    InsertFixedBookingSql,
                    reader => reader.GetInt32(0),
                    p => AddFixedBookingParameters(p, plan.BusinessId, appointmentId.Value, student, plan.Now),
                    ct);

                if (inserted.Count > 0)
                    createdFixedBookings++;
            }
        }

        return new WeekGenerationWriteOutcome(createdSessions, createdFixedBookings);
    }

    private static void AddWeekRangeParameters(
        SqlParameterCollection parameters, int businessId, DateTime weekStart, DateTime dayAfterWeekEnd) =>
        parameters
            .AddValue("@businessId", businessId)
            .AddDateTime2("@weekStart", weekStart)
            .AddDateTime2("@dayAfterWeekEnd", dayAfterWeekEnd);

    private static void AddSessionParameters(
        SqlParameterCollection parameters, WeekGenerationPlan plan, WeekSessionPlan session,
        DateTime startsAt, DateTime endsAt) =>
        parameters
            .AddValue("@businessId", plan.BusinessId)
            .AddValue("@kind", AppointmentKind.ClassSession)
            .AddValue("@serviceId", session.ServiceId)
            .AddValue("@workerId", session.WorkerId)
            .AddValue("@gridClassId", session.GridClassId)
            .AddDateTime2("@startsAt", startsAt)
            .AddDateTime2("@endsAt", endsAt)
            .AddValue("@maxCapacity", session.MaxCapacity)
            .AddValue("@status", AppointmentStatus.Scheduled)
            .AddDateTime2("@createdAt", plan.Now);

    private static void AddFixedBookingParameters(
        SqlParameterCollection parameters, int businessId, int appointmentId, WeekStudentToAnnotate student,
        DateTime now) =>
        parameters
            .AddValue("@businessId", businessId)
            .AddValue("@appointmentId", appointmentId)
            .AddValue("@clientId", student.ClientId)
            .AddValue("@enrollmentId", student.EnrollmentId)
            .AddValue("@modality", BookingModality.Fixed)
            .AddValue("@status", BookingStatus.Pending)
            .AddValue("@cancelled", BookingStatus.Cancelled)
            .AddDateTime2("@createdAt", now);

    private static WeekGridClassRow MapGridClass(SqlDataReader reader) => new(
        reader.Get<int>("GridClassId"),
        reader.Get<int>("ServiceId"),
        reader.Get<int>("WorkerId"),
        reader.GetEnum<DayOfWeek>("DayOfWeek"),
        reader.Get<TimeOnly>("StartTime"),
        reader.Get<int>("DurationMinutes"),
        reader.Get<int>("MaxCapacity"));

    private static SessionRow MapSession(SqlDataReader reader) => new(
        reader.Get<int>("AppointmentId"),
        reader.Get<int>("GridClassId"),
        reader.Get<DateOnly>("Date"),
        reader.Get<TimeOnly>("StartTime"),
        reader.GetEnum<AppointmentStatus>("Status"));

    private static DatedBooking MapBooking(SqlDataReader reader) => new(
        reader.Get<int>("AppointmentId"),
        new WeekSessionBookingRow(
            reader.Get<int>("BookingId"),
            reader.Get<int>("EnrollmentId"),
            reader.Get<int>("ClientId"),
            reader.Get<string>("ClientName"),
            reader.GetEnum<BookingStatus>("Status")));

    private static AssignmentRow MapAssignment(SqlDataReader reader) => new(
        reader.Get<int>("EnrollmentId"),
        reader.Get<int>("GridClassId"),
        reader.Get<int>("ClientId"),
        reader.Get<string>("ClientName"),
        reader.GetEnum<EnrollmentStatus>("EnrollmentStatus"),
        reader.Get<DateOnly>("ValidFrom"),
        reader.GetNullable<DateOnly>("ValidTo"));

    private static MembershipPeriod MapMembershipPeriod(SqlDataReader reader) => new()
    {
        EnrollmentId = reader.Get<int>("EnrollmentId"),
        CoverageStart = reader.Get<DateOnly>("CoverageStart"),
        ExpiresOn = reader.Get<DateOnly>("ExpiresOn"),
        PaymentId = reader.Get<int>("PaymentId"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };

    private sealed record SessionRow(
        int AppointmentId, int GridClassId, DateOnly Date, TimeOnly StartTime, AppointmentStatus Status);

    private sealed record DatedBooking(int AppointmentId, WeekSessionBookingRow Booking);

    private sealed record AssignmentRow(
        int EnrollmentId,
        int GridClassId,
        int ClientId,
        string ClientName,
        EnrollmentStatus EnrollmentStatus,
        DateOnly ValidFrom,
        DateOnly? ValidTo);
}
