using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Payments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Payments;

/// <summary>
/// SQL Server persistence for the payment ledger (RF-28, D-20). It owns the three statements the port promises: the
/// tenant-scoped read that locks the payment row, the two guarded inserts that are the last storage-level word on the
/// annulment and the refund, and the tenant-scoped list that carries every correction of a payment without touching
/// the payment row itself. Every command joins the caller's ambient transaction, which owns the commit: nothing here
/// commits, rolls back or writes outside the requested tenant, and the class has no statement that updates or deletes
/// a payment, a membership period or a booking.
/// <para>
/// <b>The read is the lock.</b> <see cref="LockPaymentAsync"/> reads the payment with <c>UPDLOCK, ROWLOCK</c>, so a
/// competing correction of the same payment waits for the unit instead of racing it, and the annulment and the refunds
/// are read inside that lock. Without the hint an annulment and a refund could each read a payment with no correction
/// and both write, which is exactly the state the coexistence rule forbids.
/// </para>
/// <para>
/// <b>The guarded inserts repeat the storage-expressible rules.</b> Each write is one <c>INSERT ... SELECT</c> whose
/// predicate re-checks the tenant ownership, the coexistence rule and — for a refund — the cumulative cap against the
/// refund rows already stored. Before running it, the method takes the payment row lock itself, so the statement's
/// judgment is stable even when a caller did not lock first: a writer that lost the race changes no row and the answer
/// says why, and the method reads that reason from the stored rows instead of guessing. The annulment insert also
/// handles <c>UQ_PaymentAnnulments_Payment</c>: a writer that skipped the lock entirely can still only produce one
/// row, and the unique index reports the same already-annulled answer.
/// </para>
/// <para>
/// There is no method that could update or delete a payment: an annulment of a membership payment is financial-only,
/// and the enrollment, its periods, its seats and the attendance are not reachable from this repository.
/// </para>
/// </summary>
public sealed class SqlPaymentLedgerRepository(DbSession session)
    : SqlRepositoryBase(session), IPaymentLedgerRepository
{
    /// <summary>
    /// Reads one payment of the business under <c>UPDLOCK, ROWLOCK</c>. The lock lives until the caller's transaction
    /// ends and is the same one the two guarded inserts take, so an annulment and a refund of the same payment are
    /// serialized and the corrections read below cannot change under the reader.
    /// </summary>
    private const string LockPaymentSql = """
        SELECT Id, BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt
          FROM Payments WITH (UPDLOCK, ROWLOCK)
         WHERE Id = @paymentId
           AND BusinessId = @businessId
        """;

    /// <summary>The same row lock without the projection, for a guarded insert that only needs to know it may write.</summary>
    private const string LockPaymentRowSql = """
        SELECT Id
          FROM Payments WITH (UPDLOCK, ROWLOCK)
         WHERE Id = @paymentId
           AND BusinessId = @businessId
        """;

    /// <summary>
    /// The annulment linked to the payment. It carries no <c>BusinessId</c>, so it is read only after the locked read
    /// above proved the payment belongs to the tenant; that is the same pattern the other modules use for child rows.
    /// </summary>
    private const string LockedAnnulmentSql = """
        SELECT Id, PaymentId, Reason, AnnulledAt, AnnulledBy
          FROM PaymentAnnulments
         WHERE PaymentId = @paymentId
        """;

    /// <summary>
    /// Every refund already linked to the payment. The accumulated amount is the sum of these rows, and the caller's
    /// transaction read them under the payment row lock, so the cap is judged from one stable state.
    /// </summary>
    private const string LockedRefundsSql = """
        SELECT Id, PaymentId, Amount, RefundedOn, Reason, RecordedAt
          FROM Refunds
         WHERE PaymentId = @paymentId
         ORDER BY RefundedOn, Id
        """;

    /// <summary>
    /// The guarded annulment insert. The predicate is the whole rule the storage can express: the payment row of this
    /// business exists (taken <c>UPDLOCK</c> so the read is serialized) and it has neither an annulment nor any refund
    /// yet, because the two corrections never coexist. A lost race leaves the statement with zero rows and the caller
    /// reports the reason instead of writing a second correction; <c>UQ_PaymentAnnulments_Payment</c> is the last
    /// guard for a writer that skipped the lock, and is handled as the same answer.
    /// </summary>
    private const string GuardedInsertAnnulmentSql = """
        INSERT INTO PaymentAnnulments (PaymentId, Reason, AnnulledAt, AnnulledBy)
        OUTPUT INSERTED.Id
        SELECT p.Id, @reason, @annulledAt, @annulledBy
          FROM Payments AS p WITH (UPDLOCK, ROWLOCK)
         WHERE p.Id = @paymentId
           AND p.BusinessId = @businessId
           AND NOT EXISTS (SELECT 1 FROM PaymentAnnulments AS a WHERE a.PaymentId = p.Id)
           AND NOT EXISTS (SELECT 1 FROM Refunds AS r WHERE r.PaymentId = p.Id)
        """;

    /// <summary>
    /// The guarded refund insert. Besides the tenant ownership and the coexistence rule, its predicate sums the refund
    /// rows already stored and writes only while that sum plus this amount stays within the amount charged, so the cap
    /// is enforced on the rows themselves and not only on the service's precheck: under the payment row lock the sum
    /// cannot go stale, and a refund that lost the race changes no row.
    /// </summary>
    private const string GuardedInsertRefundSql = """
        INSERT INTO Refunds (PaymentId, Amount, RefundedOn, Reason, RecordedAt)
        OUTPUT INSERTED.Id
        SELECT p.Id, @amount, @refundedOn, @reason, @recordedAt
          FROM Payments AS p WITH (UPDLOCK, ROWLOCK)
         WHERE p.Id = @paymentId
           AND p.BusinessId = @businessId
           AND NOT EXISTS (SELECT 1 FROM PaymentAnnulments AS a WHERE a.PaymentId = p.Id)
           AND (SELECT ISNULL(SUM(r.Amount), 0) FROM Refunds AS r WHERE r.PaymentId = p.Id) + @amount <= p.Amount
        """;

    /// <summary>
    /// Why the guarded annulment insert wrote nothing, read while the payment row is still locked so the answer is
    /// stable: <c>2</c> the payment already has an annulment, <c>3</c> a refund landed first. <c>0</c> — a payment of
    /// the tenant with neither correction — is not a state the guard can produce, and the caller fails loudly on it.
    /// </summary>
    private const string DiagnoseAnnulmentSql = """
        SELECT CASE
                 WHEN EXISTS (SELECT 1 FROM PaymentAnnulments AS a WHERE a.PaymentId = @paymentId) THEN 2
                 WHEN EXISTS (SELECT 1 FROM Refunds AS r WHERE r.PaymentId = @paymentId) THEN 3
                 ELSE 0
               END
        """;

    /// <summary>
    /// Why the guarded refund insert wrote nothing, under the same lock: <c>2</c> the payment is annulled, and
    /// otherwise the accumulated refunds would pass the amount charged. There is no third reason the guard can
    /// produce, so a non-annulled payment that was refused can only be a refund past the amount.
    /// </summary>
    private const string DiagnoseRefundSql = """
        SELECT CASE
                 WHEN EXISTS (SELECT 1 FROM PaymentAnnulments AS a WHERE a.PaymentId = @paymentId) THEN 2
                 ELSE 3
               END
        """;

    /// <summary>
    /// The tenant-scoped payment history. Every filter is optional and exact: the client and the concept by equality,
    /// the period inclusive on the day the payment was paid (the upper bound is the day after, compared exclusively so
    /// a payment late that same day is still inside), and the tenant by <c>p.BusinessId</c>.
    /// <para>
    /// The annulment is joined one-to-one because <c>UQ_PaymentAnnulments_Payment</c> allows at most one, and the
    /// refunds are a correlated aggregate instead of a join: a payment with three partial refunds is still one row
    /// whose <c>Amount</c> is the original amount and whose <c>RefundedAmount</c> is the sum, never the original amount
    /// multiplied by the number of corrections. The read pages nothing — <c>ORDER BY</c> only makes the answer
    /// deterministic — because the page is the service's contract.
    /// </para>
    /// </summary>
    private const string ListSql = """
        SELECT p.Id,
               p.ClientId,
               CASE WHEN c.Id IS NULL THEN NULL ELSE CONCAT(c.FirstName, N' ', c.LastName) END AS ClientName,
               p.Concept,
               p.BookingId,
               p.EnrollmentId,
               p.Amount,
               p.PaidAt,
               CONVERT(BIT, CASE WHEN a.Id IS NULL THEN 0 ELSE 1 END) AS Annulled,
               a.Reason AS AnnulmentReason,
               a.AnnulledAt,
               a.AnnulledBy,
               ISNULL((SELECT SUM(r.Amount) FROM Refunds AS r WHERE r.PaymentId = p.Id), 0) AS RefundedAmount
          FROM Payments AS p
          LEFT JOIN Clients AS c ON c.Id = p.ClientId AND c.BusinessId = p.BusinessId
          LEFT JOIN PaymentAnnulments AS a ON a.PaymentId = p.Id
         WHERE p.BusinessId = @businessId
           AND (@clientId IS NULL OR p.ClientId = @clientId)
           AND (@concept IS NULL OR p.Concept = @concept)
           AND (@from IS NULL OR p.PaidAt >= @from)
           AND (@toExclusive IS NULL OR p.PaidAt < @toExclusive)
         ORDER BY p.PaidAt DESC, p.Id DESC
        """;

    public async Task<PaymentLedgerState?> LockPaymentAsync(
        int businessId, int paymentId, CancellationToken ct = default)
    {
        var payment = await QuerySingleOrDefaultAsync(
            LockPaymentSql,
            MapPayment,
            p => p.AddValue("@paymentId", paymentId).AddValue("@businessId", businessId),
            ct);

        // A foreign payment and a missing one are the same answer: nothing about another tenant's rows leaks.
        if (payment is null)
            return null;

        var annulment = await QuerySingleOrDefaultAsync(
            LockedAnnulmentSql,
            MapAnnulment,
            p => p.AddValue("@paymentId", paymentId),
            ct);

        var refunds = await QueryAsync(
            LockedRefundsSql,
            MapRefund,
            p => p.AddValue("@paymentId", paymentId),
            ct);

        return new PaymentLedgerState(payment, annulment, refunds);
    }

    public async Task<PaymentAnnulmentInsertResult> TryInsertAnnulmentAsync(
        int businessId, PaymentAnnulment annulment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(annulment);

        // The lock is taken before the guarded insert reads the stored corrections, so the coexistence rule is judged
        // from a state no competing writer can change until this unit ends. A payment the tenant does not own is not
        // annullable and answers like a lost race, the same way a foreign or missing payment does in the service.
        if (!await LockPaymentRowAsync(businessId, annulment.PaymentId, ct))
            return PaymentAnnulmentInsertResult.AlreadyAnnulled();

        try
        {
            var annulmentId = await ScalarAsync<int>(
                GuardedInsertAnnulmentSql,
                p => p
                    .AddValue("@paymentId", annulment.PaymentId)
                    .AddValue("@businessId", businessId)
                    .AddValue("@reason", annulment.Reason)
                    .AddDateTime2("@annulledAt", annulment.AnnulledAt)
                    .AddValue("@annulledBy", annulment.AnnulledBy),
                ct);

            // The guarded insert wrote the row and returned its identity; no rows means the guard refused it.
            if (annulmentId > 0)
                return PaymentAnnulmentInsertResult.Inserted(annulmentId);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_PaymentAnnulments_Payment: a writer that skipped the payment lock can land between the guard and
            // this insert. The unique index is the last guard, and the answer is the same already-annulled one.
            return PaymentAnnulmentInsertResult.AlreadyAnnulled();
        }

        var diagnosis = await ScalarAsync<int>(
            DiagnoseAnnulmentSql,
            p => p.AddValue("@paymentId", annulment.PaymentId),
            ct);

        return diagnosis switch
        {
            2 => PaymentAnnulmentInsertResult.AlreadyAnnulled(),
            3 => PaymentAnnulmentInsertResult.Refunded(),
            _ => throw new InvalidOperationException(
                "The guarded annulment insert wrote no row and the locked payment has neither an annulment nor a refund to explain it.")
        };
    }

    public async Task<PaymentRefundInsertResult> TryInsertRefundAsync(
        int businessId, Refund refund, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refund);

        // Same lock, so the cumulative cap below is read from the rows this unit already sees and a concurrent refund
        // of the same payment waits instead of passing the cap unnoticed.
        if (!await LockPaymentRowAsync(businessId, refund.PaymentId, ct))
            return PaymentRefundInsertResult.AlreadyAnnulled();

        var refundId = await ScalarAsync<int>(
            GuardedInsertRefundSql,
            p => p
                .AddValue("@paymentId", refund.PaymentId)
                .AddValue("@businessId", businessId)
                .AddValue("@amount", refund.Amount)
                .AddValue("@refundedOn", refund.RefundedOn)
                .AddValue("@reason", refund.Reason)
                .AddDateTime2("@recordedAt", refund.RecordedAt),
            ct);

        if (refundId > 0)
            return PaymentRefundInsertResult.Inserted(refundId);

        var diagnosis = await ScalarAsync<int>(
            DiagnoseRefundSql,
            p => p.AddValue("@paymentId", refund.PaymentId),
            ct);

        return diagnosis switch
        {
            2 => PaymentRefundInsertResult.AlreadyAnnulled(),
            3 => PaymentRefundInsertResult.ExceedsAmount(),
            _ => throw new InvalidOperationException(
                "The guarded refund insert wrote no row and the locked payment has no annulment to explain it.")
        };
    }

    public async Task<IReadOnlyList<PaymentLedgerRow>> ListAsync(
        int businessId, PaymentLedgerQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The inclusive upper bound is the day after the requested one, compared exclusively, so every instant of the
        // requested day falls inside — including a payment paid at 23:45 (RF-28). DateOnly.MaxValue is the last
        // representable day and has no day after it, so it is the "no upper bound" of the filter: the request keeps
        // its lower bound and its tenant, client and concept filters, and only the date ceiling disappears.
        var toExclusive = query.To is { } to && to < DateOnly.MaxValue ? to.AddDays(1) : (DateOnly?)null;

        return await QueryAsync(
            ListSql,
            MapLedgerRow,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@clientId", query.ClientId)
                .AddValue("@concept", query.Concept)
                .AddValue("@from", query.From)
                .AddValue("@toExclusive", toExclusive),
            ct);
    }

    /// <summary>The locked payment row of the business, or <c>false</c> when it is missing or of another tenant.</summary>
    private async Task<bool> LockPaymentRowAsync(int businessId, int paymentId, CancellationToken ct) =>
        await ScalarAsync<int?>(
            LockPaymentRowSql,
            p => p.AddValue("@paymentId", paymentId).AddValue("@businessId", businessId),
            ct) is not null;

    private static Payment MapPayment(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        ClientId = reader.GetNullable<int>("ClientId"),
        Concept = reader.GetEnum<PaymentConcept>("Concept"),
        BookingId = reader.GetNullable<int>("BookingId"),
        EnrollmentId = reader.GetNullable<int>("EnrollmentId"),
        Amount = reader.Get<decimal>("Amount"),
        ListPrice = reader.GetNullable<decimal>("ListPrice"),
        PriceReason = reader.GetNullableString("PriceReason"),
        PaidAt = reader.Get<DateTime>("PaidAt"),
        RecordedAt = reader.Get<DateTime>("RecordedAt")
    };

    private static PaymentAnnulment MapAnnulment(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        PaymentId = reader.Get<int>("PaymentId"),
        Reason = reader.Get<string>("Reason"),
        AnnulledAt = reader.Get<DateTime>("AnnulledAt"),
        AnnulledBy = reader.Get<string>("AnnulledBy")
    };

    private static Refund MapRefund(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        PaymentId = reader.Get<int>("PaymentId"),
        Amount = reader.Get<decimal>("Amount"),
        RefundedOn = reader.Get<DateOnly>("RefundedOn"),
        Reason = reader.Get<string>("Reason"),
        RecordedAt = reader.Get<DateTime>("RecordedAt")
    };

    private static PaymentLedgerRow MapLedgerRow(SqlDataReader reader) => new(
        reader.Get<int>("Id"),
        reader.GetNullable<int>("ClientId"),
        reader.GetNullableString("ClientName"),
        reader.GetEnum<PaymentConcept>("Concept"),
        reader.GetNullable<int>("BookingId"),
        reader.GetNullable<int>("EnrollmentId"),
        reader.Get<decimal>("Amount"),
        reader.Get<DateTime>("PaidAt"),
        reader.Get<bool>("Annulled"),
        reader.GetNullableString("AnnulmentReason"),
        reader.GetNullable<DateTime>("AnnulledAt"),
        reader.GetNullableString("AnnulledBy"),
        reader.Get<decimal>("RefundedAmount"));
}
