using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Payments;
using WorkUp.Data;
using WorkUp.Data.Payments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Payments;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlPaymentLedgerRepository"/> and the payment ledger
/// service that drives it (RF-28, D-20): the tenant-scoped lock read of one payment with its annulment and refunds,
/// the two guarded inserts that refuse to write a second correction or past the amount charged, and the tenant-scoped
/// list. It requires a new, dedicated database supplied through WORKUP_T18_SQL_CONNECTION; without it every test
/// returns N/A and no SQL connection is opened. Each test derives its own database name from the supplied one, so the
/// whole class runs in a single command, and nothing is torn down: the target is retained on purpose.
/// <para>
/// The unit tests cannot prove what lives in storage — the annulment row written beside an untouched payment, the
/// enrollment and its membership period exactly as they were after a financial-only correction, the accumulated
/// partial refunds that never pass the amount charged because the guard reads the rows already stored, the annulment
/// and the refund that refuse each other in both orders, the unique index that is the last guard of a repeated
/// annulment, the tenant that stays invisible, and the write that neither committed on its own nor survived a lost
/// race — so this fixture is the runtime proof of those claims.
/// </para>
/// </summary>
public sealed class PaymentLedgerSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T18_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>Friday 2026-03-20 09:00, the single clock instant every write decides from.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 20, 9, 0, 0);

    /// <summary>The night the ledger list must include when its date filter is inclusive (RF-28).</summary>
    private static readonly DateTime LateSameDayPayment = new(2026, 3, 6, 23, 45, 0);

    private const decimal MembershipAmount = 5000m;
    private const int WeeklyFrequency = 1;
    private const int ReferenceDay = 20;

    /// <summary>A payment id no seed ever creates, for the missing-payment side of the ledger guards.</summary>
    private const int MissingPaymentId = 999999;

    private static readonly DateOnly PeriodStart = new(2026, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 4, 1);

    [Fact]
    public async Task An_annulment_is_financial_only_and_the_refund_first_rule_holds_in_both_orders()
    {
        var target = await ResolveTargetAsync("_Annulment");
        if (target is null)
            return;

        var ids = await SeedLedgerAsync(
            target.ConnectionString, new LedgerSeed("synthetic-t18-annul", "20201818180"));

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session);

        var result = await service.AnnulAsync(
            ids.BusinessId, new AnnulPaymentRequest(ids.Payment, "Cobro duplicado", "Lucía"));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(ids.Payment, result.Value.PaymentId);
        Assert.True(result.Value.AnnulmentId > 0);
        Assert.Equal("Cobro duplicado", result.Value.Reason);
        Assert.Equal(FixedNow, result.Value.AnnulledAt);
        Assert.Equal("Lucía", result.Value.AnnulledBy);

        // One annulment row, linked to the payment, with the reason, the clock instant and the person responsible.
        var annulments = await ReadAnnulmentsAsync(target.ConnectionString, ids.Payment);
        var annulment = Assert.Single(annulments);
        Assert.Equal(result.Value.AnnulmentId, annulment.Id);
        Assert.Equal("Cobro duplicado", annulment.Reason);
        Assert.Equal(FixedNow, annulment.AnnulledAt);
        Assert.Equal("Lucía", annulment.AnnulledBy);

        // The payment is a fact: every column is exactly as it was seeded, and none was rewritten.
        var payment = await ReadPaymentAsync(target.ConnectionString, ids.Payment);
        Assert.Equal(MembershipAmount, payment.Amount);
        Assert.Equal(PaymentConcept.Membership, payment.Concept);
        Assert.Equal(ids.Enrollment, payment.EnrollmentId);
        Assert.Null(payment.BookingId);
        Assert.Equal(ids.ClientId, payment.ClientId);
        Assert.Equal(LedgerSeed.DefaultPaidAt, payment.PaidAt);

        // The annulment of a membership payment is a financial-only correction: the enrollment and its paid coverage
        // are untouched (RF-29 owns ending a membership).
        var enrollment = await ReadEnrollmentAsync(target.ConnectionString, ids.Enrollment);
        Assert.Equal(EnrollmentStatus.Active, enrollment.Status);
        Assert.Equal(MembershipAmount, enrollment.AgreedPrice);
        Assert.Equal(ReferenceDay, enrollment.ReferenceDay);
        Assert.Equal(FixedNow, enrollment.ActivatedAt);
        Assert.Null(enrollment.AnnulledAt);
        Assert.Null(enrollment.TerminatedAt);
        var periods = await ReadPeriodsAsync(target.ConnectionString, ids.Enrollment);
        var period = Assert.Single(periods);
        Assert.Equal(ids.Period, period.Id);
        Assert.Equal(PeriodStart, period.CoverageStart);
        Assert.Equal(PeriodEnd, period.ExpiresOn);
        Assert.Equal(ids.Payment, period.PaymentId);

        // The appointment and its booking share the tenant and were not touched either: nothing here updates or
        // deletes a payment, a period or a booking.
        var booking = Assert.Single(await ReadBookingsAsync(target.ConnectionString, ids.BusinessId));
        Assert.Equal(ids.Booking, booking.Id);
        Assert.Equal(ids.Appointment, booking.AppointmentId);
        Assert.Equal(BookingStatus.Pending, booking.Status);

        // The refund is refused while the annulment exists, and it writes nothing.
        var refused = await service.RefundAsync(
            ids.BusinessId,
            new RefundPaymentRequest(ids.Payment, 1000m, new DateOnly(2026, 3, 21), "Devolución parcial"));
        Assert.False(refused.Succeeded);
        Assert.Equal(PaymentLedgerService.AlreadyAnnulledCode, refused.Error!.Code);
        Assert.Empty(await ReadRefundsAsync(target.ConnectionString, ids.Payment));

        // The guard repeats the rule from the stored rows: a refund insert inside a transaction answers AlreadyAnnulled
        // and changes no row, and a second annulment answers AlreadyAnnulled while the unique index remains the last
        // guard of a repeated correction.
        await using (var transaction = await session.BeginAsync())
        {
            var repository = new SqlPaymentLedgerRepository(session);
            var state = await repository.LockPaymentAsync(ids.BusinessId, ids.Payment);
            Assert.NotNull(state);
            Assert.True(state.IsAnnulled);
            Assert.Empty(state.Refunds);

            var guardedRefund = await repository.TryInsertRefundAsync(
                ids.BusinessId, NewRefund(ids.Payment, 1000m, new DateOnly(2026, 3, 21)));
            Assert.Equal(PaymentRefundInsertResultType.AlreadyAnnulled, guardedRefund.Result);
            Assert.Equal(0, guardedRefund.RefundId);

            var repeated = await repository.TryInsertAnnulmentAsync(
                ids.BusinessId, NewAnnulment(ids.Payment, "Otra vez"));
            Assert.Equal(PaymentAnnulmentInsertResultType.AlreadyAnnulled, repeated.Result);
            await transaction.CommitAsync();
        }

        Assert.Single(await ReadAnnulmentsAsync(target.ConnectionString, ids.Payment));
        Assert.Empty(await ReadRefundsAsync(target.ConnectionString, ids.Payment));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessId)));

        output.WriteLine("Annulment assertions passed: the annulment was the only row written, the payment, the enrollment, the paid period and the booking were exactly as seeded, the refund was refused with payment_already_annulled, and the guarded inserts wrote nothing.");
    }

    [Fact]
    public async Task A_refund_first_refuses_the_annulment_and_keeps_the_membership_covered()
    {
        var target = await ResolveTargetAsync("_RefundFirst");
        if (target is null)
            return;

        var ids = await SeedLedgerAsync(
            target.ConnectionString, new LedgerSeed("synthetic-t18-refund", "20201818280"));

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session);
        var refundedOn = new DateOnly(2026, 3, 21);

        var refund = await service.RefundAsync(
            ids.BusinessId, new RefundPaymentRequest(ids.Payment, 2000m, refundedOn, "Devolución parcial"));

        Assert.True(refund.Succeeded, refund.Error?.Code);
        Assert.Equal(2000m, refund.Value.Amount);
        Assert.Equal(refundedOn, refund.Value.RefundedOn);
        Assert.Equal(2000m, refund.Value.TotalRefunded);
        Assert.Equal(3000m, refund.Value.RemainingAmount);
        Assert.True(refund.Value.RefundId > 0);

        var refunds = await ReadRefundsAsync(target.ConnectionString, ids.Payment);
        var stored = Assert.Single(refunds);
        Assert.Equal(refund.Value.RefundId, stored.Id);
        Assert.Equal(2000m, stored.Amount);
        Assert.Equal(refundedOn, stored.RefundedOn);
        Assert.Equal(FixedNow, stored.RecordedAt);

        // With a refund already linked, the annulment is refused and no correction row exists.
        var refused = await service.AnnulAsync(
            ids.BusinessId, new AnnulPaymentRequest(ids.Payment, "Cobro duplicado", "Lucía"));
        Assert.False(refused.Succeeded);
        Assert.Equal(PaymentLedgerService.AlreadyRefundedCode, refused.Error!.Code);
        Assert.Empty(await ReadAnnulmentsAsync(target.ConnectionString, ids.Payment));

        // The guard repeats the rule from the stored rows: the annulment insert answers Refunded and writes nothing.
        await using (var transaction = await session.BeginAsync())
        {
            var repository = new SqlPaymentLedgerRepository(session);
            var state = await repository.LockPaymentAsync(ids.BusinessId, ids.Payment);
            Assert.NotNull(state);
            Assert.False(state.IsAnnulled);
            Assert.Equal(2000m, state.RefundedAmount);

            var guardedAnnulment = await repository.TryInsertAnnulmentAsync(
                ids.BusinessId, NewAnnulment(ids.Payment, "Cobro duplicado"));
            Assert.Equal(PaymentAnnulmentInsertResultType.Refunded, guardedAnnulment.Result);
            await transaction.CommitAsync();
        }

        // The refund is a financial-only correction too: the covered membership stays exactly as it was.
        var enrollment = await ReadEnrollmentAsync(target.ConnectionString, ids.Enrollment);
        Assert.Equal(EnrollmentStatus.Active, enrollment.Status);
        Assert.Null(enrollment.AnnulledAt);
        Assert.Null(enrollment.TerminatedAt);
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId",
            ("@enrollmentId", ids.Enrollment)));
        Assert.Equal(MembershipAmount, (await ReadPaymentAsync(target.ConnectionString, ids.Payment)).Amount);
        Assert.Empty(await ReadAnnulmentsAsync(target.ConnectionString, ids.Payment));

        output.WriteLine("Refund-first assertions passed: the partial refund was the only correction written and stayed linked to the payment, the annulment was refused with payment_already_refunded in the service and by the guarded insert, and the enrollment and its period were untouched.");
    }

    [Fact]
    public async Task Partial_refunds_cannot_pass_the_amount_charged_and_the_boundary_is_inclusive()
    {
        var target = await ResolveTargetAsync("_Boundary");
        if (target is null)
            return;

        var ids = await SeedLedgerAsync(
            target.ConnectionString, new LedgerSeed("synthetic-t18-boundary", "20201818380"));
        var extraPayment = await SeedExtraMembershipPaymentAsync(
            target.ConnectionString, ids, MembershipAmount, LedgerSeed.DefaultPaidAt.AddDays(1));

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session);

        // 3000 then 2000: the two partial refunds add up to exactly the amount charged, which is allowed.
        var first = await service.RefundAsync(
            ids.BusinessId,
            new RefundPaymentRequest(ids.Payment, 3000m, new DateOnly(2026, 3, 21), "Primera devolución"));
        Assert.True(first.Succeeded, first.Error?.Code);
        Assert.Equal(3000m, first.Value.TotalRefunded);
        Assert.Equal(2000m, first.Value.RemainingAmount);

        var second = await service.RefundAsync(
            ids.BusinessId,
            new RefundPaymentRequest(ids.Payment, 2000m, new DateOnly(2026, 3, 22), "Segunda devolución"));
        Assert.True(second.Succeeded, second.Error?.Code);
        Assert.Equal(5000m, second.Value.TotalRefunded);
        Assert.Equal(0m, second.Value.RemainingAmount);

        // One cent more is refused, and the two stored refunds stay exactly two.
        var over = await service.RefundAsync(
            ids.BusinessId,
            new RefundPaymentRequest(ids.Payment, 0.01m, new DateOnly(2026, 3, 22), "Un centavo más"));
        Assert.False(over.Succeeded);
        Assert.Equal(PaymentLedgerService.RefundExceedsAmountCode, over.Error!.Code);
        var stored = await ReadRefundsAsync(target.ConnectionString, ids.Payment);
        Assert.Equal(2, stored.Count);
        Assert.Equal(5000m, stored.Sum(row => row.Amount));

        /* The guard judges the cap from the rows actually stored, not from the service precheck: called directly it
           refuses the cent that passes the amount, and it accepts the exact remainder. The whole check happens under
           the payment row lock the repository takes, so a competing writer waits instead of racing the sum. */
        await using (var transaction = await session.BeginAsync())
        {
            var repository = new SqlPaymentLedgerRepository(session);
            var overGuard = await repository.TryInsertRefundAsync(
                ids.BusinessId, NewRefund(ids.Payment, 0.01m, new DateOnly(2026, 3, 22)));
            Assert.Equal(PaymentRefundInsertResultType.ExceedsAmount, overGuard.Result);
            Assert.Equal(0, overGuard.RefundId);
            await transaction.CommitAsync();
        }

        await using (var transaction = await session.BeginAsync())
        {
            var repository = new SqlPaymentLedgerRepository(session);
            var pastAmount = await repository.TryInsertRefundAsync(
                ids.BusinessId, NewRefund(extraPayment, 5000.01m, new DateOnly(2026, 3, 21)));
            Assert.Equal(PaymentRefundInsertResultType.ExceedsAmount, pastAmount.Result);

            var exact = await repository.TryInsertRefundAsync(
                ids.BusinessId, NewRefund(extraPayment, 5000m, new DateOnly(2026, 3, 21)));
            Assert.True(exact.IsInserted);
            Assert.True(exact.RefundId > 0);
            await transaction.CommitAsync();
        }

        Assert.Equal(2, (await ReadRefundsAsync(target.ConnectionString, ids.Payment)).Count);
        var extra = Assert.Single(await ReadRefundsAsync(target.ConnectionString, extraPayment));
        Assert.Equal(5000m, extra.Amount);

        output.WriteLine("Boundary assertions passed: 3000+2000 was accepted as exactly the amount charged, one cent more was refused with payment_refund_exceeds_amount in the service and ExceedsAmount by the guard, the guard also refused an amount over a payment with no refunds yet, and it accepted the exact remainder.");
    }

    [Fact]
    public async Task Two_corrections_that_lost_the_race_change_no_row_and_leave_one_annulment_at_most()
    {
        var target = await ResolveTargetAsync("_LostRace");
        if (target is null)
            return;

        var refundRace = await SeedLedgerAsync(
            target.ConnectionString, new LedgerSeed("synthetic-t18-race-refund", "20201818480"));
        var annulmentRace = await SeedLedgerAsync(
            target.ConnectionString, new LedgerSeed("synthetic-t18-race-annul", "20201818481"));

        // Two writers try to refund 3000 of a 5000 payment at the same time, each inside its own transaction. The
        // payment row lock serializes them, so whichever arrives second reads the first refund from storage and the
        // cumulative cap refuses it: exactly one Inserted, one ExceedsAmount and one stored row.
        using var refundGate = new Barrier(participantCount: 2);
        Task<PaymentRefundInsertResult> RefundAsync() => Task.Run(async () =>
        {
            await using var raceSession = new DbSession(target.ConnectionString);
            var repository = new SqlPaymentLedgerRepository(raceSession);
            await using var transaction = await raceSession.BeginAsync();
            refundGate.SignalAndWait();
            var write = await repository.TryInsertRefundAsync(
                refundRace.BusinessId, NewRefund(refundRace.Payment, 3000m, new DateOnly(2026, 3, 21)));
            await transaction.CommitAsync();
            return write;
        });

        var refundResults = await Task.WhenAll(RefundAsync(), RefundAsync());

        var refundWinner = Assert.Single(refundResults, write => write.IsInserted);
        var refundLoser = Assert.Single(refundResults, write => !write.IsInserted);
        Assert.True(refundWinner.RefundId > 0);
        Assert.Equal(PaymentRefundInsertResultType.ExceedsAmount, refundLoser.Result);
        var storedRefunds = await ReadRefundsAsync(target.ConnectionString, refundRace.Payment);
        var onlyRefund = Assert.Single(storedRefunds);
        Assert.Equal(3000m, onlyRefund.Amount);

        // The same for a repeated annulment: the guard refuses the second one and, should a writer skip the lock and
        // slip past it, UQ_PaymentAnnulments_Payment is the last guard. Either way exactly one row and the same answer.
        using var annulmentGate = new Barrier(participantCount: 2);
        Task<PaymentAnnulmentInsertResult> AnnulAsync() => Task.Run(async () =>
        {
            await using var raceSession = new DbSession(target.ConnectionString);
            var repository = new SqlPaymentLedgerRepository(raceSession);
            await using var transaction = await raceSession.BeginAsync();
            annulmentGate.SignalAndWait();
            var write = await repository.TryInsertAnnulmentAsync(
                annulmentRace.BusinessId, NewAnnulment(annulmentRace.Payment, "Cobro duplicado"));
            await transaction.CommitAsync();
            return write;
        });

        var annulmentResults = await Task.WhenAll(AnnulAsync(), AnnulAsync());

        var annulmentWinner = Assert.Single(annulmentResults, write => write.IsInserted);
        var annulmentLoser = Assert.Single(annulmentResults, write => !write.IsInserted);
        Assert.True(annulmentWinner.AnnulmentId > 0);
        Assert.Equal(PaymentAnnulmentInsertResultType.AlreadyAnnulled, annulmentLoser.Result);
        Assert.Single(await ReadAnnulmentsAsync(target.ConnectionString, annulmentRace.Payment));

        // Neither correction leaked into the other payment.
        Assert.Empty(await ReadAnnulmentsAsync(target.ConnectionString, refundRace.Payment));
        Assert.Empty(await ReadRefundsAsync(target.ConnectionString, annulmentRace.Payment));

        output.WriteLine("Lost-race assertions passed: two concurrent refunds ended with one Inserted and one ExceedsAmount and a single 3000 row, two concurrent annulments ended with one Inserted and one AlreadyAnnulled and a single correction, and neither payment carried the other's correction.");
    }

    [Fact]
    public async Task The_list_is_tenant_scoped_and_filters_by_an_inclusive_paid_date_client_and_concept()
    {
        var target = await ResolveTargetAsync("_List");
        if (target is null)
            return;

        var own = await SeedLedgerAsync(
            target.ConnectionString,
            new LedgerSeed("synthetic-t18-list-own", "20201818580", PaidAt: new DateTime(2026, 3, 6, 11, 30, 0)));
        var foreign = await SeedLedgerAsync(
            target.ConnectionString,
            new LedgerSeed("synthetic-t18-list-foreign", "20201818581", PaidAt: new DateTime(2026, 3, 6, 12, 0, 0)));
        var annulledPayment = await SeedExtraMembershipPaymentAsync(
            target.ConnectionString, own, MembershipAmount, new DateTime(2026, 3, 5, 10, 0, 0));
        var individualPayment = await SeedExtraIndividualPaymentAsync(
            target.ConnectionString, own, 2000m, LateSameDayPayment);

        var annulledAt = new DateTime(2026, 3, 7, 8, 15, 0);
        await ExecuteAsync(target.ConnectionString,
            "INSERT INTO PaymentAnnulments (PaymentId, Reason, AnnulledAt, AnnulledBy) VALUES (@paymentId, @reason, @annulledAt, @annulledBy)",
            ("@paymentId", annulledPayment), ("@reason", "Cobro duplicado"),
            ("@annulledAt", annulledAt), ("@annulledBy", "Lucía"));
        await InsertRefundAsync(target.ConnectionString, own.Payment, 1000m, new DateOnly(2026, 3, 8), "Primera devolución");
        await InsertRefundAsync(target.ConnectionString, own.Payment, 2000m, new DateOnly(2026, 3, 9), "Segunda devolución");

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlPaymentLedgerRepository(session);

        // The tenant scope: the unfiltered list of one business never carries a payment of the other.
        var all = await repository.ListAsync(own.BusinessId, new PaymentLedgerQuery());
        Assert.Equal(3, all.Count);
        Assert.DoesNotContain(all, row => row.PaymentId == foreign.Payment);
        var foreignRows = await repository.ListAsync(foreign.BusinessId, new PaymentLedgerQuery());
        var onlyForeign = Assert.Single(foreignRows);
        Assert.Equal(foreign.Payment, onlyForeign.PaymentId);

        // The date filter is inclusive on the day the payment was paid: the 23:45 payment of 2026-03-06 is in.
        var sameDay = await repository.ListAsync(
            own.BusinessId,
            new PaymentLedgerQuery(From: new DateOnly(2026, 3, 6), To: new DateOnly(2026, 3, 6)));
        Assert.Equal(2, sameDay.Count);
        Assert.Contains(sameDay, row => row.PaymentId == individualPayment && row.PaidAt == LateSameDayPayment);
        Assert.Contains(sameDay, row => row.PaymentId == own.Payment);

        // The annulment metadata travels with the row, never as a mutated payment.
        var annulledRow = Assert.Single(await repository.ListAsync(
            own.BusinessId,
            new PaymentLedgerQuery(From: new DateOnly(2026, 3, 5), To: new DateOnly(2026, 3, 5))));
        Assert.Equal(annulledPayment, annulledRow.PaymentId);
        Assert.True(annulledRow.Annulled);
        Assert.Equal("Cobro duplicado", annulledRow.AnnulmentReason);
        Assert.Equal(annulledAt, annulledRow.AnnulledAt);
        Assert.Equal("Lucía", annulledRow.AnnulledBy);
        Assert.Equal(0m, annulledRow.RefundedAmount);
        Assert.Equal(MembershipAmount, annulledRow.Amount);

        /* The two refunds of one payment are one row with their summed amount and the original amount beside it: the
           aggregate never multiplies the payment amount across the refund rows. */
        var refundedRow = Assert.Single(await repository.ListAsync(
            own.BusinessId,
            new PaymentLedgerQuery(
                ClientId: own.ClientId,
                Concept: PaymentConcept.Membership,
                From: new DateOnly(2026, 3, 6),
                To: new DateOnly(2026, 3, 6))));
        Assert.Equal(own.Payment, refundedRow.PaymentId);
        Assert.Equal(MembershipAmount, refundedRow.Amount);
        Assert.Equal(3000m, refundedRow.RefundedAmount);
        Assert.False(refundedRow.Annulled);
        Assert.Equal(own.Enrollment, refundedRow.EnrollmentId);
        Assert.NotNull(refundedRow.ClientName);

        // The concept and client filters are exact, and a client of another business is not reachable from this one.
        var conceptRows = await repository.ListAsync(
            own.BusinessId, new PaymentLedgerQuery(Concept: PaymentConcept.IndividualAppointment));
        var individualRow = Assert.Single(conceptRows);
        Assert.Equal(individualPayment, individualRow.PaymentId);
        Assert.Equal(2000m, individualRow.Amount);
        Assert.NotNull(individualRow.BookingId);
        Assert.Null(individualRow.EnrollmentId);
        Assert.Empty(await repository.ListAsync(own.BusinessId, new PaymentLedgerQuery(ClientId: foreign.ClientId)));

        // The page is the service's, not storage's: the repository returned every match and the service sliced it in
        // recent-first order with the total that lets the caller page it.
        var service = new PaymentLedgerService(repository, session, new FakeClock { Now = FixedNow });
        var firstPage = await service.ListAsync(own.BusinessId, new PaymentLedgerQuery(PageSize: 1));
        Assert.True(firstPage.Succeeded, firstPage.Error?.Code);
        Assert.Equal(3, firstPage.Value.TotalCount);
        Assert.Equal(1, firstPage.Value.PageSize);
        var item = Assert.Single(firstPage.Value.Items);
        Assert.Equal(individualPayment, item.PaymentId);

        output.WriteLine("List assertions passed: the unfiltered list carried the three payments of the tenant and none of the other, the inclusive date filter caught the 23:45 payment, the annulment row carried its reason, date and person, the refunded row stayed one row with 3000 already returned beside the original 5000, the concept and client filters were exact, and the service paged what storage returned.");
    }

    [Fact]
    public async Task The_guarded_corrections_commit_only_with_the_callers_transaction()
    {
        var target = await ResolveTargetAsync("_Rollback");
        if (target is null)
            return;

        var annulled = await SeedLedgerAsync(
            target.ConnectionString, new LedgerSeed("synthetic-t18-rollback-annul", "20201818680"));
        var refunded = await SeedLedgerAsync(
            target.ConnectionString, new LedgerSeed("synthetic-t18-rollback-refund", "20201818681"));

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlPaymentLedgerRepository(session);
        var transaction = await session.BeginAsync();

        var annulment = await repository.TryInsertAnnulmentAsync(
            annulled.BusinessId, NewAnnulment(annulled.Payment, "Cobro duplicado"));
        var refund = await repository.TryInsertRefundAsync(
            refunded.BusinessId, NewRefund(refunded.Payment, 1000m, new DateOnly(2026, 3, 21)));
        Assert.True(annulment.IsInserted);
        Assert.True(refund.IsInserted);

        // Inside the unit both rows are already there, so what the rollback below erases is a real write.
        Assert.Equal(1, await ReadScalarInUnitAsync<int>(
            session,
            "SELECT COUNT(*) FROM PaymentAnnulments WHERE PaymentId = @paymentId",
            ("@paymentId", annulled.Payment)));
        Assert.Equal(1, await ReadScalarInUnitAsync<int>(
            session,
            "SELECT COUNT(*) FROM Refunds WHERE PaymentId = @paymentId",
            ("@paymentId", refunded.Payment)));

        // The repository never commits: leaving the unit without committing puts storage back where it was.
        await transaction.DisposeAsync();

        Assert.Empty(await ReadAnnulmentsAsync(target.ConnectionString, annulled.Payment));
        Assert.Empty(await ReadRefundsAsync(target.ConnectionString, refunded.Payment));
        Assert.Equal(MembershipAmount, (await ReadPaymentAsync(target.ConnectionString, annulled.Payment)).Amount);
        Assert.Equal(MembershipAmount, (await ReadPaymentAsync(target.ConnectionString, refunded.Payment)).Amount);

        output.WriteLine("Rollback assertions passed: inside the unit the annulment and the refund existed, and after leaving it without committing storage kept neither row and both payments unchanged.");
    }

    [Fact]
    public async Task The_max_date_scope_is_unbounded_and_a_foreign_or_missing_payment_writes_nothing()
    {
        var target = await ResolveTargetAsync("_MaxDateScope");
        if (target is null)
            return;

        var own = await SeedLedgerAsync(
            target.ConnectionString,
            new LedgerSeed("synthetic-t18-maxdate-own", "20201818780",
                PaidAt: new DateTime(2026, 3, 6, 11, 30, 0)));
        var lateOwn = await SeedExtraIndividualPaymentAsync(
            target.ConnectionString, own, 2000m, LateSameDayPayment);
        var foreign = await SeedLedgerAsync(
            target.ConnectionString,
            new LedgerSeed("synthetic-t18-maxdate-foreign", "20201818781",
                PaidAt: new DateTime(2026, 3, 7, 12, 0, 0)));

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlPaymentLedgerRepository(session);

        // To = DateOnly.MaxValue is the last representable day and has no next day, so it means "no upper bound"
        // instead of an AddDays overflow. The tenant is still the scope and the client and concept filters still
        // apply below: only the date ceiling disappears.
        var unbounded = await repository.ListAsync(own.BusinessId, new PaymentLedgerQuery(To: DateOnly.MaxValue));
        Assert.Equal(2, unbounded.Count);
        Assert.DoesNotContain(unbounded, row => row.PaymentId == foreign.Payment);
        Assert.Equal(MembershipAmount, Assert.Single(unbounded, row => row.PaymentId == own.Payment).Amount);
        Assert.Equal(2000m, Assert.Single(unbounded, row => row.PaymentId == lateOwn).Amount);

        var onlyForeign = Assert.Single(await repository.ListAsync(
            foreign.BusinessId, new PaymentLedgerQuery(To: DateOnly.MaxValue)));
        Assert.Equal(foreign.Payment, onlyForeign.PaymentId);

        var filteredRow = Assert.Single(await repository.ListAsync(
            own.BusinessId,
            new PaymentLedgerQuery(
                ClientId: own.ClientId,
                Concept: PaymentConcept.Membership,
                From: new DateOnly(2026, 1, 1),
                To: DateOnly.MaxValue)));
        Assert.Equal(own.Payment, filteredRow.PaymentId);
        Assert.Equal(MembershipAmount, filteredRow.Amount);

        // An unbounded upper date still has a lower bound: nothing was paid on the last representable day, so the
        // lower end of the range kept filtering while To stopped being a ceiling.
        Assert.Empty(await repository.ListAsync(
            own.BusinessId, new PaymentLedgerQuery(From: DateOnly.MaxValue, To: DateOnly.MaxValue)));

        // The inclusive day filter is unchanged by the max case: the 23:45 payment is still inside its own day and
        // still outside the day before.
        var sameDay = await repository.ListAsync(
            own.BusinessId,
            new PaymentLedgerQuery(From: new DateOnly(2026, 3, 6), To: new DateOnly(2026, 3, 6)));
        Assert.Equal(2, sameDay.Count);
        Assert.Contains(sameDay, row => row.PaymentId == lateOwn && row.PaidAt == LateSameDayPayment);
        Assert.Empty(await repository.ListAsync(
            own.BusinessId, new PaymentLedgerQuery(To: new DateOnly(2026, 3, 5))));

        // A foreign payment and a missing one are the same answer from the lock read: null, and the row of the other
        // tenant stays invisible.
        await using (var transaction = await session.BeginAsync())
        {
            Assert.Null(await repository.LockPaymentAsync(own.BusinessId, foreign.Payment));
            Assert.Null(await repository.LockPaymentAsync(foreign.BusinessId, own.Payment));
            Assert.Null(await repository.LockPaymentAsync(own.BusinessId, MissingPaymentId));
            Assert.NotNull(await repository.LockPaymentAsync(own.BusinessId, own.Payment));

            /* The guarded inserts are called directly across the tenant boundary, with a foreign and a missing
               payment. What is asserted is the write result, never which refusal member comes back: the port has no
               "not found" result today and this test does not pin one, so a direct caller reads that nothing was
               inserted and the ambiguity of the refusal member stays a documented low-risk gap of the port. */
            var foreignAnnulment = await repository.TryInsertAnnulmentAsync(
                own.BusinessId, NewAnnulment(foreign.Payment, "Cobro duplicado"));
            var foreignRefund = await repository.TryInsertRefundAsync(
                foreign.BusinessId, NewRefund(own.Payment, 1000m, new DateOnly(2026, 3, 21)));
            var missingAnnulment = await repository.TryInsertAnnulmentAsync(
                own.BusinessId, NewAnnulment(MissingPaymentId, "Cobro duplicado"));
            var missingRefund = await repository.TryInsertRefundAsync(
                own.BusinessId, NewRefund(MissingPaymentId, 1000m, new DateOnly(2026, 3, 21)));

            Assert.False(foreignAnnulment.IsInserted);
            Assert.Equal(0, foreignAnnulment.AnnulmentId);
            Assert.False(foreignRefund.IsInserted);
            Assert.Equal(0, foreignRefund.RefundId);
            Assert.False(missingAnnulment.IsInserted);
            Assert.False(missingRefund.IsInserted);

            await transaction.CommitAsync();
        }

        // The unit committed, so the absence below is the guard writing nothing and not a rollback erasing a write:
        // no correction row anywhere and no payment created, removed or moved between the two tenants.
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM PaymentAnnulments"));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Refunds"));
        Assert.Equal(3, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Payments"));
        Assert.Equal(2, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", own.BusinessId)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", foreign.BusinessId)));

        // "No encontramos ese cobro" belongs to the service, not to the port's insert result: a foreign and a missing
        // payment answer payment_not_found for both corrections, and neither leaves a row behind.
        var service = CreateService(session);

        var annulForeign = await service.AnnulAsync(
            own.BusinessId, new AnnulPaymentRequest(foreign.Payment, "Cobro duplicado", "Lucía"));
        Assert.False(annulForeign.Succeeded);
        Assert.Equal("payment_not_found", annulForeign.Error!.Code);

        var refundForeign = await service.RefundAsync(
            own.BusinessId,
            new RefundPaymentRequest(foreign.Payment, 1000m, new DateOnly(2026, 3, 21), "Devolución parcial"));
        Assert.False(refundForeign.Succeeded);
        Assert.Equal("payment_not_found", refundForeign.Error!.Code);

        var annulMissing = await service.AnnulAsync(
            own.BusinessId, new AnnulPaymentRequest(MissingPaymentId, "Cobro duplicado", "Lucía"));
        Assert.False(annulMissing.Succeeded);
        Assert.Equal("payment_not_found", annulMissing.Error!.Code);

        var refundMissing = await service.RefundAsync(
            own.BusinessId,
            new RefundPaymentRequest(MissingPaymentId, 1000m, new DateOnly(2026, 3, 21), "Devolución parcial"));
        Assert.False(refundMissing.Succeeded);
        Assert.Equal("payment_not_found", refundMissing.Error!.Code);

        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM PaymentAnnulments"));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Refunds"));

        output.WriteLine("Max-date and foreign-payment assertions passed: To = DateOnly.MaxValue listed the tenant rows with no upper bound and no overflow while tenant, client, concept and the lower bound still applied, the inclusive day filter kept the 23:45 payment, the lock read answered null for a foreign and a missing payment, the guarded inserts wrote nothing across the two tenants, and the service answered payment_not_found for both corrections.");
    }

    /// <summary>
    /// The service under test, driven by the real SQL repository and the same unit of work they share.
    /// </summary>
    private static PaymentLedgerService CreateService(DbSession session) =>
        new(new SqlPaymentLedgerRepository(session), session, new FakeClock { Now = FixedNow });

    private static PaymentAnnulment NewAnnulment(int paymentId, string reason) => new()
    {
        PaymentId = paymentId,
        Reason = reason,
        AnnulledAt = FixedNow,
        AnnulledBy = "Lucía"
    };

    private static Refund NewRefund(int paymentId, decimal amount, DateOnly refundedOn) => new()
    {
        PaymentId = paymentId,
        Amount = amount,
        RefundedOn = refundedOn,
        Reason = "Motivo de prueba",
        RecordedAt = FixedNow
    };

    /// <summary>
    /// Resolves this test's opt-in target. <c>null</c> means the variable is absent: the test returns N/A without
    /// opening any connection or touching any server.
    /// </summary>
    private async Task<SqlTarget?> ResolveTargetAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"N/A: {VariableName} absent; no SQL connection opened.");
            return null;
        }

        var target = ValidateTarget(supplied, testSuffix);
        var applied = await CreateTargetAsync(target);
        output.WriteLine($"Target: {target.DatabaseName}; DB_ID absent before creation; applied {applied} schema script(s); retained without teardown.");
        return target;
    }

    private static SqlTarget ValidateTarget(string suppliedConnectionString, string testSuffix)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{VariableName} could not be parsed; no SQL connection was opened.");
        }

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T18_SQL_CONNECTION and
        // the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT18Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
        if (!string.Equals(supplied.DataSource, Server, StringComparison.OrdinalIgnoreCase)
            || !supplied.IntegratedSecurity
            || !safeName
            || !string.IsNullOrEmpty(supplied.UserID)
            || !string.IsNullOrEmpty(supplied.Password)
            || !string.IsNullOrEmpty(supplied.AttachDBFilename)
            || supplied.UserInstance
            || !string.IsNullOrEmpty(supplied.FailoverPartner))
        {
            throw new InvalidOperationException(
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT18Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T18 SQL integration test",
            TrustServerCertificate = true
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<int> CreateTargetAsync(SqlTarget target)
    {
        var startInfo = new ProcessStartInfo("sqlcmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-I", "-S", Server, "-d", "master", "-E", "-b", "-h", "-1", "-W", "-Q",
                     $"SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'{target.DatabaseName}') IS NULL THEN 0 ELSE 1 END;"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartSqlCmd(startInfo);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"master preflight failed: {stderr.Trim()}");
        Assert.Equal("0", stdout.Trim());

        // SQL identifiers cannot be parameters. The strict allowlist above excludes brackets and quotes, so quoting
        // the identifier keeps CREATE DATABASE bound to exactly this target, and a competing creator fails loudly
        // instead of letting this run migrate someone else's database.
        var masterConnectionString = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{target.DatabaseName}]", master);
            await create.ExecuteNonQueryAsync();
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        return scripts.Count;
    }

    /// <summary>
    /// Starts <c>sqlcmd</c>, turning a missing binary into an explicit message instead of an uncaught
    /// <see cref="Win32Exception"/> that hides which tool is required.
    /// </summary>
    private static Process StartSqlCmd(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("sqlcmd preflight unavailable.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "sqlcmd could not be started; install the SQL Server command line tools and make sure sqlcmd is on PATH.",
                exception);
        }
    }

    /// <summary>
    /// One tenant with a client, an active enrollment on a paid month and one membership payment, plus an unrelated
    /// individual appointment and its booking used to prove a correction never touches them.
    /// </summary>
    private static async Task<LedgerIds> SeedLedgerAsync(string connectionString, LedgerSeed seed)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessId = await InsertBusinessAsync(connection, transaction, seed.Slug, FixedNow);
        var serviceId = await InsertServiceAsync(connection, transaction, businessId, $"{seed.Slug} service");
        var workerId = await InsertWorkerAsync(connection, transaction, businessId, seed.Slug, seed.Cuil);
        var clientId = await InsertClientAsync(connection, transaction, businessId, $"{seed.Slug}-client", FixedNow);
        var enrollment = await InsertEnrollmentAsync(
            connection, transaction, businessId, clientId, seed.Status, FixedNow);
        var paidAt = seed.PaidAt ?? LedgerSeed.DefaultPaidAt;
        var (payment, period) = await InsertMembershipAsync(
            connection, transaction, businessId, clientId, enrollment, seed.Amount, paidAt, FixedNow);
        var appointment = await InsertAppointmentAsync(
            connection, transaction, businessId, serviceId, workerId, paidAt.AddHours(1), FixedNow);
        var booking = await InsertBookingAsync(
            connection, transaction, businessId, appointment, clientId, enrollment: null, FixedNow);

        await transaction.CommitAsync();
        return new LedgerIds(
            businessId, serviceId, workerId, clientId, enrollment, payment, period, appointment, booking);
    }

    /// <summary>Adds a second membership payment (and its paid period) to the enrollment of an existing ledger.</summary>
    private static async Task<int> SeedExtraMembershipPaymentAsync(
        string connectionString, LedgerIds ids, decimal amount, DateTime paidAt)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var (payment, _) = await InsertMembershipAsync(
            connection, transaction, ids.BusinessId, ids.ClientId, ids.Enrollment, amount, paidAt, FixedNow);
        await transaction.CommitAsync();
        return payment;
    }

    /// <summary>Adds an individual-appointment payment, with its own appointment and booking, to an existing ledger.</summary>
    private static async Task<int> SeedExtraIndividualPaymentAsync(
        string connectionString, LedgerIds ids, decimal amount, DateTime paidAt)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var appointment = await InsertAppointmentAsync(
            connection, transaction, ids.BusinessId, ids.ServiceId, ids.WorkerId, paidAt.AddHours(-1), FixedNow);
        var booking = await InsertBookingAsync(
            connection, transaction, ids.BusinessId, appointment, ids.ClientId, enrollment: null, FixedNow);
        var payment = await InsertIdAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @concept, @bookingId, NULL, @amount, NULL, NULL, @paidAt, @recordedAt)",
            ("@businessId", ids.BusinessId), ("@clientId", ids.ClientId),
            ("@concept", PaymentConcept.IndividualAppointment), ("@bookingId", booking),
            ("@amount", amount), ("@paidAt", paidAt), ("@recordedAt", FixedNow));
        await transaction.CommitAsync();
        return payment;
    }

    private static async Task<int> InsertBusinessAsync(
        SqlConnection connection, SqlTransaction transaction, string slug, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @allowsWalkIns, @createdAt)",
            ("@name", slug), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@allowsWalkIns", true), ("@createdAt", now));

    private static async Task<int> InsertServiceAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, @capacity)",
            ("@businessId", businessId), ("@name", name), ("@price", 100.00m),
            ("@duration", 60), ("@capacity", 4));

    private static async Task<int> InsertWorkerAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string slug, string cuil) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", businessId), ("@firstName", "Synthetic"), ("@lastName", slug),
            ("@phone", "0000000000"), ("@cuil", cuil));

    private static async Task<int> InsertClientAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
            ("@businessId", businessId), ("@firstName", name), ("@lastName", "Synthetic"),
            ("@email", $"{name}@example.invalid"), ("@phone", "0000000000"), ("@createdAt", now));

    private static async Task<int> InsertEnrollmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId,
        EnrollmentStatus status, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, ReferenceDay, CreatedAt, ActivatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @referenceDay, @createdAt, @activatedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", WeeklyFrequency),
            ("@status", status), ("@price", MembershipAmount), ("@referenceDay", ReferenceDay),
            ("@createdAt", now), ("@activatedAt", now));

    private static async Task<(int Payment, int Period)> InsertMembershipAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId, int enrollmentId,
        decimal amount, DateTime paidAt, DateTime now)
    {
        var paymentId = await InsertIdAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @concept, NULL, @enrollmentId, @amount, NULL, NULL, @paidAt, @recordedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@concept", PaymentConcept.Membership),
            ("@enrollmentId", enrollmentId), ("@amount", amount), ("@paidAt", paidAt), ("@recordedAt", now));
        var periodId = await InsertIdAsync(connection, transaction,
            "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) OUTPUT INSERTED.Id VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
            ("@enrollmentId", enrollmentId), ("@coverageStart", PeriodStart), ("@expiresOn", PeriodEnd),
            ("@paymentId", paymentId), ("@createdAt", now));
        return (paymentId, periodId);
    }

    private static async Task<int> InsertAppointmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DateTime startsAt, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.Individual), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(60)),
            ("@origin", AppointmentOrigin.Reserved), ("@status", AppointmentStatus.Scheduled), ("@createdAt", now));

    private static async Task<int> InsertBookingAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int appointmentId, int clientId,
        int? enrollment, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @enrollmentId, NULL, @status, @createdAt)",
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@enrollmentId", enrollment), ("@status", BookingStatus.Pending), ("@createdAt", now));

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task InsertRefundAsync(
        string connectionString, int paymentId, decimal amount, DateOnly refundedOn, string reason) =>
        await ExecuteAsync(connectionString,
            "INSERT INTO Refunds (PaymentId, Amount, RefundedOn, Reason, RecordedAt) VALUES (@paymentId, @amount, @refundedOn, @reason, @recordedAt)",
            ("@paymentId", paymentId), ("@amount", amount), ("@refundedOn", refundedOn),
            ("@reason", reason), ("@recordedAt", FixedNow));

    private static async Task ExecuteAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, null, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters) =>
        await ReadScalarAsync<int>(connectionString, sql, parameters);

    private static async Task<T?> ReadScalarAsync<T>(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, null, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
            return default;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads through the unit of work's own connection and transaction, so a write that is not committed yet is
    /// visible: that is what makes the rollback assertion about an erasure instead of about a write that never
    /// happened.
    /// </summary>
    private static async Task<T?> ReadScalarInUnitAsync<T>(
        DbSession unit, string sql, params (string Name, object? Value)[] parameters)
    {
        var connection = await unit.GetOpenConnectionAsync();
        await using var command = Create(connection, unit.Transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
            return default;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<PaymentSnapshot> ReadPaymentAsync(string connectionString, int paymentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PaidAt, RecordedAt FROM Payments WHERE Id = @paymentId",
            connection);
        AddParameter(command, "@paymentId", paymentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Payment {paymentId} was not found.");
        return new PaymentSnapshot(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            (PaymentConcept)reader.GetByte(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.GetDecimal(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.GetDateTime(8),
            reader.GetDateTime(9));
    }

    private static async Task<List<AnnulmentSnapshot>> ReadAnnulmentsAsync(string connectionString, int paymentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, PaymentId, Reason, AnnulledAt, AnnulledBy FROM PaymentAnnulments WHERE PaymentId = @paymentId ORDER BY Id",
            connection);
        AddParameter(command, "@paymentId", paymentId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AnnulmentSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new AnnulmentSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetDateTime(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static async Task<List<RefundSnapshot>> ReadRefundsAsync(string connectionString, int paymentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, PaymentId, Amount, RefundedOn, Reason, RecordedAt FROM Refunds WHERE PaymentId = @paymentId ORDER BY Id",
            connection);
        AddParameter(command, "@paymentId", paymentId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<RefundSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new RefundSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetDecimal(2),
                DateOnly.FromDateTime(reader.GetDateTime(3)),
                reader.GetString(4),
                reader.GetDateTime(5)));
        }

        return rows;
    }

    private static async Task<EnrollmentSnapshot> ReadEnrollmentAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Status, AgreedPrice, ReferenceDay, ActivatedAt, AnnulledAt, TerminatedAt FROM Enrollments WHERE Id = @enrollmentId",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Enrollment {enrollmentId} was not found.");
        return new EnrollmentSnapshot(
            (EnrollmentStatus)reader.GetByte(0),
            reader.GetDecimal(1),
            reader.IsDBNull(2) ? null : reader.GetByte(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5));
    }

    private static async Task<List<PeriodSnapshot>> ReadPeriodsAsync(string connectionString, int enrollmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, CoverageStart, ExpiresOn, PaymentId FROM MembershipPeriods WHERE EnrollmentId = @enrollmentId ORDER BY CoverageStart, Id",
            connection);
        AddParameter(command, "@enrollmentId", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<PeriodSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new PeriodSnapshot(
                reader.GetInt32(0),
                DateOnly.FromDateTime(reader.GetDateTime(1)),
                DateOnly.FromDateTime(reader.GetDateTime(2)),
                reader.GetInt32(3)));
        }

        return rows;
    }

    private static async Task<List<BookingSnapshot>> ReadBookingsAsync(string connectionString, int businessId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, AppointmentId, ClientId, EnrollmentId, Modality, Status FROM Bookings WHERE BusinessId = @businessId ORDER BY Id",
            connection);
        AddParameter(command, "@businessId", businessId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<BookingSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new BookingSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : (BookingModality)reader.GetByte(4),
                (BookingStatus)reader.GetByte(5)));
        }

        return rows;
    }

    private static SqlCommand Create(
        SqlConnection connection, SqlTransaction? transaction, string sql,
        (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            AddParameter(command, name, value);
        return command;
    }

    private static void AddParameter(SqlCommand command, string name, object? value)
    {
        switch (value)
        {
            case DateTime dateTime:
                command.Parameters.Add(name, SqlDbType.DateTime2).Value = dateTime;
                return;
            case DateOnly date:
                command.Parameters.Add(name, SqlDbType.Date).Value = date.ToDateTime(TimeOnly.MinValue);
                return;
            case TimeOnly time:
                command.Parameters.Add(name, SqlDbType.Time).Value = time;
                return;
            case null:
                command.Parameters.AddWithValue(name, DBNull.Value);
                return;
            case Enum enumeration:
                command.Parameters.AddWithValue(name, Convert.ToByte(enumeration, CultureInfo.InvariantCulture));
                return;
            default:
                command.Parameters.AddWithValue(name, value);
                return;
        }
    }

    /// <summary>One synthetic tenant ready for the ledger: a membership payment and the reference rows it never touches.</summary>
    private sealed record LedgerSeed(
        string Slug,
        string Cuil,
        decimal Amount = MembershipAmount,
        DateTime? PaidAt = null,
        EnrollmentStatus Status = EnrollmentStatus.Active)
    {
        public static DateTime DefaultPaidAt { get; } = new(2026, 3, 1, 10, 0, 0);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record LedgerIds(
        int BusinessId,
        int ServiceId,
        int WorkerId,
        int ClientId,
        int Enrollment,
        int Payment,
        int Period,
        int Appointment,
        int Booking);

    private sealed record PaymentSnapshot(
        int Id,
        int BusinessId,
        int? ClientId,
        PaymentConcept Concept,
        int? BookingId,
        int? EnrollmentId,
        decimal Amount,
        decimal? ListPrice,
        DateTime PaidAt,
        DateTime RecordedAt);

    private sealed record AnnulmentSnapshot(
        int Id, int PaymentId, string Reason, DateTime AnnulledAt, string AnnulledBy);

    private sealed record RefundSnapshot(
        int Id, int PaymentId, decimal Amount, DateOnly RefundedOn, string Reason, DateTime RecordedAt);

    private sealed record EnrollmentSnapshot(
        EnrollmentStatus Status,
        decimal AgreedPrice,
        int? ReferenceDay,
        DateTime? ActivatedAt,
        DateTime? AnnulledAt,
        DateTime? TerminatedAt);

    private sealed record PeriodSnapshot(int Id, DateOnly CoverageStart, DateOnly ExpiresOn, int PaymentId);

    private sealed record BookingSnapshot(
        int Id, int AppointmentId, int ClientId, int? EnrollmentId, BookingModality? Modality, BookingStatus Status);
}