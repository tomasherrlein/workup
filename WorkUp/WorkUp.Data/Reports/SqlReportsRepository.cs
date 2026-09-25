using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Reports;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Reports;

/// <summary>
/// SQL Server persistence for the read-only business report (RF-16, D-20, T19). It owns the single statement the port
/// promises: one tenant-scoped batch whose four result sets are the payments of the period with their session already
/// resolved, the refunds whose money came back in the period, the expenses of the period and the attendance of the
/// period plus its 90-day lookback. There is no method that could write, so computing a report never changes a
/// payment, an annulment, a refund, an expense or a booking.
/// <para>
/// <b>One batch, four result sets, no read per row.</b> The four projections travel in one command and are read with
/// <see cref="SqlDataReader.NextResultAsync(System.Threading.CancellationToken)"/>, so a period of a hundred payments
/// is still one round trip and the session of a payment is a join and never a query. That is why this class does not
/// derive from <c>SqlRepositoryBase</c>: its helpers read one result set per command, and a reader per result set would
/// turn the snapshot back into a query per projection.
/// </para>
/// <para>
/// <b>The joins are tenant-bound and never fan out.</b> The annulment is a one-to-one left join —
/// <c>UQ_PaymentAnnulments_Payment</c> allows at most one — and the refunds are their own projection instead of a join
/// against the payments, so a payment with three partial refunds is one payment row and three refund rows and never a
/// payment repeated once per refund. Every joined row carries the business predicate, so a booking, an appointment, a
/// service, a worker, an enrollment or a refund of another business is invisible instead of merely filtered later.
/// </para>
/// <para>
/// <b>The dates are the business's days.</b> A payment is read by the day it was paid — the whole day, from its
/// midnight to the midnight after the last day, compared exclusively so a payment late that same day is still inside
/// —, a refund by <c>RefundedOn</c> (the day the money came back, never the day of the original payment), an expense
/// by <c>SpentOn</c> and a booking by the start of its session. The attendance window starts on the first day of the
/// lookback the caller sent and ends on the last day of the period, both inclusive.
/// </para>
/// <para>
/// <b>The exclusions are the caller's.</b> An annulled payment is returned with its flag rather than filtered here, and
/// a booking is returned with its status, because the policy that decides what integrates the result and what counts
/// as an attention lives in one place: the report. This read only brings the period's facts, never a fact of the
/// future, and it writes nothing.
/// </para>
/// </summary>
public sealed class SqlReportsRepository(DbSession session) : IReportsRepository
{
    /// <summary>
    /// The whole evidence of one period in one tenant-scoped batch. Every parameter the batch needs is declared once
    /// and shared by the statements that use it.
    /// <para>
    /// The payments set reads the period's payments by the day they were paid and resolves their session in the same
    /// statement: the booking, its appointment — the worker and the service — and the service name. A membership or
    /// any payment without a concrete session leaves those columns null, which is exactly what the report reads as
    /// unallocated money. The annulment is a one-to-one join, so the payment stays one row.
    /// </para>
    /// <para>
    /// The refunds set hangs off the business-scoped payment and reads <c>RefundedOn</c>, so a refund belongs to the
    /// day the money came back even when the payment was collected in another period. The expenses set reads
    /// <c>SpentOn</c> and joins the expense type tenant-bound, keeping the type name of a type later archived. The
    /// attendance set reads the bookings of the sessions of the lookback and the period by the start of the session
    /// with the client and the worker already resolved, and it hands every status to the caller instead of deciding
    /// what an attention is.
    /// </para>
    /// </summary>
    private const string LoadSql = """
        SELECT p.Id AS PaymentId, p.Concept, p.Amount, p.PaidAt,
               CONVERT(BIT, CASE WHEN an.Id IS NULL THEN 0 ELSE 1 END) AS Annulled,
               b.AppointmentId, ap.WorkerId, ap.ServiceId, s.Name AS ServiceName, ap.Kind AS SessionKind
          FROM Payments AS p
          LEFT JOIN PaymentAnnulments AS an ON an.PaymentId = p.Id
          LEFT JOIN Bookings AS b ON b.Id = p.BookingId AND b.BusinessId = p.BusinessId
          LEFT JOIN Appointments AS ap ON ap.Id = b.AppointmentId AND ap.BusinessId = p.BusinessId
          LEFT JOIN Services AS s ON s.Id = ap.ServiceId AND s.BusinessId = p.BusinessId
         WHERE p.BusinessId = @businessId
           AND p.PaidAt >= @periodFrom
           AND (@toExclusive IS NULL OR p.PaidAt < @toExclusive)
         ORDER BY p.PaidAt, p.Id;

        SELECT r.Id AS RefundId, r.PaymentId, r.Amount, r.RefundedOn
          FROM Refunds AS r
          JOIN Payments AS p ON p.Id = r.PaymentId AND p.BusinessId = @businessId
         WHERE r.RefundedOn >= @periodFromDate
           AND r.RefundedOn <= @periodToDate
         ORDER BY r.RefundedOn, r.Id;

        SELECT e.Id AS ExpenseId, e.ExpenseTypeId, t.Name AS ExpenseTypeName, e.Amount, e.SpentOn
          FROM Expenses AS e
          JOIN ExpenseTypes AS t ON t.Id = e.ExpenseTypeId AND t.BusinessId = e.BusinessId
         WHERE e.BusinessId = @businessId
           AND e.SpentOn >= @periodFromDate
           AND e.SpentOn <= @periodToDate
         ORDER BY e.SpentOn, e.Id;

        SELECT ap.Id AS AppointmentId, b.ClientId, ap.WorkerId,
               CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName,
               b.Status, ap.StartsAt
          FROM Bookings AS b
          JOIN Appointments AS ap ON ap.Id = b.AppointmentId AND ap.BusinessId = @businessId
          JOIN Workers AS w ON w.Id = ap.WorkerId AND w.BusinessId = @businessId
         WHERE b.BusinessId = @businessId
           AND ap.StartsAt >= @lookbackFrom
           AND (@toExclusive IS NULL OR ap.StartsAt < @toExclusive)
         ORDER BY ap.StartsAt, ap.Id, b.Id
        """;

