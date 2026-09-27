using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

// T25 membership expiry core (RF-29, RF-30, D-22, D-23). Only the current/latest paid period of an Active
// enrollment is noticed: no pending enrollment without a period, no annulled or terminated enrollment, and no old
// period once a newer one exists. The expiring stage covers the local dates [ExpiresOn - 3, ExpiresOn) and the
// expired stage is due on the local expiry date and that date only (a missed minute is caught up the same day, a
// later day never serves it); each stage is once per period under its own key, an expired notice suppresses the
// earlier expiring one, and the recipient is the client record email of the same business. Every persisted period
// is valid coverage: annulling a payment is purely financial and never removes monthly coverage (T18).
public class MembershipExpiryServiceTests
{
    private static readonly DateOnly Expiry = new(2026, 3, 10);
    private static readonly DateTime FourDaysBefore = new(2026, 3, 6, 9, 0, 0);
    private static readonly DateTime ThreeDaysBefore = new(2026, 3, 7, 9, 0, 0);
    private static readonly DateTime TwoDaysBefore = new(2026, 3, 8, 9, 0, 0);
    private static readonly DateTime OneDayBefore = new(2026, 3, 9, 9, 0, 0);
    private static readonly DateTime ExpiryDay = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime OneDayAfter = new(2026, 3, 11, 9, 0, 0);
    private static readonly DateTime FiveDaysAfter = new(2026, 3, 15, 9, 0, 0);

    private readonly FakeMembershipExpiryRepository _repository = new();
    private readonly MembershipExpiryService _service;

    public MembershipExpiryServiceTests() => _service = new MembershipExpiryService(_repository);

    private FakeMembershipEnrollment AddActive(
        int enrollmentId = 1,
        int periodId = 100,
        DateOnly? expiresOn = null,
        string recipient = "alumno@example.com",
        string clientName = "Ana",
        string businessName = "Estudio",
        string accountEmail = "cuenta@example.com")
    {
        var expiry = expiresOn ?? Expiry;
        var enrollment = _repository.AddEnrollment(
            enrollmentId,
            clientId: 10 + enrollmentId,
            businessId: 1,
            status: EnrollmentStatus.Active,
            recipient: recipient,
            clientName: clientName,
            businessName: businessName,
            accountEmail: accountEmail);
        _repository.AddPeriod(enrollment, periodId, expiry.AddMonths(-1), expiry);
        return enrollment;
    }

    // ---- The expiring window: [ExpiresOn - 3, ExpiresOn) ----

    [Fact]
    public async Task Three_days_before_expiry_enqueues_the_expiring_notice_once()
    {
        AddActive();

        Assert.Equal(1, await _service.RunAsync(ThreeDaysBefore));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(NotificationType.MembershipExpiring, notice.Type);
        Assert.Equal("membership:enrollment:1:period:100:expiring", notice.DedupKey);
        Assert.Equal(ThreeDaysBefore, notice.CreatedAt);
        Assert.Equal(1, notice.BusinessId);
        Assert.Equal("alumno@example.com", notice.Recipient);
        Assert.Contains(MembershipExpiryService.ExpiringSubject, notice.Subject, StringComparison.Ordinal);
        Assert.Null(notice.LastError);
    }

