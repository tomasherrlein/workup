namespace WorkUp.BusinessLogic.Reports;

/// <summary>
/// Persistence boundary of the business report (RF-16, T19). It is read-only on purpose — there is no method that
/// could write — so computing a report can never change a payment, a refund, an expense or an attendance. Every read
/// is scoped by <c>businessId</c>: a row of another business is invisible and indistinguishable from one that does
/// not exist.
/// <para>
/// One call returns the whole evidence of one period, so the SQL implementation is one statement per period with its
/// joins already resolved and never a query per payment or per row. The service asks for the effective period and the
/// start of the 90-day lookback, and the snapshot returns exactly the payments, refunds, expenses and attendance of
/// those windows.
/// </para>
/// </summary>
public interface IReportsRepository
{
    /// <summary>
    /// The snapshot of one period of the business: payments by the day they were paid — annulled ones included with
    /// their flag, so the report itself applies the exclusion — refunds by <c>RefundedOn</c>, expenses by
    /// <c>SpentOn</c>, and attendance from <see cref="ReportReadRequest.AttendanceLookbackFrom"/> through
    /// <see cref="ReportReadRequest.PeriodTo"/>. Each payment arrives with its session, worker and service resolved
    /// when it has a concrete session, which keeps the caller free of a read per row. It writes nothing.
    /// </summary>
    Task<ReportSnapshot> LoadAsync(
        int businessId, ReportReadRequest request, CancellationToken ct = default);
}
