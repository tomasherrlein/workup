using System.Security.Cryptography;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

// T24 scheduled reminder core (RF-17, D-22). It is the strict complement of the immediate producers after 4223b63:
// a booking created strictly outside the configured anticipation is reminded once its lead instant arrives, an
// individual turn asks for confirmation only while it is still `Pendiente`, a `Confirmado` one is informational, and
// a drop-in class booking gets details only. Cancelled, attended, absent, fixed, suspended, started and late bookings
// are never reached; an immediate `AppointmentReminder` already owns its booking, an ordinary `BookingCreated` does
// not; and the guarded enqueue rechecks the live rows so a race never duplicates the email.
public class ReminderServiceTests
{
    private static readonly DateTime Start = new(2026, 3, 12, 10, 0, 0);

    private readonly FakeReminderRepository _repository = new();
    private readonly TestLinkTokenProtector _protector = new();
    private readonly TestAppointmentUrlBuilder _urlBuilder = new();
    private readonly ReminderService _service;

    public ReminderServiceTests() => _service = new ReminderService(_repository, _protector, _urlBuilder);

    // ---- The user decision: individual Pending asks, individual Confirmed informs, drop-in only informs ----

    [Fact]
    public async Task Pending_individual_reminder_asks_for_confirmation_and_carries_the_private_link()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        var now = Start.AddHours(-24);

        var enqueued = await _service.RunAsync(now);

