using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Classes;

// RF-06, D-16, D-23: read-only preview of one Monday-to-Sunday week. It classifies every active grid class as a new
// or already generated session and lists the fixed students whose enrollment still holds places. "Pendiente de
// renovación" is derived for the preview from an active enrollment whose periods do not cover the session date; it is
// never persisted. Cancelled fixed bookings and suspended sessions stay visible and are never announced as reused.
public class WeekGenerationServiceTests
{
    private readonly FakeWeekGenerationRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();
    private readonly WeekGenerationService _service;

    private int _nextGridClassId = 1;
    private int _nextAppointmentId = 100;
    private int _nextBookingId = 500;

    public WeekGenerationServiceTests()
    {
        _service = new WeekGenerationService(_repository, _unitOfWork, _clock);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    private int AddGridClass(
        DayOfWeek day, int hour, int serviceId = 7, int workerId = 3, int durationMinutes = 60, int maxCapacity = 8)
    {
        var gridClassId = _nextGridClassId++;
        _repository.GridClasses.Add(new WeekGridClassRow(
            gridClassId, serviceId, workerId, day, T(hour), durationMinutes, maxCapacity));
        return gridClassId;
    }

    private int AddSession(
        int gridClassId,
        DateOnly date,
        int hour = 18,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        params WeekSessionBookingRow[] bookings)
    {
        var appointmentId = _nextAppointmentId++;
        _repository.Sessions.Add(new WeekSessionRow(appointmentId, gridClassId, date, T(hour), status, bookings));
        return appointmentId;
    }

    private WeekSessionBookingRow AddBooking(int enrollmentId, BookingStatus status, string clientName = "Alumna", int clientId = 1)
        => new(_nextBookingId++, enrollmentId, clientId, clientName, status);

    private void AddFixedAssignment(
        int enrollmentId,
        int gridClassId,
        string clientName,
        EnrollmentStatus status,
        DateOnly validFrom,
        DateOnly? validTo = null,
        int clientId = 1,
        params MembershipPeriod[] periods)
    {
        _repository.FixedAssignments.Add(new WeekFixedAssignmentRow(
            enrollmentId, gridClassId, clientId, clientName, status, validFrom, validTo, periods));
    }

    [Fact]
    public async Task Preview_defaults_to_the_next_week_from_the_injected_clock()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday

        var result = await _service.PreviewAsync(1);

        Assert.True(result.Succeeded);
        Assert.Equal(D(2026, 3, 16), result.Value.WeekStart);
        Assert.Equal(D(2026, 3, 22), result.Value.WeekEnd);
        Assert.Equal(D(2026, 3, 16), _repository.RequestedWeekStart);
        Assert.Equal(D(2026, 3, 22), _repository.RequestedWeekEnd);
        Assert.Equal(1, _repository.RequestedBusinessId);
    }

    [Fact]
    public async Task Preview_reads_the_clock_once_so_the_default_week_cannot_drift()
    {
        // Sunday 23:59:59: a second read would already be Monday and would resolve the following week instead.
        var clock = new AdvancingClock(new DateTime(2026, 3, 15, 23, 59, 59));
        var service = new WeekGenerationService(_repository, _unitOfWork, clock);
        AddGridClass(DayOfWeek.Monday, 8);

        var result = await service.PreviewAsync(1);

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(D(2026, 3, 16), result.Value.WeekStart);
        Assert.Equal(D(2026, 3, 22), result.Value.WeekEnd);
    }

    [Fact]
    public async Task Preview_normalizes_the_selected_date_to_the_monday_sunday_week()
    {
        var result = await _service.PreviewAsync(1, D(2026, 3, 11)); // Wednesday

        Assert.True(result.Succeeded);
        Assert.Equal(D(2026, 3, 9), result.Value.WeekStart);
        Assert.Equal(D(2026, 3, 15), result.Value.WeekEnd);
    }

