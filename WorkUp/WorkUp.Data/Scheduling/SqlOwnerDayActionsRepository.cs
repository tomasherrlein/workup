using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

/// <summary>All operations use the shared unit-of-work session and a tenant-scoped booking lock.</summary>
public sealed class SqlOwnerDayActionsRepository(DbSession session) : SqlRepositoryBase(session), IOwnerDayActionsRepository
{
    public Task<OwnerDayBookingState?> LockAsync(int businessId, int bookingId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            """
            SELECT b.BusinessId, b.Id AS BookingId, b.ClientId, a.Id AS AppointmentId, a.StartsAt,
                   a.Kind, a.Origin, a.Status AS AppointmentStatus, a.MaxCapacity,
                   b.Status AS BookingStatus, b.Modality, b.EnrollmentId, b.ConfirmedAt,
                   b.CancelledAt, b.AttendanceRecordedAt, b.AbsenceSource, b.AgreedPrice,
                   b.PriceReason, s.Price AS ListPrice
              FROM Bookings b WITH (UPDLOCK, HOLDLOCK)
              JOIN Appointments a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
              JOIN Clients c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
              JOIN Services s ON s.Id = a.ServiceId AND s.BusinessId = b.BusinessId
              JOIN Workers w ON w.Id = a.WorkerId AND w.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
            """,
            r => new OwnerDayBookingState(r.Get<int>("BusinessId"), r.Get<int>("BookingId"), r.Get<int>("ClientId"),
                r.Get<int>("AppointmentId"), r.Get<DateTime>("StartsAt"), r.GetEnum<AppointmentKind>("Kind"),
                r.GetNullableEnum<AppointmentOrigin>("Origin"), r.GetEnum<AppointmentStatus>("AppointmentStatus"),
                r.Get<int>("MaxCapacity"), r.GetEnum<BookingStatus>("BookingStatus"),
                r.GetNullableEnum<BookingModality>("Modality"), r.GetNullable<int>("EnrollmentId"),
                r.GetNullable<DateTime>("ConfirmedAt"), r.GetNullable<DateTime>("CancelledAt"),
                r.GetNullable<DateTime>("AttendanceRecordedAt"), r.GetNullableEnum<AbsenceSource>("AbsenceSource"),
                r.GetNullable<decimal>("AgreedPrice"), r.GetNullableString("PriceReason"), r.Get<decimal>("ListPrice")),
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId), ct);

    public async Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default) =>
        await QuerySingleOrDefaultAsync(
            """
            SELECT TOP (1) p.Id FROM Payments p WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN PaymentAnnulments pa ON pa.PaymentId = p.Id
            WHERE p.BusinessId = @businessId AND p.BookingId = @bookingId AND pa.Id IS NULL
            """, r => new PaymentMarker(r.GetInt32(0)),
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId), ct) is not null;

    private sealed record PaymentMarker(int Id);

    public async Task<bool> SetStatusAsync(int businessId, int bookingId, BookingStatus status, DateTime now,
        CancellationToken ct = default) =>
        await ExecuteAsync(
            """
            UPDATE b SET Status = @status, AttendanceRecordedAt = @now, AbsenceSource = @absenceSource
              FROM Bookings b
              JOIN Appointments a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND b.Id = @bookingId
               AND a.Status = @scheduled AND a.Kind = @individual AND a.StartsAt <= @now
               AND b.Status IN (@pending, @confirmed) AND b.CancelledAt IS NULL
               AND b.AttendanceRecordedAt IS NULL AND b.AbsenceSource IS NULL
               AND ((b.Status = @pending AND b.ConfirmedAt IS NULL)
                 OR (b.Status = @confirmed AND b.ConfirmedAt IS NOT NULL))
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId)
                .AddValue("@status", status).AddDateTime2("@now", now)
                .AddValue("@absenceSource", status == BookingStatus.Absent ? AbsenceSource.Owner : (AbsenceSource?)null)
                .AddValue("@scheduled", AppointmentStatus.Scheduled).AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@pending", BookingStatus.Pending).AddValue("@confirmed", BookingStatus.Confirmed), ct) == 1;

    public async Task InsertPaymentAsync(OwnerDayPayment payment, CancellationToken ct = default) =>
        await ExecuteAsync(
            """
            INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt)
            VALUES (@businessId, @clientId, @concept, @bookingId, @amount, @listPrice, @priceReason, @now, @now)
            """,
            p => p.AddValue("@businessId", payment.BusinessId).AddValue("@clientId", payment.ClientId)
                .AddValue("@concept", payment.Concept).AddValue("@bookingId", payment.BookingId)
                .AddValue("@amount", payment.Amount).AddValue("@listPrice", payment.ListPrice)
                .AddValue("@priceReason", payment.PriceReason).AddDateTime2("@now", payment.PaidAt), ct);

    public async Task InsertNoteAsync(int businessId, int clientId, int appointmentId, string note, DateTime now,
        CancellationToken ct = default)
    {
        var inserted = await ExecuteAsync(
            """
            INSERT INTO ClientNotes (ClientId, AppointmentId, Text, CreatedAt)
            SELECT c.Id, a.Id, @note, @now
              FROM Clients c JOIN Appointments a ON a.BusinessId = c.BusinessId AND a.Id = @appointmentId
             WHERE c.BusinessId = @businessId AND c.Id = @clientId
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId)
                .AddValue("@appointmentId", appointmentId).AddValue("@note", note).AddDateTime2("@now", now), ct);
        if (inserted != 1)
            throw new InvalidOperationException("The tenant-scoped client note was not inserted.");
    }
}
