using WorkUp.BusinessLogic.Reports;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Reports;

// T19 (RF-16, D-20, assumption 7): the read-only business report. The period result is the non-annulled payments of
// the period minus the manual refunds by the day the money came back minus the expenses by the day they were spent.
// A refund never counts by the original payment day and an annulled payment never counts at all. Membership revenue
// is shown as unallocated "Abonos" and a service line needs a concrete session. Worker production counts the
// attentions of the worker and the payments attached to the sessions that were actually attended. Recurring and
// lapsed clients follow assumption 7, whose definitions are explicitly provisional. A period that starts after today
// is not computed; one that ends after today is clipped to today and marked partial.
public class ReportsServiceTests
{
    private static readonly DateTime Today = new(2026, 4, 10, 9, 0, 0);
    private static readonly DateOnly MarchFrom = new(2026, 3, 1);
    private static readonly DateOnly MarchTo = new(2026, 3, 31);

    private readonly FakeReportsRepository _repository = new();
    private readonly FakeClock _clock = new();
    private readonly ReportsService _service;

    public ReportsServiceTests()
    {
        _clock.Now = Today;
        _service = new ReportsService(_repository, _clock);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static ReportPeriod March => new(MarchFrom, MarchTo);

    private static ReportPaymentRow Payment(
        int id,
        decimal amount,
        DateOnly paidOn,
        PaymentConcept concept = PaymentConcept.DropInClass,
        int? serviceId = 10,
        string? serviceName = "Yoga",
        AppointmentKind? sessionKind = AppointmentKind.ClassSession,
        int? appointmentId = 100,
        int? workerId = 7,
        bool annulled = false) => new(
            id, concept, appointmentId, workerId, serviceId, serviceName, sessionKind, amount, paidOn, annulled);

    private static ReportRefundRow Refund(int id, int paymentId, decimal amount, DateOnly refundedOn) =>
        new(id, paymentId, amount, refundedOn);

    private static ReportExpenseRow Expense(
        int id, decimal amount, DateOnly spentOn, int typeId = 1, string typeName = "Alquiler") =>
        new(id, typeId, typeName, amount, spentOn);

    private static ReportAttendanceRow Attendance(
        int appointmentId, int clientId, int workerId, string workerName, BookingStatus status, DateOnly occurredOn) =>
        new(appointmentId, clientId, workerId, workerName, status, occurredOn);

    // --- Period result ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Period_result_is_payments_minus_refunds_minus_expenses()
    {
        _repository.Payments.Add((1, Payment(1, 1000m, D(2026, 3, 5))));
        _repository.Payments.Add((1, Payment(
            2, 500m, D(2026, 3, 20),
            concept: PaymentConcept.IndividualAppointment,
            serviceId: 20,
            serviceName: "Corte",
            sessionKind: AppointmentKind.Individual,
            appointmentId: 200)));
        _repository.Refunds.Add((1, Refund(1, 1, 200m, D(2026, 3, 22))));
        _repository.Expenses.Add((1, Expense(1, 300m, D(2026, 3, 15))));

        var result = await _service.GetAsync(1, new ReportQuery(March));

        Assert.True(result.Succeeded);
        var period = result.Value.Period;
        Assert.Equal(1500m, period.PaymentsTotal);
        Assert.Equal(200m, period.RefundsTotal);
        Assert.Equal(300m, period.ExpensesTotal);
        Assert.Equal(1000m, period.NetResult);
        Assert.False(period.IsPartial);
        Assert.Equal(MarchFrom, period.EffectiveFrom);
        Assert.Equal(MarchTo, period.EffectiveTo);
        Assert.Equal(MarchFrom, period.RequestedFrom);
        Assert.Equal(MarchTo, period.RequestedTo);
        Assert.Equal(1, _repository.LoadCallCount);
    }

    [Fact]
    public async Task Annulled_payment_is_excluded_from_the_result_and_from_the_service_revenue()
    {
        _repository.Payments.Add((1, Payment(1, 1000m, D(2026, 3, 5), annulled: true)));
        _repository.Payments.Add((1, Payment(2, 400m, D(2026, 3, 6))));

        var result = await _service.GetAsync(1, new ReportQuery(March));

        var period = result.Value.Period;
        Assert.Equal(400m, period.PaymentsTotal);
        Assert.Equal(400m, period.NetResult);
        var line = Assert.Single(period.RevenueByService);
        Assert.Equal(400m, line.Amount);
    }

    [Fact]
    public async Task Refund_counts_on_the_day_the_money_came_back_and_not_on_the_payment_day()
    {
        // Collected in February: the original payment day is not where the refund is subtracted.
        _repository.Payments.Add((1, Payment(1, 1000m, D(2026, 2, 20))));
        _repository.Refunds.Add((1, Refund(1, 1, 300m, D(2026, 3, 10))));

        var march = await _service.GetAsync(1, new ReportQuery(new ReportPeriod(D(2026, 3, 1), D(2026, 3, 31))));
        var february = await _service.GetAsync(1, new ReportQuery(new ReportPeriod(D(2026, 2, 1), D(2026, 2, 28))));

        Assert.Equal(0m, march.Value.Period.PaymentsTotal);
        Assert.Equal(300m, march.Value.Period.RefundsTotal);
        Assert.Equal(-300m, march.Value.Period.NetResult);

        Assert.Equal(1000m, february.Value.Period.PaymentsTotal);
        Assert.Equal(0m, february.Value.Period.RefundsTotal);
        Assert.Equal(1000m, february.Value.Period.NetResult);
    }

    [Fact]
    public async Task Partial_refunds_subtract_only_the_amount_that_came_back()
    {
        _repository.Payments.Add((1, Payment(1, 1000m, D(2026, 3, 5))));
        _repository.Refunds.Add((1, Refund(1, 1, 300m, D(2026, 3, 8))));
        _repository.Refunds.Add((1, Refund(2, 1, 200m, D(2026, 3, 9))));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Equal(1000m, period.PaymentsTotal);
        Assert.Equal(500m, period.RefundsTotal);
        Assert.Equal(500m, period.NetResult);
    }

    // --- Revenue allocation --------------------------------------------------------------------------------------

    [Fact]
    public async Task Membership_revenue_is_shown_as_unallocated_abonos_and_never_assigned_to_a_service()
    {
        _repository.Payments.Add((1, Payment(
            1, 5000m, D(2026, 3, 2),
            concept: PaymentConcept.Membership,
            serviceId: null,
            serviceName: null,
            sessionKind: null,
            appointmentId: null,
            workerId: null)));
        _repository.Payments.Add((1, Payment(2, 800m, D(2026, 3, 3))));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Equal(5800m, period.PaymentsTotal);
        Assert.Equal(5000m, period.MembershipRevenue);
        Assert.Equal(0m, period.UnallocatedRevenue);
        var line = Assert.Single(period.RevenueByService);
        Assert.Equal(10, line.ServiceId);
        Assert.Equal(800m, line.Amount);

        // Every peso of the period is in exactly one bucket.
        var allocated = period.RevenueByService.Sum(row => row.Amount);
        Assert.Equal(
            period.PaymentsTotal,
            allocated + period.MembershipRevenue + period.UnallocatedRevenue);
    }

    [Fact]
    public async Task Revenue_by_service_keeps_class_and_individual_sessions_in_their_own_lines()
    {
        _repository.Payments.Add((1, Payment(1, 800m, D(2026, 3, 3), appointmentId: 100)));
        _repository.Payments.Add((1, Payment(2, 200m, D(2026, 3, 4), appointmentId: 101)));
        _repository.Payments.Add((1, Payment(
            3, 500m, D(2026, 3, 5),
            concept: PaymentConcept.IndividualAppointment,
            serviceId: 20,
            serviceName: "Corte",
            sessionKind: AppointmentKind.Individual,
            appointmentId: 200)));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Equal(2, period.RevenueByService.Count);
        var yoga = period.RevenueByService.Single(row => row.ServiceId == 10);
        Assert.Equal(1000m, yoga.Amount);
        Assert.Equal(AppointmentKind.ClassSession, yoga.Kind);
        var corte = period.RevenueByService.Single(row => row.ServiceId == 20);
        Assert.Equal(500m, corte.Amount);
        Assert.Equal(AppointmentKind.Individual, corte.Kind);
    }

    [Fact]
    public async Task A_payment_without_a_concrete_session_is_shown_as_unallocated_instead_of_lost()
    {
        _repository.Payments.Add((1, Payment(
            1, 250m, D(2026, 3, 5),
            concept: PaymentConcept.WalkIn,
            serviceId: null,
            serviceName: null,
            sessionKind: null,
            appointmentId: null,
            workerId: null)));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Empty(period.RevenueByService);
        Assert.Equal(0m, period.MembershipRevenue);
        Assert.Equal(250m, period.UnallocatedRevenue);
        Assert.Equal(period.PaymentsTotal, period.UnallocatedRevenue);
    }

    // --- Indicators (assumption 7) -------------------------------------------------------------------------------

    [Fact]
    public async Task Worker_production_counts_attentions_and_only_payments_attached_to_attended_sessions()
    {
        _repository.Attendances.Add((1, Attendance(100, 1, 7, "Lucía", BookingStatus.Attended, D(2026, 3, 4))));
        _repository.Attendances.Add((1, Attendance(100, 2, 7, "Lucía", BookingStatus.Attended, D(2026, 3, 4))));
        _repository.Attendances.Add((1, Attendance(101, 1, 7, "Lucía", BookingStatus.Attended, D(2026, 3, 10))));
        _repository.Attendances.Add((1, Attendance(102, 3, 8, "Marina", BookingStatus.Attended, D(2026, 3, 11))));

        _repository.Payments.Add((1, Payment(1, 800m, D(2026, 3, 4), appointmentId: 100, workerId: 7)));
        _repository.Payments.Add((1, Payment(2, 900m, D(2026, 3, 10), appointmentId: 101, workerId: 7)));
        _repository.Payments.Add((1, Payment(3, 700m, D(2026, 3, 11), appointmentId: 102, workerId: 8)));
        // A session nobody attended: its money is in the period, but it is not worker production.
        _repository.Payments.Add((1, Payment(4, 950m, D(2026, 3, 12), appointmentId: 103, workerId: 7)));
        // An abono is never assigned to a worker.
        _repository.Payments.Add((1, Payment(
            5, 5000m, D(2026, 3, 2),
            concept: PaymentConcept.Membership,
            serviceId: null,
            serviceName: null,
            sessionKind: null,
            appointmentId: null,
            workerId: null)));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Equal(8350m, period.PaymentsTotal);

        var lucia = period.WorkerProduction.Single(row => row.WorkerId == 7);
        Assert.Equal("Lucía", lucia.WorkerName);
        Assert.Equal(3, lucia.AttendedCount);
        Assert.Equal(1700m, lucia.CollectedAmount);

        var marina = period.WorkerProduction.Single(row => row.WorkerId == 8);
        Assert.Equal(1, marina.AttendedCount);
        Assert.Equal(700m, marina.CollectedAmount);
    }

    [Fact]
    public async Task Recurring_client_needs_two_attentions_inside_the_period()
    {
        _repository.Attendances.Add((1, Attendance(100, 1, 7, "Lucía", BookingStatus.Attended, D(2026, 3, 4))));
        _repository.Attendances.Add((1, Attendance(101, 1, 7, "Lucía", BookingStatus.Attended, D(2026, 3, 10))));
        _repository.Attendances.Add((1, Attendance(102, 2, 7, "Lucía", BookingStatus.Attended, D(2026, 3, 5))));
        _repository.Attendances.Add((1, Attendance(103, 3, 8, "Marina", BookingStatus.Attended, D(2026, 3, 6))));
        _repository.Attendances.Add((1, Attendance(104, 3, 8, "Marina", BookingStatus.Attended, D(2026, 3, 7))));
        _repository.Attendances.Add((1, Attendance(105, 3, 8, "Marina", BookingStatus.Attended, D(2026, 3, 8))));
        // An absence is not an attention.
        _repository.Attendances.Add((1, Attendance(106, 2, 7, "Lucía", BookingStatus.Absent, D(2026, 3, 9))));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Equal(2, period.RecurringClientCount);
    }

    [Fact]
    public async Task Lapsed_client_is_read_from_the_90_days_before_the_period()
    {
        // The 90 days before March are 2025-12-01 .. 2026-02-28 inclusive; 2025-11-30 is already outside.
        _repository.Attendances.Add((1, Attendance(100, 1, 7, "Lucía", BookingStatus.Attended, D(2025, 12, 1))));
        _repository.Attendances.Add((1, Attendance(101, 2, 7, "Lucía", BookingStatus.Attended, D(2025, 11, 30))));
        _repository.Attendances.Add((1, Attendance(102, 3, 8, "Marina", BookingStatus.Attended, D(2026, 2, 28))));
        _repository.Attendances.Add((1, Attendance(103, 3, 8, "Marina", BookingStatus.Attended, D(2026, 3, 5))));
        _repository.Attendances.Add((1, Attendance(104, 4, 8, "Marina", BookingStatus.Attended, D(2026, 3, 1))));

        var result = await _service.GetAsync(1, new ReportQuery(March));

        Assert.Equal(1, result.Value.Period.LapsedClientCount);
        Assert.Equal(D(2025, 12, 1), _repository.LoadedRequests[0].AttendanceLookbackFrom);
    }

    [Fact]
    public async Task A_period_so_early_that_the_90_day_lookback_would_underflow_is_clamped()
    {
        // 0001-01-02 is the earliest day the service accepts, because DateOnly.MinValue is the missing-date default
        // it rejects. Ninety days before it do not exist, so the lookback is clamped to the first representable day
        // instead of underflowing the calendar.
        var earliest = new DateOnly(1, 1, 2);
        _repository.Attendances.Add((1, Attendance(100, 1, 7, "Lucía", BookingStatus.Attended, earliest)));
        _repository.Attendances.Add((1, Attendance(101, 1, 7, "Lucía", BookingStatus.Attended, earliest)));

        var result = await _service.GetAsync(1, new ReportQuery(new ReportPeriod(earliest, earliest)));

        Assert.True(result.Succeeded, result.Error?.Code);
        var request = Assert.Single(_repository.LoadedRequests);
        Assert.Equal(DateOnly.MinValue, request.AttendanceLookbackFrom);
        Assert.Equal(earliest, request.PeriodFrom);
        Assert.Equal(earliest, request.PeriodTo);
        Assert.Equal(1, result.Value.Period.RecurringClientCount);

        // DateOnly.MinValue itself is the missing-date default and is refused before reading, which is why the clamp
        // above is reached from the day after it and not from the first representable day.
        var missing = await _service.GetAsync(1, new ReportQuery(new ReportPeriod(DateOnly.MinValue, DateOnly.MinValue)));
        Assert.False(missing.Succeeded);
        Assert.Equal(ReportsService.InvalidPeriodCode, missing.Error!.Code);
        Assert.Single(_repository.LoadedRequests);
    }

    [Fact]
    public async Task Absence_count_uses_the_absent_status_and_ignores_the_period_outside()
    {
        _repository.Attendances.Add((1, Attendance(100, 1, 7, "Lucía", BookingStatus.Absent, D(2026, 3, 4))));
        _repository.Attendances.Add((1, Attendance(101, 2, 7, "Lucía", BookingStatus.Absent, D(2026, 3, 5))));
        _repository.Attendances.Add((1, Attendance(102, 3, 7, "Lucía", BookingStatus.Absent, D(2026, 3, 6))));
        _repository.Attendances.Add((1, Attendance(103, 4, 7, "Lucía", BookingStatus.Attended, D(2026, 3, 7))));
        // Outside the period.
        _repository.Attendances.Add((1, Attendance(104, 5, 7, "Lucía", BookingStatus.Absent, D(2026, 4, 1))));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Equal(3, period.AbsenceCount);
    }

    [Fact]
    public async Task Expenses_are_totalled_by_type_for_the_period()
    {
        _repository.Expenses.Add((1, Expense(1, 300m, D(2026, 3, 2), typeId: 1, typeName: "Alquiler")));
        _repository.Expenses.Add((1, Expense(2, 200m, D(2026, 3, 3), typeId: 1, typeName: "Alquiler")));
        _repository.Expenses.Add((1, Expense(3, 100m, D(2026, 3, 4), typeId: 2, typeName: "Insumos")));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Equal(600m, period.ExpensesTotal);
        Assert.Equal(2, period.ExpensesByType.Count);
        Assert.Equal(500m, period.ExpensesByType.Single(row => row.ExpenseTypeId == 1).Amount);
        Assert.Equal(100m, period.ExpensesByType.Single(row => row.ExpenseTypeId == 2).Amount);
    }

    // --- Comparison ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Comparison_applies_the_same_rules_to_the_second_period()
    {
        _repository.Payments.Add((1, Payment(
            1, 5000m, D(2026, 3, 5),
            concept: PaymentConcept.Membership,
            serviceId: null,
            serviceName: null,
            sessionKind: null,
            appointmentId: null,
            workerId: null)));
        _repository.Expenses.Add((1, Expense(1, 200m, D(2026, 3, 6))));
        _repository.Payments.Add((1, Payment(
            2, 600m, D(2026, 2, 6),
            concept: PaymentConcept.Membership,
            serviceId: null,
            serviceName: null,
            sessionKind: null,
            appointmentId: null,
            workerId: null)));
        _repository.Refunds.Add((1, Refund(1, 2, 100m, D(2026, 2, 7))));

        var result = await _service.GetAsync(1, new ReportQuery(
            new ReportPeriod(D(2026, 3, 1), D(2026, 3, 31)),
            new ReportPeriod(D(2026, 2, 1), D(2026, 2, 28))));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value.Comparison);

        Assert.Equal(4800m, result.Value.Period.NetResult);
        Assert.Equal(5000m, result.Value.Period.MembershipRevenue);

        var comparison = result.Value.Comparison!;
        Assert.Equal(600m, comparison.PaymentsTotal);
        Assert.Equal(100m, comparison.RefundsTotal);
        Assert.Equal(500m, comparison.NetResult);
        Assert.Equal(600m, comparison.MembershipRevenue);
        Assert.Empty(comparison.RevenueByService);
        Assert.False(comparison.IsPartial);
        Assert.Equal(2, _repository.LoadCallCount);
    }

