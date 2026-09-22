using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

public sealed class SqlPublicBookingRepository(DbSession session) : SqlRepositoryBase(session), IPublicBookingRepository
{
    public Task<Worker?> LockActiveWorkerAsync(int businessId, int workerId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            """
            SELECT Id, BusinessId, FirstName, LastName, Phone, Cuil, IsActive
              FROM Workers WITH (UPDLOCK, ROWLOCK)
             WHERE Id = @workerId AND BusinessId = @businessId AND IsActive = 1
            """,
            MapWorker,
            p => p.AddValue("@workerId", workerId).AddValue("@businessId", businessId),
            ct);

    public Task<Service?> GetActiveIndividualServiceAsync(int businessId, int serviceId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            """
            SELECT Id, BusinessId, Name, Price, DurationMinutes, MaxCapacity, IsActive
              FROM Services
             WHERE Id = @serviceId AND BusinessId = @businessId AND IsActive = 1 AND MaxCapacity = 1
            """,
            MapService,
            p => p.AddValue("@serviceId", serviceId).AddValue("@businessId", businessId),
            ct);

    public Task<PublicBookingBusinessSettings?> GetBusinessSettingsAsync(int businessId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            "SELECT Name, ReminderLeadHours FROM Businesses WHERE Id = @businessId",
            reader => new PublicBookingBusinessSettings(reader.Get<string>("Name"), reader.Get<int>("ReminderLeadHours")),
            p => p.AddValue("@businessId", businessId),
            ct);

