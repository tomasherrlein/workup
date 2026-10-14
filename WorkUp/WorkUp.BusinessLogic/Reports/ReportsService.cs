using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Reports;

/// <summary>
/// The business report (RF-16, D-20): the result and the indicators of one period, with an optional comparison. It
/// reads one snapshot per period and writes nothing, so asking for a report never changes a payment, a refund, an
/// expense or an attendance.
/// <para>
/// <b>The result is money of the period.</b> Non-annulled payments by the day they were paid, minus the manual
/// refunds by the day the money came back (<c>RefundedOn</c>, never the original payment day), minus the expenses by
/// the day they were spent. An annulled payment is a correction and never integrates the result, and this service
/// applies that exclusion itself instead of trusting the read to have filtered it.
/// </para>
/// <para>
/// <b>Revenue is only allocated to a concrete session.</b> A collection of an individual turn, a walk-in or a
/// drop-in class belongs to the service of its session; membership money is unallocated and shown as the "Abonos"
/// bucket, never distributed across classes or workers, because an abono pays an enrollment and not a session. Any
/// other payment without a concrete session is kept as unallocated rather than silently dropped, so every peso of the
/// period is in exactly one bucket.
/// </para>
/// <para>
/// <b>Indicators follow assumption 7, which is explicitly provisional.</b> A recurring client attended at least twice
/// in the period; a client that stopped coming attended in the 90 days before the period and not once inside it; the
/// absences are counted from <see cref="BookingStatus.Absent"/>; worker production counts the attentions registered
/// in that worker's sessions and the non-annulled payments attached to sessions that were actually attended.
/// </para>
/// <para>
/// <b>A period that has not happened is not computed.</b> A period starting after today is refused; one that ends
/// after today is computed up to today and reported as partial, so no fact of the future is invented. The comparison
/// follows the same rules.
/// </para>
/// </summary>
public sealed class ReportsService(IReportsRepository repository, IClock clock)
{
    /// <summary>Stable codes of the report rules (RF-16). They are the caller's contract.</summary>
    public const string InvalidBusinessCode = "report_invalid_business";

    public const string InvalidPeriodCode = "report_invalid_period";

    public const string PeriodInFutureCode = "report_period_in_future";

    /// <summary>The lookback of the lapsed-client indicator: the 90 days before the period (assumption 7).</summary>
    public const int LapsedLookbackDays = 90;

    /// <summary>The visible name of the unallocated membership revenue bucket (RF-16). UI text, in Spanish.</summary>
    public const string MembershipRevenueLabel = "Abonos";

    /// <summary>
    /// Builds the report of the requested period and, when it was asked for, the same report for the comparison
    /// period. It reads one snapshot per period, so a report with a comparison is exactly two reads and never one per
    /// row. A period that starts after the business's today is refused, and one that ends after today is clipped to
    /// today and marked partial.
    /// </summary>
    public async Task<Result<ReportResult>> GetAsync(
        int businessId, ReportQuery? query, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Result<ReportResult>.Failure(InvalidBusinessCode, "No pudimos identificar el negocio.");

        if (query?.Period is null)
            return Result<ReportResult>.Failure(
                InvalidPeriodCode, "Indicá el período del reporte.", "period");

        var today = clock.Today;

        var window = ResolveWindow(query.Period, today, "period");
        if (!window.Succeeded)
            return Result<ReportResult>.Failure(window.Error!);

        Window? comparisonWindow = null;
        if (query.Comparison is not null)
        {
            var comparison = ResolveWindow(query.Comparison, today, "comparison");
            if (!comparison.Succeeded)
                return Result<ReportResult>.Failure(comparison.Error!);

            comparisonWindow = comparison.Value;
        }

        var period = await BuildPeriodReportAsync(businessId, window.Value, ct);
        var comparisonReport = comparisonWindow is null
            ? null
            : await BuildPeriodReportAsync(businessId, comparisonWindow, ct);

        return Result<ReportResult>.Success(new ReportResult(period, comparisonReport));
    }