    // --- Bounds (today) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Period_that_starts_after_today_is_not_computed()
    {
        var result = await _service.GetAsync(1, new ReportQuery(new ReportPeriod(D(2026, 5, 1), D(2026, 5, 31))));

        Assert.False(result.Succeeded);
        Assert.Equal(ReportsService.PeriodInFutureCode, result.Error!.Code);
        Assert.Equal(0, _repository.LoadCallCount);
    }

    [Fact]
    public async Task Period_that_ends_after_today_is_clipped_to_today_and_marked_partial()
    {
        _repository.Payments.Add((1, Payment(1, 1000m, D(2026, 4, 5))));
        // Dated after the business's today: never counted, because the period does not reach it.
        _repository.Payments.Add((1, Payment(2, 700m, D(2026, 4, 20))));

        var result = await _service.GetAsync(1, new ReportQuery(new ReportPeriod(D(2026, 3, 15), D(2026, 4, 30))));

        var period = result.Value.Period;
        Assert.True(period.IsPartial);
        Assert.Equal(D(2026, 3, 15), period.EffectiveFrom);
        Assert.Equal(D(2026, 4, 10), period.EffectiveTo);
        Assert.Equal(D(2026, 4, 30), period.RequestedTo);
        Assert.Equal(1000m, period.PaymentsTotal);
        Assert.Equal(D(2026, 4, 10), _repository.LoadedRequests[0].PeriodTo);
    }