    [Fact]
    public async Task Two_days_before_expiry_still_enqueues_the_expiring_notice()
    {
        AddActive();

        // The host missed the exact three-days-before instant; while the period is still in force it is served once.
        Assert.Equal(1, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal("membership:enrollment:1:period:100:expiring",
            Assert.Single(_repository.Notifications).DedupKey);
    }

    [Fact]
    public async Task One_day_before_expiry_still_enqueues_the_expiring_notice()
    {
        AddActive();

        Assert.Equal(1, await _service.RunAsync(OneDayBefore));
        Assert.Equal(NotificationType.MembershipExpiring, Assert.Single(_repository.Notifications).Type);
    }

    [Fact]
    public async Task Four_days_before_expiry_enqueues_nothing()
    {
        AddActive();

        Assert.Equal(0, await _service.RunAsync(FourDaysBefore));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_second_tick_inside_the_expiring_window_never_enqueues_a_second_notice()
    {
        AddActive();

        Assert.Equal(1, await _service.RunAsync(ThreeDaysBefore));
        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal(0, await _service.RunAsync(OneDayBefore));
        Assert.Single(_repository.Notifications);
    }

    // ---- The expired stage: the local expiry date only ----

    [Fact]
    public async Task Exactly_on_the_expiry_day_enqueues_the_expired_notice()
    {
        AddActive();

        Assert.Equal(1, await _service.RunAsync(ExpiryDay));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(NotificationType.MembershipExpired, notice.Type);
        Assert.Equal("membership:enrollment:1:period:100:expired", notice.DedupKey);
        Assert.Equal(ExpiryDay, notice.CreatedAt);
        Assert.Contains(MembershipExpiryService.ExpiredSubject, notice.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_day_after_expiry_enqueues_nothing()
    {
        AddActive();

        // The expired email is due on the expiry date only; a later day is not a catch-up.
        Assert.Equal(0, await _service.RunAsync(OneDayAfter));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Months_after_expiry_enqueue_nothing()
    {
        AddActive();

        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 9, 10, 9, 0, 0)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task The_expired_notice_is_caught_up_later_on_the_same_expiry_date()
    {
        AddActive();

        // The exact expiry minute was missed, but the local date is still the expiry date.
        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 3, 10, 23, 59, 0)));
        Assert.Equal("membership:enrollment:1:period:100:expired",
            Assert.Single(_repository.Notifications).DedupKey);
    }

    [Fact]
    public async Task A_later_day_never_serves_a_missed_expiry_date()
    {
        AddActive();

        // Unlike the expiring window, the expired stage does not stretch: a missed expiry date is not backfilled.
        Assert.Equal(0, await _service.RunAsync(FiveDaysAfter));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task The_expired_notice_follows_the_expiring_notice_for_the_same_period()
    {
        AddActive();

        Assert.Equal(1, await _service.RunAsync(ThreeDaysBefore));
        Assert.Equal(1, await _service.RunAsync(ExpiryDay));

        var keys = _repository.Notifications.Select(notice => notice.DedupKey).ToArray();
        Assert.Equal(
            ["membership:enrollment:1:period:100:expiring", "membership:enrollment:1:period:100:expired"],
            keys);
    }

    [Fact]
    public async Task A_repeated_tick_after_expiry_never_enqueues_a_daily_second_notice()
    {
        AddActive();

        Assert.Equal(1, await _service.RunAsync(ExpiryDay));
        Assert.Equal(0, await _service.RunAsync(OneDayAfter));
        Assert.Equal(0, await _service.RunAsync(FiveDaysAfter));
        Assert.Single(_repository.Notifications);
    }

    // ---- Renewal and multiple periods: only the current/latest period ----

    [Fact]
    public async Task Renewing_early_suppresses_the_old_periods_expiring_notice()
    {
        var enrollment = AddActive();
        _repository.AddPeriod(enrollment, 200, Expiry, new DateOnly(2026, 4, 10));

        // The newest period is not in its own window yet, and the superseded period must not be noticed.
        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Renewing_early_suppresses_the_old_periods_expired_notice()
    {
        var enrollment = AddActive();
        _repository.AddPeriod(enrollment, 200, Expiry, new DateOnly(2026, 4, 10));

        Assert.Equal(0, await _service.RunAsync(ExpiryDay));
        Assert.Equal(0, await _service.RunAsync(OneDayAfter));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task After_renewing_the_notice_belongs_to_the_new_period()
    {
        var enrollment = AddActive();
        _repository.AddPeriod(enrollment, 200, Expiry, new DateOnly(2026, 4, 10));

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 4, 7, 9, 0, 0)));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal("membership:enrollment:1:period:200:expiring", notice.DedupKey);
        Assert.Equal(NotificationType.MembershipExpiring, notice.Type);
    }

    [Fact]
    public async Task Renewing_late_never_backfills_the_old_period()
    {
        var enrollment = AddActive();
        // Late renewal (RF-29 step 4): the new coverage starts on the payment day, not at the old expiry.
        _repository.AddPeriod(enrollment, 200, new DateOnly(2026, 3, 20), new DateOnly(2026, 4, 20));

        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 3, 25, 9, 0, 0)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task The_notice_always_belongs_to_the_latest_valid_period()
    {
        var enrollment = AddActive();
        _repository.AddPeriod(enrollment, 200, Expiry, new DateOnly(2026, 4, 10));

        // At the old period's window the newest period is chosen, so nothing is due.
        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        // At the newest period's window the newest period is the one noticed.
        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 4, 8, 9, 0, 0)));
        Assert.Equal("membership:enrollment:1:period:200:expiring",
            Assert.Single(_repository.Notifications).DedupKey);
    }

    [Fact]
    public async Task An_annulled_payment_period_is_still_valid_coverage()
    {
        var enrollment = AddActive(periodId: 100);
        // T18: annulling a payment is a purely financial correction that keeps monthly coverage, the seat and the
        // attendance, so the period still produces its notice.
        enrollment.Periods[0].PaymentAnnulled = true;

        Assert.Equal(1, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal("membership:enrollment:1:period:100:expiring",
            Assert.Single(_repository.Notifications).DedupKey);
    }

    [Fact]
    public async Task An_annulled_payment_does_not_hide_the_latest_period()
    {
        var enrollment = AddActive(periodId: 200, expiresOn: new DateOnly(2026, 4, 10));
        // An older period whose payment was annulled must not become the noticed one; the latest period still wins.
        _repository.AddPeriod(enrollment, 100, new DateOnly(2026, 2, 10), Expiry, paymentAnnulled: true);

        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Empty(_repository.Notifications);
    }

    // ---- Ineligible enrollments ----

    [Fact]
    public async Task An_active_enrollment_with_no_paid_period_produces_nothing()
    {
        _repository.AddEnrollment();

        Assert.Equal(0, await _service.RunAsync(ExpiryDay));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_pending_payment_enrollment_produces_nothing()
    {
        var enrollment = _repository.AddEnrollment(status: EnrollmentStatus.PendingPayment);
        _repository.AddPeriod(enrollment, 100, Expiry.AddMonths(-1), Expiry);

        Assert.Equal(0, await _service.RunAsync(ThreeDaysBefore));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task An_annulled_enrollment_produces_nothing()
    {
        var enrollment = _repository.AddEnrollment(status: EnrollmentStatus.Annulled);
        _repository.AddPeriod(enrollment, 100, Expiry.AddMonths(-1), Expiry);

        Assert.Equal(0, await _service.RunAsync(ExpiryDay));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_terminated_enrollment_produces_nothing()
    {
        var enrollment = _repository.AddEnrollment(status: EnrollmentStatus.Terminated);
        _repository.AddPeriod(enrollment, 100, Expiry.AddMonths(-1), Expiry);

        Assert.Equal(0, await _service.RunAsync(ExpiryDay));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task An_enrollment_whose_client_email_is_malformed_is_skipped()
    {
        AddActive(recipient: "no-es-un-email");

        Assert.Equal(0, await _service.RunAsync(ThreeDaysBefore));
        Assert.Empty(_repository.Notifications);
    }

    // ---- Clock rewind after the expired notice ----

    [Fact]
    public async Task A_rewind_after_the_expired_notice_never_issues_the_earlier_expiring_notice()
    {
        AddActive();
        _repository.SeedNotice(1, 100, MembershipExpiryStage.Expired, ExpiryDay);

        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Single(_repository.Notifications);
    }

    [Fact]
    public async Task A_rewind_after_the_expired_notice_is_also_rejected_by_the_guarded_enqueue()
    {
        AddActive();
        _repository.SeedNotice(1, 100, MembershipExpiryStage.Expired, ExpiryDay);
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal(1, _repository.EnqueueAttempts);
        Assert.Single(_repository.Notifications);
    }

    [Fact]
    public async Task A_rewind_before_the_expiring_window_enqueues_nothing()
    {
        AddActive();

        Assert.Equal(1, await _service.RunAsync(ExpiryDay));
        Assert.Equal(0, await _service.RunAsync(FourDaysBefore));
        Assert.Single(_repository.Notifications);
    }

    // ---- Recipient and body safety ----

    [Fact]
    public async Task The_recipient_is_the_client_record_email_not_the_account_email()
    {
        AddActive(recipient: "ficha@example.com", accountEmail: "cuenta@example.com");

        Assert.Equal(1, await _service.RunAsync(ThreeDaysBefore));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal("ficha@example.com", notice.Recipient);
        Assert.DoesNotContain("cuenta@example.com", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_and_business_names_are_html_encoded()
    {
        AddActive(clientName: "<b>Ana</b> & Co", businessName: "<script>x</script>");

        Assert.Equal(1, await _service.RunAsync(ExpiryDay));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("&lt;b&gt;Ana&lt;/b&gt; &amp; Co", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Ana</b>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Null(notice.LastError);
    }

    [Fact]
    public async Task The_body_contains_no_link_no_payment_data_and_no_automatic_release_wording()
    {
        AddActive();

        Assert.Equal(1, await _service.RunAsync(ThreeDaysBefore));
        Assert.Equal(1, await _service.RunAsync(ExpiryDay));

        foreach (var notice in _repository.Notifications)
        {
            Assert.DoesNotContain("http", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("liber", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("$", notice.BodyHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("salud", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Races between the scan and the guarded insert ----

    [Fact]
    public async Task A_renewal_between_the_scan_and_the_insert_prevents_the_notice()
    {
        var enrollment = AddActive();
        _repository.AfterList = () =>
            _repository.AddPeriod(enrollment, 200, Expiry, new DateOnly(2026, 4, 10));

        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal(1, _repository.EnqueueAttempts);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_termination_between_the_scan_and_the_insert_prevents_the_notice()
    {
        var enrollment = AddActive();
        _repository.AfterList = () => _repository.Terminate(enrollment);

        Assert.Equal(0, await _service.RunAsync(ExpiryDay));
        Assert.Equal(1, _repository.EnqueueAttempts);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task An_email_change_between_the_scan_and_the_insert_prevents_the_notice()
    {
        var enrollment = AddActive();
        _repository.AfterList = () => _repository.ChangeRecipient(enrollment, "nuevo@example.com");

        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal(1, _repository.EnqueueAttempts);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_concurrent_tick_that_already_wrote_the_key_prevents_a_duplicate()
    {
        AddActive();
        _repository.AfterList = () =>
            _repository.SeedNotice(1, 100, MembershipExpiryStage.Expiring, ThreeDaysBefore);

        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal(1, _repository.EnqueueAttempts);
        Assert.Single(_repository.Notifications);
    }

    // ---- A permissive reader cannot force an ineligible write ----

    [Fact]
    public async Task A_permissive_reader_cannot_force_a_notice_for_a_superseded_period()
    {
        AddActive();
        _repository.OverReturnIneligible = true;
        _repository.ForceLaterPeriod = true;

        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal(0, _repository.EnqueueAttempts);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_permissive_reader_cannot_force_a_notice_for_a_terminated_enrollment()
    {
        var enrollment = AddActive();
        _repository.Terminate(enrollment);
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(TwoDaysBefore));
        Assert.Equal(0, _repository.EnqueueAttempts);
        Assert.Empty(_repository.Notifications);
    }

    // ---- Bounded batch, date extremes and cancellation ----

    [Fact]
    public async Task The_batch_is_bounded_to_twenty_enrollments_per_tick()
    {
        for (var index = 1; index <= 25; index++)
            AddActive(index, periodId: 100 + index, recipient: $"alumno{index}@example.com");
        _repository.IgnoreBatchCap = true;

        Assert.Equal(MembershipExpiryService.BatchSize, await _service.RunAsync(ThreeDaysBefore));

        Assert.Equal(25, _repository.LastScanCandidateCount);
        Assert.Equal(MembershipExpiryService.BatchSize, _repository.Notifications.Count);
        Assert.Equal(MembershipExpiryService.BatchSize, _repository.LastBatchSize);
    }

    [Fact]
    public void The_stage_boundaries_hold_at_the_first_and_last_representable_days()
    {
        Assert.Equal(MembershipExpiryStage.Expiring,
            MembershipExpiryService.StageAt(DateOnly.MaxValue, new DateTime(9999, 12, 30, 9, 0, 0)));
        Assert.Equal(MembershipExpiryStage.Expired,
            MembershipExpiryService.StageAt(DateOnly.MaxValue, DateTime.MaxValue));
        Assert.Null(MembershipExpiryService.StageAt(DateOnly.MaxValue, new DateTime(9999, 12, 20, 9, 0, 0)));
        Assert.Equal(MembershipExpiryStage.Expired,
            MembershipExpiryService.StageAt(DateOnly.MinValue, new DateTime(1, 1, 1, 0, 0, 0)));
        Assert.Null(MembershipExpiryService.StageAt(Expiry, OneDayAfter));
        Assert.Null(MembershipExpiryService.StageAt(Expiry, FiveDaysAfter));
    }

    [Fact]
    public async Task The_last_representable_expiry_day_enqueues_without_overflow()
    {
        var enrollment = _repository.AddEnrollment();
        _repository.AddPeriod(enrollment, 100, DateOnly.MaxValue.AddDays(-1), DateOnly.MaxValue);

        Assert.Equal(1, await _service.RunAsync(new DateTime(9999, 12, 30, 9, 0, 0)));
        Assert.Equal("membership:enrollment:1:period:100:expiring",
            Assert.Single(_repository.Notifications).DedupKey);
    }

    [Fact]
    public async Task The_service_uses_the_supplied_instant_and_never_reads_a_clock()
    {
        AddActive();
        var supplied = new DateTime(2026, 3, 8, 22, 15, 0);

        Assert.Equal(1, await _service.RunAsync(supplied));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(supplied, notice.CreatedAt);
    }

    [Fact]
    public async Task Cancellation_before_the_run_propagates_and_enqueues_nothing()
    {
        AddActive();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RunAsync(ThreeDaysBefore, cancellation.Token));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Cancellation_after_the_scan_propagates_without_enqueuing()
    {
        AddActive();
        using var cancellation = new CancellationTokenSource();
        _repository.AfterList = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RunAsync(ThreeDaysBefore, cancellation.Token));
        Assert.Empty(_repository.Notifications);
    }
}
