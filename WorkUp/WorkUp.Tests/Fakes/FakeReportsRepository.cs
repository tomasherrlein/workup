using WorkUp.BusinessLogic.Reports;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the reports port (RF-16, T19). It emulates the tenant scope and the inclusive windows the
/// SQL implementation would apply: a row of another business is invisible, payments are read by the day they were
/// paid, refunds by the day the money came back (<c>RefundedOn</c>, never the original payment day), expenses by the
/// day they were spent and attendance by the day of its session. Every list carries the business of the row, so a
/// test can place a foreign row and prove it is not counted.
/// <para>
/// Only the read port is modelled: the module writes nothing. <see cref="LoadCallCount"/> and
/// <see cref="LoadedRequests"/> prove one snapshot per period.
/// </para>
/// </summary>
public sealed class FakeReportsRepository : IReportsRepository
{
    public List<(int BusinessId, ReportPaymentRow Row)> Payments { get; } = [];

    public List<(int BusinessId, ReportRefundRow Row)> Refunds { get; } = [];

    public List<(int BusinessId, ReportExpenseRow Row)> Expenses { get; } = [];

    public List<(int BusinessId, ReportAttendanceRow Row)> Attendances { get; } = [];

    public int LoadCallCount { get; private set; }

    public List<int> LoadedBusinessIds { get; } = [];

    public List<ReportReadRequest> LoadedRequests { get; } = [];

    public Task<ReportSnapshot> LoadAsync(
        int businessId, ReportReadRequest request, CancellationToken ct = default)
    {
        LoadCallCount++;
        LoadedBusinessIds.Add(businessId);
        LoadedRequests.Add(request);

        IReadOnlyList<ReportPaymentRow> payments = Payments
            .Where(entry => entry.BusinessId == businessId)
            .Select(entry => entry.Row)
            .Where(row => row.PaidOn >= request.PeriodFrom && row.PaidOn <= request.PeriodTo)
            .ToArray();

        IReadOnlyList<ReportRefundRow> refunds = Refunds
            .Where(entry => entry.BusinessId == businessId)
            .Select(entry => entry.Row)
            .Where(row => row.RefundedOn >= request.PeriodFrom && row.RefundedOn <= request.PeriodTo)
            .ToArray();

        IReadOnlyList<ReportExpenseRow> expenses = Expenses
            .Where(entry => entry.BusinessId == businessId)
            .Select(entry => entry.Row)
            .Where(row => row.SpentOn >= request.PeriodFrom && row.SpentOn <= request.PeriodTo)
            .ToArray();

        IReadOnlyList<ReportAttendanceRow> attendances = Attendances
            .Where(entry => entry.BusinessId == businessId)
            .Select(entry => entry.Row)
            .Where(row => row.OccurredOn >= request.AttendanceLookbackFrom && row.OccurredOn <= request.PeriodTo)
            .ToArray();

        return Task.FromResult(new ReportSnapshot(payments, refunds, expenses, attendances));
    }
}
