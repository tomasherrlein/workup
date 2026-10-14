using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Enrollments;

// T15 business core (RF-05, RF-20, RF-29, D-16, D-20, D-21, D-23, D-24, Reglas 16-19 and 23): fixed-class enrollment
// and monthly membership. The pending enrollment retains its permanent seats from the moment it is registered, the
// first payment activates it and a later one renews it with `MembershipCalendar`, the annulment and the termination
// release places without deleting a row, the membership states are always derived and never persisted, and every
// activation or renewal backfills only the missing fixed annotations of generated sessions that have not started.
public class EnrollmentServiceTests
{
    private readonly FakeEnrollmentRepository _repository = new();
    private readonly FakeMembershipPriceRepository _prices = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();
    private readonly EnrollmentService _service;

    private int _nextAppointmentId = 100;

    public EnrollmentServiceTests()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday
        _service = new EnrollmentService(_repository, _prices, _unitOfWork, _clock);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    private static DateTime At(int year, int month, int day, int hour = 18) => new(year, month, day, hour, 0, 0);

    private void AddClient(int clientId = 100, string fullName = "Ana Perez", int businessId = 1) =>
        _repository.Clients.Add(new FakeEnrollmentClient(businessId, new EnrollmentClient(clientId, fullName)));

    private EnrollmentGridClassRow AddGridClass(
        int gridClassId = 10,
        int businessId = 1,
        int maxCapacity = 8,
        bool isActive = true,
        DayOfWeek dayOfWeek = DayOfWeek.Tuesday,
        TimeOnly? startTime = null)
    {
        var row = new EnrollmentGridClassRow(
            gridClassId, ServiceId: 7, WorkerId: 3, dayOfWeek, startTime ?? T(18), maxCapacity, isActive);
        _repository.GridClasses.Add(new FakeEnrollmentGridClass(businessId, row));
        return row;
    }

    private void AddPrice(int weeklyFrequency = 1, decimal price = 5000m, int businessId = 1) =>
        _prices.InsertAsync(new MembershipPrice
        {
            BusinessId = businessId, WeeklyFrequency = weeklyFrequency, Price = price
        }).GetAwaiter().GetResult();

    private Enrollment StoreEnrollment(
        int clientId = 100,
        EnrollmentStatus status = EnrollmentStatus.PendingPayment,
        int weeklyFrequency = 1,
        decimal agreedPrice = 5000m,
        int? referenceDay = null,
        DateTime? activatedAt = null,
        string? priceReason = null,
        int businessId = 1)
    {
        var enrollment = new Enrollment
        {
            Id = _repository.NextEnrollmentId++,
            BusinessId = businessId,
            ClientId = clientId,
            WeeklyFrequency = weeklyFrequency,
            Status = status,
            AgreedPrice = agreedPrice,
            PriceReason = priceReason,
            ReferenceDay = referenceDay,
            CreatedAt = _clock.Now,
            ActivatedAt = activatedAt
        };
        _repository.Enrollments.Add(enrollment);
        return enrollment;
    }

    private EnrollmentFixedClass StoreFixedClass(Enrollment enrollment, int gridClassId, DateOnly validFrom) =>
        StoreFixedClass(enrollment, gridClassId, validFrom, validTo: null);

    private EnrollmentFixedClass StoreFixedClass(
        Enrollment enrollment, int gridClassId, DateOnly validFrom, DateOnly? validTo)
    {
        var fixedClass = new EnrollmentFixedClass
        {
            EnrollmentId = enrollment.Id,
            GridClassId = gridClassId,
            ValidFrom = validFrom,
            ValidTo = validTo
        };
        _repository.FixedClasses.Add(fixedClass);
        return fixedClass;
    }

    private MembershipPeriod StorePeriod(Enrollment enrollment, DateOnly coverageStart, DateOnly expiresOn)
    {
        var period = new MembershipPeriod
        {
            EnrollmentId = enrollment.Id,
            CoverageStart = coverageStart,
            ExpiresOn = expiresOn,
            PaymentId = 0,
            CreatedAt = _clock.Now
        };
        _repository.Periods.Add(period);
        return period;
    }

    /// <summary>An active membership with one fixed class and one paid period, as the renewal flows read it.</summary>
    private Enrollment StoreActiveMembership(
        DateOnly coverageStart,
        DateOnly expiresOn,
        int referenceDay,
        int clientId = 100,
        int gridClassId = 10)
    {
        var enrollment = StoreEnrollment(
            clientId,
            EnrollmentStatus.Active,
            referenceDay: referenceDay,
            activatedAt: _clock.Now.AddDays(-30));
        StoreFixedClass(enrollment, gridClassId, coverageStart);
        StorePeriod(enrollment, coverageStart, expiresOn);
        return enrollment;
    }

    private FakeEnrollmentSyncSession AddSyncSession(
        int gridClassId,
        DateTime startsAt,
        int businessId = 1,
        int maxCapacity = 8,
        params (int EnrollmentId, int ClientId, BookingStatus Status)[] bookings)
    {
        var session = new FakeEnrollmentSyncSession(
            businessId, _nextAppointmentId++, gridClassId, startsAt, maxCapacity);
        foreach (var (enrollmentId, clientId, status) in bookings)
            session.Bookings.Add(new FakeEnrollmentSyncBooking(enrollmentId, clientId, status));

        _repository.SyncSessions.Add(session);
        return session;
    }

    private static CreateEnrollmentRequest CreateRequest(
        int clientId = 100,
        int weeklyFrequency = 1,
        decimal? agreedPrice = null,
        string? priceReason = null,
        int[]? gridClassIds = null,
        DateOnly? effectiveFrom = null) =>
        new(
            clientId,
            weeklyFrequency,
            agreedPrice,
            priceReason,
            gridClassIds ?? [10],
            effectiveFrom ?? new DateOnly(2026, 3, 10));

    private static ChargeMembershipRequest ChargeRequest(
        int enrollmentId, decimal? amount = null, string? reason = null) =>
        new(enrollmentId, amount, reason);