    public async Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly startDate, DateOnly endDate, CancellationToken ct = default)
    {
        var rangeStart = startDate.ToDateTime(TimeOnly.MinValue);
        var rangeEnd = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var schedules = await QueryAsync(
            """
            SELECT ws.Id, ws.WorkerId, ws.DayOfWeek, ws.StartTime, ws.EndTime
              FROM WorkerSchedules ws
              JOIN Workers w ON w.Id = ws.WorkerId
             WHERE w.Id = @workerId AND w.BusinessId = @businessId
             ORDER BY ws.DayOfWeek
            """,
            MapSchedule,
            p => p.AddValue("@workerId", workerId).AddValue("@businessId", businessId),
            ct);

        var breaks = await QueryAsync(
            """
            SELECT wb.Id, wb.WorkerId, wb.DayOfWeek, wb.StartTime, wb.EndTime
              FROM WorkerBreaks wb
              JOIN Workers w ON w.Id = wb.WorkerId
             WHERE w.Id = @workerId AND w.BusinessId = @businessId
             ORDER BY wb.DayOfWeek, wb.StartTime
            """,
            MapBreak,
            p => p.AddValue("@workerId", workerId).AddValue("@businessId", businessId),
            ct);

        var appointments = await QueryAsync(
            """
            SELECT a.Id, a.BusinessId, a.Kind, a.ServiceId, a.WorkerId, a.GridClassId,
                   a.StartsAt, a.EndsAt, a.MaxCapacity, a.Origin, a.Status, a.SuspensionReason,
                   a.SuspendedAt, a.CreatedAt
              FROM Appointments a
              JOIN Workers w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
             WHERE a.BusinessId = @businessId AND a.WorkerId = @workerId
               AND a.StartsAt < @rangeEnd AND a.EndsAt > @rangeStart
            """,
            MapAppointment,
            p => p.AddValue("@businessId", businessId).AddValue("@workerId", workerId)
                .AddValue("@rangeStart", rangeStart).AddValue("@rangeEnd", rangeEnd),
            ct);

        var bookings = await QueryAsync(
            """
            SELECT b.Id, b.BusinessId, b.AppointmentId, b.ClientId, b.EnrollmentId, b.Modality,
                   b.Status, b.AbsenceSource, b.CancelledBy, b.AgreedPrice, b.PriceReason,
                   b.CreatedAt, b.ConfirmedAt, b.CancelledAt, b.AttendanceRecordedAt
              FROM Bookings b
              JOIN Appointments a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
              JOIN Workers w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
             WHERE b.BusinessId = @businessId AND a.WorkerId = @workerId
               AND a.StartsAt < @rangeEnd AND a.EndsAt > @rangeStart
            """,
            MapBooking,
            p => p.AddValue("@businessId", businessId).AddValue("@workerId", workerId)
                .AddValue("@rangeStart", rangeStart).AddValue("@rangeEnd", rangeEnd),
            ct);

        return new BookingAvailabilitySnapshot(schedules, breaks, appointments, bookings);
    }

    public Task<Client?> GetClientByEmailAsync(int businessId, string normalizedEmail, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            "SELECT Id, BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            MapClient,
            p => p.AddValue("@businessId", businessId).AddValue("@email", normalizedEmail),
            ct);

    public async Task<int?> TryInsertClientAsync(Client client, CancellationToken ct = default)
    {
        try
        {
            return await InsertAsync(
                """
                INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt)
                OUTPUT INSERTED.Id
                VALUES (@businessId, @firstName, @lastName, @email, @phone, @healthNotes, @createdAt)
                """,
                p => p.AddValue("@businessId", client.BusinessId).AddValue("@firstName", client.FirstName)
                    .AddValue("@lastName", client.LastName).AddValue("@email", client.Email).AddValue("@phone", client.Phone)
                    .AddValue("@healthNotes", client.HealthNotes).AddValue("@createdAt", client.CreatedAt),
                ct);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_Clients_Email won a concurrent per-business insert. The caller aborts the whole booking transaction
            // with a generic retry response rather than revealing that this email belongs to an existing client.
            return null;
        }
    }

    public Task<int> InsertAppointmentAsync(Appointment appointment, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO Appointments
                (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status,
                 SuspensionReason, SuspendedAt, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES
                (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 1, @origin, @status,
                 NULL, NULL, @createdAt)
            """,
            p => p.AddValue("@businessId", appointment.BusinessId).AddValue("@kind", appointment.Kind)
                .AddValue("@serviceId", appointment.ServiceId).AddValue("@workerId", appointment.WorkerId)
                .AddValue("@startsAt", appointment.StartsAt).AddValue("@endsAt", appointment.EndsAt)
                .AddValue("@origin", appointment.Origin).AddValue("@status", appointment.Status)
                .AddValue("@createdAt", appointment.CreatedAt),
            ct);

    public Task<int> InsertBookingAsync(Booking booking, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO Bookings
                (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, AbsenceSource, CancelledBy,
                 AgreedPrice, PriceReason, CreatedAt, ConfirmedAt, CancelledAt, AttendanceRecordedAt)
            OUTPUT INSERTED.Id
            VALUES
                (@businessId, @appointmentId, @clientId, NULL, NULL, @status, NULL, NULL,
                 @agreedPrice, NULL, @createdAt, NULL, NULL, NULL)
            """,
            p => p.AddValue("@businessId", booking.BusinessId).AddValue("@appointmentId", booking.AppointmentId)
                .AddValue("@clientId", booking.ClientId).AddValue("@status", booking.Status)
                .AddValue("@agreedPrice", booking.AgreedPrice).AddValue("@createdAt", booking.CreatedAt),
            ct);

    public Task<int> InsertAppointmentLinkAsync(AppointmentLink link, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO AppointmentLinks (BookingId, TokenHash, ProtectedToken, ExpiresAt, RevokedAt, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@bookingId, @tokenHash, @protectedToken, @expiresAt, NULL, @createdAt)
            """,
            p => p.AddValue("@bookingId", link.BookingId).AddValue("@tokenHash", link.TokenHash)
                .AddValue("@protectedToken", link.ProtectedToken).AddValue("@expiresAt", link.ExpiresAt)
                .AddValue("@createdAt", link.CreatedAt),
            ct);

    public Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL)
            """,
            p => p.AddValue("@businessId", notification.BusinessId).AddValue("@type", notification.Type)
                .AddValue("@recipient", notification.Recipient).AddValue("@subject", notification.Subject)
                .AddValue("@bodyHtml", notification.BodyHtml).AddValue("@dedupKey", notification.DedupKey)
                .AddValue("@status", notification.Status).AddValue("@createdAt", notification.CreatedAt),
            ct);

    private static Worker MapWorker(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        FirstName = reader.Get<string>("FirstName"),
        LastName = reader.Get<string>("LastName"),
        Phone = reader.Get<string>("Phone"),
        Cuil = reader.Get<string>("Cuil"),
        IsActive = reader.Get<bool>("IsActive")
    };

    private static Service MapService(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        Name = reader.Get<string>("Name"),
        Price = reader.Get<decimal>("Price"),
        DurationMinutes = reader.Get<int>("DurationMinutes"),
        MaxCapacity = reader.Get<int>("MaxCapacity"),
        IsActive = reader.Get<bool>("IsActive")
    };

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

    private static Booking MapBooking(SqlDataReader reader) => new()
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

    private static Client MapClient(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        FirstName = reader.Get<string>("FirstName"),
        LastName = reader.Get<string>("LastName"),
        Email = reader.Get<string>("Email"),
        Phone = reader.GetNullableString("Phone"),
        HealthNotes = reader.GetNullableString("HealthNotes"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };
}
