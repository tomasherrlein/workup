using WorkUp.BusinessLogic.Payments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the payment ledger port (RF-28, D-20). It emulates the tenant scope and the two guarded
/// statements exactly like the SQL predicates: a payment of another business is invisible, an annulment is unique per
/// payment, an annulment and a refund can never coexist and the accumulated refunds cannot pass the amount charged.
/// It also models the payment row lock the real port takes: <see cref="LockCallCount"/> counts the reads and
/// <see cref="WriteOrder"/> pins the single write each operation performs.
/// <para>
/// It does not model the transaction rollback, so a test that proves atomicity asserts the transaction counters (no
/// commit, every begin rolled back) instead of the in-memory state. The callbacks
/// <see cref="OnBeforeAnnulmentInsert"/> and <see cref="OnBeforeRefundInsert"/> let a test simulate a competing writer
/// landing between the locked read and the guarded insert, which is what the real guard exists for.
/// </para>
/// </summary>
public sealed class FakePaymentLedgerRepository : IPaymentLedgerRepository
{
    public List<Payment> Payments { get; } = [];

    public List<PaymentAnnulment> Annulments { get; } = [];

    public List<Refund> Refunds { get; } = [];

    /// <summary>Client names the list projection resolves the client id against.</summary>
    public Dictionary<int, string> ClientNames { get; } = [];

    public int LockCallCount { get; private set; }

    public int ListCallCount { get; private set; }

    public int AnnulmentInsertAttempts { get; private set; }

    public int RefundInsertAttempts { get; private set; }

    /// <summary>The writes the service asked for, in order, so a test can pin that only one row is written.</summary>
    public List<string> WriteOrder { get; } = [];

    /// <summary>Runs just before the guarded annulment insert, to simulate a competing writer that landed first.</summary>
    public Action? OnBeforeAnnulmentInsert { get; set; }

    /// <summary>Runs just before the guarded refund insert, to simulate a competing writer that landed first.</summary>
    public Action? OnBeforeRefundInsert { get; set; }

    private int _nextAnnulmentId = 1;
    private int _nextRefundId = 1;

    public Task<PaymentLedgerState?> LockPaymentAsync(
        int businessId, int paymentId, CancellationToken ct = default)
    {
        LockCallCount++;

        var payment = Payments.FirstOrDefault(row => row.Id == paymentId && row.BusinessId == businessId);
        if (payment is null)
            return Task.FromResult<PaymentLedgerState?>(null);

        var annulment = Annulments.FirstOrDefault(row => row.PaymentId == paymentId);
        var refunds = Refunds.Where(row => row.PaymentId == paymentId).ToArray();
        return Task.FromResult<PaymentLedgerState?>(new PaymentLedgerState(payment, annulment, refunds));
    }

    public Task<PaymentAnnulmentInsertResult> TryInsertAnnulmentAsync(
        int businessId, PaymentAnnulment annulment, CancellationToken ct = default)
    {
        WriteOrder.Add("annulment");
        AnnulmentInsertAttempts++;
        OnBeforeAnnulmentInsert?.Invoke();

        var payment = Payments.FirstOrDefault(row => row.Id == annulment.PaymentId && row.BusinessId == businessId);
        if (payment is null || Annulments.Any(row => row.PaymentId == annulment.PaymentId))
            return Task.FromResult(PaymentAnnulmentInsertResult.AlreadyAnnulled());

        if (Refunds.Any(row => row.PaymentId == annulment.PaymentId))
            return Task.FromResult(PaymentAnnulmentInsertResult.Refunded());

        annulment.Id = _nextAnnulmentId++;
        Annulments.Add(annulment);
        return Task.FromResult(PaymentAnnulmentInsertResult.Inserted(annulment.Id));
    }

    public Task<PaymentRefundInsertResult> TryInsertRefundAsync(
        int businessId, Refund refund, CancellationToken ct = default)
    {
        WriteOrder.Add("refund");
        RefundInsertAttempts++;
        OnBeforeRefundInsert?.Invoke();

        var payment = Payments.FirstOrDefault(row => row.Id == refund.PaymentId && row.BusinessId == businessId);
        if (payment is null || Annulments.Any(row => row.PaymentId == refund.PaymentId))
            return Task.FromResult(PaymentRefundInsertResult.AlreadyAnnulled());

        var accumulated = Refunds.Where(row => row.PaymentId == refund.PaymentId).Sum(row => row.Amount);
        if (accumulated + refund.Amount > payment.Amount)
            return Task.FromResult(PaymentRefundInsertResult.ExceedsAmount());

        refund.Id = _nextRefundId++;
        Refunds.Add(refund);
        return Task.FromResult(PaymentRefundInsertResult.Inserted(refund.Id));
    }

    public Task<IReadOnlyList<PaymentLedgerRow>> ListAsync(
        int businessId, PaymentLedgerQuery query, CancellationToken ct = default)
    {
        ListCallCount++;

        var matching = Payments
            .Where(payment => payment.BusinessId == businessId)
            .Where(payment => query.ClientId is null || payment.ClientId == query.ClientId)
            .Where(payment => query.Concept is null || payment.Concept == query.Concept)
            .Where(payment => query.From is null || DateOnly.FromDateTime(payment.PaidAt) >= query.From)
            .Where(payment => query.To is null || DateOnly.FromDateTime(payment.PaidAt) <= query.To)
            .Select(payment =>
            {
                var annulment = Annulments.FirstOrDefault(row => row.PaymentId == payment.Id);
                var refunded = Refunds.Where(row => row.PaymentId == payment.Id).Sum(row => row.Amount);
                var clientName = payment.ClientId is { } clientId && ClientNames.TryGetValue(clientId, out var name)
                    ? name
                    : null;

                return new PaymentLedgerRow(
                    payment.Id,
                    payment.ClientId,
                    clientName,
                    payment.Concept,
                    payment.BookingId,
                    payment.EnrollmentId,
                    payment.Amount,
                    payment.PaidAt,
                    annulment is not null,
                    annulment?.Reason,
                    annulment?.AnnulledAt,
                    annulment?.AnnulledBy,
                    refunded);
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<PaymentLedgerRow>>(matching);
    }
}