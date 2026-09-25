using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using System.Globalization;

namespace WorkUp.Tests.Classes;

// T16 unit 1 read model (RF-10, RF-11, RF-12, RF-22, D-15, D-16, D-23): the read-only detail of one class session.
// One read returns the session with every annotation it holds — fixed and drop-in, cancelled included — plus the
// derived state of the fixed students and the occupancy, which is computed exclusively with `ClassCapacity`.
public class ClassDetailServiceTests
{
    private readonly FakeClassDetailRepository _repository = new();
    private readonly FakeClock _clock = new();
    private readonly ClassDetailService _service;

    private int _nextAppointmentId = 100;

    public ClassDetailServiceTests()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday
        _service = new ClassDetailService(_repository, _clock);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    private int AddSession(
        DateOnly date,
        TimeOnly time,
        int maxCapacity = 8,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        string? suspensionReason = null,
        int businessId = 1,
        int serviceId = 7,
        int workerId = 3,
        AppointmentKind kind = AppointmentKind.ClassSession,
        int durationMinutes = 60,
        IReadOnlyList<ClassDetailBookingRow>? bookings = null)
    {
        var appointmentId = _nextAppointmentId++;
        var startsAt = date.ToDateTime(time);
        _repository.Sessions.Add(new FakeClassDetailSession(
            businessId,
            new ClassDetailSessionRow(
                appointmentId, serviceId, workerId, startsAt, startsAt.AddMinutes(durationMinutes), maxCapacity, kind,
                status, suspensionReason, bookings ?? [])));
        return appointmentId;
    }

    /// <summary>One fixed annotation with its enrollment status and, optionally, one paid period.</summary>
    private static ClassDetailBookingRow FixedBooking(
        int bookingId,
        int clientId,
        string clientName,
        EnrollmentStatus enrollmentStatus,
        int enrollmentId = 100,
        DateOnly? coverageStart = null,
        DateOnly? expiresOn = null,
        BookingStatus status = BookingStatus.Pending,
        string clientEmail = "fija@workup.test")
    {
        IReadOnlyList<MembershipPeriod> periods = coverageStart is { } start && expiresOn is { } end
            ? [new MembershipPeriod { EnrollmentId = enrollmentId, CoverageStart = start, ExpiresOn = end }]
            : [];
        return new ClassDetailBookingRow(
            bookingId, clientId, clientName, clientEmail, BookingModality.Fixed, enrollmentId, status,
            enrollmentStatus, periods);
    }

    /// <summary>One drop-in annotation: no enrollment, no periods and no fixed state.</summary>
    private static ClassDetailBookingRow DropInBooking(
        int bookingId, int clientId, string clientName, BookingStatus status = BookingStatus.Pending,
        string clientEmail = "suelta@workup.test") =>
        new(bookingId, clientId, clientName, clientEmail, BookingModality.DropIn, null, status, null, []);

