using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

/// <summary>Tenant-scoped SQL projections for the owner's agenda and individual booking details.</summary>
public sealed class SqlOwnerAgendaRepository(DbSession session) : SqlRepositoryBase(session), IOwnerAgendaRepository
{
    public async Task<IReadOnlyList<OwnerAgendaWorkerOption>> ListWorkersAsync(
        int businessId, CancellationToken ct = default) =>
        await QueryAsync(
            """
            SELECT Id, CONCAT(FirstName, N' ', LastName) AS WorkerName
              FROM Workers
             WHERE BusinessId = @businessId
             ORDER BY LastName, FirstName, Id
            """,
            reader => new OwnerAgendaWorkerOption(reader.Get<int>("Id"), reader.Get<string>("WorkerName")),
            p => p.AddValue("@businessId", businessId),
            ct);

    public async Task<IReadOnlyList<OwnerAgendaRow>> ListAgendaAsync(
        int businessId,
        DateOnly rangeStart,
        DateOnly rangeEndExclusive,
        int? workerId,
        BookingStatus? bookingStatus,
        AppointmentOrigin? origin,
        CancellationToken ct = default)
    {
        var start = rangeStart.ToDateTime(TimeOnly.MinValue);
        var end = rangeEndExclusive.ToDateTime(TimeOnly.MinValue);
        return await QueryAsync(
            """
            SELECT a.Id AS AppointmentId, b.Id AS BookingId, a.StartsAt, a.EndsAt, a.WorkerId,
                   a.Kind, a.Status AS AppointmentStatus, a.Origin, b.Status AS BookingStatus,
                   s.Name AS ServiceName, CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName,
                   CASE WHEN c.Id IS NULL THEN NULL ELSE CONCAT(c.FirstName, N' ', c.LastName) END AS ClientName,
                   b.AgreedPrice
              FROM Appointments AS a
              JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
              JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
              LEFT JOIN Bookings AS b ON b.AppointmentId = a.Id AND b.BusinessId = a.BusinessId
              LEFT JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
             WHERE a.BusinessId = @businessId
               AND a.StartsAt >= @rangeStart AND a.StartsAt < @rangeEndExclusive
               AND (@workerId IS NULL OR a.WorkerId = @workerId)
               AND (@bookingStatus IS NULL OR b.Status = @bookingStatus)
               AND (@origin IS NULL OR a.Origin = @origin)
             ORDER BY a.StartsAt, w.LastName, w.FirstName, s.Name, a.Id, b.Id
            """,
            MapAgendaRow,
            p => p.AddValue("@businessId", businessId).AddDateTime2("@rangeStart", start)
                .AddDateTime2("@rangeEndExclusive", end).AddValue("@workerId", workerId)
                .AddValue("@bookingStatus", bookingStatus).AddValue("@origin", origin),
            ct);
    }

    public Task<OwnerAgendaBookingDetail?> GetIndividualBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            """
            SELECT b.Id AS BookingId, a.Id AS AppointmentId, a.StartsAt, a.EndsAt, a.WorkerId,
                   a.Kind AS AppointmentKind, a.MaxCapacity, a.Status AS AppointmentStatus, a.Origin,
                   b.Status AS BookingStatus, b.ConfirmedAt, b.CancelledAt, b.CancelledBy,
                   b.AttendanceRecordedAt, b.AbsenceSource,
                   s.Name AS ServiceName, CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName,
                   CONCAT(c.FirstName, N' ', c.LastName) AS ClientName, c.Email AS ClientEmail,
                   c.Phone AS ClientPhone, b.AgreedPrice
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
              JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
              JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
              JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.Kind = @individual AND a.MaxCapacity = 1
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
            """,
            MapBookingDetail,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId)
                .AddValue("@individual", AppointmentKind.Individual),
            ct);

    private static OwnerAgendaRow MapAgendaRow(SqlDataReader reader) => new(
        reader.Get<int>("AppointmentId"),
        reader.GetNullable<int>("BookingId"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("EndsAt"),
        reader.Get<int>("WorkerId"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetNullableEnum<AppointmentOrigin>("Origin"),
        reader.GetNullableEnum<BookingStatus>("BookingStatus"),
        reader.Get<string>("ServiceName"),
        reader.Get<string>("WorkerName"),
        reader.GetNullableString("ClientName"),
        reader.GetNullable<decimal>("AgreedPrice"));

    private static OwnerAgendaBookingDetail MapBookingDetail(SqlDataReader reader) => new(
        reader.Get<int>("BookingId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("EndsAt"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetEnum<AppointmentOrigin>("Origin"),
        reader.GetEnum<BookingStatus>("BookingStatus"),
        reader.Get<string>("ServiceName"),
        reader.Get<string>("WorkerName"),
        reader.Get<string>("ClientName"),
        reader.Get<string>("ClientEmail"),
        reader.GetNullableString("ClientPhone"),
        reader.GetNullable<decimal>("AgreedPrice"),
        reader.GetEnum<AppointmentKind>("AppointmentKind"),
        reader.Get<int>("MaxCapacity"),
        reader.GetNullable<DateTime>("ConfirmedAt"),
        reader.GetNullable<DateTime>("CancelledAt"),
        reader.GetNullableEnum<CancelledBy>("CancelledBy"),
        reader.GetNullable<DateTime>("AttendanceRecordedAt"),
        reader.GetNullableEnum<AbsenceSource>("AbsenceSource"),
        WorkerId: reader.Get<int>("WorkerId"));
}