    /// <summary>
    /// Turns one requested period into the effective one: it rejects a missing or inverted range, refuses a period
    /// that has not started, and clips a period that ends after today.
    /// </summary>
    private static Result<Window> ResolveWindow(ReportPeriod period, DateOnly today, string field)
    {
        if (period.From == default || period.To == default)
            return Result<Window>.Failure(InvalidPeriodCode, "Indicá las fechas del período.", field);

        if (period.From > period.To)
            return Result<Window>.Failure(
                InvalidPeriodCode, "La fecha desde no puede ser posterior a la fecha hasta.", field);

        if (period.From > today)
            return Result<Window>.Failure(
                PeriodInFutureCode, "El período todavía no empezó: no hay nada que calcular.", field);

        var isPartial = period.To > today;
        var effectiveTo = isPartial ? today : period.To;
        return Result<Window>.Success(new Window(period.From, period.To, period.From, effectiveTo, isPartial));
    }

    /// <summary>Computes one period from one tenant-scoped snapshot.</summary>
    private async Task<PeriodReport> BuildPeriodReportAsync(int businessId, Window window, CancellationToken ct)
    {
        var lookbackFrom = LookbackStart(window.EffectiveFrom);
        var snapshot = await repository.LoadAsync(
            businessId,
            new ReportReadRequest(window.EffectiveFrom, window.EffectiveTo, lookbackFrom),
            ct);

        // An annulled payment is a correction of the money: it never integrates the result (RF-16, D-20).
        var payments = snapshot.Payments.Where(row => !row.Annulled).ToArray();

        var paymentsTotal = payments.Sum(row => row.Amount);
        var refundsTotal = snapshot.Refunds.Sum(row => row.Amount);
        var expensesTotal = snapshot.Expenses.Sum(row => row.Amount);

        var revenueByService = AllocateRevenue(payments, out var membershipRevenue, out var unallocatedRevenue);

        var attended = snapshot.Attendances
            .Where(row => row.Status == BookingStatus.Attended &&
                          row.OccurredOn >= window.EffectiveFrom &&
                          row.OccurredOn <= window.EffectiveTo)
            .ToArray();

        var attentionsByClient = attended
            .GroupBy(row => row.ClientId)
            .ToDictionary(group => group.Key, group => group.Count());

        var recurringClientCount = attentionsByClient.Count(entry => entry.Value >= 2);

        // The 90 days before the period are the lookback window: the day exactly 90 days before is still inside, the
        // day before it is already outside.
        var attendedBefore = snapshot.Attendances
            .Where(row => row.Status == BookingStatus.Attended &&
                          row.OccurredOn >= lookbackFrom &&
                          row.OccurredOn < window.EffectiveFrom)
            .Select(row => row.ClientId)
            .ToHashSet();

        var lapsedClientCount = attendedBefore.Count(clientId => !attentionsByClient.ContainsKey(clientId));

        var absenceCount = snapshot.Attendances.Count(row =>
            row.Status == BookingStatus.Absent &&
            row.OccurredOn >= window.EffectiveFrom &&
            row.OccurredOn <= window.EffectiveTo);

        var workerProduction = BuildWorkerProduction(attended, payments);

        return new PeriodReport(
            window.RequestedFrom,
            window.RequestedTo,
            window.EffectiveFrom,
            window.EffectiveTo,
            window.IsPartial,
            paymentsTotal,
            refundsTotal,
            expensesTotal,
            paymentsTotal - refundsTotal - expensesTotal,
            revenueByService,
            membershipRevenue,
            unallocatedRevenue,
            BuildExpensesByType(snapshot.Expenses),
            workerProduction,
            recurringClientCount,
            lapsedClientCount,
            absenceCount);
    }