    [Fact]
    public async Task Detail_returns_the_session_with_its_fixed_and_drop_in_annotations()
    {
        var appointmentId = AddSession(
            D(2026, 3, 12), T(18), maxCapacity: 10,
            bookings:
            [
                FixedBooking(501, 11, "Fija Estudiante", EnrollmentStatus.PendingPayment, clientEmail: "fija@correo.test"),
                DropInBooking(502, 42, "Ana Perez", clientEmail: "ana@correo.test")
            ]);

        var result = await _service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        var session = result.Value.Session;
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.Equal(D(2026, 3, 12), session.Date);
        Assert.Equal(T(18), session.StartTime);
        Assert.Equal(7, session.ServiceId);
        Assert.Equal(3, session.WorkerId);
        Assert.Equal(10, session.MaxCapacity);
        Assert.Equal(AppointmentStatus.Scheduled, session.Status);
        Assert.Null(session.SuspensionReason);
        Assert.False(session.Suspended);

        Assert.Equal(new[] { 501, 502 }, result.Value.Students.Select(student => student.BookingId).ToArray());

        var fixedStudent = result.Value.Students[0];
        Assert.Equal(11, fixedStudent.ClientId);
        Assert.Equal("Fija Estudiante", fixedStudent.ClientName);
        Assert.Equal("fija@correo.test", fixedStudent.ClientEmail);
        Assert.Equal(BookingModality.Fixed, fixedStudent.Modality);
        Assert.Equal(100, fixedStudent.EnrollmentId);
        Assert.Equal(BookingStatus.Pending, fixedStudent.Status);
        Assert.True(fixedStudent.IsFixed);

        // The drop-in has no fixed state and no enrollment: it is not a fixed student in disguise.
        var dropIn = result.Value.Students[1];
        Assert.Equal(42, dropIn.ClientId);
        Assert.Equal("Ana Perez", dropIn.ClientName);
        Assert.Equal("ana@correo.test", dropIn.ClientEmail);
        Assert.Equal(BookingModality.DropIn, dropIn.Modality);
        Assert.Null(dropIn.EnrollmentId);
        Assert.Null(dropIn.FixedState);
        Assert.True(dropIn.IsDropIn);

        Assert.Equal(1, _repository.GetCallCount);
        Assert.Equal(1, _repository.GetBusinessId);
        Assert.Equal(appointmentId, _repository.GetAppointmentId);
    }

    [Fact]
    public async Task Detail_projects_the_persisted_end_of_the_session()
    {
        // The stored end is what the session was generated with, not what the current tariff would compute: the read
        // must carry it so the screen and the walk-in window never recompute a duration the session does not have.
        var appointmentId = AddSession(D(2026, 3, 12), T(18), durationMinutes: 90);

        var result = await _service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        Assert.Equal(D(2026, 3, 12).ToDateTime(T(18)).AddMinutes(90), result.Value.Session.EndsAt);
        Assert.Equal(T(19, 30), result.Value.Session.EndTime);
    }

