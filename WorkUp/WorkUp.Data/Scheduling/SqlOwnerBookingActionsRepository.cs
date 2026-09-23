using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

/// <summary>Tenant-scoped, transaction-bound SQL for owner cancellation of individual reservations.</summary>
public sealed class SqlOwnerBookingActionsRepository(DbSession session)
    : SqlRepositoryBase(session), IOwnerBookingActionsRepository
{
    public Task<OwnerBookingCancellationState?> LockOwnerBookingForCancellationAsync(
        int businessId, int bookingId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            """
            SELECT b.Id AS BookingId, a.Id AS AppointmentId, a.StartsAt,
                   a.Kind AS AppointmentKind, a.Status AS AppointmentStatus, a.Origin, a.MaxCapacity,
                   b.Status AS BookingStatus, b.Modality, b.EnrollmentId, b.ConfirmedAt,
                   b.CancelledAt, b.CancelledBy, b.AttendanceRecordedAt, b.AbsenceSource
              FROM Bookings AS b WITH (UPDLOCK, ROWLOCK)
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
            """,
            MapCancellationState,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId),
            ct);

    public async Task<bool> CancelOwnerBookingAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            """
            UPDATE b
               SET Status = @cancelled, CancelledBy = @owner, CancelledAt = @now
              FROM Bookings AS b
              JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.Kind = @individual AND a.Origin = @reserved AND a.MaxCapacity = 1
               AND a.Status = @scheduled AND a.StartsAt > @now
               AND b.Modality IS NULL AND b.EnrollmentId IS NULL
               AND b.CancelledAt IS NULL AND b.CancelledBy IS NULL
               AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL
               AND ((b.Status = @pending AND b.ConfirmedAt IS NULL)
                 OR (b.Status = @confirmed AND b.ConfirmedAt IS NOT NULL))
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId)
                .AddValue("@cancelled", BookingStatus.Cancelled).AddValue("@owner", CancelledBy.Owner)
                .AddDateTime2("@now", now).AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@reserved", AppointmentOrigin.Reserved).AddValue("@scheduled", AppointmentStatus.Scheduled)
                .AddValue("@pending", BookingStatus.Pending).AddValue("@confirmed", BookingStatus.Confirmed),
            ct) == 1;

    private static OwnerBookingCancellationState MapCancellationState(SqlDataReader reader) => new(
        reader.Get<int>("BookingId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<DateTime>("StartsAt"),
        reader.GetEnum<AppointmentKind>("AppointmentKind"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetNullableEnum<AppointmentOrigin>("Origin"),
        reader.Get<int>("MaxCapacity"),
        reader.GetEnum<BookingStatus>("BookingStatus"),
        reader.GetNullableEnum<BookingModality>("Modality"),
        reader.GetNullable<int>("EnrollmentId"),
        reader.GetNullable<DateTime>("ConfirmedAt"),
        reader.GetNullable<DateTime>("CancelledAt"),
        reader.GetNullableEnum<CancelledBy>("CancelledBy"),
        reader.GetNullable<DateTime>("AttendanceRecordedAt"),
        reader.GetNullableEnum<AbsenceSource>("AbsenceSource"));
}
