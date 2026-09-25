using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Classes;

// T16 unit 4 (RF-10, RF-22, D-15, D-24): cancelling one annotation of a generated class date and suspending the whole
// class with a mandatory reason and one deduplicated notice per non-cancelled annotation. Both actions work under the
// class lock of Appointments taken first, one clock read and a single transaction that commits exactly once, and every
// rejection rolls that transaction back without writing anything.
public class ClassDateActionsServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime ClassStartsAt = new(2026, 3, 12, 18, 0, 0); // Thursday

    private readonly FakeClassDateActionsRepository _repository = new();
    private readonly FakeUnitOfWork _unit = new();
    private readonly FakeClock _clock = new() { Now = ClockNow };
    private readonly ClassDateActionsService _service;

    public ClassDateActionsServiceTests()
    {
        _service = new ClassDateActionsService(_repository, _unit, _clock);
        _repository.Session = new FakeClassDateSession(
            1, 100, ClassStartsAt, AppointmentKind.ClassSession, AppointmentStatus.Scheduled);
    }

    /// <summary>One annotation of the class, in the shape the fake stores it.</summary>
    private FakeClassDateBooking AddBooking(
        int bookingId,
        BookingStatus status = BookingStatus.Pending,
        BookingModality? modality = BookingModality.DropIn,
        string email = "ana@example.com",
        string name = "Ana Pérez",
        int clientId = 11)
    {
        var booking = new FakeClassDateBooking(
            1, bookingId, 100, clientId, email, name, modality, status);
        _repository.Bookings.Add(booking);
        return booking;
    }

    private void AssertRolledBack(Result result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
        // Every transaction the operation opened was rolled back and none was committed.
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
        Assert.Equal(0, _repository.LockBookingCallCount);
        Assert.Equal(0, _repository.LockSessionCallCount);
    }

    // --- Cancelling one annotation of the date (RF-22) -------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_drop_in_annotation_marks_it_cancelled_and_stamps_the_owner()
    {
        var booking = AddBooking(500);

        var result = await _service.CancelBookingAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(CancelledBy.Owner, booking.CancelledBy);
        Assert.Equal(ClockNow, booking.CancelledAt);
        Assert.Equal(1, _unit.BeginCount);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
    }

    [Fact]
    public async Task Cancelling_a_fixed_annotation_is_allowed()
    {
        var booking = AddBooking(500, modality: BookingModality.Fixed);

        var result = await _service.CancelBookingAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(ClockNow, booking.CancelledAt);
    }

    [Theory]
    // The explicit user decision of 2026-09-25: while the class is Scheduled and has not started, any non-cancelled
    // annotation can be cancelled, even one already recorded as attended or absent. The status is what counts, so those
    // facts stop counting from the moment the annotation is cancelled.
    [InlineData(BookingStatus.Attended)]
    [InlineData(BookingStatus.Absent)]
    [InlineData(BookingStatus.Confirmed)]
    public async Task Cancelling_a_pre_recorded_annotation_is_allowed(BookingStatus status)
    {
        var booking = AddBooking(500, status: status);

        var result = await _service.CancelBookingAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(ClockNow, booking.CancelledAt);
    }

    [Fact]
    public async Task A_second_cancellation_is_refused_and_keeps_the_original_instant()
    {
        var booking = AddBooking(500);
        var original = ClockNow.AddDays(-1);
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = original;
        booking.CancelledBy = CancelledBy.Client;

        var result = await _service.CancelBookingAsync(1, 500);

        AssertRolledBack(result, "class_date_booking_cancelled");
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(original, booking.CancelledAt);
        Assert.Equal(CancelledBy.Client, booking.CancelledBy);
    }

    [Theory]
    // The class lock is taken before the clock is read and the guarded write repeats the same instant, so a started
    // class can never be cancelled: from its start on the absence is the only record (RF-22 error handling), and the
    // exact start already counts as started.
    [InlineData(0)]
    [InlineData(1)]
    public async Task Cancelling_a_started_class_is_refused(int hoursAfterClassStarts)
    {
        _clock.Now = ClassStartsAt.AddHours(hoursAfterClassStarts);
        var booking = AddBooking(500);

        var result = await _service.CancelBookingAsync(1, 500);

        AssertRolledBack(result, "class_date_not_available");
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Null(booking.CancelledAt);
    }

    [Fact]
    public async Task Cancelling_an_annotation_of_a_suspended_class_is_refused()
    {
        _repository.Session!.Status = AppointmentStatus.Suspended;
        var booking = AddBooking(500);

        var result = await _service.CancelBookingAsync(1, 500);

        AssertRolledBack(result, "class_date_not_available");
        Assert.Equal(BookingStatus.Pending, booking.Status);
    }

    [Fact]
    public async Task A_foreign_missing_or_non_class_annotation_is_not_found()
    {
        // Foreign: the same booking id under another business is invisible.
        _repository.Bookings.Add(new FakeClassDateBooking(
            2, 500, 100, 11, "ana@example.com", "Ana Pérez", BookingModality.DropIn, BookingStatus.Pending));
        var foreign = await _service.CancelBookingAsync(1, 500);

        // Missing: the port finds no row.
        var missing = await _service.CancelBookingAsync(1, 999);

        // An individual turn: it is not a class annotation, whatever the modality says.
        _repository.Bookings.Clear();
        _repository.Session = new FakeClassDateSession(
            1, 100, ClassStartsAt, AppointmentKind.Individual, AppointmentStatus.Scheduled);
        AddBooking(500, modality: null);
        var individual = await _service.CancelBookingAsync(1, 500);

        // A booking without a modality is not a class annotation the rules can judge.
        _repository.Session = new FakeClassDateSession(
            1, 100, ClassStartsAt, AppointmentKind.ClassSession, AppointmentStatus.Scheduled);
        var noModality = await _service.CancelBookingAsync(1, 500);

        AssertRolledBack(foreign, "class_date_booking_not_found");
        AssertRolledBack(missing, "class_date_booking_not_found");
        AssertRolledBack(individual, "class_date_booking_not_found");
        AssertRolledBack(noModality, "class_date_booking_not_found");
        Assert.All(_repository.Bookings, booking => Assert.NotEqual(BookingStatus.Cancelled, booking.Status));
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1, 0)]
    public async Task Invalid_cancellation_ids_are_not_found_without_touching_storage(int businessId, int bookingId)
    {
        AddBooking(500);

        var result = await _service.CancelBookingAsync(businessId, bookingId);

        AssertRejectedBeforeStorage(result, "class_date_booking_not_found");
    }

    // --- Suspending the whole class (RF-10) ------------------------------------------------------------------------

    [Fact]
    public async Task Suspending_a_class_stores_the_status_the_reason_and_the_instant()
    {
        AddBooking(500);

        var result = await _service.SuspendClassAsync(1, 100, "  Feria del barrio  ");

        Assert.True(result.Succeeded);
        Assert.Equal(100, result.Value.AppointmentId);
        Assert.Equal(AppointmentStatus.Suspended, _repository.Session!.Status);
        Assert.Equal("Feria del barrio", _repository.Session.SuspensionReason);
        Assert.Equal(ClockNow, _repository.Session.SuspendedAt);
        Assert.Equal(1, _unit.BeginCount);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
    }

    [Fact]
    public async Task Suspending_a_class_queues_one_notice_per_non_cancelled_annotation()
    {
        // Fixed and drop-in alike are notified; a cancelled annotation is not, because its place was already released.
        AddBooking(500, modality: BookingModality.Fixed, email: "fija@example.com", name: "Fija Fija", clientId: 11);
        AddBooking(501, modality: BookingModality.DropIn, email: "suelta@example.com", name: "Suelta Suelta", clientId: 12);
        AddBooking(502, modality: BookingModality.DropIn, status: BookingStatus.Cancelled, email: "cancelada@example.com");

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value.NoticesQueued);
        Assert.Equal(2, _repository.Outbox.Count);
        Assert.Equal(2, _repository.EnqueueCallCount);

        var fixedNotice = Assert.Single(_repository.Outbox, notice => notice.Recipient == "fija@example.com");
        Assert.Equal(NotificationType.ClassSuspended, fixedNotice.Type);
        Assert.Equal(1, fixedNotice.BusinessId);
        Assert.Equal("suspension:appointment:100:booking:500", fixedNotice.DedupKey);
        Assert.Equal(ClockNow, fixedNotice.CreatedAt);

        var dropInNotice = Assert.Single(_repository.Outbox, notice => notice.Recipient == "suelta@example.com");
        Assert.Equal("suspension:appointment:100:booking:501", dropInNotice.DedupKey);
    }

    [Fact]
    public async Task A_foreign_missing_or_individual_appointment_is_not_found()
    {
        // Foreign: a session id of another business is invisible.
        _repository.Session = new FakeClassDateSession(
            2, 100, ClassStartsAt, AppointmentKind.ClassSession, AppointmentStatus.Scheduled);
        var foreign = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        // Missing: the port finds no row.
        _repository.Session = null;
        var missing = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        // An individual turn: only a class can be suspended (RF-10).
        _repository.Session = new FakeClassDateSession(
            1, 100, ClassStartsAt, AppointmentKind.Individual, AppointmentStatus.Scheduled);
        AddBooking(500);
        var individual = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        AssertRolledBack(foreign, "class_date_class_not_found");
        AssertRolledBack(missing, "class_date_class_not_found");
        AssertRolledBack(individual, "class_date_class_not_found");
        Assert.Empty(_repository.Outbox);
    }

    [Fact]
    public async Task Suspending_a_started_class_is_refused_and_queues_nothing()
    {
        _clock.Now = ClassStartsAt;
        AddBooking(500);

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        AssertRolledBack(result, "class_date_not_available");
        Assert.Empty(_repository.Outbox);
        Assert.Equal(AppointmentStatus.Scheduled, _repository.Session!.Status);
    }

    [Fact]
    public async Task Suspending_an_already_suspended_class_is_refused_and_queues_nothing()
    {
        _repository.Session!.Status = AppointmentStatus.Suspended;
        AddBooking(500);

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        AssertRolledBack(result, "class_date_not_available");
        Assert.Empty(_repository.Outbox);
        Assert.Equal(0, _repository.EnqueueCallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_or_blank_reason_is_rejected_without_touching_storage(string? reason)
    {
        AddBooking(500);

        var result = await _service.SuspendClassAsync(1, 100, reason);

        AssertRejectedBeforeStorage(result, "class_date_invalid_reason");
        Assert.Equal(AppointmentStatus.Scheduled, _repository.Session!.Status);
    }

    [Fact]
    public async Task A_reason_longer_than_the_stored_column_is_rejected_without_touching_storage()
    {
        AddBooking(500);

        var result = await _service.SuspendClassAsync(1, 100, new string('x', 301));

        AssertRejectedBeforeStorage(result, "class_date_invalid_reason");
    }

    [Fact]
    public async Task Invalid_suspension_ids_are_not_found_without_touching_storage()
    {
        AddBooking(500);

        var byBusiness = await _service.SuspendClassAsync(0, 100, "Feria del barrio");
        var byAppointment = await _service.SuspendClassAsync(1, 0, "Feria del barrio");

        AssertRejectedBeforeStorage(byBusiness, "class_date_class_not_found");
        AssertRejectedBeforeStorage(byAppointment, "class_date_class_not_found");
    }

    // --- The guarantees both actions share ------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_releases_the_place_of_that_date_and_leaves_the_other_annotations_untouched()
    {
        var cancelled = AddBooking(500, modality: BookingModality.Fixed);
        var kept = AddBooking(501, modality: BookingModality.DropIn);
        var absent = AddBooking(502, status: BookingStatus.Absent);
        var takenBefore = ClassCapacity.PlacesTaken(6, _repository.Bookings.Select(booking => booking.Status));

        var result = await _service.CancelBookingAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(3, takenBefore);
        // The single capacity rule of the domain is what releases the place: no parallel count exists.
        Assert.Equal(2, ClassCapacity.PlacesTaken(6, _repository.Bookings.Select(booking => booking.Status)));
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Equal(BookingModality.Fixed, cancelled.Modality);
        Assert.Equal(BookingStatus.Pending, kept.Status);
        Assert.Equal(BookingStatus.Absent, absent.Status);
        Assert.Empty(_repository.Outbox);
        Assert.Equal(0, _repository.EnqueueCallCount);
    }

    [Fact]
    public async Task Cancellation_locks_the_class_session_before_the_annotation()
    {
        AddBooking(500);

        var result = await _service.CancelBookingAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "appointments", "booking" }, _repository.LockOrder);
    }

    [Fact]
    public async Task Suspension_locks_the_class_session_before_reading_the_annotations_under_it()
    {
        AddBooking(500);

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "appointments", "bookings" }, _repository.LockOrder);
    }

    [Fact]
    public async Task A_competing_suspension_cannot_land_between_the_cancellation_lock_and_the_guarded_write()
    {
        // A suspension of the same class while a cancellation is in flight would make the guarded write refuse after
        // the read. Both actions take the Appointments row first and hold it until the unit ends, so the suspension
        // waits for this unit: the fake models the same refusal.
        var booking = AddBooking(500);
        var suspensionApplied = false;
        _repository.OnBookingLocked = () => suspensionApplied = _repository.TrySuspendSession();

        var result = await _service.CancelBookingAsync(1, 500);

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.False(suspensionApplied);
        Assert.Equal(AppointmentStatus.Scheduled, _repository.Session!.Status);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
    }

    [Fact]
    public async Task The_class_lock_only_defers_the_suspension_for_the_unit()
    {
        // The lock is not a ban on suspending the class: once the unit ends the suspension lands, and it does not
        // touch the cancellation already recorded (RF-10: suspending a class never alters its annotations).
        var booking = AddBooking(500);

        var result = await _service.CancelBookingAsync(1, 500);

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.False(_repository.SessionLockHeld);
        Assert.True(_repository.TrySuspendSession());
        Assert.Equal(AppointmentStatus.Suspended, _repository.Session!.Status);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
    }

    [Fact]
    public async Task A_guarded_cancellation_that_changes_no_row_is_reported_and_rolls_back()
    {
        var booking = AddBooking(500);
        _repository.RefuseCancelWrite = true;

        var result = await _service.CancelBookingAsync(1, 500);

        AssertRolledBack(result, "class_date_not_available");
        Assert.Equal(1, _repository.CancelWriteCallCount);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Null(booking.CancelledAt);
    }

    [Fact]
    public async Task A_guarded_suspension_that_changes_no_row_is_reported_and_queues_nothing()
    {
        AddBooking(500);
        _repository.RefuseSuspendWrite = true;

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        AssertRolledBack(result, "class_date_not_available");
        Assert.Equal(1, _repository.SuspendWriteCallCount);
        Assert.Empty(_repository.Outbox);
        Assert.Equal(0, _repository.EnqueueCallCount);
    }

    [Fact]
    public async Task Every_cancellation_rejection_rolls_back_and_never_commits()
    {
        // Already cancelled: the state of the annotation is refused after the class availability, so the class must
        // still be before its start for this case to be about the annotation.
        var booking = AddBooking(500);
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = ClockNow.AddDays(-1);
        AssertRolledBack(await _service.CancelBookingAsync(1, 500), "class_date_booking_cancelled");

        // A class that already started, with an annotation that is still open.
        AddBooking(501);
        _clock.Now = ClassStartsAt;
        AssertRolledBack(await _service.CancelBookingAsync(1, 501), "class_date_not_available");

        AssertRolledBack(await _service.CancelBookingAsync(1, 999), "class_date_booking_not_found");

        Assert.Equal(0, _unit.CommitCount);
        Assert.Equal(3, _unit.RolledBackCount);
    }

    [Fact]
    public async Task Every_suspension_rejection_rolls_back_and_queues_nothing()
    {
        AddBooking(500);
        AssertRejectedBeforeStorage(await _service.SuspendClassAsync(1, 100, "   "), "class_date_invalid_reason");
        AssertRolledBack(await _service.SuspendClassAsync(1, 999, "Feria del barrio"), "class_date_class_not_found");

        _repository.Session!.Status = AppointmentStatus.Suspended;
        AssertRolledBack(await _service.SuspendClassAsync(1, 100, "Feria del barrio"), "class_date_not_available");

        Assert.Equal(0, _unit.CommitCount);
        Assert.Equal(2, _unit.RolledBackCount);
        Assert.Empty(_repository.Outbox);
    }

    [Fact]
    public async Task Cancellation_reads_the_clock_once_and_persists_that_instant()
    {
        // A clock that advances one day per read: a second read would be the next day and could not pass unnoticed.
        var clock = new AdvancingClock(ClassStartsAt.AddMinutes(-30));
        var service = new ClassDateActionsService(_repository, _unit, clock);
        var booking = AddBooking(500);

        var result = await service.CancelBookingAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(ClassStartsAt.AddMinutes(-30), booking.CancelledAt);
    }

    [Fact]
    public async Task Suspension_reads_the_clock_once_and_stamps_its_instant_on_every_notice()
    {
        var clock = new AdvancingClock(ClockNow);
        var service = new ClassDateActionsService(_repository, _unit, clock);
        AddBooking(500);
        AddBooking(501, email: "otra@example.com", clientId: 12);

        var result = await service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(ClockNow, _repository.Session!.SuspendedAt);
        Assert.All(_repository.Outbox, notice => Assert.Equal(ClockNow, notice.CreatedAt));
    }

    // --- The suspension notice (RF-10) -----------------------------------------------------------------------------

    [Fact]
    public async Task The_suspension_notice_carries_the_reason_and_the_class_date_and_time_html_encoded()
    {
        AddBooking(500, email: "ana@example.com", name: "Ana <b>Pérez</b>");

        var result = await _service.SuspendClassAsync(1, 100, "Feria <del>barrio</del> & lluvia");

        Assert.True(result.Succeeded);
        var notice = Assert.Single(_repository.Outbox);
        Assert.Equal("Clase suspendida", notice.Subject);
        // Every value is HTML-encoded, so no stored text can inject markup: the tag is gone and its entities remain.
        Assert.Contains("Ana &lt;b&gt;", notice.BodyHtml);
        Assert.Contains("&lt;/b&gt;", notice.BodyHtml);
        Assert.Contains("Feria &lt;del&gt;barrio&lt;/del&gt; &amp; lluvia", notice.BodyHtml);
        Assert.Contains("12 de marzo", notice.BodyHtml);
        Assert.Contains("a las 18:00", notice.BodyHtml);
        Assert.DoesNotContain("<b>", notice.BodyHtml);
        Assert.DoesNotContain("<del>", notice.BodyHtml);
    }

    [Fact]
    public async Task The_suspension_notice_has_no_management_link()
    {
        // A class has no private link: only an individual turn does (RF-23).
        AddBooking(500);

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(result.Succeeded);
        var notice = Assert.Single(_repository.Outbox);
        Assert.DoesNotContain("http", notice.BodyHtml);
        Assert.DoesNotContain("/t/", notice.BodyHtml);
    }

    [Fact]
    public async Task A_notice_already_stored_for_the_same_student_and_class_is_not_duplicated()
    {
        // The outbox already holds the notice of the first student for this class, as a technical retry would leave
        // it. The stable key stops a second row and the outcome reports what actually landed.
        AddBooking(500, email: "ana@example.com");
        AddBooking(501, email: "otra@example.com", clientId: 12);
        _repository.Outbox.Add(new Notification
        {
            BusinessId = 1,
            Type = NotificationType.ClassSuspended,
            Recipient = "ana@example.com",
            Subject = "Clase suspendida",
            BodyHtml = "<p>Hola</p>",
            DedupKey = "suspension:appointment:100:booking:500",
            CreatedAt = ClockNow.AddMinutes(-5)
        });

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value.NoticesQueued);
        Assert.Equal(2, _repository.EnqueueCallCount);
        Assert.Equal(2, _repository.Outbox.Count);
        Assert.Equal(1, _repository.Outbox.Count(notice => notice.DedupKey == "suspension:appointment:100:booking:500"));
    }

    [Fact]
    public async Task A_duplicate_suspension_request_is_refused_and_emits_no_extra_notices()
    {
        AddBooking(500);
        AddBooking(501, email: "otra@example.com", clientId: 12);

        var first = await _service.SuspendClassAsync(1, 100, "Feria del barrio");
        var second = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(first.Succeeded);
        Assert.Equal(2, first.Value.NoticesQueued);
        Assert.False(second.Succeeded);
        Assert.Equal("class_date_not_available", second.Error!.Code);
        Assert.Equal(2, _repository.Outbox.Count);
        Assert.Equal(2, _repository.EnqueueCallCount);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(1, _unit.RolledBackCount);
    }

    [Fact]
    public async Task An_annotation_whose_client_cannot_be_resolved_is_still_notified()
    {
        // The client join is a LEFT JOIN, so no anomaly can drop an annotation from the suspension: the notice is
        // queued with the empty recipient and the sender surfaces the failure, instead of losing the student silently.
        AddBooking(500, email: "");

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(result.Succeeded);
        var notice = Assert.Single(_repository.Outbox);
        Assert.Equal("", notice.Recipient);
        Assert.Equal("suspension:appointment:100:booking:500", notice.DedupKey);
    }

    [Fact]
    public async Task Suspending_preserves_every_annotation_the_class_date_and_its_status()
    {
        var fixedBooking = AddBooking(500, modality: BookingModality.Fixed);
        var dropIn = AddBooking(501, modality: BookingModality.DropIn);
        var cancelled = AddBooking(502, status: BookingStatus.Cancelled);
        cancelled.CancelledAt = ClockNow.AddDays(-2);
        var startsAt = _repository.Session!.StartsAt;

        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Pending, fixedBooking.Status);
        Assert.Equal(BookingModality.Fixed, fixedBooking.Modality);
        Assert.Equal(BookingStatus.Pending, dropIn.Status);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Equal(ClockNow.AddDays(-2), cancelled.CancelledAt);
        Assert.Null(fixedBooking.CancelledAt);
        Assert.Equal(startsAt, _repository.Session!.StartsAt);
        Assert.Equal(1, _repository.SuspendWriteCallCount);
        // The cancelled annotation is preserved and is the only one that receives no notice.
        Assert.Equal(2, _repository.Outbox.Count);
    }

    [Fact]
    public async Task Suspending_a_class_without_annotations_queues_no_notices()
    {
        var result = await _service.SuspendClassAsync(1, 100, "Feria del barrio");

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value.NoticesQueued);
        Assert.Empty(_repository.Outbox);
        Assert.Equal(0, _repository.EnqueueCallCount);
        Assert.Equal(AppointmentStatus.Suspended, _repository.Session!.Status);
        Assert.Equal("Feria del barrio", _repository.Session.SuspensionReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public async Task A_reason_that_fits_after_trimming_is_accepted(int length)
    {
        AddBooking(500);

        var result = await _service.SuspendClassAsync(1, 100, $" {new string('x', length)} ");

        Assert.True(result.Succeeded);
        Assert.Equal(new string('x', length), _repository.Session!.SuspensionReason);
    }

    /// <summary>A clock that advances one day per read, so a second read changes the day and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }

        public DateTime Now => first.AddDays(ReadCount++);
    }
}
