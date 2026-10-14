using System.Globalization;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Enrollments;

// T17 business core (RF-21, D-19): guided change of one fixed class of an enrollment. The owner replaces the current
// weekly slot with another active grid class from an effective date, only while the new grid class has a free
// permanent seat considered from today — never from the effective date — and only while neither affected grid already
// has generated sessions from that date. The previous row closes the day before and the new one opens on the date;
// the payment, the frequency, the agreed price, the reference day and the earlier sessions are never touched.
public class FixedScheduleChangeServiceTests
{
    private const string InvalidRequest = "fixed_schedule_change_invalid_request";
    private const string InvalidEffectiveDate = "fixed_schedule_change_invalid_effective_date";
    private const string EnrollmentNotFound = "fixed_schedule_change_enrollment_not_found";
    private const string NotChangeable = "fixed_schedule_change_not_changeable";
    private const string AssignmentNotFound = "fixed_schedule_change_assignment_not_found";
    private const string EffectiveDateTooEarly = "fixed_schedule_change_effective_date_too_early";
    private const string DuplicateSlot = "fixed_schedule_change_duplicate_slot";
    private const string GridClassNotFound = "fixed_schedule_change_grid_class_not_found";
    private const string GridClassInactive = "fixed_schedule_change_grid_class_inactive";
    private const string NoSeat = "fixed_schedule_change_no_seat";
    private const string SessionsGenerated = "fixed_schedule_change_sessions_generated";
    private const string PreviousAssignmentChanged = "fixed_schedule_change_previous_assignment_changed";

    private readonly FakeEnrollmentRepository _enrollments = new();
    private readonly FakeFixedScheduleChangeRepository _schedules;
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();
    private readonly FixedScheduleChangeService _service;

    public FixedScheduleChangeServiceTests()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday
        _schedules = new FakeFixedScheduleChangeRepository(_enrollments);
        _service = new FixedScheduleChangeService(_enrollments, _schedules, _unitOfWork, _clock);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    private EnrollmentGridClassRow AddGridClass(
        int gridClassId,
        int maxCapacity = 4,
        bool isActive = true,
        int serviceId = 7,
        DayOfWeek dayOfWeek = DayOfWeek.Thursday,
        int businessId = 1,
        TimeOnly? startTime = null)
    {
        var row = new EnrollmentGridClassRow(
            gridClassId, serviceId, WorkerId: 3, dayOfWeek, startTime ?? T(18), maxCapacity, isActive);
        _enrollments.GridClasses.Add(new FakeEnrollmentGridClass(businessId, row));
        return row;
    }

    private Enrollment StoreEnrollment(
        int clientId = 100,
        EnrollmentStatus status = EnrollmentStatus.Active,
        int weeklyFrequency = 1,
        decimal agreedPrice = 5000m,
        int? referenceDay = 1,
        int businessId = 1)
    {
        var enrollment = new Enrollment
        {
            Id = _enrollments.NextEnrollmentId++,
            BusinessId = businessId,
            ClientId = clientId,
            WeeklyFrequency = weeklyFrequency,
            Status = status,
            AgreedPrice = agreedPrice,
            ReferenceDay = referenceDay,
            CreatedAt = _clock.Now,
            ActivatedAt = _clock.Now
        };
        _enrollments.Enrollments.Add(enrollment);
        return enrollment;
    }

    private EnrollmentFixedClass StoreFixedClass(
        Enrollment enrollment, int gridClassId, DateOnly validFrom, DateOnly? validTo = null)
    {
        var fixedClass = new EnrollmentFixedClass
        {
            Id = _schedules.NextFixedClassId++,
            EnrollmentId = enrollment.Id,
            GridClassId = gridClassId,
            ValidFrom = validFrom,
            ValidTo = validTo
        };
        _enrollments.FixedClasses.Add(fixedClass);
        return fixedClass;
    }

