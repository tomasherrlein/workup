using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Reports;
using WorkUp.Data;
using WorkUp.Data.Reports;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Reports;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlReportsRepository"/> and the report service that
/// drives it (RF-16, D-20, assumption 7). It requires a new, dedicated database supplied through
/// WORKUP_T19_SQL_CONNECTION; without it every test returns N/A and no SQL connection is opened. Each test derives
/// its own database name from the supplied one, so the whole class runs in a single command, and nothing is torn
/// down: the target is retained on purpose.
/// <para>
/// The unit tests prove the report rules against an in-memory port; they cannot prove what storage resolves. This
/// fixture is the runtime proof of the read model: the payment of the period read by the day it was paid with its
/// session, worker and service already joined — a membership left with no session at all —, the refund subtracted on
/// the day the money came back and not on the day of the original payment, the expense of the period, the attendance
/// of the period and its 90-day lookback read by the day of its session with its exact boundaries, the tenant that
/// stays invisible in all four projections, the period clipped to today, and the read that writes nothing.
/// </para>
/// <para>
/// The snapshot is one command with four result sets, and the validated target has MARS off, so a repository that
/// tried a read per payment while the snapshot reader was open would fail instead of passing silently: the successful
/// read of every row in one call is the runtime evidence that there is no query per row.
/// </para>
/// </summary>
public sealed class ReportsSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T19_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>Friday 2026-04-10 09:00, the single clock instant every report is computed from.</summary>
    private static readonly DateTime FixedNow = new(2026, 4, 10, 9, 0, 0);

    private static readonly DateOnly MarchFrom = new(2026, 3, 1);
    private static readonly DateOnly MarchTo = new(2026, 3, 31);

    /// <summary>The first day of the 90-day lookback of a March period: 2025-11-30 is already outside it.</summary>
    private static readonly DateOnly LookbackFrom = new(2025, 12, 1);

    private const string Yoga = "Yoga";
    private const string Corte = "Corte";
    private const string Alquiler = "Alquiler";
    private const string Lucia = "Lucía";
    private const string Marina = "Marina";

    /// <summary>
    /// A worker is read by its full name — <c>CONCAT(FirstName, ' ', LastName)</c> — so an attendance of a synthetic
    /// worker seeded as <see cref="Lucia"/> arrives as this. The seeded first name alone is never the label.
    /// </summary>
    private const string LuciaFullName = "Lucía Synthetic";

    /// <summary>The full name of a synthetic worker seeded with the first name <see cref="Marina"/>.</summary>
    private const string MarinaFullName = "Marina Synthetic";

    [Fact]
    public async Task The_period_result_reads_money_of_the_period_only_and_never_fans_out_over_refunds()
    {
        var target = await ResolveTargetAsync("_PeriodMoney");
        if (target is null)
            return;

        var ids = await SeedPeriodMoneyAsync(target.ConnectionString);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlReportsRepository(session);
        var service = new ReportsService(repository, new FakeClock { Now = FixedNow });

        // The validated target has MARS off, so the single open reader below cannot be joined by a read per row.
        Assert.False(new SqlConnectionStringBuilder(target.ConnectionString).MultipleActiveResultSets);

        var snapshot = await repository.LoadAsync(
            ids.BusinessId, new ReportReadRequest(MarchFrom, MarchTo, LookbackFrom));

        // Three payments of the period: the membership, the annulled individual one and the class one whose two
        // refunds are a separate projection — the payment is one row and never one row per refund.
        Assert.Equal(3, snapshot.Payments.Count);
        Assert.Equal(6800m, snapshot.Payments.Sum(row => row.Amount));

        var membership = Assert.Single(snapshot.Payments, row => row.PaymentId == ids.MembershipPayment);
        Assert.Equal(PaymentConcept.Membership, membership.Concept);
        Assert.Equal(5000m, membership.Amount);
        Assert.Equal(new DateOnly(2026, 3, 2), membership.PaidOn);
        Assert.False(membership.Annulled);
        Assert.Null(membership.AppointmentId);
        Assert.Null(membership.WorkerId);
        Assert.Null(membership.ServiceId);
        Assert.Null(membership.ServiceName);
        Assert.Null(membership.SessionKind);

        var annulled = Assert.Single(snapshot.Payments, row => row.PaymentId == ids.AnnulledPayment);
        Assert.True(annulled.Annulled);
        Assert.Equal(1000m, annulled.Amount);
        Assert.Equal(ids.IndividualSession, annulled.AppointmentId);
        Assert.Equal(AppointmentKind.Individual, annulled.SessionKind);
        Assert.Equal(Corte, annulled.ServiceName);

        var classRow = Assert.Single(snapshot.Payments, row => row.PaymentId == ids.ClassPayment);
        Assert.Equal(PaymentConcept.DropInClass, classRow.Concept);
        Assert.Equal(800m, classRow.Amount);
        Assert.Equal(new DateOnly(2026, 3, 9), classRow.PaidOn);
        Assert.False(classRow.Annulled);
        Assert.Equal(ids.ClassSession, classRow.AppointmentId);
        Assert.Equal(ids.WorkerId, classRow.WorkerId);
        Assert.Equal(ids.ServiceId, classRow.ServiceId);
        Assert.Equal(Yoga, classRow.ServiceName);
        Assert.Equal(AppointmentKind.ClassSession, classRow.SessionKind);

        // No row of the other business, in any of the four projections.
        Assert.DoesNotContain(snapshot.Payments, row => row.Amount == 9000m);
        Assert.DoesNotContain(snapshot.Refunds, row => row.Amount == 9000m);
        Assert.DoesNotContain(snapshot.Expenses, row => row.Amount == 9000m);
        Assert.DoesNotContain(snapshot.Attendances, row => row.AppointmentId == ids.ForeignSession);

        // The refund of the period, by the day the money came back: the April one is not here.
        var refund = Assert.Single(snapshot.Refunds);
        Assert.Equal(ids.ClassPayment, refund.PaymentId);
        Assert.Equal(200m, refund.Amount);
        Assert.Equal(new DateOnly(2026, 3, 20), refund.RefundedOn);

        var expense = Assert.Single(snapshot.Expenses);
        Assert.Equal(ids.ExpenseTypeId, expense.ExpenseTypeId);
        Assert.Equal(Alquiler, expense.ExpenseTypeName);
        Assert.Equal(150m, expense.Amount);
        Assert.Equal(new DateOnly(2026, 3, 15), expense.SpentOn);

        // Attendance arrives with its status: the read hands every booking of the window to the service, which is
        // where the attention and the absence are decided.
        Assert.Equal(2, snapshot.Attendances.Count);
        var classAttendance = Assert.Single(snapshot.Attendances, row => row.AppointmentId == ids.ClassSession);
        Assert.Equal(ids.ClientId, classAttendance.ClientId);
        Assert.Equal(ids.WorkerId, classAttendance.WorkerId);
        Assert.Equal(LuciaFullName, classAttendance.WorkerName);
        Assert.Equal(BookingStatus.Attended, classAttendance.Status);
        Assert.Equal(new DateOnly(2026, 3, 9), classAttendance.OccurredOn);
        var pending = Assert.Single(snapshot.Attendances, row => row.AppointmentId == ids.IndividualSession);
        Assert.Equal(BookingStatus.Pending, pending.Status);

        var march = (await service.GetAsync(ids.BusinessId, new ReportQuery(new ReportPeriod(MarchFrom, MarchTo)))).Value;
        Assert.Equal(5800m, march.Period.PaymentsTotal);
        Assert.Equal(200m, march.Period.RefundsTotal);
        Assert.Equal(150m, march.Period.ExpensesTotal);
        Assert.Equal(5450m, march.Period.NetResult);
        Assert.False(march.Period.IsPartial);
        Assert.Equal(5000m, march.Period.MembershipRevenue);
        Assert.Equal(0m, march.Period.UnallocatedRevenue);
        var line = Assert.Single(march.Period.RevenueByService);
        Assert.Equal(ids.ServiceId, line.ServiceId);
        Assert.Equal(800m, line.Amount);
        Assert.Equal(Alquiler, Assert.Single(march.Period.ExpensesByType).ExpenseTypeName);
        var production = Assert.Single(march.Period.WorkerProduction);
        Assert.Equal(1, production.AttendedCount);
        Assert.Equal(800m, production.CollectedAmount);

        // The money that came back in April belongs to April, never to the day of the payment.
        var april = (await service.GetAsync(ids.BusinessId, new ReportQuery(
            new ReportPeriod(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30))))).Value;
        Assert.True(april.Period.IsPartial);
        Assert.Equal(new DateOnly(2026, 4, 10), april.Period.EffectiveTo);
        Assert.Equal(0m, april.Period.PaymentsTotal);
        Assert.Equal(300m, april.Period.RefundsTotal);
        Assert.Equal(-300m, april.Period.NetResult);

        // Reading a report wrote nothing.
        Assert.Equal(3, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Payments WHERE BusinessId = @businessId", ("@businessId", ids.BusinessId)));
        Assert.Equal(1, await CountAsync(target.ConnectionString, """
            SELECT COUNT(*) FROM PaymentAnnulments pa
            JOIN Payments p ON p.Id = pa.PaymentId WHERE p.BusinessId = @businessId
            """, ("@businessId", ids.BusinessId)));
        Assert.Equal(2, await CountAsync(target.ConnectionString, """
            SELECT COUNT(*) FROM Refunds r
            JOIN Payments p ON p.Id = r.PaymentId WHERE p.BusinessId = @businessId
            """, ("@businessId", ids.BusinessId)));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Expenses WHERE BusinessId = @businessId", ("@businessId", ids.BusinessId)));

        output.WriteLine("Period-money assertions passed: the snapshot carried the three payments one per payment, the membership without a session, the annulled flag, the two-refund payment counted once, the March refund by RefundedOn and the April one excluded, the expense of the period, the attendance with its status, and the read wrote nothing.");
    }

    [Fact]
    public async Task Revenue_is_allocated_only_to_a_concrete_session_and_worker_production_counts_attentions()
    {
        var target = await ResolveTargetAsync("_Allocation");
        if (target is null)
            return;

        var ids = await SeedAllocationAsync(target.ConnectionString);

        await using var session = new DbSession(target.ConnectionString);
        var service = new ReportsService(new SqlReportsRepository(session), new FakeClock { Now = FixedNow });

        var report = (await service.GetAsync(ids.BusinessId, new ReportQuery(new ReportPeriod(MarchFrom, MarchTo)))).Value.Period;

        Assert.Equal(7350m, report.PaymentsTotal);
        Assert.Equal(5000m, report.MembershipRevenue);
        Assert.Equal(0m, report.UnallocatedRevenue);

        // Two concrete services, one per kind: the class of Yoga and the individual turn of Corte. Revenue is what
        // was billed and not what was attended, so the Corte line carries both individual collections — the attended
        // turn of 600 plus the pending one of 950 — while production below counts only the attended session.
        Assert.Equal(2, report.RevenueByService.Count);
        var individual = report.RevenueByService[0];
        Assert.Equal(ids.CorteServiceId, individual.ServiceId);
        Assert.Equal(Corte, individual.ServiceName);
        Assert.Equal(AppointmentKind.Individual, individual.Kind);
        Assert.Equal(1550m, individual.Amount);
        var classLine = report.RevenueByService[1];
        Assert.Equal(ids.YogaServiceId, classLine.ServiceId);
        Assert.Equal(Yoga, classLine.ServiceName);
        Assert.Equal(AppointmentKind.ClassSession, classLine.Kind);
        Assert.Equal(800m, classLine.Amount);

        // Every peso is in exactly one bucket.
        Assert.Equal(
            report.PaymentsTotal,
            report.RevenueByService.Sum(row => row.Amount) + report.MembershipRevenue + report.UnallocatedRevenue);

        // The two attentions of the class belong to Lucía and the attended individual turn to Marina, each read by
        // its full name. The session nobody attended is money of the period but not production, so the 950 it billed
        // never reaches Marina's production, and the membership belongs to no worker.
        var lucia = Assert.Single(report.WorkerProduction, row => row.WorkerId == ids.LuciaWorkerId);
        Assert.Equal(LuciaFullName, lucia.WorkerName);
        Assert.Equal(2, lucia.AttendedCount);
        Assert.Equal(800m, lucia.CollectedAmount);
        var marina = Assert.Single(report.WorkerProduction, row => row.WorkerId == ids.MarinaWorkerId);
        Assert.Equal(MarinaFullName, marina.WorkerName);
        Assert.Equal(1, marina.AttendedCount);
        Assert.Equal(600m, marina.CollectedAmount);

        // The absence is counted, the cancelled booking is neither an attention nor an absence.
        Assert.Equal(1, report.AbsenceCount);
        Assert.Equal(0, report.RecurringClientCount);

        // The read resolved the session of every payment of the window, the unattended one included.
        var snapshot = await new SqlReportsRepository(session).LoadAsync(
            ids.BusinessId, new ReportReadRequest(MarchFrom, MarchTo, LookbackFrom));
        Assert.All(
            snapshot.Payments.Where(row => row.Concept != PaymentConcept.Membership),
            row =>
            {
                Assert.NotNull(row.AppointmentId);
                Assert.NotNull(row.WorkerId);
                Assert.NotNull(row.ServiceId);
                Assert.NotNull(row.SessionKind);
            });
        var unattendedRow = Assert.Single(snapshot.Payments, row => row.PaymentId == ids.UnattendedPayment);
        Assert.Equal(ids.UnattendedSession, unattendedRow.AppointmentId);
        Assert.Equal(ids.MarinaWorkerId, unattendedRow.WorkerId);
        Assert.Equal(Corte, unattendedRow.ServiceName);

        output.WriteLine("Allocation assertions passed: the class and the individual service made one revenue line each with their kind while the individual line billed the attended and the pending turn, the membership stayed in the unallocated Abonos bucket, every peso was in exactly one bucket, production counted the attentions of each worker with the money of the attended sessions only, the unattended session was money of the period but not production, and the absence was counted while the cancelled booking was not.");
    }

    [Fact]
    public async Task Recurring_and_lapsed_clients_follow_the_exact_90_day_boundary()
    {
        var target = await ResolveTargetAsync("_Lookback");
        if (target is null)
            return;

        var ids = await SeedLookbackAsync(target.ConnectionString);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlReportsRepository(session);
        var service = new ReportsService(repository, new FakeClock { Now = FixedNow });

        var snapshot = await repository.LoadAsync(
            ids.BusinessId, new ReportReadRequest(MarchFrom, MarchTo, LookbackFrom));

        // The lookback window starts exactly on the first day of the 90 days before the period and the session of the
        // day before is outside it. The period ends exactly on its last day and the session of the day after is out.
        var occurred = snapshot.Attendances.Select(row => row.OccurredOn).ToArray();
        Assert.Equal(new DateOnly(2025, 12, 1), occurred.Min());
        Assert.Equal(new DateOnly(2026, 3, 31), occurred.Max());
        Assert.Contains(snapshot.Attendances, row => row.AppointmentId == ids.BoundarySession);
        Assert.DoesNotContain(snapshot.Attendances, row => row.OccurredOn == new DateOnly(2025, 11, 30));
        Assert.DoesNotContain(snapshot.Attendances, row => row.OccurredOn == new DateOnly(2026, 4, 1));

        // The absence travels with its status instead of being filtered away by the read.
        var absence = Assert.Single(snapshot.Attendances, row => row.AppointmentId == ids.AbsenceSession);
        Assert.Equal(BookingStatus.Absent, absence.Status);
        Assert.Equal(ids.LapsedClient, absence.ClientId);

        var report = (await service.GetAsync(ids.BusinessId, new ReportQuery(new ReportPeriod(MarchFrom, MarchTo)))).Value.Period;

        // One client attended twice inside the period; the boundary client attended exactly on the first lookback day
        // and not inside, and the client of the absence attended in the lookback and is absent inside: two lapsed.
        Assert.Equal(1, report.RecurringClientCount);
        Assert.Equal(2, report.LapsedClientCount);
        Assert.Equal(1, report.AbsenceCount);

        output.WriteLine("Lookback assertions passed: the attendance window started exactly on 2025-12-01 and ended exactly on 2026-03-31 while 2025-11-30 and 2026-04-01 stayed out, the absence arrived with its status, and the report counted one recurring client and the two lapsed ones.");
    }

    [Fact]
    public async Task Another_business_is_invisible_in_every_result_set()
    {
        var target = await ResolveTargetAsync("_Tenant");
        if (target is null)
            return;

        var ids = await SeedTenantAsync(target.ConnectionString);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlReportsRepository(session);
        var service = new ReportsService(repository, new FakeClock { Now = FixedNow });

        var own = await repository.LoadAsync(ids.OwnBusinessId, new ReportReadRequest(MarchFrom, MarchTo, LookbackFrom));
        Assert.Equal(2, own.Payments.Count);
        Assert.Equal(900m, own.Payments.Sum(row => row.Amount));
        Assert.Equal(100m, Assert.Single(own.Refunds).Amount);
        Assert.Equal(60m, Assert.Single(own.Expenses).Amount);
        // The read hands every booking of the window to the caller — Pending, Attended and Absent — and lets the
        // service decide which one is an attention, so nothing is filtered away by the read: the three bookings of
        // this business arrive here and the absence is counted from the service, not here.
        Assert.Equal(3, own.Attendances.Count);
        Assert.All(own.Attendances, row => Assert.NotEqual(ids.ForeignSessionAttended, row.AppointmentId));

        var foreign = await repository.LoadAsync(
            ids.ForeignBusinessId, new ReportReadRequest(MarchFrom, MarchTo, LookbackFrom));
        Assert.Equal(2, foreign.Payments.Count);
        Assert.Equal(9000m, foreign.Payments.Sum(row => row.Amount));
        Assert.Equal(1000m, Assert.Single(foreign.Refunds).Amount);
        Assert.Equal(600m, Assert.Single(foreign.Expenses).Amount);
        // Its three bookings arrive the same way, and none of them belongs to the other business.
        Assert.Equal(3, foreign.Attendances.Count);
        Assert.All(foreign.Attendances, row => Assert.NotEqual(ids.OwnSessionAttended, row.AppointmentId));

        // The report of one business carries only its own money, its own absences and its own attendance.
        var ownReport = (await service.GetAsync(
            ids.OwnBusinessId, new ReportQuery(new ReportPeriod(MarchFrom, MarchTo)))).Value.Period;
        Assert.Equal(500m, ownReport.PaymentsTotal);
        Assert.Equal(100m, ownReport.RefundsTotal);
        Assert.Equal(60m, ownReport.ExpensesTotal);
        Assert.Equal(340m, ownReport.NetResult);
        Assert.Equal(500m, ownReport.MembershipRevenue);
        Assert.Equal(1, ownReport.AbsenceCount);
        Assert.Equal(0, ownReport.RecurringClientCount);
        Assert.Equal(0, ownReport.LapsedClientCount);

        var foreignReport = (await service.GetAsync(
            ids.ForeignBusinessId, new ReportQuery(new ReportPeriod(MarchFrom, MarchTo)))).Value.Period;
        Assert.Equal(5000m, foreignReport.PaymentsTotal);
        Assert.Equal(1000m, foreignReport.RefundsTotal);
        Assert.Equal(600m, foreignReport.ExpensesTotal);
        Assert.Equal(3400m, foreignReport.NetResult);
        Assert.Equal(5000m, foreignReport.MembershipRevenue);
        Assert.Equal(1, foreignReport.AbsenceCount);
        Assert.Equal(0, foreignReport.RecurringClientCount);

        output.WriteLine("Tenant assertions passed: each business read only its own payments, refunds, expenses and attendance, and each report carried only its own money, absences and indicators.");
    }

    [Fact]
    public async Task A_period_that_reaches_the_future_is_clipped_and_computed_up_to_today()
    {
        var target = await ResolveTargetAsync("_Comparison");
        if (target is null)
            return;

        var ids = await SeedComparisonAsync(target.ConnectionString);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlReportsRepository(session);
        var service = new ReportsService(repository, new FakeClock { Now = FixedNow });

        // The read window is what the service asks for: the attendance of the day exactly on the clipped end is in
        // and the day after it is out, and the future payment of April is out too.
        var clippedRead = await repository.LoadAsync(
            ids.BusinessId,
            new ReportReadRequest(new DateOnly(2026, 3, 15), new DateOnly(2026, 4, 10), new DateOnly(2025, 12, 15)));
        Assert.Equal(2, clippedRead.Payments.Count);
        Assert.Equal(1500m, clippedRead.Payments.Sum(row => row.Amount));
        Assert.DoesNotContain(clippedRead.Payments, row => row.PaymentId == ids.FuturePayment);
        Assert.Equal(3, clippedRead.Attendances.Count);
        Assert.Contains(clippedRead.Attendances, row => row.OccurredOn == new DateOnly(2026, 4, 10));
        Assert.DoesNotContain(clippedRead.Attendances, row => row.OccurredOn > new DateOnly(2026, 4, 10));

        // The comparison period reaches past today, so it is computed up to today and marked partial: no fact of the
        // future is invented while the primary period, which is in the past, stays complete.
        var result = (await service.GetAsync(
            ids.BusinessId,
            new ReportQuery(
                new ReportPeriod(MarchFrom, MarchTo),
                new ReportPeriod(new DateOnly(2026, 3, 20), new DateOnly(2026, 4, 30))))).Value;

        var period = result.Period;
        Assert.False(period.IsPartial);
        Assert.Equal(1000m, period.PaymentsTotal);
        Assert.Equal(200m, period.ExpensesTotal);
        Assert.Equal(800m, period.NetResult);

        var comparison = result.Comparison!;
        Assert.True(comparison.IsPartial);
        Assert.Equal(new DateOnly(2026, 3, 20), comparison.RequestedFrom);
        Assert.Equal(new DateOnly(2026, 4, 30), comparison.RequestedTo);
        Assert.Equal(new DateOnly(2026, 3, 20), comparison.EffectiveFrom);
        Assert.Equal(new DateOnly(2026, 4, 10), comparison.EffectiveTo);
        Assert.Equal(1500m, comparison.PaymentsTotal);
        Assert.Equal(200m, comparison.ExpensesTotal);
        Assert.Equal(1300m, comparison.NetResult);
        var comparisonProduction = Assert.Single(comparison.WorkerProduction);
        Assert.Equal(3, comparisonProduction.AttendedCount);
        Assert.Equal(1500m, comparisonProduction.CollectedAmount);
        Assert.Equal(1, comparison.RecurringClientCount);

        // A period that has not started is not computed at all, and neither is a comparison that has not started.
        var futurePeriod = await service.GetAsync(
            ids.BusinessId, new ReportQuery(new ReportPeriod(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31))));
        Assert.False(futurePeriod.Succeeded);
        Assert.Equal(ReportsService.PeriodInFutureCode, futurePeriod.Error!.Code);

        var futureComparison = await service.GetAsync(
            ids.BusinessId,
            new ReportQuery(
                new ReportPeriod(MarchFrom, MarchTo),
                new ReportPeriod(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31))));
        Assert.False(futureComparison.Succeeded);
        Assert.Equal(ReportsService.PeriodInFutureCode, futureComparison.Error!.Code);

        output.WriteLine("Clipping assertions passed: the clip at today was exact at the read boundary and at the service, the partial comparison was computed up to today and marked partial, its production and recurring count used only facts up to today, and a period and a comparison that had not started were refused.");
    }

    // --- Seeding ------------------------------------------------------------------------------------------------

    private static async Task<PeriodMoneyIds> SeedPeriodMoneyAsync(string connectionString) =>
        await SeedAsync(connectionString, async (connection, transaction) =>
        {
            var businessId = await InsertBusinessAsync(connection, transaction, "synthetic-t19-period", FixedNow);
            var serviceId = await InsertServiceAsync(connection, transaction, businessId, Yoga);
            var corteServiceId = await InsertServiceAsync(connection, transaction, businessId, Corte);
            var workerId = await InsertWorkerAsync(connection, transaction, businessId, Lucia, "20201919190");
            var clientId = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-period-client", FixedNow);
            var enrollmentId = await InsertEnrollmentAsync(connection, transaction, businessId, clientId, FixedNow);
            var expenseTypeId = await InsertExpenseTypeAsync(connection, transaction, businessId, Alquiler);

            var (membershipPayment, _) = await InsertMembershipPaymentAsync(
                connection, transaction, businessId, clientId, enrollmentId, 5000m, new DateTime(2026, 3, 2, 10, 0, 0), FixedNow);

            var individualSession = await InsertIndividualSessionAsync(
                connection, transaction, businessId, corteServiceId, workerId, new DateTime(2026, 3, 5, 11, 0, 0), FixedNow);
            var individualBooking = await InsertBookingAsync(
                connection, transaction, businessId, individualSession, clientId, BookingStatus.Pending, FixedNow);
            var annulledPayment = await InsertSessionPaymentAsync(
                connection, transaction, businessId, clientId, PaymentConcept.IndividualAppointment,
                individualBooking, 1000m, new DateTime(2026, 3, 5, 11, 5, 0), FixedNow);
            await InsertAnnulmentAsync(connection, transaction, annulledPayment, FixedNow);

            var classSession = await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, new DateTime(2026, 3, 9, 18, 0, 0), FixedNow);
            var classBooking = await InsertBookingAsync(
                connection, transaction, businessId, classSession, clientId, BookingStatus.Attended, FixedNow);
            var classPayment = await InsertSessionPaymentAsync(
                connection, transaction, businessId, clientId, PaymentConcept.DropInClass,
                classBooking, 800m, new DateTime(2026, 3, 9, 18, 5, 0), FixedNow);
            await InsertRefundAsync(connection, transaction, classPayment, 200m, new DateOnly(2026, 3, 20), FixedNow);
            await InsertRefundAsync(connection, transaction, classPayment, 300m, new DateOnly(2026, 4, 5), FixedNow);

            await InsertExpenseAsync(
                connection, transaction, businessId, expenseTypeId, new DateOnly(2026, 3, 15), 150m, FixedNow);

            var foreignBusinessId = await InsertBusinessAsync(
                connection, transaction, "synthetic-t19-period-foreign", FixedNow);
            var foreignServiceId = await InsertServiceAsync(connection, transaction, foreignBusinessId, Yoga);
            var foreignWorkerId = await InsertWorkerAsync(connection, transaction, foreignBusinessId, Marina, "20201919191");
            var foreignClientId = await InsertClientAsync(
                connection, transaction, foreignBusinessId, "synthetic-t19-period-foreign-client", FixedNow);
            var foreignEnrollmentId = await InsertEnrollmentAsync(
                connection, transaction, foreignBusinessId, foreignClientId, FixedNow);
            await InsertMembershipPaymentAsync(
                connection, transaction, foreignBusinessId, foreignClientId, foreignEnrollmentId, 7000m, new DateTime(2026, 3, 3, 9, 0, 0), FixedNow);
            var foreignSession = await InsertClassSessionAsync(
                connection, transaction, foreignBusinessId, foreignServiceId, foreignWorkerId, new DateTime(2026, 3, 4, 9, 0, 0), FixedNow);
            var foreignBooking = await InsertBookingAsync(
                connection, transaction, foreignBusinessId, foreignSession, foreignClientId, BookingStatus.Attended, FixedNow);
            var foreignPayment = await InsertSessionPaymentAsync(
                connection, transaction, foreignBusinessId, foreignClientId, PaymentConcept.DropInClass,
                foreignBooking, 9000m, new DateTime(2026, 3, 4, 9, 5, 0), FixedNow);
            await InsertRefundAsync(connection, transaction, foreignPayment, 9000m, new DateOnly(2026, 3, 21), FixedNow);
            var foreignExpenseTypeId = await InsertExpenseTypeAsync(connection, transaction, foreignBusinessId, Alquiler);
            await InsertExpenseAsync(
                connection, transaction, foreignBusinessId, foreignExpenseTypeId, new DateOnly(2026, 3, 16), 9000m, FixedNow);

            return new PeriodMoneyIds(
                businessId, serviceId, workerId, clientId, expenseTypeId, enrollmentId, membershipPayment,
                individualSession, annulledPayment, classSession, classPayment, foreignBusinessId, foreignSession);
        });

    private static async Task<AllocationIds> SeedAllocationAsync(string connectionString) =>
        await SeedAsync(connectionString, async (connection, transaction) =>
        {
            var businessId = await InsertBusinessAsync(connection, transaction, "synthetic-t19-alloc", FixedNow);
            var yogaServiceId = await InsertServiceAsync(connection, transaction, businessId, Yoga);
            var corteServiceId = await InsertServiceAsync(connection, transaction, businessId, Corte);
            var luciaWorkerId = await InsertWorkerAsync(connection, transaction, businessId, Lucia, "20201919290");
            var marinaWorkerId = await InsertWorkerAsync(connection, transaction, businessId, Marina, "20201919291");
            var c1 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-alloc-c1", FixedNow);
            var c2 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-alloc-c2", FixedNow);
            var c3 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-alloc-c3", FixedNow);
            var c4 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-alloc-c4", FixedNow);
            var c5 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-alloc-c5", FixedNow);
            var enrollmentId = await InsertEnrollmentAsync(connection, transaction, businessId, c1, FixedNow);

            var classSession = await InsertClassSessionAsync(
                connection, transaction, businessId, yogaServiceId, luciaWorkerId, new DateTime(2026, 3, 4, 18, 0, 0), FixedNow);
            var c1Booking = await InsertBookingAsync(
                connection, transaction, businessId, classSession, c1, BookingStatus.Attended, FixedNow);
            await InsertBookingAsync(connection, transaction, businessId, classSession, c2, BookingStatus.Attended, FixedNow);
            await InsertBookingAsync(connection, transaction, businessId, classSession, c3, BookingStatus.Absent, FixedNow);
            await InsertBookingAsync(connection, transaction, businessId, classSession, c4, BookingStatus.Cancelled, FixedNow);
            var classPayment = await InsertSessionPaymentAsync(
                connection, transaction, businessId, c1, PaymentConcept.DropInClass,
                c1Booking, 800m, new DateTime(2026, 3, 4, 18, 5, 0), FixedNow);

            var individualSession = await InsertIndividualSessionAsync(
                connection, transaction, businessId, corteServiceId, marinaWorkerId, new DateTime(2026, 3, 6, 10, 0, 0), FixedNow);
            var c5Booking = await InsertBookingAsync(
                connection, transaction, businessId, individualSession, c5, BookingStatus.Attended, FixedNow);
            var individualPayment = await InsertSessionPaymentAsync(
                connection, transaction, businessId, c5, PaymentConcept.IndividualAppointment,
                c5Booking, 600m, new DateTime(2026, 3, 6, 10, 5, 0), FixedNow);

            var unattendedSession = await InsertIndividualSessionAsync(
                connection, transaction, businessId, corteServiceId, marinaWorkerId, new DateTime(2026, 3, 12, 10, 0, 0), FixedNow);
            var c2Booking = await InsertBookingAsync(
                connection, transaction, businessId, unattendedSession, c2, BookingStatus.Pending, FixedNow);
            var unattendedPayment = await InsertSessionPaymentAsync(
                connection, transaction, businessId, c2, PaymentConcept.IndividualAppointment,
                c2Booking, 950m, new DateTime(2026, 3, 12, 10, 5, 0), FixedNow);

            var (membershipPayment, _) = await InsertMembershipPaymentAsync(
                connection, transaction, businessId, c1, enrollmentId, 5000m, new DateTime(2026, 3, 2, 9, 0, 0), FixedNow);

            Assert.True(classPayment > 0 && individualPayment > 0 && membershipPayment > 0);
            return new AllocationIds(
                businessId, yogaServiceId, corteServiceId, luciaWorkerId, marinaWorkerId,
                classSession, individualSession, unattendedSession, unattendedPayment);
        });

    private static async Task<LookbackIds> SeedLookbackAsync(string connectionString) =>
        await SeedAsync(connectionString, async (connection, transaction) =>
        {
            var businessId = await InsertBusinessAsync(connection, transaction, "synthetic-t19-lookback", FixedNow);
            var serviceId = await InsertServiceAsync(connection, transaction, businessId, Yoga);
            var workerId = await InsertWorkerAsync(connection, transaction, businessId, Lucia, "20201919390");
            var c1 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-lb-c1", FixedNow);
            var c2 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-lb-c2", FixedNow);
            var c3 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-lb-c3", FixedNow);
            var c4 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-lb-c4", FixedNow);
            var c5 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-lb-c5", FixedNow);
            var c6 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-lb-c6", FixedNow);
            var c7 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-lb-c7", FixedNow);

            // The day before the lookback started.
            var beforeSession = await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c2, new DateTime(2025, 11, 30, 18, 0, 0));
            // Exactly on the first day of the lookback.
            var boundarySession = await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c1, new DateTime(2025, 12, 1, 18, 0, 0));
            // Inside the lookback and again inside the period: this client did not stop coming.
            await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c3, new DateTime(2026, 2, 28, 18, 0, 0));
            await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c3, new DateTime(2026, 3, 5, 18, 0, 0));
            // Attended in the lookback and absent inside the period: still lapsed, because an absence is not an attention.
            await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c5, new DateTime(2026, 2, 20, 18, 0, 0));
            var absenceSession = await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, new DateTime(2026, 3, 7, 18, 0, 0), FixedNow);
            await InsertBookingAsync(connection, transaction, businessId, absenceSession, c5, BookingStatus.Absent, FixedNow);
            // Twice inside the period: recurring.
            await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c4, new DateTime(2026, 3, 6, 18, 0, 0));
            await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c4, new DateTime(2026, 3, 13, 18, 0, 0));
            // Exactly on the last day of the period.
            await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c6, new DateTime(2026, 3, 31, 18, 0, 0));
            // The day after the period: outside the read window.
            await AttendedSessionAsync(
                connection, transaction, businessId, serviceId, workerId, c7, new DateTime(2026, 4, 1, 18, 0, 0));

            return new LookbackIds(businessId, boundarySession, absenceSession, beforeSession, c5);
        });

    private static async Task<TenantIds> SeedTenantAsync(string connectionString) =>
        await SeedAsync(connectionString, async (connection, transaction) =>
        {
            var ownBusinessId = await InsertBusinessAsync(connection, transaction, "synthetic-t19-tenant-own", FixedNow);
            var ownServiceId = await InsertServiceAsync(connection, transaction, ownBusinessId, Yoga);
            var ownWorkerId = await InsertWorkerAsync(connection, transaction, ownBusinessId, Lucia, "20201919490");
            var ownClientA = await InsertClientAsync(connection, transaction, ownBusinessId, "synthetic-t19-tenant-own-a", FixedNow);
            var ownClientB = await InsertClientAsync(connection, transaction, ownBusinessId, "synthetic-t19-tenant-own-b", FixedNow);
            var ownEnrollment = await InsertEnrollmentAsync(connection, transaction, ownBusinessId, ownClientA, FixedNow);
            var (ownMembershipPayment, _) = await InsertMembershipPaymentAsync(
                connection, transaction, ownBusinessId, ownClientA, ownEnrollment, 500m, new DateTime(2026, 3, 3, 9, 0, 0), FixedNow);
            var ownIndividualSession = await InsertIndividualSessionAsync(
                connection, transaction, ownBusinessId, ownServiceId, ownWorkerId, new DateTime(2026, 3, 4, 11, 0, 0), FixedNow);
            var ownIndividualBooking = await InsertBookingAsync(
                connection, transaction, ownBusinessId, ownIndividualSession, ownClientA, BookingStatus.Pending, FixedNow);
            var ownAnnulledPayment = await InsertSessionPaymentAsync(
                connection, transaction, ownBusinessId, ownClientA, PaymentConcept.IndividualAppointment,
                ownIndividualBooking, 400m, new DateTime(2026, 3, 4, 11, 5, 0), FixedNow);
            await InsertAnnulmentAsync(connection, transaction, ownAnnulledPayment, FixedNow);
            await InsertRefundAsync(connection, transaction, ownMembershipPayment, 100m, new DateOnly(2026, 3, 10), FixedNow);
            var ownExpenseTypeId = await InsertExpenseTypeAsync(connection, transaction, ownBusinessId, Alquiler);
            await InsertExpenseAsync(
                connection, transaction, ownBusinessId, ownExpenseTypeId, new DateOnly(2026, 3, 12), 60m, FixedNow);
            var ownSessionAttended = await AttendedSessionAsync(
                connection, transaction, ownBusinessId, ownServiceId, ownWorkerId, ownClientA, new DateTime(2026, 3, 5, 18, 0, 0));
            var ownSessionAbsent = await InsertClassSessionAsync(
                connection, transaction, ownBusinessId, ownServiceId, ownWorkerId, new DateTime(2026, 3, 6, 18, 0, 0), FixedNow);
            await InsertBookingAsync(connection, transaction, ownBusinessId, ownSessionAbsent, ownClientB, BookingStatus.Absent, FixedNow);

            var foreignBusinessId = await InsertBusinessAsync(connection, transaction, "synthetic-t19-tenant-foreign", FixedNow);
            var foreignServiceId = await InsertServiceAsync(connection, transaction, foreignBusinessId, Yoga);
            var foreignWorkerId = await InsertWorkerAsync(connection, transaction, foreignBusinessId, Marina, "20201919491");
            var foreignClientA = await InsertClientAsync(connection, transaction, foreignBusinessId, "synthetic-t19-tenant-foreign-a", FixedNow);
            var foreignClientB = await InsertClientAsync(connection, transaction, foreignBusinessId, "synthetic-t19-tenant-foreign-b", FixedNow);
            var foreignEnrollment = await InsertEnrollmentAsync(connection, transaction, foreignBusinessId, foreignClientA, FixedNow);
            var (foreignMembershipPayment, _) = await InsertMembershipPaymentAsync(
                connection, transaction, foreignBusinessId, foreignClientA, foreignEnrollment, 5000m, new DateTime(2026, 3, 3, 10, 0, 0), FixedNow);
            var foreignIndividualSession = await InsertIndividualSessionAsync(
                connection, transaction, foreignBusinessId, foreignServiceId, foreignWorkerId, new DateTime(2026, 3, 4, 12, 0, 0), FixedNow);
            var foreignIndividualBooking = await InsertBookingAsync(
                connection, transaction, foreignBusinessId, foreignIndividualSession, foreignClientA, BookingStatus.Pending, FixedNow);
            var foreignAnnulledPayment = await InsertSessionPaymentAsync(
                connection, transaction, foreignBusinessId, foreignClientA, PaymentConcept.IndividualAppointment,
                foreignIndividualBooking, 4000m, new DateTime(2026, 3, 4, 12, 5, 0), FixedNow);
            await InsertAnnulmentAsync(connection, transaction, foreignAnnulledPayment, FixedNow);
            await InsertRefundAsync(connection, transaction, foreignMembershipPayment, 1000m, new DateOnly(2026, 3, 10), FixedNow);
            var foreignExpenseTypeId = await InsertExpenseTypeAsync(connection, transaction, foreignBusinessId, Alquiler);
            await InsertExpenseAsync(
                connection, transaction, foreignBusinessId, foreignExpenseTypeId, new DateOnly(2026, 3, 12), 600m, FixedNow);
            var foreignSessionAttended = await AttendedSessionAsync(
                connection, transaction, foreignBusinessId, foreignServiceId, foreignWorkerId, foreignClientA, new DateTime(2026, 3, 5, 19, 0, 0));
            var foreignSessionAbsent = await InsertClassSessionAsync(
                connection, transaction, foreignBusinessId, foreignServiceId, foreignWorkerId, new DateTime(2026, 3, 6, 19, 0, 0), FixedNow);
            await InsertBookingAsync(connection, transaction, foreignBusinessId, foreignSessionAbsent, foreignClientB, BookingStatus.Absent, FixedNow);

            Assert.True(foreignMembershipPayment > 0 && ownMembershipPayment > 0);
            return new TenantIds(
                ownBusinessId, ownSessionAttended, foreignBusinessId, foreignSessionAttended);
        });

    private static async Task<ComparisonIds> SeedComparisonAsync(string connectionString) =>
        await SeedAsync(connectionString, async (connection, transaction) =>
        {
            var businessId = await InsertBusinessAsync(connection, transaction, "synthetic-t19-compare", FixedNow);
            var serviceId = await InsertServiceAsync(connection, transaction, businessId, Yoga);
            var workerId = await InsertWorkerAsync(connection, transaction, businessId, Lucia, "20201919590");
            var c1 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-cmp-c1", FixedNow);
            var c2 = await InsertClientAsync(connection, transaction, businessId, "synthetic-t19-cmp-c2", FixedNow);
            var expenseTypeId = await InsertExpenseTypeAsync(connection, transaction, businessId, Alquiler);

            var marchSession = await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, new DateTime(2026, 3, 20, 18, 0, 0), FixedNow);
            var marchBooking = await InsertBookingAsync(
                connection, transaction, businessId, marchSession, c1, BookingStatus.Attended, FixedNow);
            var marchPayment = await InsertSessionPaymentAsync(
                connection, transaction, businessId, c1, PaymentConcept.DropInClass,
                marchBooking, 1000m, new DateTime(2026, 3, 20, 18, 5, 0), FixedNow);
            await InsertExpenseAsync(
                connection, transaction, businessId, expenseTypeId, new DateOnly(2026, 3, 21), 200m, FixedNow);

            var aprilSession = await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, new DateTime(2026, 4, 5, 18, 0, 0), FixedNow);
            var aprilBooking = await InsertBookingAsync(
                connection, transaction, businessId, aprilSession, c2, BookingStatus.Attended, FixedNow);
            var aprilPayment = await InsertSessionPaymentAsync(
                connection, transaction, businessId, c2, PaymentConcept.DropInClass,
                aprilBooking, 500m, new DateTime(2026, 4, 5, 18, 5, 0), FixedNow);

            // Exactly on today: the last day the clipped window reaches.
            var todaySession = await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, new DateTime(2026, 4, 10, 18, 0, 0), FixedNow);
            await InsertBookingAsync(connection, transaction, businessId, todaySession, c2, BookingStatus.Attended, FixedNow);

            // The day after today and the future payment: never computed.
            var tomorrowSession = await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, new DateTime(2026, 4, 11, 18, 0, 0), FixedNow);
            await InsertBookingAsync(connection, transaction, businessId, tomorrowSession, c2, BookingStatus.Attended, FixedNow);
            var futureSession = await InsertClassSessionAsync(
                connection, transaction, businessId, serviceId, workerId, new DateTime(2026, 4, 20, 18, 0, 0), FixedNow);
            var futureBooking = await InsertBookingAsync(
                connection, transaction, businessId, futureSession, c1, BookingStatus.Attended, FixedNow);
            var futurePayment = await InsertSessionPaymentAsync(
                connection, transaction, businessId, c1, PaymentConcept.DropInClass,
                futureBooking, 700m, new DateTime(2026, 4, 20, 18, 5, 0), FixedNow);

            Assert.True(marchPayment > 0 && aprilPayment > 0);
            return new ComparisonIds(businessId, marchPayment, aprilPayment, futurePayment);
        });

    /// <summary>One class session attended by one client, with no payment: the attendance of an indicator.</summary>
    private static async Task<int> AttendedSessionAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        int clientId, DateTime startsAt)
    {
        var sessionId = await InsertClassSessionAsync(connection, transaction, businessId, serviceId, workerId, startsAt, FixedNow);
        await InsertBookingAsync(connection, transaction, businessId, sessionId, clientId, BookingStatus.Attended, FixedNow);
        return sessionId;
    }

    // --- Schema inserts -----------------------------------------------------------------------------------------

    private static async Task<T> SeedAsync<T>(
        string connectionString, Func<SqlConnection, SqlTransaction, Task<T>> work)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var result = await work(connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<int> InsertBusinessAsync(
        SqlConnection connection, SqlTransaction transaction, string slug, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @allowsWalkIns, @createdAt)",
            ("@name", slug), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@allowsWalkIns", true), ("@createdAt", now));

    private static async Task<int> InsertExpenseTypeAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO ExpenseTypes (BusinessId, Name) OUTPUT INSERTED.Id VALUES (@businessId, @name)",
            ("@businessId", businessId), ("@name", name));

    private static async Task<int> InsertServiceAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, @capacity)",
            ("@businessId", businessId), ("@name", name), ("@price", 100.00m),
            ("@duration", 60), ("@capacity", 4));

    private static async Task<int> InsertWorkerAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string firstName, string cuil) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", businessId), ("@firstName", firstName), ("@lastName", "Synthetic"),
            ("@phone", "0000000000"), ("@cuil", cuil));

    private static async Task<int> InsertClientAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
            ("@businessId", businessId), ("@firstName", name), ("@lastName", "Synthetic"),
            ("@email", $"{name}@example.invalid"), ("@phone", "0000000000"), ("@createdAt", now));

    private static async Task<int> InsertEnrollmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, ReferenceDay, CreatedAt, ActivatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @referenceDay, @createdAt, @activatedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", 1),
            ("@status", EnrollmentStatus.Active), ("@price", 5000m), ("@referenceDay", 1),
            ("@createdAt", now), ("@activatedAt", now));

    private static async Task<(int Payment, int Period)> InsertMembershipPaymentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId, int enrollmentId,
        decimal amount, DateTime paidAt, DateTime now)
    {
        var paymentId = await InsertIdAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @concept, NULL, @enrollmentId, @amount, NULL, NULL, @paidAt, @recordedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@concept", PaymentConcept.Membership),
            ("@enrollmentId", enrollmentId), ("@amount", amount), ("@paidAt", paidAt), ("@recordedAt", now));
        var periodId = await InsertIdAsync(connection, transaction,
            "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) OUTPUT INSERTED.Id VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
            ("@enrollmentId", enrollmentId), ("@coverageStart", new DateOnly(2026, 3, 1)),
            ("@expiresOn", new DateOnly(2026, 4, 1)), ("@paymentId", paymentId), ("@createdAt", now));
        return (paymentId, periodId);
    }

    private static async Task<int> InsertClassSessionAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DateTime startsAt, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 4, NULL, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.ClassSession), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(60)),
            ("@status", AppointmentStatus.Scheduled), ("@createdAt", now));

    private static async Task<int> InsertIndividualSessionAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DateTime startsAt, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.Individual), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(60)),
            ("@origin", AppointmentOrigin.Reserved), ("@status", AppointmentStatus.Scheduled), ("@createdAt", now));

    private static async Task<int> InsertBookingAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int appointmentId, int clientId,
        BookingStatus status, DateTime now)
    {
        var modality = status == BookingStatus.Cancelled
            ? (object?)null
            : BookingModality.DropIn;
        // CK_Bookings_Cancelled: a cancelled booking carries the instant it was cancelled and no other status does.
        var cancelledAt = status == BookingStatus.Cancelled ? (object?)now : null;
        return await InsertIdAsync(connection, transaction,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CancelledAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, NULL, @modality, @status, @cancelledAt, @createdAt)",
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@modality", modality), ("@status", status), ("@cancelledAt", cancelledAt), ("@createdAt", now));
    }

    private static async Task<int> InsertSessionPaymentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId,
        PaymentConcept concept, int bookingId, decimal amount, DateTime paidAt, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @concept, @bookingId, NULL, @amount, NULL, NULL, @paidAt, @recordedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@concept", concept),
            ("@bookingId", bookingId), ("@amount", amount), ("@paidAt", paidAt), ("@recordedAt", now));

    private static async Task InsertAnnulmentAsync(
        SqlConnection connection, SqlTransaction transaction, int paymentId, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO PaymentAnnulments (PaymentId, Reason, AnnulledAt, AnnulledBy) OUTPUT INSERTED.Id VALUES (@paymentId, @reason, @annulledAt, @annulledBy)",
            ("@paymentId", paymentId), ("@reason", "Cobro duplicado"), ("@annulledAt", now), ("@annulledBy", Lucia));

    private static async Task InsertRefundAsync(
        SqlConnection connection, SqlTransaction transaction, int paymentId, decimal amount, DateOnly refundedOn,
        DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Refunds (PaymentId, Amount, RefundedOn, Reason, RecordedAt) OUTPUT INSERTED.Id VALUES (@paymentId, @amount, @refundedOn, @reason, @recordedAt)",
            ("@paymentId", paymentId), ("@amount", amount), ("@refundedOn", refundedOn),
            ("@reason", "Devolución de prueba"), ("@recordedAt", now));

    private static async Task InsertExpenseAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int expenseTypeId,
        DateOnly spentOn, decimal amount, DateTime now) =>
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Expenses (BusinessId, ExpenseTypeId, SpentOn, Amount, Description, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @expenseTypeId, @spentOn, @amount, @description, @createdAt)",
            ("@businessId", businessId), ("@expenseTypeId", expenseTypeId), ("@spentOn", spentOn),
            ("@amount", amount), ("@description", "Gasto de prueba"), ("@createdAt", now));

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // --- Opt-in target ------------------------------------------------------------------------------------------

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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T19_SQL_CONNECTION and
        // the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT19Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT19Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials. MARS stays off on purpose: the snapshot read is one command.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T19 SQL integration test",
            TrustServerCertificate = true,
            MultipleActiveResultSets = false
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

    // --- Plain reads for the read-only proof --------------------------------------------------------------------

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

    // --- ADO.NET plumbing ---------------------------------------------------------------------------------------

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

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record PeriodMoneyIds(
        int BusinessId,
        int ServiceId,
        int WorkerId,
        int ClientId,
        int ExpenseTypeId,
        int Enrollment,
        int MembershipPayment,
        int IndividualSession,
        int AnnulledPayment,
        int ClassSession,
        int ClassPayment,
        int ForeignBusinessId,
        int ForeignSession);

    private sealed record AllocationIds(
        int BusinessId,
        int YogaServiceId,
        int CorteServiceId,
        int LuciaWorkerId,
        int MarinaWorkerId,
        int ClassSession,
        int IndividualSession,
        int UnattendedSession,
        int UnattendedPayment);

    private sealed record LookbackIds(
        int BusinessId, int BoundarySession, int AbsenceSession, int BeforeSession, int LapsedClient);

    private sealed record TenantIds(
        int OwnBusinessId, int OwnSessionAttended, int ForeignBusinessId, int ForeignSessionAttended);

    private sealed record ComparisonIds(
        int BusinessId, int MarchPayment, int AprilPayment, int FuturePayment);
}
