using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Payments;

/// <summary>
/// Persistence boundary for the payment ledger (RF-28, D-20). Every method is scoped by <c>businessId</c>: a payment
/// of another business is invisible and indistinguishable from one that does not exist. A payment is never updated
/// nor deleted here — there is no method for that — so every correction is a new row linked to it.
/// <para>
/// <see cref="LockPaymentAsync"/> is the lock of this module. It serializes an annulment and a refund on the same
/// payment, so the coexistence rule (never both) and the accumulated-refund cap are judged from one stable read and
/// a concurrent writer waits for the unit instead of racing them. The two guarded inserts repeat the storage-
/// expressible rules inside the statement, so a write that lost the race changes no row and reports why instead of
/// writing a second correction or a refund over the amount charged.
/// </para>
/// </summary>
public interface IPaymentLedgerRepository
{
    /// <summary>
    /// Locks the payment row of the business inside the caller's transaction, like
    /// <c>SELECT ... WITH (UPDLOCK, ROWLOCK)</c>, and returns it with its annulment and every refund already linked
    /// to it. It returns <c>null</c> when the payment does not belong to the business, so a foreign and a missing
    /// payment are the same answer and nothing about another tenant's rows leaks. The read is the lock: the
    /// coexistence rule and the accumulated-refund cap are decided from the state this read returns, so without the
    /// hint an annulment and a refund could each read a payment with no correction and both write.
    /// </summary>
    Task<PaymentLedgerState?> LockPaymentAsync(int businessId, int paymentId, CancellationToken ct = default);

    /// <summary>
    /// Guarded insert of the annulment, inside the caller's transaction. It writes only while the payment belongs to
    /// the business and has neither an annulment (<c>UQ_PaymentAnnulments_Payment</c>) nor any refund yet; a lost
    /// race reports <see cref="PaymentAnnulmentInsertResultType.AlreadyAnnulled"/> or
    /// <see cref="PaymentAnnulmentInsertResultType.Refunded"/> and changes no row. The correction is written here
    /// and nowhere else: an annulment of a membership payment is financial-only and never touches the enrollment,
    /// its periods, its fixed seats or the attendance, which is why the port has no method that could.
    /// </summary>
    Task<PaymentAnnulmentInsertResult> TryInsertAnnulmentAsync(
        int businessId, PaymentAnnulment annulment, CancellationToken ct = default);

    /// <summary>
    /// Guarded insert of the manual refund, inside the caller's transaction. It writes only while the payment belongs
    /// to the business, is not annulled and the accumulated refunds plus this amount do not pass the amount charged;
    /// a lost race reports <see cref="PaymentRefundInsertResultType.AlreadyAnnulled"/> or
    /// <see cref="PaymentRefundInsertResultType.ExceedsAmount"/> and changes no row. The refund is linked to the
    /// original payment and is never an automatic consequence of a cancellation or a suspension (D-20, D-24).
    /// </summary>
    Task<PaymentRefundInsertResult> TryInsertRefundAsync(
        int businessId, Refund refund, CancellationToken ct = default);

    /// <summary>
    /// The payment history of the business (RF-28), tenant-scoped and filtered by the period the payment was paid in,
    /// the client and the concept. It returns the matching rows in any order and pages nothing: the page is the
    /// service's contract, so the same read can serve any page and the total the caller pages with. The row carries
    /// the payment as recorded beside its annulment or its accumulated refunds, never a mutated payment.
    /// </summary>
    Task<IReadOnlyList<PaymentLedgerRow>> ListAsync(
        int businessId, PaymentLedgerQuery query, CancellationToken ct = default);
}