        Assert.Equal(1, enqueued);
        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(NotificationType.AppointmentReminder, notice.Type);
        Assert.Equal("reminder:booking:1", notice.DedupKey);
        Assert.Equal(now, notice.CreatedAt);
        Assert.Equal(1, notice.BusinessId);
        Assert.Equal("ana@example.com", notice.Recipient);
        Assert.Equal(ReminderService.PendingSubject, notice.Subject);
        Assert.Contains(ReminderService.ConfirmationLead, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("https://workup.test/t/token-1", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Null(notice.LastError);
    }

    [Fact]
    public async Task Confirmed_individual_reminder_is_informational_and_asks_nothing()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30), status: BookingStatus.Confirmed);
        _repository.AddLink(booking, "token-2", Start.AddHours(24));

        var enqueued = await _service.RunAsync(Start.AddHours(-24));

        Assert.Equal(1, enqueued);
        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(NotificationType.AppointmentReminder, notice.Type);
        Assert.Equal("reminder:booking:1", notice.DedupKey);
        Assert.Equal(ReminderService.ConfirmedSubject, notice.Subject);
        Assert.DoesNotContain(ReminderService.ConfirmationLead, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("https://workup.test/t/token-2", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_pending_and_confirmed_individual_bookings_receive_a_reminder()
    {
        var pending = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(pending, "token-1", Start.AddHours(24));
        var confirmed = _repository.AddIndividual(2, Start, Start.AddHours(-30), status: BookingStatus.Confirmed);
        _repository.AddLink(confirmed, "token-2", Start.AddHours(24));

        var enqueued = await _service.RunAsync(Start.AddHours(-24));

        Assert.Equal(2, enqueued);
        Assert.Equal(2, _repository.Notifications.Count);
    }

    [Fact]
    public async Task Drop_in_reminder_carries_details_only_without_confirmation_or_link()
    {
        _repository.AddDropIn(1, Start, Start.AddHours(-30));

        var enqueued = await _service.RunAsync(Start.AddHours(-24));

        Assert.Equal(1, enqueued);
        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(NotificationType.DropInReminder, notice.Type);
        Assert.Equal("reminder:booking:1", notice.DedupKey);
        Assert.Equal(ReminderService.DropInSubject, notice.Subject);
        Assert.DoesNotContain(ReminderService.ConfirmationLead, notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ver mi turno", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirmed_drop_in_also_gets_details_only()
    {
        _repository.AddDropIn(1, Start, Start.AddHours(-30), status: BookingStatus.Confirmed);

        Assert.Equal(1, await _service.RunAsync(Start.AddHours(-24)));
        var notice = Assert.Single(_repository.Notifications);
        Assert.DoesNotContain(ReminderService.ConfirmationLead, notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drop_in_ignores_any_private_link_and_is_never_blocked_by_its_absence()
    {
        // A drop-in never has a link; a stray one must neither be embedded nor change the notice.
        _repository.AddDropIn(1, Start, Start.AddHours(-30));
        var withLink = _repository.AddDropIn(2, Start, Start.AddHours(-30));
        _repository.AddLink(withLink, "token-2", Start.AddHours(24));

        Assert.Equal(2, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Equal(2, _repository.Notifications.Count);
        Assert.All(_repository.Notifications, notice =>
            Assert.DoesNotContain("https://", notice.BodyHtml, StringComparison.Ordinal));
        Assert.DoesNotContain(
            "token-2",
            _repository.Notifications.Single(notice => notice.DedupKey == "reminder:booking:2").BodyHtml,
            StringComparison.Ordinal);
    }

    // ---- Private link: exactly one live link, safe reconstruction ----

    [Fact]
    public async Task Individual_without_a_private_link_is_skipped()
    {
        _repository.AddIndividual(1, Start, Start.AddHours(-30));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Individual_with_only_a_revoked_link_is_skipped()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24), revokedAt: Start.AddHours(-25));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Individual_with_only_an_expired_link_is_skipped()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(-24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Individual_with_two_live_links_is_skipped()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AddLink(booking, "token-2", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Individual_with_an_unprotectable_token_is_skipped_without_echoing_it()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(
            booking, "token-1", Start.AddHours(24),
            protectedToken: TestLinkTokenProtector.BrokenPrefix + "token-1");

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Individual_with_a_token_hash_that_does_not_match_is_skipped()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLinkWithHash(booking, SecureToken.Hash("otro-token"), "protected:token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    // ---- Strict complement of the immediate producers: 12 h, 48 h and the equality boundary ----

    [Fact]
    public async Task Configured_anticipation_of_12_hours_is_used_instead_of_a_constant_24()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-20), reminderLeadHours: 12);
        _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(1, await _service.RunAsync(Start.AddHours(-12)));
    }

    [Fact]
    public async Task Configured_anticipation_of_48_hours_reminds_a_booking_a_24_hour_window_would_miss()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-60), reminderLeadHours: 48);
        _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(1, await _service.RunAsync(Start.AddHours(-40)));
    }

    [Fact]
    public async Task Booking_created_exactly_on_the_lead_boundary_is_never_reminded_again()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-24));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Booking_is_not_reminded_before_its_lead_instant_arrives()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-25)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Booking_made_inside_the_configured_anticipation_is_never_reminded()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-6));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-1)));
        Assert.Empty(_repository.Notifications);
    }

    // ---- Exclusions ----

    [Fact]
    public async Task Cancelled_booking_is_never_reminded()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30), status: BookingStatus.Cancelled);
        _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Theory]
    [InlineData(BookingStatus.Attended)]
    [InlineData(BookingStatus.Absent)]
    public async Task Attended_or_absent_booking_is_never_reminded(BookingStatus status)
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30), status: status);
        _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Fixed_enrollment_booking_is_never_reminded()
    {
        _repository.AddFixed(1, Start, Start.AddHours(-30));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Suspended_class_session_is_never_reminded()
    {
        _repository.AddDropIn(1, Start, Start.AddHours(-30), appointmentStatus: AppointmentStatus.Suspended);

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Booking_that_already_started_is_never_reminded()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start));
        Assert.Empty(_repository.Notifications);
    }

    // ---- Immediate dedup: the reminder owns `booking:{id}`, `BookingCreated` does not ----

    [Fact]
    public async Task Booking_with_an_immediate_appointment_reminder_is_not_reminded_again_after_a_settings_change()
    {
        // The window shrank after the booking: at creation it was inside a 48 h window and got the immediate reminder.
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30), reminderLeadHours: 24);
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AddImmediateAppointmentReminder(booking, Start.AddHours(-30));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.DoesNotContain(_repository.Notifications, notice => notice.DedupKey == "reminder:booking:1");
    }

    [Fact]
    public async Task Booking_with_only_a_booking_created_notice_is_still_reminded()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AddBookingCreated(booking, Start.AddHours(-30));

        Assert.Equal(1, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Contains(_repository.Notifications, notice => notice.DedupKey == "reminder:booking:1");
    }

    [Fact]
    public async Task Drop_in_immediate_email_already_owns_the_scheduled_key()
    {
        var booking = _repository.AddDropIn(1, Start, Start.AddHours(-30));
        _repository.AddImmediateDropInReminder(booking, Start.AddHours(-30));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Single(_repository.Notifications);
    }

    // ---- Guarded enqueue: races and idempotence ----

    [Fact]
    public async Task A_second_run_does_not_enqueue_a_duplicate()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        var now = Start.AddHours(-24);

        Assert.Equal(1, await _service.RunAsync(now));
        Assert.Equal(0, await _service.RunAsync(now));
        Assert.Single(_repository.Notifications);
    }

    [Fact]
    public async Task Cancellation_between_the_scan_and_the_enqueue_prevents_the_notice()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AfterList = () => booking.Status = BookingStatus.Cancelled;

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.DoesNotContain(_repository.Notifications, notice => notice.DedupKey == "reminder:booking:1");
    }

    [Fact]
    public async Task An_immediate_reminder_appearing_between_the_scan_and_the_enqueue_wins()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AfterList = () => _repository.AddImmediateAppointmentReminder(booking, Start.AddHours(-24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.DoesNotContain(_repository.Notifications, notice => notice.DedupKey == "reminder:booking:1");
    }

    // ---- Bounded batch ----

    [Fact]
    public async Task One_run_processes_at_most_the_bounded_batch()
    {
        for (var index = 1; index <= 25; index++)
        {
            var booking = _repository.AddIndividual(index, Start, Start.AddHours(-30));
            _repository.AddLink(booking, $"token-{index}", Start.AddHours(24));
        }

        var enqueued = await _service.RunAsync(Start.AddHours(-24));

        Assert.Equal(ReminderService.BatchSize, _repository.LastBatchSize);
        Assert.Equal(ReminderService.BatchSize, enqueued);
        Assert.Equal(ReminderService.BatchSize, _repository.Notifications.Count);
    }

    // ---- Link identity: the enqueue must re-present the exact still-live link the body used ----

    [Fact]
    public async Task The_notice_carries_the_exact_link_identity_used_to_build_the_body()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        var link = _repository.AddLink(booking, "token-1", Start.AddHours(24));

        Assert.Equal(1, await _service.RunAsync(Start.AddHours(-24)));

        var notice = Assert.IsType<ReminderNotice>(_repository.LastAttemptedNotice);
        var identity = Assert.IsType<ReminderLinkIdentity>(notice.Link);
        Assert.Equal(link.LinkId, identity.LinkId);
        Assert.True(link.TokenHash.AsSpan().SequenceEqual(identity.TokenHash));
    }

    [Fact]
    public async Task A_link_revoked_between_the_scan_and_the_enqueue_is_skipped()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        var link = _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AfterList = () => _repository.RevokeLink(booking, link, Start.AddHours(-24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.DoesNotContain(_repository.Notifications, notice => notice.DedupKey == "reminder:booking:1");
    }

    [Fact]
    public async Task A_link_expired_between_the_scan_and_the_enqueue_is_skipped()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        var link = _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AfterList = () => booking.Links[0] = link with { ExpiresAt = Start.AddHours(-24) };

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.DoesNotContain(_repository.Notifications, notice => notice.DedupKey == "reminder:booking:1");
    }

    [Fact]
    public async Task A_link_replaced_between_the_scan_and_the_enqueue_is_skipped()
    {
        // A replacement is a different identity: the body already carries the old URL, so the new link must not be sent.
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        var link = _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AfterList = () =>
            _repository.ReplaceLink(booking, link, "token-2", Start.AddHours(-24), Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.DoesNotContain(_repository.Notifications, notice => notice.DedupKey == "reminder:booking:1");
    }

    [Fact]
    public async Task A_tenant_change_between_the_scan_and_the_enqueue_is_rejected()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AfterList = () => booking.BusinessId = 2;

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.DoesNotContain(_repository.Notifications, notice => notice.DedupKey == "reminder:booking:1");
    }

    // ---- Malformed rows the scan should never return are rejected by the service itself ----

    [Fact]
    public async Task Service_rejects_a_candidate_that_carries_a_fixed_enrollment()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30), enrollmentId: 42);
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Equal(0, _repository.EnqueueAttempts);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Service_rejects_a_candidate_whose_kind_does_not_match_its_modality()
    {
        var booking = _repository.AddIndividual(
            1, Start, Start.AddHours(-30), kind: AppointmentKind.ClassSession);
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Equal(0, _repository.EnqueueAttempts);
        Assert.Empty(_repository.Notifications);
    }

    // ---- The service caps its own batch even if a broken reader over-returns ----

    [Fact]
    public async Task Service_caps_the_batch_even_when_the_repository_over_returns()
    {
        for (var index = 1; index <= 25; index++)
        {
            var booking = _repository.AddIndividual(index, Start, Start.AddHours(-30));
            _repository.AddLink(booking, $"token-{index}", Start.AddHours(24));
        }

        _repository.IgnoreBatchCap = true;

        var enqueued = await _service.RunAsync(Start.AddHours(-24));

        Assert.Equal(25, _repository.LastScanCandidateCount);
        Assert.Equal(ReminderService.BatchSize, enqueued);
        Assert.Equal(ReminderService.BatchSize, _repository.Notifications.Count);
    }

    // ---- BookingCreated never suppresses the scheduled reminder, confirmed or not ----

    [Fact]
    public async Task Confirmed_individual_with_its_original_booking_created_notice_is_still_reminded()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30), status: BookingStatus.Confirmed);
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        _repository.AddBookingCreated(booking, Start.AddHours(-30));

        Assert.Equal(1, await _service.RunAsync(Start.AddHours(-24)));
        var notice = Assert.Single(
            _repository.Notifications, candidate => candidate.DedupKey == "reminder:booking:1");
        Assert.Equal(NotificationType.AppointmentReminder, notice.Type);
        Assert.DoesNotContain(ReminderService.ConfirmationLead, notice.BodyHtml, StringComparison.Ordinal);
    }

    // ---- Untrusted link rows fail closed instead of throwing ----

    [Fact]
    public async Task Individual_with_a_null_token_hash_is_skipped_without_a_null_reference()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLinkWithHash(booking, tokenHash: null!, "protected:token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Individual_with_a_token_hash_of_the_wrong_length_is_skipped()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLinkWithHash(booking, new byte[16], "protected:token-1", Start.AddHours(24));

        Assert.Equal(0, await _service.RunAsync(Start.AddHours(-24)));
        Assert.Empty(_repository.Notifications);
    }

    // ---- Cancellation propagates and never leaves a partial notice ----

    [Fact]
    public async Task Cancellation_before_the_run_propagates_and_enqueues_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RunAsync(Start.AddHours(-24), cancellation.Token));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Cancellation_after_the_scan_propagates_without_enqueuing()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        using var cancellation = new CancellationTokenSource();
        _repository.AfterList = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RunAsync(Start.AddHours(-24), cancellation.Token));
        Assert.Empty(_repository.Notifications);
    }

    // ---- Safety: HTML-encoded details and the raw token only in the body ----

    [Fact]
    public async Task Personal_details_are_html_encoded_and_the_raw_token_stays_only_in_the_body()
    {
        var booking = _repository.AddIndividual(
            7, Start, Start.AddHours(-30),
            clientFirstName: "<b>Ana</b>",
            businessName: "<i>Estudio</i>",
            serviceName: "Masaje & Spa",
            workerName: "<script>alert(1)</script>");
        _repository.AddLink(booking, "secret-token-7", Start.AddHours(24));

        Assert.Equal(1, await _service.RunAsync(Start.AddHours(-24)));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("&lt;b&gt;Ana&lt;/b&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;i&gt;Estudio&lt;/i&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Masaje &amp; Spa", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Ana</b>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", notice.BodyHtml, StringComparison.Ordinal);
        // The raw link is exactly the one intended exception: it lives in the body and nowhere else.
        Assert.Contains("secret-token-7", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-7", notice.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-7", notice.Recipient, StringComparison.Ordinal);
        Assert.Null(notice.LastError);
    }

    [Fact]
    public async Task The_service_never_reads_a_clock_of_its_own_and_uses_the_supplied_instant()
    {
        var booking = _repository.AddIndividual(1, Start, Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(24));
        var supplied = Start.AddHours(-24);

        Assert.Equal(1, await _service.RunAsync(supplied));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(supplied, notice.CreatedAt);
    }

    private sealed class TestLinkTokenProtector : ILinkTokenProtector
    {
        public const string BrokenPrefix = "broken:";

        public string Protect(string token) => "protected:" + token;

        public string Unprotect(string protectedToken) =>
            protectedToken.StartsWith(BrokenPrefix, StringComparison.Ordinal)
                // A Data Protection failure must never escape as readable text.
                ? throw new CryptographicException("no-debe-salir")
                : protectedToken["protected:".Length..];
    }

    private sealed class TestAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://workup.test/t/" + token;
    }
}
