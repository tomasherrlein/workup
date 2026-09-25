using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Payments;

/// <summary>
/// The history of the payment ledger (RF-28, D-20). Every filter is optional and scoped to one business: the period
/// is inclusive on the date each payment was paid, the client and the concept are exact, and the page is the
/// service's, not storage's. Its zero value is a valid request on purpose — the unfiltered first page — so a caller
/// always asks for something meaningful.
/// </summary>
public sealed record PaymentLedgerQuery(
    int? ClientId = null,
    PaymentConcept? Concept = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>
/// One row of the payment history (RF-28). It carries the payment as it was recorded and the corrections that hang
/// from it: an annulment — with its reason, date and person responsible — or the amount already returned in partial
/// refunds. A payment is never edited nor deleted, so the row is always the original amount beside its corrections.
/// </summary>
public sealed record PaymentLedgerRow(
    int PaymentId,
    int? ClientId,
    string? ClientName,
    PaymentConcept Concept,
    int? BookingId,
    int? EnrollmentId,
    decimal Amount,
    DateTime PaidAt,
    bool Annulled,
    string? AnnulmentReason,
    DateTime? AnnulledAt,
    string? AnnulledBy,
    decimal RefundedAmount);

/// <summary>The page of payment history the list returns, with the total that lets the caller page it.</summary>
public sealed record PaymentLedgerPage(
    IReadOnlyList<PaymentLedgerRow> Items, int TotalCount, int Page, int PageSize);

/// <summary>
/// One payment read under the lock of its own row (RF-28, D-20), with its annulment and every refund already linked
/// to it. The lock is what serializes an annulment and a refund on the same payment, so the coexistence rule and the
/// accumulated-refund cap are judged from one stable read and a concurrent writer waits instead of racing them.
/// </summary>
public sealed record PaymentLedgerState(
    Payment Payment,
    PaymentAnnulment? Annulment,
    IReadOnlyList<Refund> Refunds)
{
    public decimal RefundedAmount => Refunds.Sum(refund => refund.Amount);

    public bool IsAnnulled => Annulment is not null;
}

/// <summary>What the owner sends to annul a payment (RF-28). The date is taken from the clock, never from the caller.</summary>
public sealed record AnnulPaymentRequest(int PaymentId, string Reason, string AnnulledBy);

/// <summary>
/// What the owner sends to register a manual refund linked to a payment (D-20). The amount may be partial and the
/// date is the day the money was actually returned, typed by the owner; the recording instant comes from the clock.
/// </summary>
public sealed record RefundPaymentRequest(int PaymentId, decimal Amount, DateOnly RefundedOn, string Reason);

/// <summary>The annulment written for a payment (RF-28). The payment row itself is never modified.</summary>
public sealed record PaymentAnnulmentOutcome(
    int PaymentId, int AnnulmentId, string Reason, DateTime AnnulledAt, string AnnulledBy);

/// <summary>
/// The refund written for a payment (D-20), with the accumulated total and what is left to return. The cap is
/// cumulative: partial refunds can never add up to more than the amount charged.
/// </summary>
public sealed record PaymentRefundOutcome(
    int PaymentId, int RefundId, decimal Amount, DateOnly RefundedOn, decimal TotalRefunded, decimal RemainingAmount);

/// <summary>
/// How the guarded annulment insert ended. <see cref="Inserted"/> wrote the correction;
/// <see cref="AlreadyAnnulled"/> means another annulment was already linked to the payment — a lost race or a double
/// submission; <see cref="Refunded"/> means a refund landed first, and an annulment and a refund never coexist.
/// None of them is <c>0</c>, so an undefined value is a broken implementation the caller must fail on.
/// </summary>
public enum PaymentAnnulmentInsertResultType
{
    Inserted = 1,
    AlreadyAnnulled = 2,
    Refunded = 3
}

/// <summary>Result of the guarded annulment insert. <see cref="AnnulmentId"/> only means something when it inserted.</summary>
public sealed record PaymentAnnulmentInsertResult(
    PaymentAnnulmentInsertResultType Result, int AnnulmentId)
{
    public static PaymentAnnulmentInsertResult Inserted(int annulmentId) =>
        new(PaymentAnnulmentInsertResultType.Inserted, annulmentId);

    public static PaymentAnnulmentInsertResult AlreadyAnnulled() =>
        new(PaymentAnnulmentInsertResultType.AlreadyAnnulled, 0);

    public static PaymentAnnulmentInsertResult Refunded() =>
        new(PaymentAnnulmentInsertResultType.Refunded, 0);

    public bool IsInserted => Result == PaymentAnnulmentInsertResultType.Inserted;
}

/// <summary>
/// How the guarded refund insert ended. <see cref="Inserted"/> wrote the refund; <see cref="AlreadyAnnulled"/> means
/// the payment was annulled before the write — the two corrections never coexist; <see cref="ExceedsAmount"/> means
/// the accumulated refunds would pass the amount charged, which can only be a lost race under the payment lock.
/// </summary>
public enum PaymentRefundInsertResultType
{
    Inserted = 1,
    AlreadyAnnulled = 2,
    ExceedsAmount = 3
}

/// <summary>Result of the guarded refund insert. <see cref="RefundId"/> only means something when it inserted.</summary>
public sealed record PaymentRefundInsertResult(
    PaymentRefundInsertResultType Result, int RefundId)
{
    public static PaymentRefundInsertResult Inserted(int refundId) =>
        new(PaymentRefundInsertResultType.Inserted, refundId);

    public static PaymentRefundInsertResult AlreadyAnnulled() =>
        new(PaymentRefundInsertResultType.AlreadyAnnulled, 0);

    public static PaymentRefundInsertResult ExceedsAmount() =>
        new(PaymentRefundInsertResultType.ExceedsAmount, 0);

    public bool IsInserted => Result == PaymentRefundInsertResultType.Inserted;
}