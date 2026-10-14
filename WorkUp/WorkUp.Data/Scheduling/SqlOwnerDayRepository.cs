using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

/// <summary>Tenant-scoped, read-only projections for one owner's local business day.</summary>
public sealed class SqlOwnerDayRepository(DbSession session) : SqlRepositoryBase(session), IOwnerDayRepository
{
    public async Task<IReadOnlyList<OwnerDayRow>> ListDayAsync(
        int businessId, DateOnly date, CancellationToken ct = default)
    {
        if (date == DateOnly.MaxValue)
            return [];

        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEndExclusive = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return await QueryAsync(
            """
            SELECT a.Id AS AppointmentId, a.StartsAt, a.EndsAt, a.WorkerId,
                   a.Kind, a.Status AS AppointmentStatus, a.Origin,
                   s.Name AS ServiceName, CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName,
                   b.Id AS BookingId,
                   CASE WHEN c.Id IS NULL THEN NULL ELSE CONCAT(c.FirstName, N' ', c.LastName) END AS ClientName,
                   b.Status AS BookingStatus, b.AgreedPrice,
                   CASE
                       WHEN b.Id IS NULL OR b.Status = @cancelled THEN @notApplicable
                       WHEN a.Status = @suspended THEN @notApplicable
                       -- Membership debt is enrollment-level (T15), never one charge per fixed class.
                       WHEN b.Modality = @fixed THEN @notApplicable
                       WHEN EXISTS
                       (
                           SELECT 1
                             FROM Payments AS p
                            WHERE p.BusinessId = a.BusinessId
                              AND p.BookingId = b.Id
                              AND p.Concept = CASE
                                  WHEN a.Kind = @individual AND a.Origin = @walkIn THEN @walkInConcept
                                  WHEN a.Kind = @individual THEN @individualConcept
                                  ELSE @dropInConcept
                              END
                              AND NOT EXISTS (SELECT 1 FROM PaymentAnnulments AS pa WHERE pa.PaymentId = p.Id)
                              AND p.Amount > COALESCE(
                                  (SELECT SUM(r.Amount) FROM Refunds AS r WHERE r.PaymentId = p.Id), 0)
                       ) THEN @paid
                       ELSE @pending
                   END AS PaymentState
              FROM Appointments AS a
              JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
              JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
              LEFT JOIN Bookings AS b ON b.AppointmentId = a.Id AND b.BusinessId = a.BusinessId
              LEFT JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
             WHERE a.BusinessId = @businessId
               AND a.StartsAt >= @dayStart AND a.StartsAt < @dayEndExclusive
             ORDER BY a.StartsAt, w.LastName, w.FirstName, s.Name, a.Id, b.Id
            """,
            MapDayRow,
            p => p.AddValue("@businessId", businessId)
                .AddDateTime2("@dayStart", dayStart)
                .AddDateTime2("@dayEndExclusive", dayEndExclusive)
                .AddValue("@cancelled", BookingStatus.Cancelled)
                .AddValue("@notApplicable", OwnerDayPaymentState.NotApplicable)
                .AddValue("@pending", OwnerDayPaymentState.Pending)
                .AddValue("@paid", OwnerDayPaymentState.Paid)
                .AddValue("@fixed", BookingModality.Fixed)
                .AddValue("@suspended", AppointmentStatus.Suspended)
                .AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@walkIn", AppointmentOrigin.WalkIn)
                .AddValue("@walkInConcept", PaymentConcept.WalkIn)
                .AddValue("@individualConcept", PaymentConcept.IndividualAppointment)
                .AddValue("@dropInConcept", PaymentConcept.DropInClass),
            ct);
    }

    public async Task<decimal> GetPaidTotalAsync(int businessId, DateOnly date, CancellationToken ct = default)
    {
        if (date == DateOnly.MaxValue)
            return 0m;

        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEndExclusive = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return await ScalarAsync<decimal>(
            """
            SELECT
                COALESCE(
                    (SELECT SUM(p.Amount)
                       FROM Payments AS p
                      WHERE p.BusinessId = @businessId
                        AND p.PaidAt >= @dayStart AND p.PaidAt < @dayEndExclusive
                        AND NOT EXISTS (SELECT 1 FROM PaymentAnnulments AS pa WHERE pa.PaymentId = p.Id)),
                    0)
                - COALESCE(
                    (SELECT SUM(r.Amount)
                       FROM Refunds AS r
                       JOIN Payments AS p ON p.Id = r.PaymentId AND p.BusinessId = @businessId
                      WHERE r.RefundedOn = @date
                        AND NOT EXISTS (SELECT 1 FROM PaymentAnnulments AS pa WHERE pa.PaymentId = p.Id)),
                    0)
            """,
            p => p.AddValue("@businessId", businessId)
                .AddDateTime2("@dayStart", dayStart)
                .AddDateTime2("@dayEndExclusive", dayEndExclusive)
                .AddValue("@date", date),
            ct);
    }

    private static OwnerDayRow MapDayRow(SqlDataReader reader) => new(
        reader.Get<int>("AppointmentId"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("EndsAt"),
        reader.Get<int>("WorkerId"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetNullableEnum<AppointmentOrigin>("Origin"),
        reader.Get<string>("ServiceName"),
        reader.Get<string>("WorkerName"),
        reader.GetNullable<int>("BookingId"),
        reader.GetNullableString("ClientName"),
        reader.GetNullableEnum<BookingStatus>("BookingStatus"),
        reader.GetEnum<OwnerDayPaymentState>("PaymentState"),
        reader.GetNullable<decimal>("AgreedPrice"));
}
