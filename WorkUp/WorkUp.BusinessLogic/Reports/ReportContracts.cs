using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Reports;

/// <summary>
/// One inclusive calendar range of the report (RF-16). Both ends are days of the business time zone (assumption 12)
/// and are inclusive: the period 2026-03-01 to 2026-03-31 includes both days.
/// </summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To);

/// <summary>
/// What the owner asks for (RF-16): the period to report and, optionally, a second period to compare it with. The
/// comparison is optional because a report without one is already complete; when present it is computed with exactly
/// the same rules as the primary period and the same clipping to today, so the two numbers are comparable.
/// </summary>
public sealed record ReportQuery(ReportPeriod Period, ReportPeriod? Comparison = null);

/// <summary>
/// The evidence one period is computed from, in one tenant-scoped read (RF-16). <see cref="PeriodFrom"/> and
/// <see cref="PeriodTo"/> are the inclusive effective period — already clipped to today by the service — and
/// <see cref="AttendanceLookbackFrom"/> is the first day of the 90-day lookback the lapsed-client indicator reads
/// (assumption 7). The repository is asked for everything the period needs at once, so the SQL implementation is one
/// statement per period and never a query per row.
/// </summary>
public sealed record ReportReadRequest(DateOnly PeriodFrom, DateOnly PeriodTo, DateOnly AttendanceLookbackFrom);

/// <summary>
/// Everything one period is computed from, already scoped to the business: the non-annulled and annulled payments of
/// the period, the refunds whose money came back in the period, the expenses spent in the period and the attendance
/// of the period plus its lookback. It is a projection, not entities: each payment already carries the session it
/// pays for — its appointment, worker and service — so the service never resolves a row per payment. Reading it
/// writes nothing.
/// </summary>
public sealed record ReportSnapshot(
    IReadOnlyList<ReportPaymentRow> Payments,
    IReadOnlyList<ReportRefundRow> Refunds,
    IReadOnlyList<ReportExpenseRow> Expenses,
    IReadOnlyList<ReportAttendanceRow> Attendances);

/// <summary>
/// One payment of the period, read by the day it was paid (<see cref="PaidOn"/>, from <c>Payments.PaidAt</c>).
/// <see cref="Annulled"/> is present so the report can leave an annulled payment out itself instead of trusting the
/// read to have filtered it (RF-16, D-20). The session is resolved here — <see cref="AppointmentId"/>,
/// <see cref="WorkerId"/>, <see cref="ServiceId"/>, <see cref="ServiceName"/> and <see cref="SessionKind"/> — because
/// a membership or a payment without a concrete session leaves them null; that is what separates revenue by service
/// from the unallocated "Abonos" bucket, and it is the join the SQL implementation resolves in the same statement.
/// </summary>
public sealed record ReportPaymentRow(
    int PaymentId,
    PaymentConcept Concept,
    int? AppointmentId,
    int? WorkerId,
    int? ServiceId,
    string? ServiceName,
    AppointmentKind? SessionKind,
    decimal Amount,
    DateOnly PaidOn,
    bool Annulled);

/// <summary>
/// One manual refund whose money came back in the period (D-20). It is read by <see cref="RefundedOn"/> and carries
/// no payment date on purpose: a refund is subtracted on the day it was returned, never on the day of the original
/// payment, so there is no field a caller could mistakenly group it by. Since an annulment and a refund never coexist
/// on one payment (D-20), a refund always hangs from a payment that stands.
/// </summary>
public sealed record ReportRefundRow(int RefundId, int PaymentId, decimal Amount, DateOnly RefundedOn);

/// <summary>One expense of the period, read by the day it was spent (<see cref="SpentOn"/>, from <c>Expenses.SpentOn</c>).</summary>
public sealed record ReportExpenseRow(
    int ExpenseId, int ExpenseTypeId, string ExpenseTypeName, decimal Amount, DateOnly SpentOn);

/// <summary>
/// One booking of a session, read by the day of its session (<see cref="OccurredOn"/>). <see cref="Status"/> is the
/// client's state in that booking: an <c>Attended</c> row is an attention that feeds recurring clients, lapsed
/// clients and worker production, and an <c>Absent</c> row is the absence count (RF-16, assumption 7). The rows of a
/// class session share <see cref="AppointmentId"/>, so counting rows is counting attentions while the distinct
/// appointments are the sessions that got attended.
/// </summary>
public sealed record ReportAttendanceRow(
    int AppointmentId,
    int ClientId,
    int WorkerId,
    string WorkerName,
    BookingStatus Status,
    DateOnly OccurredOn);

/// <summary>
/// Revenue of one concrete service in the period. It only exists for a payment whose session is concrete: a class
/// session or an individual turn with its appointment. Membership money never reaches here — it is unallocated — and
/// <see cref="Kind"/> tells the class revenue from the individual one.
/// </summary>
public sealed record ServiceRevenueLine(int ServiceId, string ServiceName, AppointmentKind Kind, decimal Amount);

/// <summary>Expenses of one type in the period (RF-16), with the type name kept even when the type was later archived.</summary>
public sealed record ExpenseTypeTotal(int ExpenseTypeId, string ExpenseTypeName, decimal Amount);

/// <summary>
/// Production of one worker in the period (assumption 7, provisional definition): <see cref="AttendedCount"/> is how
/// many attentions were registered in that worker's sessions and <see cref="CollectedAmount"/> is the money of the
/// non-annulled payments attached to sessions that were actually attended. Membership money is never assigned here,
/// because an abono pays an enrollment and not a session of one worker.
/// </summary>
public sealed record WorkerProductionLine(int WorkerId, string WorkerName, int AttendedCount, decimal CollectedAmount);

/// <summary>
/// The result and the indicators of one period (RF-16), computed from a single snapshot. <see cref="RequestedFrom"/>
/// and <see cref="RequestedTo"/> are what the owner asked for; <see cref="EffectiveFrom"/> and
/// <see cref="EffectiveTo"/> are what was actually computed, and <see cref="IsPartial"/> is true when the period ran
/// past today and was clipped to it, so the screen can say the period is still open.
/// <para>
/// <see cref="MembershipRevenue"/> is the unallocated "Abonos" bucket and <see cref="UnallocatedRevenue"/> keeps any
/// other payment without a concrete session, so every peso of <see cref="PaymentsTotal"/> is in exactly one bucket:
/// the service lines plus the membership bucket plus the unallocated one.
/// </para>
/// </summary>
public sealed record PeriodReport(
    DateOnly RequestedFrom,
    DateOnly RequestedTo,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    bool IsPartial,
    decimal PaymentsTotal,
    decimal RefundsTotal,
    decimal ExpensesTotal,
    decimal NetResult,
    IReadOnlyList<ServiceRevenueLine> RevenueByService,
    decimal MembershipRevenue,
    decimal UnallocatedRevenue,
    IReadOnlyList<ExpenseTypeTotal> ExpensesByType,
    IReadOnlyList<WorkerProductionLine> WorkerProduction,
    int RecurringClientCount,
    int LapsedClientCount,
    int AbsenceCount);

/// <summary>The report the owner reads: the period and, when it was asked for, the same report for the comparison period.</summary>
public sealed record ReportResult(PeriodReport Period, PeriodReport? Comparison);