    [Fact]
    public async Task Preview_marks_a_grid_class_without_a_session_as_new()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var session = Assert.Single(result.Value.Sessions);
        Assert.Equal(WeekSessionKind.New, session.Kind);
        Assert.True(session.IsNew);
        Assert.Equal(gridClassId, session.GridClassId);
        Assert.Equal(7, session.ServiceId);
        Assert.Equal(3, session.WorkerId);
        Assert.Equal(D(2026, 3, 10), session.Date);
        Assert.Equal(T(18), session.StartTime);
        Assert.Null(session.AppointmentId);
        Assert.False(session.Suspended);
        Assert.Equal(1, result.Value.NewSessionCount);
        Assert.Equal(0, result.Value.ExistingSessionCount);
    }

    [Fact]
    public async Task Preview_marks_an_existing_session_and_keeps_its_appointment()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        var appointmentId = AddSession(gridClassId, D(2026, 3, 10), 18);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var session = Assert.Single(result.Value.Sessions);
        Assert.Equal(WeekSessionKind.Existing, session.Kind);
        Assert.False(session.IsNew);
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.Equal(AppointmentStatus.Scheduled, session.Status);
        Assert.False(session.Suspended);
        Assert.Equal(0, result.Value.NewSessionCount);
        Assert.Equal(1, result.Value.ExistingSessionCount);
    }

    [Fact]
    public async Task Preview_tolerates_an_existing_session_whose_bookings_are_absent()
    {
        // A row without bookings used to blow up while flagging cancelled bookings; the flag is now computed once
        // from the local list, which is empty when the session carries none.
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        var appointmentId = _nextAppointmentId++;
        _repository.Sessions.Add(new WeekSessionRow(
            appointmentId, gridClassId, D(2026, 3, 10), T(18), AppointmentStatus.Scheduled, null!));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var session = Assert.Single(result.Value.Sessions);
        Assert.Equal(WeekSessionKind.Existing, session.Kind);
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.False(session.HasCancelledBookings);
    }

    [Fact]
    public async Task Preview_lists_a_fixed_student_with_a_pending_payment_enrollment()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(5, gridClassId, "Ana", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var student = Assert.Single(Assert.Single(result.Value.Sessions).FixedStudents);
        Assert.Equal(5, student.EnrollmentId);
        Assert.Equal("Ana", student.ClientName);
        Assert.Equal(WeekFixedStudentState.PendingPayment, student.State);
        Assert.False(student.AlreadyBooked);
        Assert.False(student.Cancelled);
    }

    [Fact]
    public async Task Preview_lists_a_fixed_student_with_a_current_membership_as_active()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(
            6, gridClassId, "Brenda", EnrollmentStatus.Active, D(2026, 3, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var student = Assert.Single(Assert.Single(result.Value.Sessions).FixedStudents);
        Assert.Equal(WeekFixedStudentState.Active, student.State);
    }

    [Fact]
    public async Task Preview_derives_pending_renewal_when_the_active_membership_expired_for_the_session_date()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(
            7, gridClassId, "Carla", EnrollmentStatus.Active, D(2026, 2, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 2, 1), ExpiresOn = D(2026, 3, 1) }]);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var student = Assert.Single(Assert.Single(result.Value.Sessions).FixedStudents);
        Assert.Equal(WeekFixedStudentState.PendingRenewal, student.State);
    }

    [Fact]
    public async Task Preview_keeps_the_latest_period_when_an_earlier_one_expired()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(
            8, gridClassId, "Dora", EnrollmentStatus.Active, D(2026, 1, 1),
            periods:
            [
                new MembershipPeriod { CoverageStart = D(2026, 1, 1), ExpiresOn = D(2026, 2, 1) },
                new MembershipPeriod { CoverageStart = D(2026, 2, 1), ExpiresOn = D(2026, 4, 1) }
            ]);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var student = Assert.Single(Assert.Single(result.Value.Sessions).FixedStudents);
        Assert.Equal(WeekFixedStudentState.Active, student.State);
    }

    [Fact]
    public async Task Preview_marks_a_fixed_student_already_booked_on_an_existing_session()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(
            gridClassId, D(2026, 3, 10), 18,
            AppointmentStatus.Scheduled,
            AddBooking(9, BookingStatus.Pending, "Elena"));
        AddFixedAssignment(9, gridClassId, "Elena", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var student = Assert.Single(Assert.Single(result.Value.Sessions).FixedStudents);
        Assert.True(student.AlreadyBooked);
        Assert.False(student.Cancelled);
    }

    [Fact]
    public async Task Preview_keeps_a_cancelled_fixed_booking_visible_without_announcing_recreation()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(
            gridClassId, D(2026, 3, 10), 18,
            AppointmentStatus.Scheduled,
            AddBooking(10, BookingStatus.Cancelled, "Fabiana"));
        AddFixedAssignment(
            10, gridClassId, "Fabiana", EnrollmentStatus.Active, D(2026, 3, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var session = Assert.Single(result.Value.Sessions);
        Assert.Equal(WeekSessionKind.Existing, session.Kind);
        Assert.True(session.HasCancelledBookings);
        var student = Assert.Single(session.FixedStudents);
        Assert.True(student.Cancelled);
        Assert.False(student.AlreadyBooked);
    }

    [Fact]
    public async Task Preview_prefers_the_active_booking_when_the_cancelled_one_was_replaced()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(
            gridClassId, D(2026, 3, 10), 18,
            AppointmentStatus.Scheduled,
            AddBooking(11, BookingStatus.Cancelled, "Grisel"),
            AddBooking(11, BookingStatus.Confirmed, "Grisel"));
        AddFixedAssignment(11, gridClassId, "Grisel", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var student = Assert.Single(Assert.Single(result.Value.Sessions).FixedStudents);
        Assert.True(student.AlreadyBooked);
        Assert.False(student.Cancelled);
    }

    [Fact]
    public async Task Preview_keeps_a_suspended_session_visible()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(gridClassId, D(2026, 3, 10), 18, AppointmentStatus.Suspended);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var session = Assert.Single(result.Value.Sessions);
        Assert.Equal(WeekSessionKind.Existing, session.Kind);
        Assert.True(session.Suspended);
        Assert.Equal(AppointmentStatus.Suspended, session.Status);
    }

    [Fact]
    public async Task Preview_ignores_a_fixed_assignment_that_is_not_valid_on_the_session_date()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(12, gridClassId, "Hilda", EnrollmentStatus.PendingPayment, D(2026, 3, 1), validTo: D(2026, 3, 9));
        AddFixedAssignment(13, gridClassId, "Irene", EnrollmentStatus.PendingPayment, D(2026, 3, 11));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        Assert.Empty(Assert.Single(result.Value.Sessions).FixedStudents);
    }

    [Theory]
    [InlineData(EnrollmentStatus.Annulled)]
    [InlineData(EnrollmentStatus.Terminated)]
    public async Task Preview_ignores_enrollments_that_no_longer_hold_their_places(EnrollmentStatus status)
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(14, gridClassId, "Julia", status, D(2026, 3, 1));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        Assert.Empty(Assert.Single(result.Value.Sessions).FixedStudents);
    }

    [Fact]
    public async Task Preview_lists_only_the_fixed_students_of_each_grid_class()
    {
        var yogaId = AddGridClass(DayOfWeek.Tuesday, 18);
        var pilatesId = AddGridClass(DayOfWeek.Thursday, 19);
        AddFixedAssignment(15, yogaId, "Karina", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(16, pilatesId, "Laura", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        Assert.Equal(2, result.Value.Sessions.Count);
        Assert.Equal("Karina", Assert.Single(result.Value.Sessions.Single(s => s.GridClassId == yogaId).FixedStudents).ClientName);
        Assert.Equal("Laura", Assert.Single(result.Value.Sessions.Single(s => s.GridClassId == pilatesId).FixedStudents).ClientName);
    }

    [Fact]
    public async Task Preview_orders_sessions_by_date_and_start_time()
    {
        AddGridClass(DayOfWeek.Thursday, 19);
        AddGridClass(DayOfWeek.Tuesday, 18);
        AddGridClass(DayOfWeek.Tuesday, 9);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        Assert.Equal(
            new[] { D(2026, 3, 10), D(2026, 3, 10), D(2026, 3, 12) },
            result.Value.Sessions.Select(session => session.Date).ToArray());
        Assert.Equal(
            new[] { T(9), T(18), T(19) },
            result.Value.Sessions.Select(session => session.StartTime).ToArray());
    }

    [Fact]
    public async Task Preview_fails_when_the_snapshot_belongs_to_another_business()
    {
        _repository.ReportedBusinessId = 2;

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_snapshot_mismatch", result.Error!.Code);
    }

    [Fact]
    public async Task Preview_fails_for_an_invalid_business_without_reading_storage()
    {
        var result = await _service.PreviewAsync(0);

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_invalid_business", result.Error!.Code);
        Assert.Equal(0, _repository.CallCount);
    }

    [Fact]
    public async Task Preview_rejects_an_incomplete_last_week_before_reading_storage()
    {
        // 9999-12-31 is the last representable day and its week has no real Saturday or Sunday. Generating it would
        // duplicate sessions on 9999-12-31 instead of representing the days that do not exist.
        var result = await _service.PreviewAsync(1, D(9999, 12, 31));

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_week_out_of_range", result.Error!.Code);
        Assert.Equal(0, _repository.CallCount);
    }

    [Fact]
    public async Task Preview_rejects_the_default_next_week_when_it_is_not_representable()
    {
        // The last complete week ends on Sunday 9999-12-26; the next Monday opens an incomplete week.
        _clock.Now = new DateTime(9999, 12, 26, 9, 0, 0);

        var result = await _service.PreviewAsync(1);

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_week_out_of_range", result.Error!.Code);
        Assert.Equal(0, _repository.CallCount);
    }

    [Fact]
    public async Task Preview_generates_the_last_complete_week()
    {
        var gridClassId = AddGridClass(DayOfWeek.Sunday, 18);
        AddFixedAssignment(30, gridClassId, "Zoe", EnrollmentStatus.PendingPayment, D(9999, 12, 1));

        var result = await _service.PreviewAsync(1, D(9999, 12, 20));

        Assert.True(result.Succeeded);
        Assert.Equal(D(9999, 12, 20), result.Value.WeekStart);
        Assert.Equal(D(9999, 12, 26), result.Value.WeekEnd);
        var session = Assert.Single(result.Value.Sessions);
        Assert.Equal(D(9999, 12, 26), session.Date);
        Assert.Equal(30, Assert.Single(session.FixedStudents).EnrollmentId);
    }

    [Fact]
    public async Task Preview_generates_the_first_calendar_week()
    {
        var gridClassId = AddGridClass(DayOfWeek.Sunday, 9);

        var result = await _service.PreviewAsync(1, DateOnly.MinValue);

        Assert.True(result.Succeeded);
        Assert.Equal(DateOnly.MinValue, result.Value.WeekStart);
        Assert.Equal(D(1, 1, 7), result.Value.WeekEnd);
        Assert.Equal(D(1, 1, 7), Assert.Single(result.Value.Sessions).Date);
    }

    [Fact]
    public async Task Preview_lists_a_fixed_student_once_when_the_assignment_rows_repeat()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(31, gridClassId, "Marta", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(31, gridClassId, "Marta", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var student = Assert.Single(Assert.Single(result.Value.Sessions).FixedStudents);
        Assert.Equal(31, student.EnrollmentId);
    }

    [Fact]
    public async Task Preview_keeps_every_other_fixed_student_when_one_enrollment_repeats()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(32, gridClassId, "Nadia", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(32, gridClassId, "Nadia", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(33, gridClassId, "Olga", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var students = Assert.Single(result.Value.Sessions).FixedStudents;
        Assert.Equal(new[] { 32, 33 }, students.Select(student => student.EnrollmentId).ToArray());
    }

    [Fact]
    public async Task Preview_keeps_a_cancelled_booking_visible_when_the_enrollment_rows_repeat()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(
            gridClassId, D(2026, 3, 10), 18,
            AppointmentStatus.Scheduled,
            AddBooking(34, BookingStatus.Cancelled, "Paula"));
        AddFixedAssignment(
            34, gridClassId, "Paula", EnrollmentStatus.Active, D(2026, 3, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]);
        AddFixedAssignment(
            34, gridClassId, "Paula", EnrollmentStatus.Active, D(2026, 3, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var student = Assert.Single(Assert.Single(result.Value.Sessions).FixedStudents);
        Assert.True(student.Cancelled);
        Assert.False(student.AlreadyBooked);
    }

    [Fact]
    public async Task Preview_reads_the_week_snapshot_once_for_the_requested_business()
    {
        AddGridClass(DayOfWeek.Monday, 8);

        await _service.PreviewAsync(4, D(2026, 3, 9));

        Assert.Equal(1, _repository.CallCount);
        Assert.Equal(4, _repository.RequestedBusinessId);
    }

    [Fact]
    public async Task Preview_ignores_a_session_dated_outside_the_requested_week()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(gridClassId, D(2026, 3, 3), 18); // previous week

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var session = Assert.Single(result.Value.Sessions);
        Assert.Equal(WeekSessionKind.New, session.Kind);
        Assert.Null(session.AppointmentId);
    }

    [Fact]
    public async Task Preview_prefers_the_existing_session_that_matches_the_grid_start_time()
    {
        // A grid class whose start time changed keeps an older dated session for the same day. The preview must
        // reuse the row that still matches the grid instead of picking an arbitrary one.
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(gridClassId, D(2026, 3, 10), 9);   // stale schedule
        var matchingId = AddSession(gridClassId, D(2026, 3, 10), 18);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        var session = Assert.Single(result.Value.Sessions);
        Assert.Equal(WeekSessionKind.Existing, session.Kind);
        Assert.Equal(matchingId, session.AppointmentId);
    }

    [Fact]
    public async Task Preview_resolves_the_earliest_appointment_when_duplicate_sessions_share_the_start_time()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        var firstId = AddSession(gridClassId, D(2026, 3, 10), 18);
        AddSession(gridClassId, D(2026, 3, 10), 18);

        var result = await _service.PreviewAsync(1, D(2026, 3, 9));

        Assert.Equal(firstId, Assert.Single(result.Value.Sessions).AppointmentId);
    }

    // RF-06, D-16, D-23: confirming a week writes one session per grid class and the fixed annotations that are
    // still missing. It runs inside one transaction, never duplicates an existing annotation, never resurrects a
    // cancellation and never touches a suspension. "Pendiente de renovación" is only counted and derived, never
    // persisted as a state.

    [Fact]
    public async Task Confirm_plans_one_new_session_per_grid_class_with_its_capacity_and_duration()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18, durationMinutes: 90, maxCapacity: 12);
        AddFixedAssignment(40, gridClassId, "Ana", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(
            41, gridClassId, "Brenda", EnrollmentStatus.Active, D(2026, 3, 1),
            clientId: 2,
            periods: [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var session = Assert.Single(_repository.ConfirmedPlan!.Sessions);
        Assert.Equal(gridClassId, session.GridClassId);
        Assert.Equal(7, session.ServiceId);
        Assert.Equal(3, session.WorkerId);
        Assert.Equal(D(2026, 3, 10), session.Date);
        Assert.Equal(T(18), session.StartTime);
        Assert.Equal(90, session.DurationMinutes);
        Assert.Equal(12, session.MaxCapacity);
        Assert.Null(session.AppointmentId);
        Assert.Equal(new[] { 40, 41 }, session.Students.Select(student => student.EnrollmentId).ToArray());
        Assert.Equal(new[] { 1, 2 }, session.Students.Select(student => student.ClientId).ToArray());
    }

    [Fact]
    public async Task Confirm_annotates_a_repeated_enrollment_only_once()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(42, gridClassId, "Carla", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(42, gridClassId, "Carla", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var student = Assert.Single(Assert.Single(_repository.ConfirmedPlan!.Sessions).Students);
        Assert.Equal(42, student.EnrollmentId);
    }

    [Fact]
    public async Task Confirm_reuses_an_existing_session_and_skips_its_existing_booking()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        var appointmentId = AddSession(
            gridClassId, D(2026, 3, 10), 18, AppointmentStatus.Scheduled,
            AddBooking(50, BookingStatus.Confirmed, "Elena"));
        AddFixedAssignment(
            50, gridClassId, "Elena", EnrollmentStatus.Active, D(2026, 3, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var plan = _repository.ConfirmedPlan!;
        var session = Assert.Single(plan.Sessions);
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.Empty(session.Students);
        Assert.Equal(1, plan.ExistingSessionCount);
        Assert.Equal(0, result.Value.CreatedSessions);
    }

    [Fact]
    public async Task Confirm_never_resurrects_a_cancelled_annotation()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(
            gridClassId, D(2026, 3, 10), 18, AppointmentStatus.Scheduled,
            AddBooking(51, BookingStatus.Cancelled, "Fabiana"));
        AddFixedAssignment(51, gridClassId, "Fabiana", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(52, gridClassId, "Grisel", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var student = Assert.Single(Assert.Single(_repository.ConfirmedPlan!.Sessions).Students);
        Assert.Equal(52, student.EnrollmentId);
    }

    [Fact]
    public async Task Confirm_adds_the_missing_annotation_to_an_already_generated_session()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        var appointmentId = AddSession(gridClassId, D(2026, 3, 10), 18);
        AddFixedAssignment(53, gridClassId, "Hilda", EnrollmentStatus.PendingPayment, D(2026, 3, 1));

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var session = Assert.Single(_repository.ConfirmedPlan!.Sessions);
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.Equal(53, Assert.Single(session.Students).EnrollmentId);
    }

    [Fact]
    public async Task Confirm_counts_existing_sessions_and_pending_renewals_and_still_annotates_the_expired_enrollment()
    {
        var newGridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        var existingGridClassId = AddGridClass(DayOfWeek.Thursday, 19);
        var appointmentId = AddSession(existingGridClassId, D(2026, 3, 12), 19);
        AddFixedAssignment(60, newGridClassId, "Julia", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(
            61, existingGridClassId, "Karina", EnrollmentStatus.Active, D(2026, 2, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 2, 1), ExpiresOn = D(2026, 3, 1) }]);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var plan = _repository.ConfirmedPlan!;
        Assert.Equal(1, plan.ExistingSessionCount);
        Assert.Equal(1, plan.PendingRenewalStudentCount);
        Assert.Equal(1, result.Value.ExistingSessions);
        Assert.Equal(1, result.Value.PendingRenewalStudents);
        var existing = plan.Sessions.Single(session => session.AppointmentId == appointmentId);
        Assert.Equal(61, Assert.Single(existing.Students).EnrollmentId);
    }

    [Fact]
    public async Task Confirm_counts_a_pending_renewal_student_that_is_already_booked()
    {
        // The week total is a property of the week, not of what this call writes: an already annotated student is
        // still pending renewal and must count once.
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(
            gridClassId, D(2026, 3, 10), 18, AppointmentStatus.Scheduled,
            AddBooking(70, BookingStatus.Confirmed, "Norma"));
        AddFixedAssignment(
            70, gridClassId, "Norma", EnrollmentStatus.Active, D(2026, 2, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 2, 1), ExpiresOn = D(2026, 3, 1) }]);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var plan = _repository.ConfirmedPlan!;
        Assert.Empty(Assert.Single(plan.Sessions).Students);
        Assert.Equal(1, plan.PendingRenewalStudentCount);
        Assert.Equal(1, result.Value.PendingRenewalStudents);
    }

    [Fact]
    public async Task Confirm_counts_a_pending_renewal_student_enrolled_in_two_classes_once()
    {
        var yogaId = AddGridClass(DayOfWeek.Tuesday, 18);
        var pilatesId = AddGridClass(DayOfWeek.Thursday, 19);
        AddFixedAssignment(
            71, yogaId, "Olga", EnrollmentStatus.Active, D(2026, 2, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 2, 1), ExpiresOn = D(2026, 3, 1) }]);
        AddFixedAssignment(
            71, pilatesId, "Olga", EnrollmentStatus.Active, D(2026, 2, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 2, 1), ExpiresOn = D(2026, 3, 1) }]);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        Assert.Equal(2, _repository.ConfirmedPlan!.Sessions.Count);
        Assert.Equal(1, _repository.ConfirmedPlan.PendingRenewalStudentCount);
        Assert.Equal(1, result.Value.PendingRenewalStudents);
    }

    [Fact]
    public async Task Confirm_does_not_count_a_cancelled_pending_renewal_student()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddSession(
            gridClassId, D(2026, 3, 10), 18, AppointmentStatus.Scheduled,
            AddBooking(72, BookingStatus.Cancelled, "Paula"));
        AddFixedAssignment(
            72, gridClassId, "Paula", EnrollmentStatus.Active, D(2026, 2, 1),
            periods: [new MembershipPeriod { CoverageStart = D(2026, 2, 1), ExpiresOn = D(2026, 3, 1) }]);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        Assert.Equal(0, _repository.ConfirmedPlan!.PendingRenewalStudentCount);
        Assert.Equal(0, result.Value.PendingRenewalStudents);
    }

    [Fact]
    public async Task Confirm_does_not_count_pending_payment_or_active_students_as_pending_renewal()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        AddFixedAssignment(73, gridClassId, "Quimey", EnrollmentStatus.PendingPayment, D(2026, 3, 1));
        AddFixedAssignment(
            74, gridClassId, "Rita", EnrollmentStatus.Active, D(2026, 3, 1), clientId: 2,
            periods: [new MembershipPeriod { CoverageStart = D(2026, 3, 1), ExpiresOn = D(2026, 4, 1) }]);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        Assert.Equal(0, _repository.ConfirmedPlan!.PendingRenewalStudentCount);
        Assert.Equal(0, result.Value.PendingRenewalStudents);
    }

    [Fact]
    public async Task Confirm_reads_the_clock_once_so_the_default_week_cannot_drift()
    {
        // Sunday 23:59:59: a second read would already be Monday and would resolve the following week instead.
        var clock = new AdvancingClock(new DateTime(2026, 3, 15, 23, 59, 59));
        var service = new WeekGenerationService(_repository, _unitOfWork, clock);
        AddGridClass(DayOfWeek.Monday, 8);

        var result = await service.ConfirmAsync(1);

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(D(2026, 3, 16), result.Value.WeekStart);
        Assert.Equal(D(2026, 3, 22), result.Value.WeekEnd);
        Assert.Equal(new DateTime(2026, 3, 15, 23, 59, 59), _repository.ConfirmedPlan!.Now);
    }

    [Fact]
    public async Task Confirm_uses_the_single_clock_read_for_the_past_check()
    {
        // The week ends today at 23:59:59, so it is still accepted. A second read would cross midnight into Monday
        // and the very same week would be rejected as past, which is why the check reuses the one resolved value.
        var clock = new AdvancingClock(new DateTime(2026, 3, 15, 23, 59, 59));
        var service = new WeekGenerationService(_repository, _unitOfWork, clock);

        var result = await service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(D(2026, 3, 15), result.Value.WeekEnd);
        Assert.Equal(new DateTime(2026, 3, 15, 23, 59, 59), _repository.ConfirmedPlan!.Now);
    }

    [Fact]
    public async Task Confirm_rejects_an_invalid_business_without_reading_or_writing()
    {
        var result = await _service.ConfirmAsync(0);

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_invalid_business", result.Error!.Code);
        Assert.Equal(0, _repository.CallCount);
        Assert.Equal(0, _repository.ConfirmCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Confirm_rejects_an_incomplete_week_before_reading_or_writing()
    {
        var result = await _service.ConfirmAsync(1, D(9999, 12, 31));

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_week_out_of_range", result.Error!.Code);
        Assert.Equal(0, _repository.CallCount);
        Assert.Equal(0, _repository.ConfirmCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Confirm_rejects_a_week_that_already_ended_before_reading_or_writing()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday of the week in progress

        var result = await _service.ConfirmAsync(1, D(2026, 3, 2)); // previous Monday to Sunday

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_week_in_the_past", result.Error!.Code);
        Assert.Equal("No se puede generar una semana que ya terminó.", result.Error.Message);
        Assert.Equal(0, _repository.CallCount);
        Assert.Equal(0, _repository.ConfirmCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Confirm_rejects_a_snapshot_from_another_business_without_writing()
    {
        _repository.ReportedBusinessId = 2;

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_snapshot_mismatch", result.Error!.Code);
        Assert.Equal(1, _repository.CallCount);
        Assert.Equal(0, _repository.ConfirmCallCount);
        // The transaction was opened to read inside it, and rejecting the snapshot disposes it without committing.
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Confirm_commits_once_and_returns_the_repository_outcome_with_the_week_bounds()
    {
        _repository.ConfirmOutcome = new WeekGenerationWriteOutcome(3, 4);
        AddGridClass(DayOfWeek.Monday, 8);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        Assert.Equal(1, _repository.CallCount);
        Assert.Equal(1, _repository.ConfirmCallCount);
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
        Assert.Equal(1, _repository.RequestedBusinessId);
        Assert.Equal(D(2026, 3, 9), result.Value.WeekStart);
        Assert.Equal(D(2026, 3, 15), result.Value.WeekEnd);
        Assert.Equal(3, result.Value.CreatedSessions);
        Assert.Equal(4, result.Value.CreatedFixedBookings);
        Assert.Equal(_clock.Now, _repository.ConfirmedPlan!.Now);
    }

    [Fact]
    public async Task Confirm_defaults_to_the_next_week_from_the_injected_clock()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday
        AddGridClass(DayOfWeek.Monday, 8);

        var result = await _service.ConfirmAsync(1);

        Assert.True(result.Succeeded);
        Assert.Equal(D(2026, 3, 16), result.Value.WeekStart);
        Assert.Equal(D(2026, 3, 22), result.Value.WeekEnd);
        Assert.Equal(D(2026, 3, 16), Assert.Single(_repository.ConfirmedPlan!.Sessions).Date);
    }

    [Fact]
    public async Task Confirm_keeps_a_suspended_session_and_plans_no_new_session_for_it()
    {
        var gridClassId = AddGridClass(DayOfWeek.Tuesday, 18);
        var appointmentId = AddSession(gridClassId, D(2026, 3, 10), 18, AppointmentStatus.Suspended);

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        var plan = _repository.ConfirmedPlan!;
        var session = Assert.Single(plan.Sessions);
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.DoesNotContain(plan.Sessions, session => session.AppointmentId is null);
        Assert.Equal(1, plan.ExistingSessionCount);
    }

    [Fact]
    public async Task Confirm_accepts_the_week_that_ends_today()
    {
        // The last day of the week may be today: only a week that already ended is rejected.
        _clock.Now = new DateTime(2026, 3, 15, 20, 0, 0); // Sunday of 2026-03-09..2026-03-15

        var result = await _service.ConfirmAsync(1, D(2026, 3, 9));

        Assert.True(result.Succeeded);
        Assert.Equal(D(2026, 3, 15), result.Value.WeekEnd);
        Assert.Equal(1, _repository.ConfirmCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Confirm_rejects_the_default_next_week_when_it_is_not_representable()
    {
        // Same guard as the preview: the last complete week ends on Sunday 9999-12-26, so the next Monday opens an
        // incomplete week whose Saturday and Sunday do not exist.
        _clock.Now = new DateTime(9999, 12, 26, 9, 0, 0);

        var result = await _service.ConfirmAsync(1);

        Assert.False(result.Succeeded);
        Assert.Equal("week_generation_week_out_of_range", result.Error!.Code);
        Assert.Equal(0, _repository.CallCount);
        Assert.Equal(0, _repository.ConfirmCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    /// <summary>A clock that advances one second per read, so a second read changes the day and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }
        public DateTime Now => first.AddSeconds(ReadCount++);
    }
}
