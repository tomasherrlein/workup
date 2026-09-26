using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

/// <summary>
/// Tenant- and ficha-scoped, transaction-bound SQL for the client panel confirm/cancel actions (T21.2). Every read
/// and every guarded write filters by <c>BusinessId</c> and <c>ClientId</c>, both taken from the client session
/// cookie, so a booking of another ficha or another tenant matches no row and is reported as not found without
/// disclosing that it exists.
/// <para>
/// The lock order is worker row (<c>UPDLOCK, ROWLOCK</c>) then booking row, the same order owner rescheduling and
/// public booking use; the writes are single guarded statements whose predicates are re-checked against the row read
/// under the lock, so a state change that slipped in between read and write makes the statement return 0 instead of
/// overwriting it. Nothing here touches <c>Payments</c>.
/// </para>
/// </summary>
public sealed class SqlClientBookingActionsRepository(DbSession session)
    : SqlRepositoryBase(session), IClientBookingActionsRepository
{
    public async Task<int?> GetWorkerIdAsync(
        int businessId, int clientId, int bookingId, CancellationToken ct = default)
    {
        var workerId = await ScalarAsync<int>(
            """
            SELECT a.WorkerId
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.ClientId = @clientId AND b.Id = @bookingId
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId).AddValue("@bookingId", bookingId),
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

    public Task<ClientBookingActionState?> LockClientBookingAsync(
        int businessId, int clientId, int bookingId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            """
            SELECT b.BusinessId, b.Id AS BookingId, a.Id AS AppointmentId, a.WorkerId, a.StartsAt, a.EndsAt,
                   a.Kind AS AppointmentKind, a.Status AS AppointmentStatus, a.Origin, a.MaxCapacity,
                   b.Modality, b.EnrollmentId, b.Status AS BookingStatus, b.ConfirmedAt, b.CancelledAt,
                   b.CancelledBy, b.AttendanceRecordedAt, b.AbsenceSource
              FROM Bookings AS b WITH (UPDLOCK, ROWLOCK)
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.ClientId = @clientId AND b.Id = @bookingId
            """,
            MapState,
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId).AddValue("@bookingId", bookingId),
            ct);

    public async Task<bool> ConfirmClientBookingAsync(
        int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            """
            UPDATE b
               SET Status = @confirmed, ConfirmedAt = @now
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.ClientId = @clientId AND b.Id = @bookingId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND a.Status = @scheduled AND a.StartsAt > @now
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND b.Status = @pending AND b.ConfirmedAt IS NULL
               AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL
               AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL
            """,
            p =>
            {
                ActionParameters(p, businessId, clientId, bookingId, now);
                p.AddValue("@confirmed", BookingStatus.Confirmed).AddValue("@pending", BookingStatus.Pending);
            },
            ct) == 1;

    public async Task<bool> CancelClientBookingAsync(
        int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            """
            UPDATE b
               SET Status = @cancelled, CancelledBy = @client, CancelledAt = @now
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.ClientId = @clientId AND b.Id = @bookingId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND a.Status = @scheduled AND a.StartsAt > @now
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL
               AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL
               AND ((b.Status = @pending AND b.ConfirmedAt IS NULL)
                 OR (b.Status = @confirmed AND b.ConfirmedAt IS NOT NULL))
            """,
            p =>
            {
                ActionParameters(p, businessId, clientId, bookingId, now);
                p.AddValue("@cancelled", BookingStatus.Cancelled).AddValue("@client", CancelledBy.Client)
                    .AddValue("@pending", BookingStatus.Pending).AddValue("@confirmed", BookingStatus.Confirmed);
            },
            ct) == 1;

    private static void ActionParameters(
        SqlParameterCollection parameters, int businessId, int clientId, int bookingId, DateTime now) =>
        parameters.AddValue("@businessId", businessId).AddValue("@clientId", clientId).AddValue("@bookingId", bookingId)
            .AddValue("@individual", AppointmentKind.Individual).AddValue("@reserved", AppointmentOrigin.Reserved)
            .AddValue("@scheduled", AppointmentStatus.Scheduled).AddDateTime2("@now", now);

    private static ClientBookingActionState MapState(SqlDataReader reader) => new(
        reader.Get<int>("BusinessId"),
        reader.Get<int>("BookingId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<int>("WorkerId"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("EndsAt"),
        reader.GetEnum<AppointmentKind>("AppointmentKind"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetNullableEnum<AppointmentOrigin>("Origin"),
        reader.Get<int>("MaxCapacity"),
        reader.GetNullableEnum<BookingModality>("Modality"),
        reader.GetNullable<int>("EnrollmentId"),
        reader.GetEnum<BookingStatus>("BookingStatus"),
        reader.GetNullable<DateTime>("ConfirmedAt"),
        reader.GetNullable<DateTime>("CancelledAt"),
        reader.GetNullableEnum<CancelledBy>("CancelledBy"),
        reader.GetNullable<DateTime>("AttendanceRecordedAt"),
        reader.GetNullableEnum<AbsenceSource>("AbsenceSource"));
}