    /// <summary>
    /// Splits the payments of the period into revenue by concrete session, the unallocated membership bucket and any
    /// other unallocated money, so every peso is in exactly one bucket and a membership is never assigned to a class
    /// or an individual service.
    /// </summary>
    private static IReadOnlyList<ServiceRevenueLine> AllocateRevenue(
        ReportPaymentRow[] payments, out decimal membershipRevenue, out decimal unallocatedRevenue)
    {
        var lines = new Dictionary<int, (string Name, AppointmentKind Kind, decimal Amount)>();
        membershipRevenue = 0m;
        unallocatedRevenue = 0m;

        foreach (var payment in payments)
        {
            if (payment.Concept == PaymentConcept.Membership)
            {
                membershipRevenue += payment.Amount;
                continue;
            }

            // A service line needs a concrete session: the service and the kind of its appointment.
            if (payment.ServiceId is not { } serviceId || payment.SessionKind is not { } kind)
            {
                unallocatedRevenue += payment.Amount;
                continue;
            }

            if (lines.TryGetValue(serviceId, out var current))
                lines[serviceId] = (current.Name, current.Kind, current.Amount + payment.Amount);
            else
                lines[serviceId] = (payment.ServiceName ?? string.Empty, kind, payment.Amount);
        }

        return lines
            .OrderBy(entry => entry.Value.Kind)
            .ThenBy(entry => entry.Value.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key)
            .Select(entry => new ServiceRevenueLine(
                entry.Key, entry.Value.Name, entry.Value.Kind, entry.Value.Amount))
            .ToArray();
    }

    /// <summary>
    /// Production per worker: the attentions of the period and the money of the non-annulled payments attached to
    /// the sessions those attentions happened in. A payment of a session nobody attended is money of the period but
    /// is not production, and membership money is attached to no worker.
    /// </summary>
    private static IReadOnlyList<WorkerProductionLine> BuildWorkerProduction(
        ReportAttendanceRow[] attended, ReportPaymentRow[] payments)
    {
        var workers = new Dictionary<int, (string Name, int AttendedCount, decimal Collected)>();
        var attendedAppointments = new HashSet<int>();

        foreach (var row in attended)
        {
            attendedAppointments.Add(row.AppointmentId);
            if (workers.TryGetValue(row.WorkerId, out var current))
                workers[row.WorkerId] = (current.Name, current.AttendedCount + 1, current.Collected);
            else
                workers[row.WorkerId] = (row.WorkerName, 1, 0m);
        }

        foreach (var payment in payments)
        {
            if (payment.AppointmentId is not { } appointmentId || payment.WorkerId is not { } workerId)
                continue;

            if (!attendedAppointments.Contains(appointmentId))
                continue;

            if (workers.TryGetValue(workerId, out var current))
                workers[workerId] = (current.Name, current.AttendedCount, current.Collected + payment.Amount);
            else
                workers[workerId] = (string.Empty, 0, payment.Amount);
        }

        return workers
            .OrderBy(entry => entry.Value.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key)
            .Select(entry => new WorkerProductionLine(
                entry.Key, entry.Value.Name, entry.Value.AttendedCount, entry.Value.Collected))
            .ToArray();
    }

    /// <summary>Expenses of the period grouped by type, keeping the type name the row carried.</summary>
    private static IReadOnlyList<ExpenseTypeTotal> BuildExpensesByType(IReadOnlyList<ReportExpenseRow> expenses)
    {
        var byType = new Dictionary<int, (string Name, decimal Amount)>();
        foreach (var expense in expenses)
        {
            if (byType.TryGetValue(expense.ExpenseTypeId, out var current))
                byType[expense.ExpenseTypeId] = (current.Name, current.Amount + expense.Amount);
            else
                byType[expense.ExpenseTypeId] = (expense.ExpenseTypeName, expense.Amount);
        }

        return byType
            .OrderBy(entry => entry.Value.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key)
            .Select(entry => new ExpenseTypeTotal(entry.Key, entry.Value.Name, entry.Value.Amount))
            .ToArray();
    }

    /// <summary>The first day of the 90-day lookback, clamped so a very old period cannot underflow the calendar.</summary>
    private static DateOnly LookbackStart(DateOnly effectiveFrom) =>
        effectiveFrom.DayNumber < LapsedLookbackDays
            ? DateOnly.MinValue
            : effectiveFrom.AddDays(-LapsedLookbackDays);

    /// <summary>One requested period with its effective, possibly clipped, bounds.</summary>
    private sealed record Window(
        DateOnly RequestedFrom,
        DateOnly RequestedTo,
        DateOnly EffectiveFrom,
        DateOnly EffectiveTo,
        bool IsPartial);
}