    [Fact]
    public async Task Comparison_that_starts_after_today_is_rejected_like_the_primary_period()
    {
        _repository.Payments.Add((1, Payment(1, 1000m, D(2026, 3, 5))));

        var result = await _service.GetAsync(1, new ReportQuery(
            March,
            new ReportPeriod(D(2026, 5, 1), D(2026, 5, 31))));

        Assert.False(result.Succeeded);
        Assert.Equal(ReportsService.PeriodInFutureCode, result.Error!.Code);
        Assert.Equal(0, _repository.LoadCallCount);
    }

    // --- Tenant isolation and shape ------------------------------------------------------------------------------

    [Fact]
    public async Task Another_business_rows_are_invisible_in_every_part_of_the_report()
    {
        _repository.Payments.Add((1, Payment(1, 100m, D(2026, 3, 5))));
        _repository.Payments.Add((2, Payment(2, 9000m, D(2026, 3, 5))));
        _repository.Refunds.Add((2, Refund(2, 2, 9000m, D(2026, 3, 6))));
        _repository.Expenses.Add((2, Expense(2, 9000m, D(2026, 3, 6))));
        _repository.Attendances.Add((2, Attendance(999, 99, 99, "Ajena", BookingStatus.Attended, D(2026, 3, 6))));
        _repository.Attendances.Add((2, Attendance(998, 98, 99, "Ajena", BookingStatus.Absent, D(2026, 3, 6))));

        var period = (await _service.GetAsync(1, new ReportQuery(March))).Value.Period;

        Assert.Equal(100m, period.PaymentsTotal);
        Assert.Equal(0m, period.RefundsTotal);
        Assert.Equal(0m, period.ExpensesTotal);
        Assert.Equal(100m, period.NetResult);
        Assert.Equal(0, period.RecurringClientCount);
        Assert.Equal(0, period.LapsedClientCount);
        Assert.Equal(0, period.AbsenceCount);
        Assert.Empty(period.WorkerProduction);
        Assert.Equal([1], _repository.LoadedBusinessIds);
    }