    /// <summary>The usual fixture: one active membership with an open fixed class on grid 10 and grid 20 available.</summary>
    private Enrollment StoreChangeableEnrollment()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20);
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));
        return enrollment;
    }

    private static ChangeFixedScheduleRequest Change(
        int enrollmentId, int currentGridClassId = 10, int newGridClassId = 20, DateOnly? effectiveFrom = null) =>
        new(enrollmentId, currentGridClassId, newGridClassId, effectiveFrom ?? D(2026, 4, 1));

    private void AssertNoWrite()
    {
        Assert.Equal(0, _schedules.ReplaceCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    // The happy path: the previous row ends the day before the effective date and the new one opens on it.

    [Fact]
    public async Task Change_closes_the_previous_row_and_opens_the_new_one_from_the_effective_date()
    {
        var enrollment = StoreChangeableEnrollment();

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(2, _enrollments.FixedClasses.Count);
        var previous = Assert.Single(_enrollments.FixedClasses, row => row.GridClassId == 10);
        Assert.Equal(D(2026, 1, 5), previous.ValidFrom);
        Assert.Equal(D(2026, 3, 31), previous.ValidTo);
        var opened = Assert.Single(_enrollments.FixedClasses, row => row.GridClassId == 20);
        Assert.Equal(D(2026, 4, 1), opened.ValidFrom);
        Assert.Null(opened.ValidTo);
        Assert.Equal(D(2026, 3, 31), result.Value.PreviousValidTo);
        Assert.Equal(opened.Id, result.Value.NewFixedClassId);
    }

    [Fact]
    public async Task Change_writes_once_inside_a_single_transaction()
    {
        var enrollment = StoreChangeableEnrollment();

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
        Assert.Equal(1, _schedules.ReplaceCallCount);
    }

    [Fact]
    public async Task Change_preserves_payment_frequency_price_reference_day_and_periods()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20);
        var enrollment = StoreEnrollment(weeklyFrequency: 1, agreedPrice: 5000m, referenceDay: 21);
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));
        _enrollments.Periods.Add(new MembershipPeriod
        {
            Id = 1,
            EnrollmentId = enrollment.Id,
            CoverageStart = D(2026, 3, 1),
            ExpiresOn = D(2026, 4, 1),
            PaymentId = 1,
            CreatedAt = _clock.Now
        });

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
        var stored = Assert.Single(_enrollments.Enrollments);
        Assert.Equal(EnrollmentStatus.Active, stored.Status);
        Assert.Equal(1, stored.WeeklyFrequency);
        Assert.Equal(5000m, stored.AgreedPrice);
        Assert.Equal(21, stored.ReferenceDay);
        Assert.Single(_enrollments.Periods);
        // A schedule change only moves the weekly slot: it never rewrites the enrollment row.
        Assert.Equal(0, _enrollments.UpdateEnrollmentCallCount);
    }

    [Fact]
    public async Task Change_never_migrates_or_cancels_existing_annotations()
    {
        var enrollment = StoreChangeableEnrollment();

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(0, _enrollments.ListSyncSessionsCallCount);
        Assert.Equal(0, _enrollments.InsertAnnotationsCallCount);
        Assert.Equal(0, _enrollments.CancelAnnotationsCallCount);
        Assert.Equal(0, _enrollments.CloseFixedClassesCallCount);
    }

    [Fact]
    public async Task Change_requests_the_affected_grid_classes_in_ascending_id_order()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20);
        var enrollment = StoreEnrollment();
        // The current assignment is on the higher id and the new one on the lower id, so the request has to be ordered
        // by id and not by the order the request happens to name them. This asserts the order of the call only:
        // neither it nor the statement's ORDER BY over an IN list proves the order SQL Server takes the locks, so the
        // suite claims no freedom from a two-writer deadlock.
        AddGridClass(30);
        _enrollments.FixedClasses.Add(new EnrollmentFixedClass
        {
            Id = _schedules.NextFixedClassId++,
            EnrollmentId = enrollment.Id,
            GridClassId = 20,
            ValidFrom = D(2026, 1, 5)
        });

        var result = await _service.ChangeAsync(
            1, Change(enrollment.Id, currentGridClassId: 20, newGridClassId: 30));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { 20, 30 }, _enrollments.LockedGridClassIds);
        Assert.Equal(1, _enrollments.LockEnrollmentCallCount);
    }

    // Tenant and enrollment-state guards.

    [Fact]
    public async Task Change_rejects_an_unknown_enrollment_without_writing()
    {
        AddGridClass(10);
        AddGridClass(20);

        var result = await _service.ChangeAsync(1, Change(999));

        Assert.False(result.Succeeded);
        Assert.Equal(EnrollmentNotFound, result.Error!.Code);
        AssertNoWrite();
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Change_hides_an_enrollment_of_another_business()
    {
        AddGridClass(10);
        AddGridClass(20);
        var foreign = StoreEnrollment(businessId: 2);
        StoreFixedClass(foreign, 10, D(2026, 1, 5));

        var result = await _service.ChangeAsync(1, Change(foreign.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(EnrollmentNotFound, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_rejects_an_annulled_enrollment()
    {
        var enrollment = StoreChangeableEnrollment();
        enrollment.Status = EnrollmentStatus.Annulled;

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(NotChangeable, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_rejects_a_terminated_enrollment()
    {
        var enrollment = StoreChangeableEnrollment();
        enrollment.Status = EnrollmentStatus.Terminated;

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(NotChangeable, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_accepts_a_pending_payment_enrollment()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20);
        var enrollment = StoreEnrollment(status: EnrollmentStatus.PendingPayment);
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(EnrollmentStatus.PendingPayment, Assert.Single(_enrollments.Enrollments).Status);
    }

    // The current assignment must exist and still be open.

    [Fact]
    public async Task Change_rejects_when_the_current_grid_class_has_no_valid_assignment()
    {
        AddGridClass(10);
        AddGridClass(20);
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 1, 5), D(2026, 3, 1));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(AssignmentNotFound, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_rejects_when_the_current_grid_class_belongs_to_another_business()
    {
        AddGridClass(20);
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(AssignmentNotFound, result.Error!.Code);
        AssertNoWrite();
    }

    // The effective date rules: never in the past, never on or before the start of the previous row.

    [Fact]
    public async Task Change_rejects_an_effective_date_before_the_start_of_the_current_assignment()
    {
        // A future-start assignment (D-21) still cannot be replaced before its own window opens: the effective date
        // may be today or later, but never earlier than the row it would close.
        var enrollment = StoreChangeableEnrollment();
        Assert.Single(_enrollments.FixedClasses).ValidFrom = D(2026, 5, 1);

        var result = await _service.ChangeAsync(
            1, Change(enrollment.Id, effectiveFrom: D(2026, 4, 1)));

        Assert.False(result.Succeeded);
        Assert.Equal(EffectiveDateTooEarly, result.Error!.Code);
        AssertNoWrite();
        Assert.Equal(D(2026, 5, 1), Assert.Single(_enrollments.FixedClasses).ValidFrom);
    }

    [Fact]
    public async Task Change_rejects_an_effective_date_equal_to_the_start_of_the_current_assignment()
    {
        // Closing the previous row would need a ValidTo before its own ValidFrom, which the range constraint forbids:
        // the change is refused with the first usable date instead of writing a malformed window or deleting the row.
        var enrollment = StoreChangeableEnrollment();
        var previous = Assert.Single(_enrollments.FixedClasses);
        previous.ValidFrom = D(2026, 4, 1);

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(EffectiveDateTooEarly, result.Error!.Code);
        // The message names the first day that works, which is the day after the assignment started, and it says
        // "desde el": the date in the message is already the first usable one, so "posterior al" would ask for one day
        // too late and make the owner retry a date the service accepts.
        Assert.Contains("desde el 02/04/2026", result.Error!.Message);
        Assert.DoesNotContain("posterior al", result.Error!.Message);
        AssertNoWrite();
        Assert.Single(_enrollments.FixedClasses);
        Assert.Null(previous.ValidTo);
    }

    [Fact]
    public async Task Change_rejects_an_effective_date_in_the_past_without_opening_a_transaction()
    {
        var enrollment = StoreChangeableEnrollment();

        var result = await _service.ChangeAsync(
            1, Change(enrollment.Id, effectiveFrom: D(2026, 3, 9)));

        Assert.False(result.Succeeded);
        Assert.Equal(InvalidEffectiveDate, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Change_rejects_a_default_effective_date_without_opening_a_transaction()
    {
        var enrollment = StoreChangeableEnrollment();

        var result = await _service.ChangeAsync(
            1, new ChangeFixedScheduleRequest(enrollment.Id, 10, 20, default));

        Assert.False(result.Succeeded);
        Assert.Equal(InvalidEffectiveDate, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Change_accepts_an_effective_date_equal_to_today()
    {
        var enrollment = StoreChangeableEnrollment();

        var result = await _service.ChangeAsync(
            1, Change(enrollment.Id, effectiveFrom: D(2026, 3, 10)));

        Assert.True(result.Succeeded);
        Assert.Equal(D(2026, 3, 9), result.Value.PreviousValidTo);
    }

    // The new grid class must belong to the business, be available and not already be part of the enrollment.

    [Fact]
    public async Task Change_rejects_a_missing_new_grid_class()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(GridClassNotFound, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_rejects_a_new_grid_class_of_another_business()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20, businessId: 2);
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(GridClassNotFound, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_rejects_an_archived_new_grid_class()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20, isActive: false);
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(GridClassInactive, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_rejects_a_new_grid_class_the_enrollment_already_holds()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20);
        var enrollment = StoreEnrollment(weeklyFrequency: 2);
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));
        StoreFixedClass(enrollment, 20, D(2026, 1, 5));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(DuplicateSlot, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_rejects_the_same_grid_class_as_current_and_new()
    {
        var enrollment = StoreChangeableEnrollment();

        var result = await _service.ChangeAsync(
            1, Change(enrollment.Id, currentGridClassId: 10, newGridClassId: 10));

        Assert.False(result.Succeeded);
        Assert.Equal(DuplicateSlot, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Change_rejects_invalid_request_values_without_opening_a_transaction()
    {
        var enrollment = StoreChangeableEnrollment();

        var wrongBusiness = await _service.ChangeAsync(0, Change(enrollment.Id));
        var wrongEnrollment = await _service.ChangeAsync(1, Change(0));
        var wrongGrid = await _service.ChangeAsync(
            1, Change(enrollment.Id, newGridClassId: 0));

        Assert.Equal(InvalidRequest, wrongBusiness.Error!.Code);
        Assert.Equal(InvalidRequest, wrongEnrollment.Error!.Code);
        Assert.Equal(InvalidRequest, wrongGrid.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // Capacity of the new grid class, judged from today and never from the future effective date (Regla 23, D-21).

    [Fact]
    public async Task Change_rejects_when_the_new_grid_class_has_no_free_seat()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20, maxCapacity: 1);
        var enrollment = StoreEnrollment(clientId: 100);
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));
        var holder = StoreEnrollment(clientId: 200);
        StoreFixedClass(holder, 20, D(2026, 2, 1));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(NoSeat, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_counts_a_future_start_assignment_as_an_occupied_seat()
    {
        // The place is retained from registration, so an assignment that starts after the effective date already
        // blocks the new grid class and the change is refused.
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20, maxCapacity: 1);
        var enrollment = StoreEnrollment(clientId: 100);
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));
        var holder = StoreEnrollment(clientId: 200);
        StoreFixedClass(holder, 20, D(2026, 6, 1));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(NoSeat, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_ignores_a_seat_released_by_an_annulled_enrollment()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20, maxCapacity: 1);
        var enrollment = StoreEnrollment(clientId: 100);
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));
        var holder = StoreEnrollment(clientId: 200, status: EnrollmentStatus.Annulled);
        StoreFixedClass(holder, 20, D(2026, 2, 1));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Change_ignores_an_assignment_whose_validity_already_ended()
    {
        AddGridClass(10, dayOfWeek: DayOfWeek.Tuesday);
        AddGridClass(20, maxCapacity: 1);
        var enrollment = StoreEnrollment(clientId: 100);
        StoreFixedClass(enrollment, 10, D(2026, 1, 5));
        var holder = StoreEnrollment(clientId: 200);
        StoreFixedClass(holder, 20, D(2026, 1, 1), D(2026, 2, 28));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
    }

    // Generated sessions: the change is refused from the first date that already has sessions in either affected grid.

    [Fact]
    public async Task Change_rejects_when_the_current_grid_already_has_a_session_from_the_effective_date()
    {
        var enrollment = StoreChangeableEnrollment();
        _schedules.Sessions.Add(new FakeGeneratedClassSession(1, 10, D(2026, 4, 7)));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(SessionsGenerated, result.Error!.Code);
        AssertNoWrite();
        Assert.Equal(1, _schedules.LastGeneratedSessionCallCount);
        Assert.Equal(new[] { 10, 20 }, _schedules.LastGeneratedGridClassIds);
        Assert.Equal(D(2026, 4, 1), _schedules.LastGeneratedFromDate);
    }

    [Fact]
    public async Task Change_rejects_when_the_new_grid_already_has_a_session_from_the_effective_date()
    {
        var enrollment = StoreChangeableEnrollment();
        _schedules.Sessions.Add(new FakeGeneratedClassSession(1, 20, D(2026, 4, 2)));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(SessionsGenerated, result.Error!.Code);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_asks_for_a_date_after_the_last_generated_session()
    {
        var enrollment = StoreChangeableEnrollment();
        _schedules.Sessions.Add(new FakeGeneratedClassSession(1, 10, D(2026, 4, 1)));
        _schedules.Sessions.Add(new FakeGeneratedClassSession(1, 20, D(2026, 4, 8)));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(SessionsGenerated, result.Error!.Code);
        // The date the message names is already last-generated-session + 1, so it reads "desde el": asking for a date
        // "posterior al" it would skip one valid day.
        Assert.Contains("desde el 09/04/2026", result.Error!.Message);
        Assert.DoesNotContain("posterior al", result.Error!.Message);
        Assert.Equal(nameof(ChangeFixedScheduleRequest.EffectiveFrom), result.Error!.Field);
        AssertNoWrite();
    }

    [Fact]
    public async Task Change_ignores_sessions_before_the_effective_date()
    {
        var enrollment = StoreChangeableEnrollment();
        _schedules.Sessions.Add(new FakeGeneratedClassSession(1, 10, D(2026, 3, 31)));
        _schedules.Sessions.Add(new FakeGeneratedClassSession(1, 20, D(2026, 3, 31)));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Change_ignores_sessions_of_unrelated_grid_classes()
    {
        var enrollment = StoreChangeableEnrollment();
        AddGridClass(30);
        _schedules.Sessions.Add(new FakeGeneratedClassSession(1, 30, D(2026, 4, 7)));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Change_ignores_sessions_of_another_business()
    {
        var enrollment = StoreChangeableEnrollment();
        _schedules.Sessions.Add(new FakeGeneratedClassSession(2, 10, D(2026, 4, 7)));

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.True(result.Succeeded);
    }

    // The refusal names the first usable date in its message; the day is written with the invariant Gregorian pattern,
    // so it reads dd/MM/yyyy whatever culture the process happens to run under and no culture with another date
    // separator can change what the owner reads.

    [Fact]
    public async Task Change_formats_the_first_usable_date_independently_of_the_process_culture()
    {
        var enrollment = StoreChangeableEnrollment();
        _schedules.Sessions.Add(new FakeGeneratedClassSession(1, 10, D(2026, 4, 1)));

        var message = await UnderCultureAsync(CultureInfo.GetCultureInfo("de-DE"), async () =>
        {
            var result = await _service.ChangeAsync(1, Change(enrollment.Id));
            Assert.Equal(SessionsGenerated, result.Error!.Code);
            return result.Error!.Message;
        });

        Assert.Contains("02/04/2026", message);
    }

    // Revalidation under the lock: a lost race writes nothing and never reports success.

    [Fact]
    public async Task Change_fails_closed_when_the_current_assignment_closed_before_the_write()
    {
        var enrollment = StoreChangeableEnrollment();
        _schedules.MarkCurrentAssignmentClosedBeforeWrite = true;

        var result = await _service.ChangeAsync(1, Change(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(PreviousAssignmentChanged, result.Error!.Code);
        Assert.Equal(1, _schedules.ReplaceCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        // The concurrent close is the only row state change: the service neither opened a second assignment nor
        // committed the change it could not complete.
        Assert.Single(_enrollments.FixedClasses);
        Assert.Equal(10, Assert.Single(_enrollments.FixedClasses).GridClassId);
    }

    /// <summary>
    /// Runs an asynchronous assertion under one culture and restores the process culture afterwards, so a message
    /// that formats a date cannot depend on the culture of the machine running the suite.
    /// </summary>
    private static async Task<T> UnderCultureAsync<T>(CultureInfo culture, Func<Task<T>> action)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return await action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