    [Fact]
    public async Task Detail_keeps_a_cancelled_annotation_visible_with_its_status()
    {
        var appointmentId = AddSession(
            D(2026, 3, 12), T(18), maxCapacity: 4,
            bookings:
            [
                DropInBooking(502, 42, "Ana Perez"),
                DropInBooking(503, 43, "Bella Cancelada", BookingStatus.Cancelled)
            ]);

        var result = await _service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { 502, 503 }, result.Value.Students.Select(student => student.BookingId).ToArray());
        var cancelled = result.Value.Students[1];
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.IsCancelled);

        // The released place is kept visible but does not occupy: one non-cancelled annotation, one place taken.
        Assert.Equal(1, result.Value.PlacesTaken);
        Assert.Equal(3, result.Value.RemainingPlaces);
    }

    [Fact]
    public async Task Detail_derives_active_coverage_pending_payment_and_pending_renewal()
    {
        var appointmentId = AddSession(
            D(2026, 3, 12), T(18),
            bookings:
            [
                FixedBooking(501, 11, "Pago Pendiente", EnrollmentStatus.PendingPayment,
                    coverageStart: D(2026, 3, 1), expiresOn: D(2026, 4, 1)),
                FixedBooking(502, 12, "Alumna Vigente", EnrollmentStatus.Active,
                    enrollmentId: 101, coverageStart: D(2026, 3, 1), expiresOn: D(2026, 4, 1)),
                FixedBooking(503, 13, "Renovar", EnrollmentStatus.Active,
                    enrollmentId: 102, coverageStart: D(2026, 2, 1), expiresOn: D(2026, 3, 1)),
                FixedBooking(504, 14, "Sin Periodos", EnrollmentStatus.Active, enrollmentId: 103)
            ]);

        var result = await _service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        var states = result.Value.Students.ToDictionary(student => student.BookingId, student => student.FixedState);
        // Pending payment is decided by the enrollment status, whatever the periods say.
        Assert.Equal(ClassFixedStudentState.PendingPayment, states[501]);
        // The active enrollment whose period covers the class date is active coverage.
        Assert.Equal(ClassFixedStudentState.Active, states[502]);
        // The active enrollment whose period expired before the class date keeps its place pending renewal (D-23).
        Assert.Equal(ClassFixedStudentState.PendingRenewal, states[503]);
        // An active enrollment that never paid a period never covered the class date either.
        Assert.Equal(ClassFixedStudentState.PendingRenewal, states[504]);
    }

    [Fact]
    public async Task Detail_computes_the_occupancy_with_the_capacity_rule()
    {
        var appointmentId = AddSession(
            D(2026, 3, 12), T(18), maxCapacity: 4,
            bookings:
            [
                FixedBooking(501, 11, "Fija", EnrollmentStatus.Active,
                    coverageStart: D(2026, 3, 1), expiresOn: D(2026, 4, 1)),
                DropInBooking(502, 42, "Ana Perez"),
                DropInBooking(503, 43, "Confirmada", BookingStatus.Confirmed),
                DropInBooking(504, 44, "Cancelada", BookingStatus.Cancelled),
                DropInBooking(505, 45, "Ausente", BookingStatus.Absent)
            ]);

        var result = await _service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        // Five annotations, one cancelled: the capacity rule counts every non-cancelled annotation against the
        // capacity, the absent one included, so the places taken match that count exactly.
        var nonCancelled = result.Value.Students.Count(student => !student.IsCancelled);
        Assert.Equal(nonCancelled, result.Value.PlacesTaken);
        Assert.Equal(4, result.Value.PlacesTaken);
        Assert.Equal(0, result.Value.RemainingPlaces);
    }

    [Fact]
    public async Task Detail_reports_the_suspension_reason_of_a_suspended_session()
    {
        var appointmentId = AddSession(
            D(2026, 3, 12), T(18),
            status: AppointmentStatus.Suspended,
            suspensionReason: "Feriado nacional",
            bookings: [DropInBooking(502, 42, "Ana Perez")]);

        var result = await _service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        Assert.True(result.Value.Suspended);
        Assert.True(result.Value.Session.Suspended);
        Assert.Equal(AppointmentStatus.Suspended, result.Value.Session.Status);
        Assert.Equal("Feriado nacional", result.Value.Session.SuspensionReason);

        // Suspending a class cancels nobody: the annotations stay visible with their status (RF-10).
        Assert.Equal(BookingStatus.Pending, Assert.Single(result.Value.Students).Status);
    }

    [Fact]
    public async Task Detail_treats_a_missing_or_foreign_session_as_not_found()
    {
        var foreign = AddSession(D(2026, 3, 12), T(18), businessId: 2);

        var missing = await _service.GetAsync(1, 999);
        var other = await _service.GetAsync(1, foreign);

        Assert.False(missing.Succeeded);
        Assert.False(other.Succeeded);
        Assert.Equal("class_detail_not_found", missing.Error!.Code);
        Assert.Equal(missing.Error!.Code, other.Error!.Code);
        Assert.Equal(missing.Error!.Message, other.Error!.Message);

        // The read happened for both: a session of another business and a missing one are the same answer.
        Assert.Equal(2, _repository.GetCallCount);
        Assert.Equal(1, _repository.GetBusinessId);
    }

    [Fact]
    public async Task Detail_treats_an_individual_appointment_as_not_found()
    {
        // An individual turn is not a class: it must never be presented as one, whatever the repository returned.
        var individual = AddSession(D(2026, 3, 12), T(18), maxCapacity: 1, kind: AppointmentKind.Individual);

        var result = await _service.GetAsync(1, individual);

        Assert.False(result.Succeeded);
        Assert.Equal("class_detail_not_found", result.Error!.Code);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    public async Task Detail_rejects_invalid_ids_without_touching_storage(int businessId, int appointmentId)
    {
        var result = await _service.GetAsync(businessId, appointmentId);

        Assert.False(result.Succeeded);
        Assert.Equal("class_detail_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.GetCallCount);
    }

    [Fact]
    public async Task Detail_reads_the_clock_once_and_derives_the_state_from_the_class_date()
    {
        // The class is dated on the first read's day and its only period covers exactly that day. A second read would
        // already be the next day, so a state derived from "now" instead of the class date would look expired and the
        // double read could not pass unnoticed.
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new ClassDetailService(_repository, clock);
        var appointmentId = AddSession(
            D(2026, 3, 10), T(18),
            bookings:
            [
                FixedBooking(501, 11, "Alumna Vigente", EnrollmentStatus.Active,
                    coverageStart: D(2026, 3, 10), expiresOn: D(2026, 3, 11))
            ]);

        var result = await service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(ClassFixedStudentState.Active, Assert.Single(result.Value.Students).FixedState);
    }

    [Theory]
    // The clock and the class date fall on opposite sides of the period boundary, so a state read from "now" would
    // invert both cases: only a state derived from the class date passes. First case: the period covers the class date
    // but already expired today, so "now" would call it pending renewal. Second case, the mirror: the period covers
    // today but starts after the class date, so "now" would call it active coverage.
    [InlineData("2026-03-20", "2026-03-12", "2026-03-01", "2026-03-15", ClassFixedStudentState.Active)]
    [InlineData("2026-03-10", "2026-03-05", "2026-03-08", "2026-04-08", ClassFixedStudentState.PendingRenewal)]
    public async Task Detail_pins_the_membership_state_to_the_class_date_across_the_period_boundary(
        string today, string classDate, string coverageStart, string expiresOn, ClassFixedStudentState expected)
    {
        _clock.Now = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(T(9));
        var appointmentId = AddSession(
            DateOnly.ParseExact(classDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), T(18),
            bookings:
            [
                FixedBooking(501, 11, "Alumna Borde", EnrollmentStatus.Active,
                    coverageStart: DateOnly.ParseExact(coverageStart, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    expiresOn: DateOnly.ParseExact(expiresOn, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            ]);

        var result = await _service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, Assert.Single(result.Value.Students).FixedState);
    }

    [Theory]
    // T15 cancels the fixed annotations when it annuls or terminates an enrollment, and no schema constraint forbids a
    // surviving non-cancelled one, so the anomaly is possible. The read does not re-decide eligibility: it projects the
    // stored annotation and derives the state from the membership calendar on the class date. A terminated enrollment
    // that was covered on a past class date still reads as active coverage; without coverage, or for an annulled
    // enrollment that never paid, it reads pending renewal. This pins that decision instead of leaving it to the
    // fall-through.
    [InlineData(EnrollmentStatus.Annulled, false, ClassFixedStudentState.PendingRenewal)]
    [InlineData(EnrollmentStatus.Terminated, true, ClassFixedStudentState.Active)]
    [InlineData(EnrollmentStatus.Terminated, false, ClassFixedStudentState.PendingRenewal)]
    public async Task Detail_derives_the_state_of_a_surviving_annulled_or_terminated_enrollment_from_its_coverage(
        EnrollmentStatus enrollmentStatus, bool covered, ClassFixedStudentState expected)
    {
        var appointmentId = AddSession(
            D(2026, 3, 12), T(18),
            bookings:
            [
                FixedBooking(501, 11, "Inscripción Anómala", enrollmentStatus,
                    coverageStart: covered ? D(2026, 3, 1) : null,
                    expiresOn: covered ? D(2026, 4, 1) : null)
            ]);

        var result = await _service.GetAsync(1, appointmentId);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, Assert.Single(result.Value.Students).FixedState);
    }

    /// <summary>A clock that advances one day per read, so a second read changes the day and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }
        public DateTime Now => first.AddDays(ReadCount++);
    }
}
