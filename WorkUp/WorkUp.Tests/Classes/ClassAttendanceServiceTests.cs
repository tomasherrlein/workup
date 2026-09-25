using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using System.Globalization;

namespace WorkUp.Tests.Classes;

// T16 unit 2 (RF-11, RF-12, D-16, D-20, D-23): per-student attendance, absence and the optional drop-in charge on a
// generated class session. The service works under one booking lock and one clock read, and every rejection rolls
// back the transaction it opened without writing anything.
public class ClassAttendanceServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime ClassStartsAt = new(2026, 3, 12, 18, 0, 0); // Thursday

    private readonly FakeClassAttendanceRepository _repository = new();
    private readonly FakeUnitOfWork _unit = new();
    private readonly FakeClock _clock = new() { Now = ClockNow };
    private readonly ClassAttendanceService _service;

    public ClassAttendanceServiceTests()
    {
        _service = new ClassAttendanceService(_repository, _unit, _clock);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    /// <summary>One membership period of the fixed enrollment, the input of the coverage rule.</summary>
    private static MembershipPeriod Period(DateOnly coverageStart, DateOnly expiresOn) => new()
    {
        EnrollmentId = 100,
        CoverageStart = coverageStart,
        ExpiresOn = expiresOn
    };

    /// <summary>
    /// One class annotation as the lock reads it. The defaults describe a drop-in pending annotation of the tenant,
    /// which is the permissive case; every test narrows what it needs.
    /// </summary>
    private static ClassAttendanceBookingState State(
        BookingModality? modality = BookingModality.DropIn,
        BookingStatus status = BookingStatus.Pending,
        EnrollmentStatus? enrollmentStatus = null,
        int? enrollmentId = null,
        IReadOnlyList<MembershipPeriod>? periods = null,
        int businessId = 1,
        int bookingId = 500,
        int clientId = 11,
        int appointmentId = 100,
        DateTime? startsAt = null,
        decimal listPrice = 100m) =>
        new(businessId, bookingId, clientId, appointmentId, startsAt ?? ClassStartsAt,
            AppointmentKind.ClassSession, status, modality, enrollmentId, enrollmentStatus, listPrice, periods ?? []);

    /// <summary>A fixed annotation whose enrollment is active and whose period covers the class date.</summary>
    private static ClassAttendanceBookingState CoveredFixedState() => State(
        modality: BookingModality.Fixed,
        enrollmentStatus: EnrollmentStatus.Active,
        enrollmentId: 100,
        periods: [Period(D(2026, 3, 1), D(2026, 4, 1))]);

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
        Assert.Equal(0, _repository.LockCallCount);
    }

    [Fact]
    public async Task Attendance_on_a_drop_in_is_allowed()
    {
        _repository.Booking = State();

        var result = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Attended, _repository.LastWrittenStatus);
        Assert.Null(_repository.LastWrittenAbsenceSource);
        Assert.Equal(ClockNow, _repository.LastWrittenAt);
        Assert.Equal(1, _unit.BeginCount);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
    }

    [Fact]
    public async Task Attendance_on_a_fixed_student_with_current_coverage_is_allowed()
    {
        _repository.Booking = CoveredFixedState();

        var result = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Attended, _repository.LastWrittenStatus);
        Assert.Equal(1, _unit.CommitCount);
    }

    [Theory]
    // Pending renewal: an active enrollment whose period expired before the class date. Pending payment: the
    // enrollment never activated. Annulled: the enrollment released its places. None of them enables attendance
    // (D-16, D-23); the first two keep the place, the third does not.
    [InlineData(EnrollmentStatus.Active, "2026-02-01", "2026-03-01")]
    [InlineData(EnrollmentStatus.PendingPayment, null, null)]
    [InlineData(EnrollmentStatus.Annulled, null, null)]
    [InlineData(EnrollmentStatus.Terminated, null, null)]
    public async Task Attendance_is_refused_for_a_fixed_student_without_current_coverage(
        EnrollmentStatus enrollmentStatus, string? coverageStart, string? expiresOn)
    {
        var periods = coverageStart is null
            ? Array.Empty<MembershipPeriod>()
            : new[]
            {
                Period(DateOnly.ParseExact(coverageStart, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(expiresOn!, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            };
        // Pending payment is an enrollment that never paid, so it is read with no periods; the active one carries
        // the expired period that makes it pending renewal.
        _repository.Booking = State(
            modality: BookingModality.Fixed, enrollmentStatus: enrollmentStatus, enrollmentId: 100, periods: periods);

        var result = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        AssertRolledBack(result, "class_attendance_without_coverage");
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task Attendance_is_refused_for_an_annulled_enrollment_even_with_a_covering_period()
    {
        // The unit-1 anomaly: a surviving annotation of an annulled enrollment that still carries a covering period.
        // The enrollment released its places, so coverage alone cannot enable attendance: the first half of the rule
        // fails even though the calendar says Current.
        _repository.Booking = State(
            modality: BookingModality.Fixed,
            enrollmentStatus: EnrollmentStatus.Annulled,
            enrollmentId: 100,
            periods: [Period(D(2026, 3, 1), D(2026, 4, 1))]);

        var result = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        AssertRolledBack(result, "class_attendance_without_coverage");
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task Attendance_is_refused_for_a_pending_payment_enrollment_even_with_a_covering_period()
    {
        // A `Pendiente de pago` enrollment retains its place but its first period was never paid, so the student has
        // no current membership to attend with (D-16). The refusal holds even in the anomalous case where a stored
        // period covered the class date, which is what keeps this rule separate from the calendar check.
        _repository.Booking = State(
            modality: BookingModality.Fixed,
            enrollmentStatus: EnrollmentStatus.PendingPayment,
            enrollmentId: 100,
            periods: [Period(D(2026, 3, 1), D(2026, 4, 1))]);

        var result = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        AssertRolledBack(result, "class_attendance_without_coverage");
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task Attendance_is_refused_for_a_cancelled_annotation()
    {
        _repository.Booking = State(status: BookingStatus.Cancelled);

        var result = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        AssertRolledBack(result, "class_attendance_cancelled");
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task Absence_is_refused_for_a_cancelled_annotation()
    {
        _repository.Booking = State(status: BookingStatus.Cancelled);

        var result = await _service.MarkAbsentAsync(1, 500);

        AssertRolledBack(result, "class_attendance_cancelled");
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task Absence_is_allowed_for_a_pending_renewal_fixed_student()
    {
        // The absence is a fact: a fixed student who keeps the place but has no current coverage can still be absent.
        _repository.Booking = State(
            modality: BookingModality.Fixed,
            enrollmentStatus: EnrollmentStatus.Active,
            enrollmentId: 100,
            periods: [Period(D(2026, 2, 1), D(2026, 3, 1))]);

        var result = await _service.MarkAbsentAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Absent, _repository.LastWrittenStatus);
        Assert.Equal(AbsenceSource.Owner, _repository.LastWrittenAbsenceSource);
        Assert.Equal(ClockNow, _repository.LastWrittenAt);
        Assert.Equal(1, _unit.CommitCount);
    }

    [Fact]
    public async Task Absence_is_allowed_for_a_pending_payment_fixed_student()
    {
        _repository.Booking = State(
            modality: BookingModality.Fixed, enrollmentStatus: EnrollmentStatus.PendingPayment, enrollmentId: 100);

        var result = await _service.MarkAbsentAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Absent, _repository.LastWrittenStatus);
        Assert.Equal(AbsenceSource.Owner, _repository.LastWrittenAbsenceSource);
    }

    [Fact]
    public async Task A_suspended_class_refuses_the_attendance_the_charge_and_the_absence()
    {
        // RF-10: a suspended class did not happen, so nobody attends it, a drop-in does not pay for it and nobody is
        // absent from it. The status comes from the lock, so the refusal happens before any write.
        _repository.Booking = State();
        _repository.SessionStatus = AppointmentStatus.Suspended;

        var attended = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(500m)));
        AssertRolledBack(attended, "class_attendance_suspended");
        Assert.Null(_repository.LastWrittenStatus);
        Assert.Empty(_repository.Payments);

        var absent = await _service.MarkAbsentAsync(1, 500);
        AssertRolledBack(absent, "class_attendance_suspended");
        Assert.Null(_repository.LastWrittenStatus);

        // The suspension is judged before the coverage rule, so a fixed student with current coverage is refused
        // exactly the same way: the class did not happen for anyone.
        _repository.Booking = CoveredFixedState();
        var covered = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());
        AssertRolledBack(covered, "class_attendance_suspended");
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task A_second_attendance_is_refused_with_a_stable_code_and_never_rewrites()
    {
        _repository.Booking = State(status: BookingStatus.Attended);

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(100m)));

        AssertRolledBack(result, "class_attendance_already_attended");
        Assert.Empty(_repository.Payments);
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task A_second_absence_is_refused_with_a_stable_code()
    {
        _repository.Booking = State(status: BookingStatus.Absent);

        var result = await _service.MarkAbsentAsync(1, 500);

        AssertRolledBack(result, "class_attendance_already_absent");
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task Attendance_never_overwrites_an_absence()
    {
        _repository.Booking = State(status: BookingStatus.Absent);

        var result = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        AssertRolledBack(result, "class_attendance_already_absent");
        Assert.Equal(BookingStatus.Absent, _repository.Booking!.BookingStatus);
    }

    [Fact]
    public async Task Absence_never_overwrites_an_attendance()
    {
        _repository.Booking = State(status: BookingStatus.Attended);

        var result = await _service.MarkAbsentAsync(1, 500);

        AssertRolledBack(result, "class_attendance_already_attended");
        Assert.Equal(BookingStatus.Attended, _repository.Booking!.BookingStatus);
    }

    [Fact]
    public async Task A_foreign_or_missing_or_non_class_annotation_is_not_found()
    {
        // Foreign: the same booking id under another business is invisible.
        _repository.Booking = State(businessId: 2);
        var foreign = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        // Missing: the port returns null.
        _repository.Booking = null;
        var missing = await _service.MarkAbsentAsync(1, 500);

        // An individual turn: it is not a class session, whatever the modality says.
        _repository.Booking = State() with { Kind = AppointmentKind.Individual };
        var individual = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        // A class annotation without a modality is not a class annotation the rules can judge.
        _repository.Booking = State(modality: null);
        var noModality = await _service.MarkAbsentAsync(1, 500);

        AssertRolledBack(foreign, "class_attendance_not_found");
        AssertRolledBack(missing, "class_attendance_not_found");
        AssertRolledBack(individual, "class_attendance_not_found");
        AssertRolledBack(noModality, "class_attendance_not_found");
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1, 0)]
    public async Task Invalid_ids_are_not_found_without_touching_storage(int businessId, int bookingId)
    {
        _repository.Booking = State();

        var result = await _service.MarkAttendedAsync(businessId, bookingId, new ClassAttendanceRequest());

        AssertRejectedBeforeStorage(result, "class_attendance_not_found");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(10000000000)]
    public async Task Invalid_amounts_are_rejected_without_touching_storage(decimal amount)
    {
        _repository.Booking = State();

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(amount)));

        AssertRejectedBeforeStorage(result, "class_attendance_invalid_charge");
    }

    [Fact]
    public async Task A_long_price_reason_is_rejected_without_touching_storage()
    {
        _repository.Booking = State();

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(500m, new string('x', 201))));

        AssertRejectedBeforeStorage(result, "class_attendance_invalid_charge");
    }

    [Fact]
    public async Task A_long_note_is_rejected_without_touching_storage()
    {
        _repository.Booking = State();

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(Note: new string('x', 1001)));

        AssertRejectedBeforeStorage(result, "class_attendance_invalid_note");
    }

    [Fact]
    public async Task A_charge_on_a_fixed_annotation_is_refused()
    {
        _repository.Booking = CoveredFixedState();

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(500m)));

        AssertRolledBack(result, "class_attendance_charge_not_allowed");
        Assert.Empty(_repository.Payments);
        Assert.Null(_repository.LastWrittenStatus);
    }

    [Fact]
    public async Task A_drop_in_charge_is_written_with_the_attendance_in_one_transaction()
    {
        _repository.Booking = State();

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(450m, "Clase suelta con descuento")));

        Assert.True(result.Succeeded);
        var payment = Assert.Single(_repository.Payments);
        Assert.Equal(1, payment.BusinessId);
        Assert.Equal(11, payment.ClientId);
        Assert.Equal(500, payment.BookingId);
        Assert.Equal(PaymentConcept.DropInClass, payment.Concept);
        Assert.Equal(450m, payment.Amount);
        Assert.Equal(100m, payment.ListPrice);
        Assert.Equal("Clase suelta con descuento", payment.PriceReason);
        Assert.Equal(ClockNow, payment.PaidAt);
        Assert.Equal(BookingStatus.Attended, _repository.LastWrittenStatus);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
    }

    [Fact]
    public async Task A_charge_is_never_duplicated_for_the_same_booking()
    {
        _repository.Booking = State();
        _repository.Payments.Add(new ClassAttendancePayment(
            1, 11, 500, PaymentConcept.DropInClass, 500m, 100m, null, ClockNow));

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(500m)));

        AssertRolledBack(result, "class_attendance_already_charged");
        Assert.Single(_repository.Payments);
        Assert.Null(_repository.LastWrittenStatus);
        Assert.Equal(1, _repository.PaymentCheckCount);
    }

    [Fact]
    public async Task An_absence_never_charges()
    {
        _repository.Booking = State();

        var result = await _service.MarkAbsentAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Empty(_repository.Payments);
        Assert.Empty(_repository.Notes);
        Assert.Equal(0, _repository.PaymentCheckCount);
    }

    [Fact]
    public async Task A_note_lands_atomically_with_the_status()
    {
        _repository.Booking = State();

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(Note: "  Alergia al amoníaco  "));

        Assert.True(result.Succeeded);
        var note = Assert.Single(_repository.Notes);
        Assert.Equal(1, note.BusinessId);
        Assert.Equal(11, note.ClientId);
        Assert.Equal(100, note.AppointmentId);
        Assert.Equal("Alergia al amoníaco", note.Text);
        Assert.Equal(ClockNow, note.CreatedAt);
        Assert.Equal(BookingStatus.Attended, _repository.LastWrittenStatus);
        Assert.Equal(1, _unit.CommitCount);
    }

    [Fact]
    public async Task The_note_is_written_before_the_status_and_the_charge()
    {
        // The observation is the only write the storage can refuse while answering a failure, so it is written
        // first: when this operation joins an outer transaction that its opener later commits, a refused note cannot
        // leave the status write or the charge behind, because neither was written yet.
        _repository.Booking = State();

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(450m), "Alergia al amoníaco"));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "note", "status", "payment" }, _repository.WriteOrder);
    }

    [Fact]
    public async Task A_note_that_cannot_land_rolls_the_whole_operation_back()
    {
        _repository.Booking = State();
        _repository.FailNoteInsert = true;

        var result = await _service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(500m), "Alergia al amoníaco"));

        // The note never landed, so the whole unit was rolled back: no commit, and the status write and the charge
        // were never even asked for, because the note goes first. The fake does not model the SQL rollback, so the
        // transaction counters are the observable proof here; ClassAttendanceSqlIntegrationTests proves on real SQL
        // that the rolled-back unit erases the status write and the charge, and that a failed note leaves nothing
        // behind even inside an outer transaction its opener commits.
        AssertRolledBack(result, "class_attendance_note_failed");
        Assert.Empty(_repository.Notes);
        Assert.Empty(_repository.Payments);
        Assert.Null(_repository.LastWrittenStatus);
        Assert.Equal(new[] { "note" }, _repository.WriteOrder);
    }

    [Fact]
    public async Task A_guarded_status_write_that_changes_no_row_is_reported_and_rolls_back()
    {
        _repository.Booking = State();
        _repository.RefuseStatusWrite = true;

        var result = await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest());

        AssertRolledBack(result, "class_attendance_unavailable");
        Assert.Equal(0, _unit.CommitCount);
    }

    [Fact]
    public async Task Attendance_reads_the_clock_once_and_persists_that_instant()
    {
        // A clock that advances one day per read: a second read would be the next day and could not pass unnoticed.
        var clock = new AdvancingClock(ClassStartsAt);
        var service = new ClassAttendanceService(_repository, _unit, clock);
        _repository.Booking = State();

        var result = await service.MarkAttendedAsync(
            1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(500m), "Nota de una sola lectura"));

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(ClassStartsAt, _repository.LastWrittenAt);
        Assert.Equal(ClassStartsAt, _repository.Payments.Single().PaidAt);
        Assert.Equal(ClassStartsAt, _repository.Notes.Single().CreatedAt);
    }

    [Fact]
    public async Task Every_rejection_rolls_back_and_never_commits()
    {
        // The four in-transaction rejections, each with its own stable code, share the same atomicity guarantee: the
        // transaction that was opened is rolled back and nothing is written.
        _repository.Booking = State(status: BookingStatus.Cancelled);
        AssertRolledBack(
            await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest()),
            "class_attendance_cancelled");

        _repository.Booking = State(status: BookingStatus.Attended);
        AssertRolledBack(
            await _service.MarkAbsentAsync(1, 500),
            "class_attendance_already_attended");

        _repository.Booking = State(modality: BookingModality.Fixed, enrollmentStatus: EnrollmentStatus.PendingPayment);
        AssertRolledBack(
            await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest()),
            "class_attendance_without_coverage");

        _repository.Booking = CoveredFixedState();
        AssertRolledBack(
            await _service.MarkAttendedAsync(1, 500, new ClassAttendanceRequest(new ClassAttendanceCharge(500m))),
            "class_attendance_charge_not_allowed");

        Assert.Equal(0, _unit.CommitCount);
        Assert.Equal(4, _unit.RolledBackCount);
    }

    /// <summary>A clock that advances one day per read, so a second read changes the day and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }

        public DateTime Now => first.AddDays(ReadCount++);
    }
}
