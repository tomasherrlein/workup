using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

public sealed class SqlOwnerWalkInRepository(DbSession session) : SqlRepositoryBase(session), IOwnerWalkInRepository
{
    public async Task<OwnerWalkInContext?> LockContextAsync(int businessId, int clientId, int serviceId,
        int workerId, CancellationToken ct = default)
    {
        var worker = await QuerySingleOrDefaultAsync(
            "SELECT Id FROM Workers WITH (UPDLOCK, HOLDLOCK) WHERE BusinessId = @businessId AND Id = @workerId AND IsActive = 1",
            r => new Marker(r.GetInt32(0)), p => p.AddValue("@businessId", businessId).AddValue("@workerId", workerId), ct);
        if (worker is null) return null;
        return await QuerySingleOrDefaultAsync(
            """
            SELECT b.AllowsWalkIns, s.DurationMinutes, s.MaxCapacity, s.Price
            FROM Businesses b WITH (HOLDLOCK)
            JOIN Clients c WITH (HOLDLOCK) ON c.BusinessId = b.Id AND c.Id = @clientId
            JOIN Services s WITH (HOLDLOCK) ON s.BusinessId = b.Id AND s.Id = @serviceId AND s.IsActive = 1
            WHERE b.Id = @businessId
            """,
            r => new OwnerWalkInContext(r.GetBoolean(0), true, true, true, r.GetInt32(1), r.GetInt32(2), r.GetDecimal(3)),
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId)
                .AddValue("@serviceId", serviceId), ct);
    }

    private sealed record Marker(int Id);

    public async Task<bool> HasOverlapAsync(int businessId, int workerId, DateTime start, DateTime end,
        CancellationToken ct = default) => await QuerySingleOrDefaultAsync(
            """
            SELECT TOP (1) a.Id FROM Appointments a WITH (HOLDLOCK)
            WHERE a.BusinessId = @businessId AND a.WorkerId = @workerId
              AND a.Status = @scheduled AND a.StartsAt < @end AND a.EndsAt > @start
              AND (a.Kind = @classSession OR (a.Kind = @individual AND
                   NOT EXISTS (SELECT 1 FROM Bookings b WHERE b.BusinessId = a.BusinessId
                               AND b.AppointmentId = a.Id)
                   OR EXISTS (SELECT 1 FROM Bookings b WHERE b.BusinessId = a.BusinessId
                              AND b.AppointmentId = a.Id AND b.Status <> @cancelled)))
            """, r => new Marker(r.GetInt32(0)), p => p.AddValue("@businessId", businessId)
                .AddValue("@workerId", workerId).AddValue("@scheduled", AppointmentStatus.Scheduled)
                .AddValue("@classSession", AppointmentKind.ClassSession).AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@cancelled", BookingStatus.Cancelled).AddDateTime2("@start", start)
                .AddDateTime2("@end", end), ct) is not null;

    public async Task<(int AppointmentId, int BookingId)> InsertAsync(OwnerWalkInEntry e, CancellationToken ct = default)
    {
        var appointmentId = await InsertAsync(
            """
            INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, StartsAt, EndsAt,
                                      MaxCapacity, Origin, Status, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, @start, @end,
                                       1, @origin, @status, @start)
            """, p => p.AddValue("@businessId", e.BusinessId).AddValue("@kind", AppointmentKind.Individual)
                .AddValue("@serviceId", e.ServiceId).AddValue("@workerId", e.WorkerId)
                .AddDateTime2("@start", e.StartsAt).AddDateTime2("@end", e.EndsAt)
                .AddValue("@origin", AppointmentOrigin.WalkIn).AddValue("@status", AppointmentStatus.Scheduled), ct);
        var bookingId = await InsertAsync(
            """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, Status, AgreedPrice,
                                  PriceReason, CreatedAt, AttendanceRecordedAt)
            OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @status, @price,
                                       @reason, @now, @now)
            """, p => p.AddValue("@businessId", e.BusinessId).AddValue("@appointmentId", appointmentId)
                .AddValue("@clientId", e.ClientId).AddValue("@status", BookingStatus.Attended)
                .AddValue("@price", e.AgreedPrice).AddValue("@reason", e.PriceReason)
                .AddDateTime2("@now", e.StartsAt), ct);
        if (e.CollectPayment)
        {
            var count = await ExecuteAsync(
                """
                INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, Amount, ListPrice,
                                      PriceReason, PaidAt, RecordedAt)
                SELECT b.BusinessId, b.ClientId, @concept, b.Id, @price, @listPrice, @reason, @now, @now
                FROM Bookings b JOIN Appointments a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
                WHERE b.BusinessId = @businessId AND b.Id = @bookingId AND a.Id = @appointmentId
                """, p => p.AddValue("@businessId", e.BusinessId).AddValue("@bookingId", bookingId)
                    .AddValue("@appointmentId", appointmentId).AddValue("@concept", PaymentConcept.WalkIn)
                    .AddValue("@price", e.AgreedPrice).AddValue("@listPrice", e.ListPrice)
                    .AddValue("@reason", e.PriceReason).AddDateTime2("@now", e.StartsAt), ct);
            if (count != 1) throw new InvalidOperationException("Walk-in payment was not inserted.");
        }
        if (!string.IsNullOrWhiteSpace(e.NextVisitNote))
        {
            var count = await ExecuteAsync(
                """
                INSERT INTO ClientNotes (ClientId, AppointmentId, Text, CreatedAt)
                SELECT c.Id, a.Id, @note, @now FROM Clients c
                JOIN Appointments a ON a.BusinessId = c.BusinessId AND a.Id = @appointmentId
                WHERE c.BusinessId = @businessId AND c.Id = @clientId
                """, p => p.AddValue("@businessId", e.BusinessId).AddValue("@clientId", e.ClientId)
                    .AddValue("@appointmentId", appointmentId).AddValue("@note", e.NextVisitNote)
                    .AddDateTime2("@now", e.StartsAt), ct);
            if (count != 1) throw new InvalidOperationException("Walk-in note was not inserted.");
        }
        return (appointmentId, bookingId);
    }
}