    private void AssertNoWrite()
    {
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    // RF-05 step 1, Regla 23 and D-21: the pending enrollment is registered for a student of the business, its chosen
    // grid classes retain a free permanent seat each, and it starts in `Pendiente de pago`.

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    public async Task Create_rejects_an_invalid_request_without_reading_storage(int businessId, int clientId)
    {
        var result = await _service.CreatePendingAsync(businessId, CreateRequest(clientId: clientId));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.FindClientCallCount);
        Assert.Equal(0, _repository.LockGridClassesCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-1)]
    public async Task Create_rejects_a_weekly_frequency_outside_one_to_seven(int weeklyFrequency)
    {
        AddClient();

        var result = await _service.CreatePendingAsync(1, CreateRequest(weeklyFrequency: weeklyFrequency));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_invalid_frequency", result.Error!.Code);
        Assert.Equal(0, _repository.FindClientCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Create_rejects_more_grid_classes_than_the_weekly_frequency()
    {
        AddClient();
        AddPrice(weeklyFrequency: 1);

        var result = await _service.CreatePendingAsync(
            1, CreateRequest(weeklyFrequency: 1, gridClassIds: [10, 11]));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_invalid_grid_classes", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Create_rejects_an_enrollment_without_grid_classes()
    {
        AddClient();

        var result = await _service.CreatePendingAsync(1, CreateRequest(gridClassIds: []));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_invalid_grid_classes", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Create_rejects_a_default_effective_date()
    {
        var result = await _service.CreatePendingAsync(
            1, new CreateEnrollmentRequest(100, 1, null, null, [10], default));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_invalid_effective_date", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Create_rejects_a_client_of_another_business()
    {
        AddClient(clientId: 100, businessId: 2);
        AddPrice();
        AddGridClass();

        var result = await _service.CreatePendingAsync(1, CreateRequest(clientId: 100));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_client_not_found", result.Error!.Code);
        Assert.Equal(1, _repository.FindClientCallCount);
        Assert.Equal(0, _repository.LockGridClassesCallCount);
        Assert.Empty(_repository.Enrollments);
        AssertNoWrite();
    }

    [Fact]
    public async Task Create_rejects_a_frequency_without_a_configured_price()
    {
        AddClient();
        AddGridClass();

        var result = await _service.CreatePendingAsync(1, CreateRequest(weeklyFrequency: 1));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_price_not_configured", result.Error!.Code);
        Assert.Equal(0, _repository.LockGridClassesCallCount);
        AssertNoWrite();
    }

    [Fact]
    public async Task Create_rejects_a_grid_class_of_another_business()
    {
        AddClient();
        AddPrice();
        AddGridClass(gridClassId: 10, businessId: 2);

        var result = await _service.CreatePendingAsync(1, CreateRequest(gridClassIds: [10]));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_grid_class_not_found", result.Error!.Code);
        Assert.Equal(1, _repository.LockGridClassesCallCount);
        Assert.Equal(0, _repository.TryInsertCallCount);
        AssertNoWrite();
    }

    [Fact]
    public async Task Create_rejects_an_archived_grid_class()
    {
        AddClient();
        AddPrice();
        AddGridClass(gridClassId: 10, isActive: false);

        var result = await _service.CreatePendingAsync(1, CreateRequest(gridClassIds: [10]));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_grid_class_inactive", result.Error!.Code);
        Assert.Equal(0, _repository.TryInsertCallCount);
        AssertNoWrite();
    }

    // D-20: the agreed price defaults to the tariff of that weekly frequency; any other amount needs a reason.

    [Fact]
    public async Task Create_defaults_the_agreed_price_to_the_tariff_of_the_frequency()
    {
        AddClient();
        AddPrice(weeklyFrequency: 1, price: 5000m);
        AddGridClass();

        var result = await _service.CreatePendingAsync(1, CreateRequest());

        Assert.True(result.Succeeded);
        Assert.Equal(5000m, result.Value.AgreedPrice);
        var stored = Assert.Single(_repository.Enrollments);
        Assert.Equal(5000m, stored.AgreedPrice);
        Assert.Null(stored.PriceReason);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Create_requires_a_reason_when_the_agreed_price_differs_from_the_tariff()
    {
        AddClient();
        AddPrice(price: 5000m);
        AddGridClass();

        var result = await _service.CreatePendingAsync(1, CreateRequest(agreedPrice: 4000m));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_price_reason_required", result.Error!.Code);
        Assert.Equal(0, _repository.TryInsertCallCount);
        AssertNoWrite();
    }

    [Fact]
    public async Task Create_accepts_a_different_agreed_price_with_a_reason()
    {
        AddClient();
        AddPrice(price: 5000m);
        AddGridClass();

        var result = await _service.CreatePendingAsync(
            1, CreateRequest(agreedPrice: 4000m, priceReason: "  Descuento por hermanas  "));

        Assert.True(result.Succeeded);
        var stored = Assert.Single(_repository.Enrollments);
        Assert.Equal(4000m, stored.AgreedPrice);
        Assert.Equal("Descuento por hermanas", stored.PriceReason);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task Create_rejects_a_non_positive_agreed_price(decimal agreedPrice)
    {
        AddClient();
        AddPrice(price: 5000m);
        AddGridClass();

        var result = await _service.CreatePendingAsync(1, CreateRequest(agreedPrice: agreedPrice));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_invalid_price", result.Error!.Code);
        Assert.Equal(0, _repository.LockGridClassesCallCount);
        AssertNoWrite();
    }

    // Regla 23 and D-21: the chosen places stay held by the pending enrollment, from the moment it is registered and
    // without expiring on the first class by themselves.

    [Fact]
    public async Task A_pending_enrollment_retains_its_seat_and_the_last_one_is_not_offered_twice()
    {
        AddClient(clientId: 100);
        AddClient(clientId: 200, fullName: "Beto Diaz");
        AddPrice();
        AddGridClass(maxCapacity: 1);

        var first = await _service.CreatePendingAsync(1, CreateRequest(clientId: 100));
        var second = await _service.CreatePendingAsync(1, CreateRequest(clientId: 200));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal("enrollment_no_seat", second.Error!.Code);
        Assert.Single(_repository.Enrollments);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task A_pending_enrollment_for_a_future_date_retains_the_seat_from_registration()
    {
        // The reported regression: with capacity 1, an enrollment registered today for a future effective date must
        // already retain the place, so a second enrollment with an earlier start cannot slip into the grid class. The
        // place is held from registration (Regla 23, D-21), never from the start of its validity window.
        AddClient(clientId: 100);
        AddClient(clientId: 200, fullName: "Beto Diaz");
        AddPrice();
        AddGridClass(maxCapacity: 1);

        var future = await _service.CreatePendingAsync(
            1, CreateRequest(clientId: 100, effectiveFrom: D(2026, 4, 1)));
        var earlier = await _service.CreatePendingAsync(
            1, CreateRequest(clientId: 200, effectiveFrom: D(2026, 3, 15)));

        Assert.True(future.Succeeded);
        Assert.False(earlier.Succeeded);
        Assert.Equal("enrollment_no_seat", earlier.Error!.Code);
        Assert.Single(_repository.Enrollments);
        // Only the first insert ran: the refused enrollment never reached the guarded insert.
        Assert.Equal(1, _repository.TryInsertCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Create_rejects_a_grid_class_whose_permanent_seats_are_taken()
    {
        AddClient(clientId: 100);
        AddClient(clientId: 200, fullName: "Beto Diaz");
        AddPrice();
        AddGridClass(maxCapacity: 1);
        var holder = StoreEnrollment(clientId: 200, status: EnrollmentStatus.Active, referenceDay: 1);
        StoreFixedClass(holder, 10, D(2026, 3, 1));

        var result = await _service.CreatePendingAsync(1, CreateRequest(clientId: 100));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_no_seat", result.Error!.Code);
        Assert.Equal(0, _repository.TryInsertCallCount);
        Assert.Single(_repository.Enrollments);
        AssertNoWrite();
    }

    [Fact]
    public async Task An_annulled_enrollment_releases_its_seat_without_removing_any_row()
    {
        AddClient(clientId: 100);
        AddClient(clientId: 200, fullName: "Beto Diaz");
        AddPrice();
        AddGridClass(maxCapacity: 1);
        var holder = StoreEnrollment(clientId: 100);
        StoreFixedClass(holder, 10, D(2026, 3, 1));

        var annulled = await _service.AnnulPendingAsync(1, new AnnulEnrollmentRequest(holder.Id));
        var created = await _service.CreatePendingAsync(1, CreateRequest(clientId: 200));

        Assert.True(annulled.Succeeded);
        Assert.True(created.Succeeded);
        Assert.Equal(2, _repository.Enrollments.Count);
        Assert.Equal(2, _repository.FixedClasses.Count);
        Assert.Equal(2, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task A_terminated_enrollment_releases_its_seat_without_removing_any_row()
    {
        AddClient(clientId: 100);
        AddClient(clientId: 200, fullName: "Beto Diaz");
        AddPrice();
        AddGridClass(maxCapacity: 1);
        var holder = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Active, referenceDay: 1);
        StoreFixedClass(holder, 10, D(2026, 3, 1));

        var terminated = await _service.TerminateAsync(
            1, new TerminateEnrollmentRequest(holder.Id, D(2026, 3, 10)));
        var created = await _service.CreatePendingAsync(1, CreateRequest(clientId: 200));

        Assert.True(terminated.Succeeded);
        Assert.True(created.Succeeded);
        Assert.Equal(2, _repository.Enrollments.Count);
        Assert.Equal(2, _repository.FixedClasses.Count);
        Assert.Equal(2, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Create_rejects_the_lost_race_for_the_last_seat()
    {
        AddClient();
        AddPrice();
        AddGridClass(maxCapacity: 1);
        _repository.TryInsertReturnsNoSeatLeft = true;

        var result = await _service.CreatePendingAsync(1, CreateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_no_seat", result.Error!.Code);
        Assert.Equal(1, _repository.TryInsertCallCount);
        Assert.Empty(_repository.Enrollments);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Create_registers_the_pending_enrollment_with_its_fixed_classes_and_no_period()
    {
        AddClient();
        AddPrice(weeklyFrequency: 2, price: 6000m);
        AddGridClass(gridClassId: 10);
        AddGridClass(gridClassId: 11);

        var result = await _service.CreatePendingAsync(
            1,
            CreateRequest(
                weeklyFrequency: 2,
                gridClassIds: [10, 11],
                effectiveFrom: D(2026, 3, 12)));

        Assert.True(result.Succeeded);
        var stored = Assert.Single(_repository.Enrollments);
        Assert.Equal(EnrollmentStatus.PendingPayment, stored.Status);
        Assert.Equal(100, stored.ClientId);
        Assert.Equal(2, stored.WeeklyFrequency);
        Assert.Equal(6000m, stored.AgreedPrice);
        Assert.Equal(_clock.Now, stored.CreatedAt);
        Assert.Null(stored.ActivatedAt);
        Assert.Null(stored.ReferenceDay);

        Assert.Equal(2, _repository.FixedClasses.Count);
        Assert.All(_repository.FixedClasses, fixedClass => Assert.Equal(D(2026, 3, 12), fixedClass.ValidFrom));
        Assert.All(_repository.FixedClasses, fixedClass => Assert.Null(fixedClass.ValidTo));
        Assert.Empty(_repository.Payments);
        Assert.Empty(_repository.Periods);

        Assert.Equal(stored.Id, result.Value.EnrollmentId);
        Assert.Equal(new[] { 10, 11 }, result.Value.GridClassIds.OrderBy(id => id).ToArray());
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Create_reads_the_clock_once_for_the_registered_instant()
    {
        // The clock advances one second per read, so the persisted instant can only be the first one.
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new EnrollmentService(_repository, _prices, _unitOfWork, clock);
        AddClient();
        AddPrice();
        AddGridClass();

        var result = await service.CreatePendingAsync(1, CreateRequest());

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0), Assert.Single(_repository.Enrollments).CreatedAt);
    }

    // RF-05 step 4-5, RF-29 steps 3-4 and Regla 16: the first charge activates the membership with the first period of
    // `MembershipCalendar`; a later one renews it, early from the previous expiry and late from the payment day.

    [Fact]
    public async Task Charge_activates_the_membership_and_writes_one_payment_and_one_period()
    {
        AddClient();
        AddPrice(weeklyFrequency: 1, price: 5000m);
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 10));

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.True(result.Value.Activated);

        var stored = Assert.Single(_repository.Enrollments);
        Assert.Equal(EnrollmentStatus.Active, stored.Status);
        Assert.Equal(_clock.Now, stored.ActivatedAt);
        Assert.Equal(10, stored.ReferenceDay);

        var payment = Assert.Single(_repository.Payments);
        Assert.Equal(PaymentConcept.Membership, payment.Concept);
        Assert.Equal(enrollment.Id, payment.EnrollmentId);
        Assert.Equal(100, payment.ClientId);
        Assert.Equal(5000m, payment.Amount);
        Assert.Equal(5000m, payment.ListPrice);
        Assert.Equal(_clock.Now, payment.PaidAt);
        Assert.Equal(_clock.Now, payment.RecordedAt);

        var period = Assert.Single(_repository.Periods);
        Assert.Equal(enrollment.Id, period.EnrollmentId);
        Assert.Equal(D(2026, 3, 10), period.CoverageStart);
        Assert.Equal(D(2026, 4, 10), period.ExpiresOn);
        Assert.Equal(payment.Id, period.PaymentId);
        Assert.Equal(_clock.Now, period.CreatedAt);

        Assert.Equal(period.Id, result.Value.MembershipPeriodId);
        Assert.Equal(payment.Id, result.Value.PaymentId);
        Assert.Equal(D(2026, 3, 10), result.Value.CoverageStart);
        Assert.Equal(D(2026, 4, 10), result.Value.ExpiresOn);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Charge_activates_a_membership_with_no_generated_sessions_without_touching_anything_else()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 10));

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value.Sync.CreatedAnnotations);
        Assert.Equal(0, result.Value.Sync.OmittedAnnotations);
        Assert.Equal(0, result.Value.Sync.RefusedWithoutSeat);
        Assert.Empty(result.Value.Sync.SessionIds);
        Assert.Equal(1, _repository.ListSyncSessionsCallCount);
        Assert.Equal(0, _repository.InsertAnnotationsCallCount);
    }

    [Fact]
    public async Task Early_renewal_starts_at_the_existing_expiry_and_keeps_the_reference_day()
    {
        AddClient();
        var enrollment = StoreActiveMembership(D(2026, 3, 15), D(2026, 4, 15), referenceDay: 15);

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.False(result.Value.Activated);
        Assert.Equal(D(2026, 4, 15), result.Value.CoverageStart);
        Assert.Equal(D(2026, 5, 15), result.Value.ExpiresOn);
        Assert.Equal(15, result.Value.ReferenceDay);

        var stored = Assert.Single(_repository.Enrollments);
        Assert.Equal(_clock.Now.AddDays(-30), stored.ActivatedAt);
        Assert.Equal(1, _repository.InsertPaymentCallCount);
        Assert.Equal(1, _repository.InsertPeriodCallCount);
    }

    [Fact]
    public async Task Renewal_paid_on_the_expiry_day_continues_without_interruption()
    {
        AddClient();
        var enrollment = StoreActiveMembership(D(2026, 2, 10), D(2026, 3, 10), referenceDay: 10);

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.Equal(D(2026, 3, 10), result.Value.CoverageStart);
        Assert.Equal(D(2026, 4, 10), result.Value.ExpiresOn);
    }

    [Fact]
    public async Task Late_renewal_starts_on_the_payment_day_and_resets_the_reference_day()
    {
        AddClient();
        var enrollment = StoreActiveMembership(D(2026, 2, 5), D(2026, 3, 5), referenceDay: 5);

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.Equal(D(2026, 3, 10), result.Value.CoverageStart);
        Assert.Equal(D(2026, 4, 10), result.Value.ExpiresOn);
        Assert.Equal(10, result.Value.ReferenceDay);
        Assert.Equal(10, Assert.Single(_repository.Enrollments).ReferenceDay);
    }

    [Fact]
    public async Task Charge_treats_a_foreign_or_missing_enrollment_the_same_way()
    {
        var foreign = StoreEnrollment(businessId: 2);

        var foreignResult = await _service.ChargeAsync(1, ChargeRequest(foreign.Id));
        var missingResult = await _service.ChargeAsync(1, ChargeRequest(9999));

        Assert.False(foreignResult.Succeeded);
        Assert.False(missingResult.Succeeded);
        Assert.Equal("membership_not_found", foreignResult.Error!.Code);
        Assert.Equal(foreignResult.Error!.Code, missingResult.Error!.Code);
        Assert.Equal(foreignResult.Error!.Message, missingResult.Error!.Message);
        Assert.Empty(_repository.Payments);
        Assert.Empty(_repository.Periods);
        Assert.Equal(2, _unitOfWork.RolledBackCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(EnrollmentStatus.Annulled)]
    [InlineData(EnrollmentStatus.Terminated)]
    public async Task Charge_rejects_an_enrollment_that_no_longer_holds_places(EnrollmentStatus status)
    {
        AddClient();
        var enrollment = StoreEnrollment(status: status);
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal("membership_not_chargeable", result.Error!.Code);
        Assert.Empty(_repository.Payments);
        Assert.Empty(_repository.Periods);
        AssertNoWrite();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Charge_rejects_a_non_positive_amount(decimal amount)
    {
        AddClient();
        var enrollment = StoreEnrollment();

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id, amount));

        Assert.False(result.Succeeded);
        Assert.Equal("membership_invalid_amount", result.Error!.Code);
        Assert.Empty(_repository.Payments);
        AssertNoWrite();
    }

    [Fact]
    public async Task Charge_requires_a_reason_when_the_amount_differs_from_the_agreed_price()
    {
        AddClient();
        var enrollment = StoreEnrollment(agreedPrice: 5000m);

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id, amount: 4000m));

        Assert.False(result.Succeeded);
        Assert.Equal("membership_reason_required", result.Error!.Code);
        Assert.Empty(_repository.Payments);
        AssertNoWrite();
    }

    [Fact]
    public async Task Charge_records_a_different_amount_with_its_reason_and_the_tariff()
    {
        AddClient();
        AddPrice(weeklyFrequency: 1, price: 6000m);
        var enrollment = StoreEnrollment(agreedPrice: 4000m, priceReason: "Acuerdo previo");

        var result = await _service.ChargeAsync(
            1, ChargeRequest(enrollment.Id, amount: 4500m, reason: "  Pago parcial acordado  "));

        Assert.True(result.Succeeded);
        var payment = Assert.Single(_repository.Payments);
        Assert.Equal(4500m, payment.Amount);
        Assert.Equal(6000m, payment.ListPrice);
        Assert.Equal("Pago parcial acordado", payment.PriceReason);
    }

    [Fact]
    public async Task Charge_reads_the_clock_once_for_the_period_and_the_timestamps()
    {
        // The clock advances one second per read: a second read would move the payment instant and the coverage start
        // of a payment made at the very end of the day.
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new EnrollmentService(_repository, _prices, _unitOfWork, clock);
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 10));

        var result = await service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0), Assert.Single(_repository.Payments).PaidAt);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0), Assert.Single(_repository.Periods).CreatedAt);
    }

    // RF-05 step 6 and RF-06 step 3: activating or renewing backfills only the missing fixed annotations of already
    // generated sessions that have not started. An existing annotation is never modified, a cancelled one is never
    // resurrected and a full session is refused instead of overbooked.

    [Fact]
    public async Task Charge_skips_the_sync_read_for_an_enrollment_without_fixed_classes()
    {
        AddClient();
        var enrollment = StoreEnrollment();

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(0, _repository.ListSyncSessionsCallCount);
        Assert.Equal(0, result.Value.Sync.CreatedAnnotations);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Charge_syncs_only_the_missing_future_annotations_and_reports_the_rest()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 10));

