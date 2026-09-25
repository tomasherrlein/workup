using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Payments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Payments;

// T18 (RF-11, RF-28, D-20): the payment ledger core. Registrations are facts that are never edited nor deleted;
// corrections go through an annulment (reason, date and person responsible) and money returned goes through a manual
// refund linked to the payment. The annulment of a membership payment is a financial-only correction: it never
// changes the paid coverage, the fixed seats or the attendance. Annulment and refund never coexist on one payment,
// partial refunds cannot exceed the amount charged, and every write is one transaction with a single clock read.
public class PaymentLedgerServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 3, 20, 9, 0, 0);

    private readonly FakePaymentLedgerRepository _repository = new();
    private readonly FakeUnitOfWork _unit = new();
    private readonly CountingClock _clock = new();
    private readonly PaymentLedgerService _service;

    public PaymentLedgerServiceTests()
    {
        _service = new PaymentLedgerService(_repository, _unit, _clock);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    /// <summary>
    /// One recorded payment. The defaults describe a membership payment of the tenant, which is the case the user
    /// decision protects: annulling it must stay a financial-only correction.
    /// </summary>
    private static Payment NewPayment(
        int id = 500,
        int businessId = 1,
        int? clientId = 11,
        PaymentConcept concept = PaymentConcept.Membership,
        int? enrollmentId = 100,
        int? bookingId = null,
        decimal amount = 5000m,
        DateTime? paidAt = null) => new()
        {
            Id = id,
            BusinessId = businessId,
            ClientId = clientId,
            Concept = concept,
            EnrollmentId = enrollmentId,
            BookingId = bookingId,
            Amount = amount,
            PaidAt = paidAt ?? new DateTime(2026, 3, 1, 10, 0, 0),
            RecordedAt = paidAt ?? new DateTime(2026, 3, 1, 10, 0, 0)
        };

    private static Refund NewRefund(int paymentId, decimal amount, DateOnly refundedOn) => new()
    {
        PaymentId = paymentId,
        Amount = amount,
        RefundedOn = refundedOn,
        Reason = "Motivo previo",
        RecordedAt = new DateTime(2026, 3, 15, 9, 0, 0)
    };

    private static PaymentAnnulment NewAnnulment(int paymentId) => new()
    {
        PaymentId = paymentId,
        Reason = "Motivo previo",
        AnnulledAt = new DateTime(2026, 3, 15, 9, 0, 0),
        AnnulledBy = "Lucía"
    };

    private void AssertRolledBack(Result result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(0, _unit.CommitCount);
        Assert.Equal(_unit.BeginCount, _unit.RolledBackCount);
    }

    private void AssertRejectedBeforeStorage(Result result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(0, _unit.BeginCount);
        Assert.Equal(0, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
        Assert.Equal(0, _repository.LockCallCount);
    }

    // --- Annulment (RF-28) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Annulling_a_membership_payment_writes_only_the_annulment()
    {
        var payment = NewPayment();
        _repository.Payments.Add(payment);

        var result = await _service.AnnulAsync(
            1, new AnnulPaymentRequest(500, "  Cobro duplicado  ", "  Lucía  "));

        Assert.True(result.Succeeded);
        Assert.Equal(500, result.Value.PaymentId);
        Assert.Equal("Cobro duplicado", result.Value.Reason);
        Assert.Equal("Lucía", result.Value.AnnulledBy);
        Assert.Equal(ClockNow, result.Value.AnnulledAt);

        var annulment = Assert.Single(_repository.Annulments);
        Assert.Equal(500, annulment.PaymentId);
        Assert.Equal("Cobro duplicado", annulment.Reason);
        Assert.Equal("Lucía", annulment.AnnulledBy);
        Assert.Equal(ClockNow, annulment.AnnulledAt);

        // The only write of the operation is the annulment: the payment row is never edited nor deleted.
        Assert.Equal(["annulment"], _repository.WriteOrder);
        Assert.Equal(1, _unit.BeginCount);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
        Assert.Equal(1, _clock.ReadCount);
    }

    [Fact]
    public async Task Annulling_a_membership_payment_is_a_financial_only_correction()
    {
        var payment = NewPayment();
        _repository.Payments.Add(payment);

        var result = await _service.AnnulAsync(1, new AnnulPaymentRequest(500, "Cobro duplicado", "Lucía"));

        Assert.True(result.Succeeded);
        // The payment keeps every value it had: no coverage, seat or attendance decision can be reached from here
        // because the operation writes an annulment and nothing else.
        Assert.Equal(5000m, payment.Amount);
        Assert.Equal(100, payment.EnrollmentId);
        Assert.Null(payment.BookingId);
        Assert.Equal(PaymentConcept.Membership, payment.Concept);
        Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0), payment.PaidAt);
        Assert.Equal([500], _repository.Annulments.Select(a => a.PaymentId).ToArray());
    }

    [Theory]
    [InlineData(2, 500, "payment_not_found")] // another tenant's payment
    [InlineData(1, 999, "payment_not_found")] // a payment that does not exist
    public async Task Annulment_is_refused_for_a_foreign_or_missing_payment(int businessId, int paymentId, string code)
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.AnnulAsync(businessId, new AnnulPaymentRequest(paymentId, "Motivo", "Lucía"));

        AssertRolledBack(result, code);
        Assert.Empty(_repository.Annulments);
        Assert.Equal(0, _repository.AnnulmentInsertAttempts);
    }

    [Fact]
    public async Task Annulment_is_refused_when_the_payment_is_already_annulled()
    {
        _repository.Payments.Add(NewPayment());
        _repository.Annulments.Add(NewAnnulment(500));

        var result = await _service.AnnulAsync(1, new AnnulPaymentRequest(500, "Otro motivo", "Lucía"));

        AssertRolledBack(result, "payment_already_annulled");
        Assert.Single(_repository.Annulments);
        Assert.Equal(0, _repository.AnnulmentInsertAttempts);
    }

    [Fact]
    public async Task Annulment_is_refused_when_the_payment_already_has_a_refund()
    {
        _repository.Payments.Add(NewPayment());
        _repository.Refunds.Add(NewRefund(500, 1000m, D(2026, 3, 15)));

        var result = await _service.AnnulAsync(1, new AnnulPaymentRequest(500, "Motivo", "Lucía"));

        AssertRolledBack(result, "payment_already_refunded");
        Assert.Empty(_repository.Annulments);
        Assert.Equal(0, _repository.AnnulmentInsertAttempts);
    }

    [Theory]
    [InlineData("", "payment_invalid_reason")]
    [InlineData("   ", "payment_invalid_reason")]
    public async Task Annulment_is_refused_for_a_missing_reason(string reason, string code)
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.AnnulAsync(1, new AnnulPaymentRequest(500, reason, "Lucía"));

        AssertRejectedBeforeStorage(result, code);
    }

    [Fact]
    public async Task Annulment_is_refused_for_a_reason_that_does_not_fit()
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.AnnulAsync(
            1, new AnnulPaymentRequest(500, new string('x', 301), "Lucía"));

        AssertRejectedBeforeStorage(result, "payment_invalid_reason");
    }

    [Theory]
    [InlineData("", "payment_invalid_actor")]
    [InlineData("   ", "payment_invalid_actor")]
    public async Task Annulment_is_refused_for_a_missing_actor(string actor, string code)
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.AnnulAsync(1, new AnnulPaymentRequest(500, "Motivo", actor));

        AssertRejectedBeforeStorage(result, code);
    }

    [Fact]
    public async Task Annulment_is_refused_for_an_actor_that_does_not_fit()
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.AnnulAsync(
            1, new AnnulPaymentRequest(500, "Motivo", new string('x', 61)));

        AssertRejectedBeforeStorage(result, "payment_invalid_actor");
    }

    [Fact]
    public async Task Annulment_is_refused_before_storage_for_invalid_identifiers()
    {
        var result = await _service.AnnulAsync(0, new AnnulPaymentRequest(500, "Motivo", "Lucía"));

        AssertRejectedBeforeStorage(result, "payment_not_found");
    }

    // --- Refund (D-20) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_manual_refund_is_registered_linked_to_the_payment()
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 1500m, D(2026, 3, 18), "  Devolución acordada  "));

        Assert.True(result.Succeeded);
        Assert.Equal(500, result.Value.PaymentId);
        Assert.Equal(1500m, result.Value.Amount);
        Assert.Equal(D(2026, 3, 18), result.Value.RefundedOn);
        Assert.Equal(1500m, result.Value.TotalRefunded);
        Assert.Equal(3500m, result.Value.RemainingAmount);

        var refund = Assert.Single(_repository.Refunds);
        Assert.Equal(500, refund.PaymentId);
        Assert.Equal(1500m, refund.Amount);
        Assert.Equal(D(2026, 3, 18), refund.RefundedOn);
        Assert.Equal("Devolución acordada", refund.Reason);
        Assert.Equal(ClockNow, refund.RecordedAt);

        Assert.Equal(["refund"], _repository.WriteOrder);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(1, _clock.ReadCount);
    }

    [Fact]
    public async Task Rejected_refund_before_storage_for_invalid_amount()
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 0m, D(2026, 3, 18), "Motivo"));

        AssertRejectedBeforeStorage(result, "payment_invalid_amount");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000000000)]
    [InlineData(10.005)]
    public async Task A_refund_amount_out_of_range_is_rejected_before_storage(decimal amount)
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, amount, D(2026, 3, 18), "Motivo"));

        AssertRejectedBeforeStorage(result, "payment_invalid_amount");
    }

    [Fact]
    public async Task Partial_refunds_accumulate_up_to_the_payment_amount()
    {
        _repository.Payments.Add(NewPayment());

        var first = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 2000m, D(2026, 3, 16), "Primera"));
        var second = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 3000m, D(2026, 3, 18), "Segunda"));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(5000m, second.Value.TotalRefunded);
        Assert.Equal(0m, second.Value.RemainingAmount);
        Assert.Equal(2, _repository.Refunds.Count);
        Assert.Equal(2, _unit.CommitCount);
    }

    [Fact]
    public async Task A_refund_that_exceeds_the_remaining_amount_is_refused()
    {
        _repository.Payments.Add(NewPayment());
        _repository.Refunds.Add(NewRefund(500, 4000m, D(2026, 3, 15)));

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 2000m, D(2026, 3, 18), "Motivo"));

        AssertRolledBack(result, "payment_refund_exceeds_amount");
        Assert.Single(_repository.Refunds);
        Assert.Equal(0, _repository.RefundInsertAttempts);
    }

    [Fact]
    public async Task A_refund_on_an_annulled_payment_is_refused()
    {
        _repository.Payments.Add(NewPayment());
        _repository.Annulments.Add(NewAnnulment(500));

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 1000m, D(2026, 3, 18), "Motivo"));

        AssertRolledBack(result, "payment_already_annulled");
        Assert.Empty(_repository.Refunds);
        Assert.Equal(0, _repository.RefundInsertAttempts);
    }

    [Theory]
    [InlineData(2, 500, "payment_not_found")]
    [InlineData(1, 999, "payment_not_found")]
    public async Task Refund_is_refused_for_a_foreign_or_missing_payment(int businessId, int paymentId, string code)
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.RefundAsync(
            businessId, new RefundPaymentRequest(paymentId, 1000m, D(2026, 3, 18), "Motivo"));

        AssertRolledBack(result, code);
        Assert.Empty(_repository.Refunds);
    }

    [Fact]
    public async Task A_refund_without_a_date_is_refused_before_storage()
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 1000m, default, "Motivo"));

        AssertRejectedBeforeStorage(result, "payment_invalid_date");
    }

    [Fact]
    public async Task A_refund_dated_before_the_payment_is_refused()
    {
        _repository.Payments.Add(NewPayment(paidAt: new DateTime(2026, 3, 10, 10, 0, 0)));

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 1000m, D(2026, 3, 9), "Motivo"));

        AssertRolledBack(result, "payment_invalid_date");
        Assert.Empty(_repository.Refunds);
        Assert.Equal(0, _repository.RefundInsertAttempts);
    }

    [Theory]
    [InlineData("", "payment_invalid_reason")]
    [InlineData("   ", "payment_invalid_reason")]
    public async Task A_refund_without_a_reason_is_refused_before_storage(string reason, string code)
    {
        _repository.Payments.Add(NewPayment());

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 1000m, D(2026, 3, 18), reason));

        AssertRejectedBeforeStorage(result, code);
    }

    // --- Guarded write and one clock read ------------------------------------------------------------------------

    [Fact]
    public async Task A_concurrent_annulment_is_refused_by_the_guarded_insert()
    {
        _repository.Payments.Add(NewPayment());
        _repository.OnBeforeAnnulmentInsert = () => _repository.Annulments.Add(NewAnnulment(500));

        var result = await _service.AnnulAsync(1, new AnnulPaymentRequest(500, "Motivo", "Lucía"));

        AssertRolledBack(result, "payment_already_annulled");
        Assert.Equal(1, _repository.AnnulmentInsertAttempts);
        // The concurrent annulment won: this unit wrote nothing and never committed.
        Assert.Single(_repository.Annulments);
        Assert.Equal(0, _unit.CommitCount);
    }

    [Fact]
    public async Task A_concurrent_refund_is_refused_by_the_guarded_insert()
    {
        _repository.Payments.Add(NewPayment());
        _repository.OnBeforeRefundInsert = () => _repository.Refunds.Add(NewRefund(500, 4000m, D(2026, 3, 16)));

        var result = await _service.RefundAsync(
            1, new RefundPaymentRequest(500, 2000m, D(2026, 3, 18), "Motivo"));

        AssertRolledBack(result, "payment_refund_exceeds_amount");
        Assert.Equal(1, _repository.RefundInsertAttempts);
        Assert.Single(_repository.Refunds);
        Assert.Equal(0, _unit.CommitCount);
    }

    // --- Listing (RF-28 history) ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_ledger_list_is_scoped_to_the_business()
    {
        _repository.Payments.Add(NewPayment(id: 1, clientId: 11, paidAt: new DateTime(2026, 3, 1)));
        _repository.Payments.Add(NewPayment(id: 2, businessId: 2, clientId: 99, paidAt: new DateTime(2026, 3, 2)));
        _repository.ClientNames[11] = "Ana";
        _repository.ClientNames[99] = "Ajena";

        var result = await _service.ListAsync(1, new PaymentLedgerQuery());

        Assert.True(result.Succeeded);
        var row = Assert.Single(result.Value.Items);
        Assert.Equal(1, row.PaymentId);
        Assert.Equal("Ana", row.ClientName);
    }

    [Fact]
    public async Task The_ledger_list_filters_by_period_client_and_concept()
    {
        _repository.Payments.Add(NewPayment(id: 1, clientId: 11, concept: PaymentConcept.Membership, paidAt: new DateTime(2026, 3, 1)));
        _repository.Payments.Add(NewPayment(id: 2, clientId: 12, concept: PaymentConcept.IndividualAppointment, enrollmentId: null, bookingId: 70, paidAt: new DateTime(2026, 3, 5)));
        _repository.Payments.Add(NewPayment(id: 3, clientId: 11, concept: PaymentConcept.DropInClass, enrollmentId: null, bookingId: 71, paidAt: new DateTime(2026, 3, 10)));

        var byClient = await _service.ListAsync(1, new PaymentLedgerQuery(ClientId: 11));
        Assert.Equal([1, 3], byClient.Value.Items.OrderBy(i => i.PaymentId).Select(i => i.PaymentId).ToArray());

        var byConcept = await _service.ListAsync(1, new PaymentLedgerQuery(Concept: PaymentConcept.IndividualAppointment));
        Assert.Equal(2, Assert.Single(byConcept.Value.Items).PaymentId);

        var byPeriod = await _service.ListAsync(1, new PaymentLedgerQuery(From: D(2026, 3, 4), To: D(2026, 3, 9)));
        Assert.Equal(2, Assert.Single(byPeriod.Value.Items).PaymentId);
    }

    [Fact]
    public async Task The_ledger_list_pages_in_recent_first_order_and_reports_the_total()
    {
        for (var day = 1; day <= 5; day++)
        {
            _repository.Payments.Add(NewPayment(id: day, paidAt: new DateTime(2026, 3, day, 10, 0, 0)));
        }

        var first = await _service.ListAsync(1, new PaymentLedgerQuery(Page: 1, PageSize: 2));
        var second = await _service.ListAsync(1, new PaymentLedgerQuery(Page: 2, PageSize: 2));
        var third = await _service.ListAsync(1, new PaymentLedgerQuery(Page: 3, PageSize: 2));

        Assert.Equal(5, first.Value.TotalCount);
        Assert.Equal([5, 4], first.Value.Items.Select(i => i.PaymentId).ToArray());
        Assert.Equal([3, 2], second.Value.Items.Select(i => i.PaymentId).ToArray());
        Assert.Equal([1], third.Value.Items.Select(i => i.PaymentId).ToArray());
        Assert.Equal(1, first.Value.Page);
        Assert.Equal(2, first.Value.PageSize);
    }

    [Fact]
    public async Task The_ledger_list_reports_the_annulment_and_the_accumulated_refunds_of_each_payment()
    {
        _repository.Payments.Add(NewPayment(id: 1));
        _repository.Annulments.Add(NewAnnulment(1));
        _repository.Payments.Add(NewPayment(id: 2, paidAt: new DateTime(2026, 3, 2)));
        _repository.Refunds.Add(NewRefund(2, 500m, D(2026, 3, 16)));
        _repository.Refunds.Add(NewRefund(2, 700m, D(2026, 3, 17)));

        var result = await _service.ListAsync(1, new PaymentLedgerQuery());

        var annulled = result.Value.Items.Single(i => i.PaymentId == 1);
        Assert.True(annulled.Annulled);
        Assert.Equal("Motivo previo", annulled.AnnulmentReason);

        var refunded = result.Value.Items.Single(i => i.PaymentId == 2);
        Assert.False(refunded.Annulled);
        Assert.Equal(1200m, refunded.RefundedAmount);
    }

    [Fact]
    public async Task The_ledger_list_is_refused_before_storage_for_an_invalid_business()
    {
        var result = await _service.ListAsync(0, new PaymentLedgerQuery());

        Assert.False(result.Succeeded);
        Assert.Equal("payment_not_found", result.Error!.Code);
        Assert.Equal(0, _repository.ListCallCount);
    }

    /// <summary>Clock that counts its reads, so a test can prove the operation read the current time exactly once.</summary>
    private sealed class CountingClock : IClock
    {
        private DateTime _now = ClockNow;

        public int ReadCount { get; private set; }

        public DateTime Now
        {
            get
            {
                ReadCount++;
                return _now;
            }

            set => _now = value;
        }
    }
}