    [Fact]
    public async Task One_snapshot_is_read_per_period()
    {
        _repository.Payments.Add((1, Payment(1, 100m, D(2026, 3, 5))));
        _repository.Payments.Add((1, Payment(2, 100m, D(2026, 2, 5))));

        var single = await _service.GetAsync(1, new ReportQuery(March));
        Assert.Equal(1, _repository.LoadCallCount);

        var compared = await _service.GetAsync(1, new ReportQuery(
            March,
            new ReportPeriod(D(2026, 2, 1), D(2026, 2, 28))));

        Assert.True(single.Succeeded);
        Assert.True(compared.Succeeded);
        Assert.Equal(3, _repository.LoadCallCount);
    }

    [Fact]
    public async Task Inverted_or_missing_period_is_rejected_before_reading()
    {
        var inverted = await _service.GetAsync(1, new ReportQuery(new ReportPeriod(D(2026, 3, 31), D(2026, 3, 1))));
        var missing = await _service.GetAsync(1, new ReportQuery(null!));

        Assert.Equal(ReportsService.InvalidPeriodCode, inverted.Error!.Code);
        Assert.Equal(ReportsService.InvalidPeriodCode, missing.Error!.Code);
        Assert.Equal(0, _repository.LoadCallCount);
    }

    [Fact]
    public async Task A_missing_business_is_rejected()
    {
        var result = await _service.GetAsync(0, new ReportQuery(March));

        Assert.False(result.Succeeded);
        Assert.Equal(ReportsService.InvalidBusinessCode, result.Error!.Code);
        Assert.Equal(0, _repository.LoadCallCount);
    }
}