        var annotated = AddSyncSession(
            10, At(2026, 3, 11), bookings: [(enrollment.Id, 100, BookingStatus.Pending)]);
        var cancelled = AddSyncSession(
            10, At(2026, 3, 12), bookings: [(enrollment.Id, 100, BookingStatus.Cancelled)]);
        var full = AddSyncSession(10, At(2026, 3, 13), maxCapacity: 1, bookings: [(999, 999, BookingStatus.Pending)]);
        var missing = AddSyncSession(10, At(2026, 3, 14));

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        var sync = result.Value.Sync;
        Assert.Equal(1, sync.CreatedAnnotations);
        Assert.Equal(2, sync.OmittedAnnotations);
        Assert.Equal(1, sync.RefusedWithoutSeat);
        Assert.Equal(
            new[] { annotated.AppointmentId, cancelled.AppointmentId, full.AppointmentId, missing.AppointmentId },
            sync.SessionIds);

        var written = Assert.Single(_repository.InsertedAnnotations);
        Assert.Equal(missing.AppointmentId, written.AppointmentId);
        Assert.Equal(enrollment.Id, written.EnrollmentId);
        Assert.Equal(100, written.ClientId);
        Assert.Equal(_clock.Now, written.CreatedAt);

        var booking = Assert.Single(missing.Bookings);
        Assert.Equal(enrollment.Id, booking.EnrollmentId);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Equal(BookingStatus.Pending, Assert.Single(annotated.Bookings).Status);
        Assert.Equal(BookingStatus.Cancelled, Assert.Single(cancelled.Bookings).Status);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Charge_never_annotates_a_session_that_already_started()
    {
        // The repository hands the service sessions the query should not have returned; the rule is judged by the
        // service too, so neither a session that ran yesterday nor one starting exactly now is annotated.
        _repository.IncludeStartedSessions = true;
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));
        AddSyncSession(10, At(2026, 3, 9));
        AddSyncSession(10, At(2026, 3, 10, hour: 9));

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.Equal(0, result.Value.Sync.CreatedAnnotations);
        Assert.Empty(result.Value.Sync.SessionIds);
        Assert.Empty(_repository.InsertedAnnotations);
        Assert.Equal(0, _repository.InsertAnnotationsCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Charge_does_not_annotate_a_session_outside_the_validity_of_the_fixed_class()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 15));
        AddSyncSession(10, At(2026, 3, 14));
        var inside = AddSyncSession(10, At(2026, 3, 16));

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.Equal(1, result.Value.Sync.CreatedAnnotations);
        Assert.Equal(new[] { inside.AppointmentId }, result.Value.Sync.SessionIds);
    }

    [Fact]
    public async Task Charge_ignores_the_sessions_of_other_grid_classes()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));
        var other = AddSyncSession(11, At(2026, 3, 11));
        var mine = AddSyncSession(10, At(2026, 3, 11));

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.Equal(1, result.Value.Sync.CreatedAnnotations);
        Assert.Equal(new[] { mine.AppointmentId }, result.Value.Sync.SessionIds);
        Assert.Empty(other.Bookings);
    }

    [Fact]
    public async Task Charge_reports_the_lost_race_of_a_fixed_annotation_as_a_refusal()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));
        AddSyncSession(10, At(2026, 3, 11));
        _repository.FixedAnnotationResultOverride = FixedAnnotationInsertResultType.NoSeatLeft;

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value.Sync.CreatedAnnotations);
        Assert.Equal(1, result.Value.Sync.RefusedWithoutSeat);
        Assert.Empty(_repository.InsertedAnnotations);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Charge_reports_an_annotation_written_concurrently_as_omitted()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));
        AddSyncSession(10, At(2026, 3, 11));
        _repository.FixedAnnotationResultOverride = FixedAnnotationInsertResultType.AlreadyPresent;

        var result = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value.Sync.CreatedAnnotations);
        Assert.Equal(1, result.Value.Sync.OmittedAnnotations);
        Assert.Empty(_repository.InsertedAnnotations);
    }

    [Fact]
    public async Task Charge_fails_loud_on_an_unknown_fixed_annotation_result()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));
        AddSyncSession(10, At(2026, 3, 11));
        _repository.FixedAnnotationResultOverride = (FixedAnnotationInsertResultType)0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ChargeAsync(1, ChargeRequest(enrollment.Id)));

        AssertNoWrite();
    }

    // RF-20 and D-21: only the owner annuls, only a pending enrollment can be annulled, and the annulment releases the
    // held places without deleting any row.

    [Fact]
    public async Task Annul_marks_the_pending_enrollment_and_releases_only_its_places()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        var fixedClass = StoreFixedClass(enrollment, 10, D(2026, 3, 1));
        var yesterday = AddSyncSession(10, At(2026, 3, 9), bookings: [(enrollment.Id, 100, BookingStatus.Pending)]);
        var today = AddSyncSession(10, At(2026, 3, 10), bookings: [(enrollment.Id, 100, BookingStatus.Pending)]);
        var tomorrow = AddSyncSession(10, At(2026, 3, 11), bookings: [(enrollment.Id, 100, BookingStatus.Pending)]);

        var result = await _service.AnnulPendingAsync(1, new AnnulEnrollmentRequest(enrollment.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value.CancelledAnnotations);

        var stored = Assert.Single(_repository.Enrollments);
        Assert.Equal(EnrollmentStatus.Annulled, stored.Status);
        Assert.Equal(_clock.Now, stored.AnnulledAt);
        Assert.Null(stored.TerminatedAt);

        Assert.Equal(D(2026, 3, 9), fixedClass.ValidTo);
        Assert.Single(_repository.FixedClasses);
        Assert.Equal(BookingStatus.Pending, Assert.Single(yesterday.Bookings).Status);
        Assert.Equal(BookingStatus.Cancelled, Assert.Single(today.Bookings).Status);
        Assert.Equal(BookingStatus.Cancelled, Assert.Single(tomorrow.Bookings).Status);

        Assert.Equal(1, _repository.UpdateEnrollmentCallCount);
        Assert.Equal(1, _repository.CloseFixedClassesCallCount);
        Assert.Equal(1, _repository.CancelAnnotationsCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Annul_rejects_an_enrollment_that_is_not_pending()
    {
        AddClient();
        var enrollment = StoreActiveMembership(D(2026, 3, 1), D(2026, 4, 1), referenceDay: 1);

        var result = await _service.AnnulPendingAsync(1, new AnnulEnrollmentRequest(enrollment.Id));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_not_pending", result.Error!.Code);
        Assert.Equal(EnrollmentStatus.Active, Assert.Single(_repository.Enrollments).Status);
        Assert.Equal(0, _repository.UpdateEnrollmentCallCount);
        Assert.Equal(0, _repository.CloseFixedClassesCallCount);
        Assert.Equal(0, _repository.CancelAnnotationsCallCount);
        AssertNoWrite();
    }

    [Fact]
    public async Task Annul_treats_a_foreign_or_missing_enrollment_the_same_way()
    {
        var foreign = StoreEnrollment(businessId: 2);

        var foreignResult = await _service.AnnulPendingAsync(1, new AnnulEnrollmentRequest(foreign.Id));
        var missingResult = await _service.AnnulPendingAsync(1, new AnnulEnrollmentRequest(9999));

        Assert.False(foreignResult.Succeeded);
        Assert.False(missingResult.Succeeded);
        Assert.Equal("enrollment_not_found", foreignResult.Error!.Code);
        Assert.Equal(foreignResult.Error!.Code, missingResult.Error!.Code);
        Assert.Equal(foreignResult.Error!.Message, missingResult.Error!.Message);
        Assert.Equal(2, _unitOfWork.RolledBackCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 0)]
    public async Task Annul_rejects_an_invalid_request_without_opening_a_transaction(int businessId, int enrollmentId)
    {
        var result = await _service.AnnulPendingAsync(businessId, new AnnulEnrollmentRequest(enrollmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_not_found", result.Error!.Code);
        Assert.Equal(0, _repository.LockEnrollmentCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // RF-29 step 6 and D-19: ending a membership changes the status, records the effective date and cancels the fixed
    // annotations from that date on, never the earlier ones.

    [Fact]
    public async Task Terminate_ends_the_membership_from_the_effective_date_and_keeps_the_earlier_annotations()
    {
        AddClient();
        var enrollment = StoreActiveMembership(D(2026, 2, 1), D(2026, 3, 1), referenceDay: 1);
        var fixedClass = Assert.Single(_repository.FixedClasses);
        var before = AddSyncSession(10, At(2026, 3, 14), bookings: [(enrollment.Id, 100, BookingStatus.Pending)]);
        var onTheDate = AddSyncSession(10, At(2026, 3, 15), bookings: [(enrollment.Id, 100, BookingStatus.Pending)]);
        var after = AddSyncSession(10, At(2026, 3, 16), bookings: [(enrollment.Id, 100, BookingStatus.Pending)]);

        var result = await _service.TerminateAsync(
            1, new TerminateEnrollmentRequest(enrollment.Id, D(2026, 3, 15)));

        Assert.True(result.Succeeded);
        Assert.Equal(D(2026, 3, 15), result.Value.EffectiveFrom);
        Assert.Equal(2, result.Value.CancelledAnnotations);

        var stored = Assert.Single(_repository.Enrollments);
        Assert.Equal(EnrollmentStatus.Terminated, stored.Status);
        Assert.Equal(_clock.Now, stored.TerminatedAt);
        Assert.Null(stored.AnnulledAt);

        Assert.Equal(D(2026, 3, 14), fixedClass.ValidTo);
        Assert.Equal(BookingStatus.Pending, Assert.Single(before.Bookings).Status);
        Assert.Equal(BookingStatus.Cancelled, Assert.Single(onTheDate.Bookings).Status);
        Assert.Equal(BookingStatus.Cancelled, Assert.Single(after.Bookings).Status);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Terminate_also_ends_a_pending_enrollment()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));

        var result = await _service.TerminateAsync(
            1, new TerminateEnrollmentRequest(enrollment.Id, D(2026, 3, 20)));

        Assert.True(result.Succeeded);
        Assert.Equal(EnrollmentStatus.Terminated, Assert.Single(_repository.Enrollments).Status);
        Assert.Equal(1, _repository.CloseFixedClassesCallCount);
    }

    [Theory]
    [InlineData(EnrollmentStatus.Annulled)]
    [InlineData(EnrollmentStatus.Terminated)]
    public async Task Terminate_rejects_an_enrollment_that_already_ended(EnrollmentStatus status)
    {
        AddClient();
        var enrollment = StoreEnrollment(status: status);

        var result = await _service.TerminateAsync(
            1, new TerminateEnrollmentRequest(enrollment.Id, D(2026, 3, 20)));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_not_terminable", result.Error!.Code);
        Assert.Equal(0, _repository.UpdateEnrollmentCallCount);
        Assert.Equal(0, _repository.CancelAnnotationsCallCount);
        AssertNoWrite();
    }

    [Fact]
    public async Task Terminate_rejects_a_default_effective_date()
    {
        var result = await _service.TerminateAsync(1, new TerminateEnrollmentRequest(1000, default));

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_invalid_effective_date", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Terminate_treats_a_foreign_or_missing_enrollment_the_same_way()
    {
        var foreign = StoreEnrollment(businessId: 2);

        var foreignResult = await _service.TerminateAsync(
            1, new TerminateEnrollmentRequest(foreign.Id, D(2026, 3, 20)));
        var missingResult = await _service.TerminateAsync(
            1, new TerminateEnrollmentRequest(9999, D(2026, 3, 20)));

        Assert.False(foreignResult.Succeeded);
        Assert.False(missingResult.Succeeded);
        Assert.Equal("enrollment_not_found", foreignResult.Error!.Code);
        Assert.Equal(foreignResult.Error!.Code, missingResult.Error!.Code);
        Assert.Equal(2, _unitOfWork.RolledBackCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    // RF-29: the list derives the states from the paid periods and never persists them. `Vencen pronto` is the three
    // days before the expiry that RF-30 warns about, and annulled and terminated memberships are only visible in the
    // "all" view, each one with its own label.

    [Fact]
    public async Task List_derives_every_state_and_hides_ended_memberships_outside_the_all_view()
    {
        AddClient(clientId: 100, fullName: "Ana Vigente");
        AddClient(clientId: 200, fullName: "Bruno Pronto");
        AddClient(clientId: 300, fullName: "Carla Vencida");
        AddClient(clientId: 400, fullName: "Diego Pendiente");
        AddClient(clientId: 500, fullName: "Elena Anulada");
        AddClient(clientId: 600, fullName: "Fabio Baja");

        StoreActiveMembership(D(2026, 3, 1), D(2026, 4, 1), referenceDay: 1, clientId: 100);
        StoreActiveMembership(D(2026, 2, 13), D(2026, 3, 13), referenceDay: 13, clientId: 200);
        StoreActiveMembership(D(2026, 2, 5), D(2026, 3, 5), referenceDay: 5, clientId: 300);
        StoreEnrollment(clientId: 400);
        StoreEnrollment(clientId: 500, status: EnrollmentStatus.Annulled);
        StoreEnrollment(clientId: 600, status: EnrollmentStatus.Terminated);

        var all = await _service.ListAsync(1, EnrollmentListFilter.All);
        Assert.True(all.Succeeded);
        Assert.Equal(6, all.Value.Count);
        Assert.Equal(
            new[] { "Ana Vigente", "Bruno Pronto", "Carla Vencida", "Diego Pendiente", "Elena Anulada", "Fabio Baja" },
            all.Value.Select(item => item.ClientName).ToArray());
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);

        var current = await _service.ListAsync(1, EnrollmentListFilter.Current);
        Assert.Equal(new[] { 100, 200 }, current.Value.Select(item => item.ClientId).ToArray());
        Assert.All(current.Value, item => Assert.Equal(MembershipState.Current, item.MembershipState));
        Assert.All(current.Value, item => Assert.False(item.PendingPayment));
        Assert.False(current.Value.Single(item => item.ClientId == 100).ExpiresSoon);

        var expiringSoon = await _service.ListAsync(1, EnrollmentListFilter.ExpiringSoon);
        var soon = Assert.Single(expiringSoon.Value);
        Assert.Equal(200, soon.ClientId);
        Assert.True(soon.ExpiresSoon);
        Assert.Equal(D(2026, 3, 13), soon.CurrentPeriodEnd);
        Assert.Equal(MembershipState.Current, soon.MembershipState);

        var expired = await _service.ListAsync(1, EnrollmentListFilter.Expired);
        var lapsed = Assert.Single(expired.Value);
        Assert.Equal(300, lapsed.ClientId);
        Assert.Equal(MembershipState.Expired, lapsed.MembershipState);
        Assert.Equal(D(2026, 3, 5), lapsed.CurrentPeriodEnd);
        Assert.False(lapsed.ExpiresSoon);
        Assert.False(lapsed.PendingPayment);

        var pendingPayment = await _service.ListAsync(1, EnrollmentListFilter.PendingPayment);
        var pending = Assert.Single(pendingPayment.Value);
        Assert.Equal(400, pending.ClientId);
        Assert.True(pending.PendingPayment);
        Assert.Equal(MembershipState.None, pending.MembershipState);
        Assert.Null(pending.CurrentPeriodEnd);
        Assert.False(pending.ExpiresSoon);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task List_marks_expiring_soon_only_within_the_three_day_threshold(int daysAhead, bool expected)
    {
        AddClient();
        var expiry = D(2026, 3, 10).AddDays(daysAhead);
        StoreActiveMembership(expiry.AddMonths(-1), expiry, referenceDay: expiry.Day);

        var result = await _service.ListAsync(1, EnrollmentListFilter.All);

        var item = Assert.Single(result.Value);
        Assert.Equal(expected, item.ExpiresSoon);
        Assert.Equal(expiry, item.CurrentPeriodEnd);
    }

    [Fact]
    public async Task List_reads_the_clock_once_for_every_derived_state()
    {
        // The clock advances one day per read: with a second read the membership of tomorrow would already be expired
        // and would leave the `Vencen pronto` view, so a double read cannot pass.
        var clock = new DailyAdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new EnrollmentService(_repository, _prices, _unitOfWork, clock);
        AddClient();
        StoreActiveMembership(D(2026, 2, 11), D(2026, 3, 11), referenceDay: 11);

        var result = await service.ListAsync(1, EnrollmentListFilter.ExpiringSoon);

        Assert.Equal(1, clock.ReadCount);
        var item = Assert.Single(result.Value);
        Assert.True(item.ExpiresSoon);
    }

    [Fact]
    public async Task List_rejects_an_unknown_business_without_reading_storage()
    {
        var result = await _service.ListAsync(0, EnrollmentListFilter.All);

        Assert.False(result.Succeeded);
        Assert.Equal("enrollment_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.ListEnrollmentsCallCount);
    }

    [Fact]
    public async Task Two_charges_extend_the_coverage_month_by_month_without_overlapping()
    {
        // The second charge reads the period the first one wrote under the same lock, so the renewal follows it
        // instead of writing a second period that overlaps the first one.
        AddClient();
        var enrollment = StoreActiveMembership(D(2026, 2, 10), D(2026, 3, 10), referenceDay: 10);

        var first = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));
        var second = await _service.ChargeAsync(1, ChargeRequest(enrollment.Id));

        Assert.Equal(D(2026, 3, 10), first.Value.CoverageStart);
        Assert.Equal(D(2026, 4, 10), first.Value.ExpiresOn);
        Assert.Equal(D(2026, 4, 10), second.Value.CoverageStart);
        Assert.Equal(D(2026, 5, 10), second.Value.ExpiresOn);
        Assert.Equal(2, _repository.Payments.Count);
        // The seeded period plus the two the charges wrote: one payment, one period, never a duplicate of the previous.
        Assert.Equal(3, _repository.Periods.Count);
        Assert.Equal(2, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Charge_fails_loud_when_the_repository_does_not_report_every_annotation()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));
        AddSyncSession(10, At(2026, 3, 11));
        _repository.DropAnnotationResults = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ChargeAsync(1, ChargeRequest(enrollment.Id)));

        AssertNoWrite();
    }

    [Fact]
    public async Task Create_accepts_a_grid_class_whose_assignment_already_ended()
    {
        // An ended assignment keeps its row for history but releases its place (RF-20, RF-21): judged on the
        // registration instant, an assignment whose validity ended before it holds no seat, even when the requested
        // effective date still fell inside that window.
        AddClient(clientId: 100);
        AddClient(clientId: 200, fullName: "Beto Diaz");
        AddPrice();
        AddGridClass(maxCapacity: 1);
        var holder = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Active, referenceDay: 20);
        StoreFixedClass(holder, 10, D(2026, 2, 1), D(2026, 3, 9));

        var result = await _service.CreatePendingAsync(
            1, CreateRequest(clientId: 200, effectiveFrom: D(2026, 3, 1)));

        Assert.True(result.Succeeded);
        Assert.Equal(2, _repository.Enrollments.Count);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task A_fixed_class_of_the_same_enrollment_blocks_a_single_seat()
    {
        // A schedule change leaves two rows of the same enrollment (RF-21). Counting rows instead of enrollments would
        // consume two seats and refuse the next student of a grid class that still has one free place.
        AddClient(clientId: 100);
        AddClient(clientId: 200, fullName: "Beto Diaz");
        AddClient(clientId: 300, fullName: "Carla Diaz");
        AddPrice();
        AddGridClass(maxCapacity: 2);
        var holder = StoreEnrollment(clientId: 100, status: EnrollmentStatus.Active, referenceDay: 1);
        StoreFixedClass(holder, 10, D(2026, 2, 1), D(2026, 3, 9));
        StoreFixedClass(holder, 10, D(2026, 3, 10), validTo: null);

        var accepted = await _service.CreatePendingAsync(1, CreateRequest(clientId: 200));
        var refused = await _service.CreatePendingAsync(1, CreateRequest(clientId: 300));

        Assert.True(accepted.Succeeded);
        Assert.False(refused.Succeeded);
        Assert.Equal("enrollment_no_seat", refused.Error!.Code);
    }

    // RF-20 and RF-29: both endings are terminal transitions of the enrollment, so repeating one is rejected instead of
    // writing again.

    [Fact]
    public async Task Annul_rejects_a_second_annulment_without_writing_again()
    {
        AddClient();
        var enrollment = StoreEnrollment();
        StoreFixedClass(enrollment, 10, D(2026, 3, 1));

        var first = await _service.AnnulPendingAsync(1, new AnnulEnrollmentRequest(enrollment.Id));
        var second = await _service.AnnulPendingAsync(1, new AnnulEnrollmentRequest(enrollment.Id));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal("enrollment_not_pending", second.Error!.Code);
        Assert.Equal(1, _repository.CloseFixedClassesCallCount);
        Assert.Equal(1, _repository.CancelAnnotationsCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Terminate_rejects_a_second_termination_without_writing_again()
    {
        AddClient();
        var enrollment = StoreActiveMembership(D(2026, 3, 1), D(2026, 4, 1), referenceDay: 1);

        var first = await _service.TerminateAsync(
            1, new TerminateEnrollmentRequest(enrollment.Id, D(2026, 3, 20)));
        var second = await _service.TerminateAsync(
            1, new TerminateEnrollmentRequest(enrollment.Id, D(2026, 3, 25)));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal("enrollment_not_terminable", second.Error!.Code);
        Assert.Equal(1, _repository.CloseFixedClassesCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task List_fails_loud_on_an_unknown_filter()
    {
        AddClient();
        StoreEnrollment();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ListAsync(1, (EnrollmentListFilter)99));
    }

    /// <summary>A clock that advances one second per read, so a second read changes the persisted instant.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }
        public DateTime Now => first.AddSeconds(ReadCount++);
    }

    /// <summary>A clock that advances one day per read, so a second read changes the derived membership state.</summary>
    private sealed class DailyAdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }
        public DateTime Now => first.AddDays(ReadCount++);
    }
}
