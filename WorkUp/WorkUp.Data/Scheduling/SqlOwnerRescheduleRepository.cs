using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

/// <summary>Transaction-bound SQL persistence for owner rescheduling of individual reservations.</summary>
public sealed class SqlOwnerRescheduleRepository(DbSession session)
    : SqlRepositoryBase(session), IOwnerRescheduleRepository
{
    public async Task<int?> GetWorkerIdAsync(int businessId, int bookingId, CancellationToken ct = default)
    {
        var workerId = await ScalarAsync<int>(
            """
            SELECT a.WorkerId
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId),
            ct);
        return workerId > 0 ? workerId : null;
    }

    public async Task<bool> LockWorkerAsync(int businessId, int workerId, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT TOP (1) Id
              FROM Workers WITH (UPDLOCK, ROWLOCK)
             WHERE Id = @workerId AND BusinessId = @businessId
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@workerId", workerId),
            ct) > 0;

    private const string SelectBookingSql =
        """
        SELECT b.BusinessId, b.Id AS BookingId, a.Id AS AppointmentId, a.WorkerId,
               a.Kind AS AppointmentKind, a.Status AS AppointmentStatus, a.Origin, a.MaxCapacity,
               b.Modality, b.EnrollmentId, b.Status AS BookingStatus,
               a.StartsAt, a.EndsAt, b.ConfirmedAt, b.CancelledAt, b.CancelledBy,
               b.AttendanceRecordedAt, b.AbsenceSource, s.DurationMinutes,
               bus.Name AS BusinessName, c.FirstName AS ClientFirstName, c.Email AS ClientEmail,
               s.Name AS ServiceName, CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName
          FROM Bookings AS b {0}
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
          JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
          JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
          JOIN Businesses AS bus ON bus.Id = b.BusinessId
          JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId AND b.Id = @bookingId
        """;

    private static readonly string LockBookingSql = string.Format(SelectBookingSql, "WITH (UPDLOCK, ROWLOCK)");
    private static readonly string ReadBookingSql = string.Format(SelectBookingSql, "");

    public Task<OwnerRescheduleBooking?> LockOwnerBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            LockBookingSql,
            MapBooking,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId),
            ct);

    public Task<OwnerRescheduleBooking?> GetOwnerBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            ReadBookingSql,
            MapBooking,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId),
            ct);

    public Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly date, CancellationToken ct = default) =>
        GetAvailabilitySnapshotAsync(businessId, workerId, date, date, ct);

    public async Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly startDate, DateOnly endDate, CancellationToken ct = default)
    {
        var rangeStart = startDate.ToDateTime(TimeOnly.MinValue);
        var rangeEnd = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var parameters = new Action<SqlParameterCollection>(p =>
            p.AddValue("@businessId", businessId).AddValue("@workerId", workerId)
                .AddDateTime2("@rangeStart", rangeStart).AddDateTime2("@rangeEnd", rangeEnd));

        var schedules = await QueryAsync(
            """
            SELECT ws.Id, ws.WorkerId, ws.DayOfWeek, ws.StartTime, ws.EndTime
              FROM WorkerSchedules AS ws
              JOIN Workers AS w ON w.Id = ws.WorkerId AND w.BusinessId = @businessId
             WHERE ws.WorkerId = @workerId
             ORDER BY ws.DayOfWeek
            """,
            MapSchedule,
            p => p.AddValue("@businessId", businessId).AddValue("@workerId", workerId),
            ct);

        var breaks = await QueryAsync(
            """
            SELECT wb.Id, wb.WorkerId, wb.DayOfWeek, wb.StartTime, wb.EndTime
              FROM WorkerBreaks AS wb
              JOIN Workers AS w ON w.Id = wb.WorkerId AND w.BusinessId = @businessId
             WHERE wb.WorkerId = @workerId
             ORDER BY wb.DayOfWeek, wb.StartTime
            """,
            MapBreak,
            p => p.AddValue("@businessId", businessId).AddValue("@workerId", workerId),
            ct);

        var appointments = await QueryAsync(
            """
            SELECT a.Id, a.BusinessId, a.Kind, a.ServiceId, a.WorkerId, a.GridClassId,
                   a.StartsAt, a.EndsAt, a.MaxCapacity, a.Origin, a.Status, a.SuspensionReason,
                   a.SuspendedAt, a.CreatedAt
              FROM Appointments AS a
              JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
             WHERE a.BusinessId = @businessId AND a.WorkerId = @workerId
               AND a.StartsAt < @rangeEnd AND a.EndsAt > @rangeStart
            """,
            MapAppointment,
            parameters,
            ct);

        var bookings = await QueryAsync(
            """
            SELECT b.Id, b.BusinessId, b.AppointmentId, b.ClientId, b.EnrollmentId, b.Modality,
                   b.Status, b.AbsenceSource, b.CancelledBy, b.AgreedPrice, b.PriceReason,
                   b.CreatedAt, b.ConfirmedAt, b.CancelledAt, b.AttendanceRecordedAt
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
              JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
             WHERE b.BusinessId = @businessId AND a.WorkerId = @workerId
               AND a.StartsAt < @rangeEnd AND a.EndsAt > @rangeStart
            """,
            MapBookingEntity,
            parameters,
            ct);

        return new BookingAvailabilitySnapshot(schedules, breaks, appointments, bookings);
    }

    public async Task<IReadOnlyList<OwnerRescheduleCurrentLink>> GetCurrentLinksAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default) =>
        await QueryAsync(
            """
            SELECT TOP (2) l.Id, l.ExpiresAt
              FROM AppointmentLinks AS l WITH (UPDLOCK, ROWLOCK)
              JOIN Bookings AS b ON b.Id = l.BookingId
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND l.RevokedAt IS NULL AND l.ExpiresAt > @now
             ORDER BY l.Id
            """,
            reader => new OwnerRescheduleCurrentLink(reader.Get<int>("Id"), reader.Get<DateTime>("ExpiresAt")),
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId)
                .AddValue("@individual", AppointmentKind.Individual).AddValue("@reserved", AppointmentOrigin.Reserved)
                .AddDateTime2("@now", now),
            ct);

    public async Task<bool> TryApplyRescheduleAsync(
        int businessId,
        OwnerRescheduleBooking booking,
        DateTime newStartsAt,
        DateTime newEndsAt,
        DateTime now,
        CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            UPDATE a
               SET StartsAt = @newStartsAt, EndsAt = @newEndsAt
              FROM Appointments AS a
              JOIN Bookings AS b ON b.AppointmentId = a.Id AND b.BusinessId = a.BusinessId
             WHERE a.BusinessId = @businessId AND a.Id = @appointmentId
               AND b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.WorkerId = @workerId AND a.StartsAt = @originalStartsAt AND a.EndsAt = @originalEndsAt
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1 AND a.Status = @scheduled
               AND a.StartsAt > @now AND @newStartsAt > @now AND @newEndsAt > @newStartsAt
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL
               AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL
               AND ((b.Status = @pending AND b.ConfirmedAt IS NULL)
                 OR (b.Status = @confirmed AND b.ConfirmedAt IS NOT NULL));
            IF @@ROWCOUNT <> 1
            BEGIN
                SELECT 0;
                RETURN;
            END;

            UPDATE b
               SET Status = @confirmed, ConfirmedAt = @now
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.BusinessId = @businessId AND a.Id = @appointmentId AND a.WorkerId = @workerId
               AND a.StartsAt = @newStartsAt AND a.EndsAt = @newEndsAt
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1 AND a.Status = @scheduled
               AND @originalStartsAt > @now AND a.StartsAt > @now
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL
               AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL
               AND ((b.Status = @pending AND b.ConfirmedAt IS NULL)
                 OR (b.Status = @confirmed AND b.ConfirmedAt IS NOT NULL));
            IF @@ROWCOUNT <> 1
            BEGIN
                SELECT 0;
                RETURN;
            END;
            SELECT 1;
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", booking.BookingId)
                .AddValue("@appointmentId", booking.AppointmentId).AddValue("@workerId", booking.WorkerId)
                .AddDateTime2("@originalStartsAt", booking.StartsAt).AddDateTime2("@originalEndsAt", booking.EndsAt)
                .AddDateTime2("@newStartsAt", newStartsAt).AddDateTime2("@newEndsAt", newEndsAt)
                .AddDateTime2("@now", now).AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@reserved", AppointmentOrigin.Reserved).AddValue("@scheduled", AppointmentStatus.Scheduled)
                .AddValue("@pending", BookingStatus.Pending).AddValue("@confirmed", BookingStatus.Confirmed),
            ct) == 1;

    public async Task<bool> UpdateCurrentLinkExpiryAsync(
        int businessId,
        int bookingId,
        int linkId,
        DateTime expectedExpiresAt,
        DateTime newExpiresAt,
        DateTime now,
        CancellationToken ct = default) =>
        await ExecuteAsync(
            """
            UPDATE l
               SET ExpiresAt = @newExpiresAt
              FROM AppointmentLinks AS l
              JOIN Bookings AS b ON b.Id = l.BookingId
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId AND l.Id = @linkId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND l.RevokedAt IS NULL AND l.ExpiresAt = @expectedExpiresAt AND l.ExpiresAt > @now
               AND @newExpiresAt > @now
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId).AddValue("@linkId", linkId)
                .AddDateTime2("@expectedExpiresAt", expectedExpiresAt).AddDateTime2("@newExpiresAt", newExpiresAt)
                .AddDateTime2("@now", now).AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@reserved", AppointmentOrigin.Reserved),
            ct) == 1;

    public async Task InsertNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        await ExecuteAsync(
            """
            INSERT INTO Notifications
                (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt)
            VALUES
                (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL)
            """,
            p => p.AddValue("@businessId", notification.BusinessId).AddValue("@type", notification.Type)
                .AddValue("@recipient", notification.Recipient).AddValue("@subject", notification.Subject)
                .AddValue("@bodyHtml", notification.BodyHtml).AddValue("@dedupKey", notification.DedupKey)
                .AddValue("@status", notification.Status).AddDateTime2("@createdAt", notification.CreatedAt),
            ct);
    }

    private static OwnerRescheduleBooking MapBooking(SqlDataReader reader) => new(
        reader.Get<int>("BusinessId"),
        reader.Get<int>("BookingId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<int>("WorkerId"),
        reader.GetEnum<AppointmentKind>("AppointmentKind"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetNullableEnum<AppointmentOrigin>("Origin"),
        reader.Get<int>("MaxCapacity"),
        reader.GetNullableEnum<BookingModality>("Modality"),
        reader.GetNullable<int>("EnrollmentId"),
        reader.GetEnum<BookingStatus>("BookingStatus"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("EndsAt"),
        reader.GetNullable<DateTime>("ConfirmedAt"),
        reader.GetNullable<DateTime>("CancelledAt"),
        reader.GetNullableEnum<CancelledBy>("CancelledBy"),
        reader.GetNullable<DateTime>("AttendanceRecordedAt"),
        reader.GetNullableEnum<AbsenceSource>("AbsenceSource"),
        reader.Get<int>("DurationMinutes"),
        reader.Get<string>("BusinessName"),
        reader.Get<string>("ClientFirstName"),
        reader.Get<string>("ClientEmail"),
        reader.Get<string>("ServiceName"),
        reader.Get<string>("WorkerName"));

    private static WorkerSchedule MapSchedule(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        WorkerId = reader.Get<int>("WorkerId"),
        DayOfWeek = reader.GetEnum<DayOfWeek>("DayOfWeek"),
        StartTime = reader.Get<TimeOnly>("StartTime"),
        EndTime = reader.Get<TimeOnly>("EndTime")
    };

    private static WorkerBreak MapBreak(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        WorkerId = reader.Get<int>("WorkerId"),
        DayOfWeek = reader.GetEnum<DayOfWeek>("DayOfWeek"),
        StartTime = reader.Get<TimeOnly>("StartTime"),
        EndTime = reader.Get<TimeOnly>("EndTime")
    };

    private static Appointment MapAppointment(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        Kind = reader.GetEnum<AppointmentKind>("Kind"),
        ServiceId = reader.Get<int>("ServiceId"),
        WorkerId = reader.Get<int>("WorkerId"),
        GridClassId = reader.GetNullable<int>("GridClassId"),
        StartsAt = reader.Get<DateTime>("StartsAt"),
        EndsAt = reader.Get<DateTime>("EndsAt"),
        MaxCapacity = reader.Get<int>("MaxCapacity"),
        Origin = reader.GetNullableEnum<AppointmentOrigin>("Origin"),
        Status = reader.GetEnum<AppointmentStatus>("Status"),
        SuspensionReason = reader.GetNullableString("SuspensionReason"),
        SuspendedAt = reader.GetNullable<DateTime>("SuspendedAt"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };

    private static Booking MapBookingEntity(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        AppointmentId = reader.Get<int>("AppointmentId"),
        ClientId = reader.Get<int>("ClientId"),
        EnrollmentId = reader.GetNullable<int>("EnrollmentId"),
        Modality = reader.GetNullableEnum<BookingModality>("Modality"),
        Status = reader.GetEnum<BookingStatus>("Status"),
        AbsenceSource = reader.GetNullableEnum<AbsenceSource>("AbsenceSource"),
        CancelledBy = reader.GetNullableEnum<CancelledBy>("CancelledBy"),
        AgreedPrice = reader.GetNullable<decimal>("AgreedPrice"),
        PriceReason = reader.GetNullableString("PriceReason"),
        CreatedAt = reader.Get<DateTime>("CreatedAt"),
        ConfirmedAt = reader.GetNullable<DateTime>("ConfirmedAt"),
        CancelledAt = reader.GetNullable<DateTime>("CancelledAt"),
        AttendanceRecordedAt = reader.GetNullable<DateTime>("AttendanceRecordedAt")
    };
}