    public async Task<ReportSnapshot> LoadAsync(
        int businessId, ReportReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await session.GetOpenConnectionAsync(ct);
        await using var command = new SqlCommand(LoadSql, connection, session.Transaction);
        command.Parameters
            .AddValue("@businessId", businessId)
            .AddValue("@periodFrom", request.PeriodFrom)
            .AddValue("@toExclusive", ExclusiveEnd(request.PeriodTo))
            .AddValue("@periodFromDate", request.PeriodFrom)
            .AddValue("@periodToDate", request.PeriodTo)
            .AddValue("@lookbackFrom", request.AttendanceLookbackFrom);

        // One reader walks the four result sets of the one batch: the whole snapshot is one round trip and no read
        // happens per payment, per refund, per expense or per booking.
        await using var reader = await command.ExecuteReaderAsync(ct);

        var payments = await ReadRowsAsync(reader, MapPayment, ct);
        await reader.NextResultAsync(ct);
        var refunds = await ReadRowsAsync(reader, MapRefund, ct);
        await reader.NextResultAsync(ct);
        var expenses = await ReadRowsAsync(reader, MapExpense, ct);
        await reader.NextResultAsync(ct);
        var attendances = await ReadRowsAsync(reader, MapAttendance, ct);

        return new ReportSnapshot(payments, refunds, expenses, attendances);
    }

    /// <summary>
    /// The midnight after the last day of the period, or <c>null</c> when there is no day after it:
    /// <see cref="DateOnly.MaxValue"/> is the last representable day, so it means "no upper bound" instead of an
    /// overflow, and the statement drops the ceiling while the tenant, the lower bound and the other filters stay.
    /// </summary>
    private static DateOnly? ExclusiveEnd(DateOnly lastDay) =>
        lastDay == DateOnly.MaxValue ? null : lastDay.AddDays(1);

    private static async Task<List<T>> ReadRowsAsync<T>(
        SqlDataReader reader, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }

    private static ReportPaymentRow MapPayment(SqlDataReader reader) => new(
        reader.Get<int>("PaymentId"),
        reader.GetEnum<PaymentConcept>("Concept"),
        reader.GetNullable<int>("AppointmentId"),
        reader.GetNullable<int>("WorkerId"),
        reader.GetNullable<int>("ServiceId"),
        reader.GetNullableString("ServiceName"),
        reader.GetNullableEnum<AppointmentKind>("SessionKind"),
        reader.Get<decimal>("Amount"),
        DateOnly.FromDateTime(reader.Get<DateTime>("PaidAt")),
        reader.Get<bool>("Annulled"));

    private static ReportRefundRow MapRefund(SqlDataReader reader) => new(
        reader.Get<int>("RefundId"),
        reader.Get<int>("PaymentId"),
        reader.Get<decimal>("Amount"),
        reader.Get<DateOnly>("RefundedOn"));

    private static ReportExpenseRow MapExpense(SqlDataReader reader) => new(
        reader.Get<int>("ExpenseId"),
        reader.Get<int>("ExpenseTypeId"),
        reader.Get<string>("ExpenseTypeName"),
        reader.Get<decimal>("Amount"),
        reader.Get<DateOnly>("SpentOn"));

    private static ReportAttendanceRow MapAttendance(SqlDataReader reader) => new(
        reader.Get<int>("AppointmentId"),
        reader.Get<int>("ClientId"),
        reader.Get<int>("WorkerId"),
        reader.Get<string>("WorkerName"),
        reader.GetEnum<BookingStatus>("Status"),
        DateOnly.FromDateTime(reader.Get<DateTime>("StartsAt")));
}
