using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Classes;

// T18 (RF-11, D-20): registering the drop-in charge the owner forgot when marking the attendance. The flow exists
// because a second attendance is refused and the drop-in charge has no other path, so this operation charges an
// annotation that is already `Atendido` and never touches that status. One transaction, one lock — the same
// session-then-booking lock the attendance path takes — and exactly one clock read, taken under that lock. No email is
// queued by any path of the operation.
public class ClassDropInChargeServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 3, 10, 9, 0, 0);

    // The class already happened: the owner marked the attendance yesterday and is registering the charge today.
    private static readonly DateTime ClassStartsAt = new(2026, 3, 9, 18, 0, 0);

    private readonly FakeChargeRepository _repository = new();
    private readonly FakeUnitOfWork _unit = new();
    private readonly FakeClock _clock = new() { Now = ClockNow };
    private readonly ClassDropInChargeService _service;

    public ClassDropInChargeServiceTests()
    {
        _service = new ClassDropInChargeService(_repository, _unit, _clock);
    }

    /// <summary>
    /// One class annotation as the lock reads it. The defaults are the case the flow exists for: a drop-in of the
    /// tenant already marked as present, with no effective charge. Every test narrows what it needs.
    /// </summary>
    private static ClassAttendanceBookingState State(
        BookingModality? modality = BookingModality.DropIn,
        BookingStatus status = BookingStatus.Attended,
        int businessId = 1,
        int bookingId = 500,
        int clientId = 11,
        int appointmentId = 100,
        decimal listPrice = 650m) =>
        new(businessId, bookingId, clientId, appointmentId, ClassStartsAt,
            AppointmentKind.ClassSession, status, modality, null, null, listPrice, []);

    /// <summary>One charge already stored for the booking of the tests.</summary>
    private static ClassAttendancePayment NewPayment(decimal amount, DateTime paidAt) =>
        new(1, 11, 500, PaymentConcept.DropInClass, amount, 650m, null, paidAt);

    private void AssertRolledBack(Result result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
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
    public async Task A_forgotten_charge_lands_on_an_attended_drop_in()
    {
        _repository.Booking = State();

        var result = await _service.ChargeAsync(
            1, 500, new ClassAttendanceCharge(600m, "Precio acordado con la alumna"));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(100, result.Value.AppointmentId);
        Assert.Equal(500, result.Value.BookingId);
        Assert.Equal(11, result.Value.ClientId);
        Assert.Equal(600m, result.Value.Amount);
        Assert.Equal(650m, result.Value.ListPrice);
        Assert.Equal(ClockNow, result.Value.PaidAt);

        // The payment references the booking, not an enrollment (CK_Payments_Reference), and records the class list
        // price beside the amount actually charged, exactly like the at-arrival charge of the same annotation.
        var payment = Assert.Single(_repository.Payments);
        Assert.Equal(1, payment.BusinessId);
        Assert.Equal(11, payment.ClientId);
        Assert.Equal(500, payment.BookingId);
        Assert.Equal(PaymentConcept.DropInClass, payment.Concept);
        Assert.Equal(600m, payment.Amount);
        Assert.Equal(650m, payment.ListPrice);
        Assert.Equal("Precio acordado con la alumna", payment.PriceReason);
        Assert.Equal(ClockNow, payment.PaidAt);

        Assert.Equal(1, _unit.BeginCount);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
    }

    [Fact]
    public async Task The_charge_reads_the_clock_once_and_only_after_the_lock()
    {
        // The instant of the charge has to come from a read taken while the lock is held: reading it before the lock
        // would let a wait for the row be recorded as the charge time, and reading it twice could split the payment
        // from the answer. A clock that advances one day per read makes a second read impossible to miss.
        var clock = new AdvancingClock(ClockNow);
        var service = new ClassDropInChargeService(_repository, _unit, clock);
        _repository.Booking = State();
        var readsAtLock = -1;
        _repository.OnLockCalled = () => readsAtLock = clock.ReadCount;

        var result = await service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m, "Precio acordado"));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(0, readsAtLock);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(ClockNow, Assert.Single(_repository.Payments).PaidAt);
    }

    [Fact]
    public async Task The_charge_never_changes_the_attendance_nor_writes_a_note()
    {
        // The whole point of the flow: the annotation is already `Atendido` and this operation only adds the money. It
        // has no notification port at all, so a charge can never queue an email, and the only persisted write it asks
        // for is the payment itself.
        _repository.Booking = State();

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(["payment"], _repository.Writes);
        Assert.Equal(0, _repository.StatusWriteAttempts);
        Assert.Equal(0, _repository.NoteWriteAttempts);
        Assert.Equal(BookingStatus.Attended, _repository.Booking!.BookingStatus);
    }

    [Fact]
    public async Task A_second_charge_for_the_same_booking_is_refused()
    {
        // An effective payment of the booking is never duplicated, so the flow cannot be used to charge the same
        // drop-in twice; the guard is read under the same lock that serializes the write.
        _repository.Booking = State();
        _repository.Payments.Add(NewPayment(600m, ClockNow.AddHours(-1)));

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        AssertRolledBack(result, "class_charge_already_charged");
        Assert.Single(_repository.Payments);
        Assert.Empty(_repository.Writes);
        Assert.Equal(1, _repository.PaymentCheckCount);
    }

    [Fact]
    public async Task An_annulled_previous_payment_permits_the_corrected_charge()
    {
        // The owner charged the wrong amount, annulled that payment in the ledger and now records the corrected one.
        // An annulled payment is no longer a charge, so the effective-payment rule does not see it — the same rule the
        // attendance path and the SQL read apply — and the corrected charge lands.
        _repository.Booking = State();
        _repository.AnnulledPayments.Add(NewPayment(400m, ClockNow.AddHours(-2)));

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m, "Precio corregido"));

        Assert.True(result.Succeeded, result.Error?.Code);
        var payment = Assert.Single(_repository.Payments);
        Assert.Equal(600m, payment.Amount);
        Assert.Equal(1, _repository.PaymentCheckCount);
        Assert.Single(_repository.AnnulledPayments);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
    }

    [Theory]
    [InlineData(AppointmentStatus.Suspended)]
    [InlineData((AppointmentStatus)7)]
    public async Task A_class_that_is_not_scheduled_refuses_the_charge(AppointmentStatus sessionStatus)
    {
        // RF-10: a suspended class did not happen, so a drop-in does not pay for it even though its annotation still
        // says `Atendido` — suspending a class never alters its annotations. Any session status the rules do not know
        // is refused the same way, because only `Programado` can be charged.
        _repository.Booking = State();
        _repository.SessionStatus = sessionStatus;

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        AssertRolledBack(result, "class_charge_not_available");
        Assert.Empty(_repository.Payments);
        Assert.Equal(BookingStatus.Attended, _repository.Booking!.BookingStatus);
    }

    [Theory]
    [InlineData(BookingModality.Fixed)]
    [InlineData(null)]
    public async Task A_class_annotation_that_is_not_a_drop_in_is_refused(BookingModality? modality)
    {
        // Only the drop-in pays on arrival: a fixed student's money lives in the membership, and an annotation without
        // a modality is not a class annotation the charge rule can judge.
        _repository.Booking = State(modality: modality);

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        AssertRolledBack(result, "class_charge_not_drop_in");
        Assert.Empty(_repository.Payments);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Absent)]
    public async Task An_annotation_that_is_not_attended_is_refused(BookingStatus status)
    {
        // The forgotten charge is registered after the attendance, never instead of it: a pending, a confirmed or an
        // absent annotation still has to be marked as present by the attendance path before it can be charged.
        _repository.Booking = State(status: status);

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        AssertRolledBack(result, "class_charge_not_attended");
        Assert.Empty(_repository.Payments);
        Assert.Equal(status, _repository.Booking!.BookingStatus);
    }

    [Fact]
    public async Task A_cancelled_annotation_is_refused()
    {
        // A cancelled annotation released its place and is never revived by a charge.
        _repository.Booking = State(status: BookingStatus.Cancelled);

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        AssertRolledBack(result, "class_charge_cancelled");
        Assert.Empty(_repository.Payments);
        Assert.Equal(BookingStatus.Cancelled, _repository.Booking!.BookingStatus);
    }

    [Fact]
    public async Task A_foreign_a_missing_or_a_non_class_booking_is_not_found()
    {
        // Foreign: the same booking id under another business is invisible.
        _repository.Booking = State(businessId: 2);
        var foreign = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        // Missing: the port returns no lock.
        _repository.Booking = null;
        var missing = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        // An individual turn: it is not a class session, whatever the modality and the status say.
        _repository.Booking = State() with { Kind = AppointmentKind.Individual };
        var individual = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m));

        AssertRolledBack(foreign, "class_charge_not_found");
        AssertRolledBack(missing, "class_charge_not_found");
        AssertRolledBack(individual, "class_charge_not_found");
        Assert.Empty(_repository.Payments);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1, 0)]
    public async Task Invalid_ids_are_not_found_without_touching_storage(int businessId, int bookingId)
    {
        _repository.Booking = State();

        var result = await _service.ChargeAsync(businessId, bookingId, new ClassAttendanceCharge(600m));

        AssertRejectedBeforeStorage(result, "class_charge_not_found");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(10000000000)]
    public async Task An_amount_out_of_range_is_rejected_without_touching_storage(decimal amount)
    {
        _repository.Booking = State();

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(amount));

        AssertRejectedBeforeStorage(result, "class_charge_invalid_charge");
    }

    [Fact]
    public async Task A_missing_charge_is_refused_without_touching_storage()
    {
        // This flow exists to write a charge, so a request without one is not a valid operation: it answers with the
        // same stable code as a malformed amount instead of throwing, so a caller that binds an empty form cannot
        // break the request with a null.
        _repository.Booking = State();

        var result = await _service.ChargeAsync(1, 500, null!);

        AssertRejectedBeforeStorage(result, "class_charge_invalid_charge");
    }

    [Fact]
    public async Task A_long_price_reason_is_rejected_without_touching_storage()
    {
        _repository.Booking = State();

        var result = await _service.ChargeAsync(
            1, 500, new ClassAttendanceCharge(600m, new string('x', 201)));

        AssertRejectedBeforeStorage(result, "class_charge_invalid_charge");
    }

    [Fact]
    public async Task The_amount_and_reason_limits_of_the_stored_columns_are_accepted()
    {
        // The ceiling of DECIMAL(12, 2) and the 200 characters of PriceReason are the stored limits: both ends of the
        // range are valid charges, so the validation cannot be stricter than the columns.
        _repository.Booking = State();
        var reason = new string('x', 200);

        var result = await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(9999999999.99m, reason));

        Assert.True(result.Succeeded, result.Error?.Code);
        var payment = Assert.Single(_repository.Payments);
        Assert.Equal(9999999999.99m, payment.Amount);
        Assert.Equal(200, payment.PriceReason!.Length);
    }

    [Fact]
    public async Task Every_rejection_after_the_lock_rolls_back_and_never_commits()
    {
        // The in-transaction rejections share one atomicity guarantee: the transaction the operation opened is rolled
        // back and nothing is written.
        _repository.Booking = State(status: BookingStatus.Pending);
        AssertRolledBack(
            await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m)), "class_charge_not_attended");

        _repository.Booking = State(modality: BookingModality.Fixed);
        AssertRolledBack(
            await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m)), "class_charge_not_drop_in");

        _repository.Booking = State(status: BookingStatus.Cancelled);
        AssertRolledBack(
            await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m)), "class_charge_cancelled");

        _repository.Booking = State();
        _repository.Payments.Add(NewPayment(600m, ClockNow));
        AssertRolledBack(
            await _service.ChargeAsync(1, 500, new ClassAttendanceCharge(600m)), "class_charge_already_charged");

        Assert.Equal(0, _unit.CommitCount);
        Assert.Equal(4, _unit.RolledBackCount);
    }

    /// <summary>
    /// In-memory stand-in for the attendance port, narrowed to the charge-only operation. It models the tenant scope
    /// and the class lock exactly like the shared <c>FakeClassAttendanceRepository</c> does and adds the one thing that
    /// fake documents it does not model: the annulment table. An annulled payment is stored but is not effective, which
    /// is the rule this flow leans on when the owner corrects a wrong charge, so the annulment lives here instead of in
    /// the shared fake. The two attendance writes are counted and refused loudly: this operation must never mark a
    /// status nor write a note.
    /// </summary>
    private sealed class FakeChargeRepository : IClassAttendanceRepository
    {
        public ClassAttendanceBookingState? Booking { get; set; }

        /// <summary>The status of the booking's session, as the lock would read it.</summary>
        public AppointmentStatus SessionStatus { get; set; } = AppointmentStatus.Scheduled;

        /// <summary>The effective charges of the business, including the one the operation writes.</summary>
        public List<ClassAttendancePayment> Payments { get; } = [];

        /// <summary>Payments already annulled: they are stored, but they are no longer a charge.</summary>
        public List<ClassAttendancePayment> AnnulledPayments { get; } = [];

        /// <summary>The persisted writes the service asked for, in order, so a test can pin them.</summary>
        public List<string> Writes { get; } = [];

        public int LockCallCount { get; private set; }

        public int PaymentCheckCount { get; private set; }

        public int StatusWriteAttempts { get; private set; }

        public int NoteWriteAttempts { get; private set; }

        /// <summary>Runs inside the lock, so a test can pin what the operation had read by then.</summary>
        public Action? OnLockCalled { get; set; }

        public Task<ClassAttendanceLock?> LockAsync(int businessId, int bookingId, CancellationToken ct = default)
        {
            LockCallCount++;
            OnLockCalled?.Invoke();

            var booking = Booking;
            var resolved = booking is not null && booking.BusinessId == businessId && booking.BookingId == bookingId;
            return Task.FromResult(resolved ? new ClassAttendanceLock(booking!, SessionStatus) : null);
        }

        public Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default)
        {
            // An annulled payment is not in `Payments`, so it never answers as an effective charge.
            PaymentCheckCount++;
            return Task.FromResult(Payments.Any(payment => payment.BookingId == bookingId));
        }

        public Task InsertPaymentAsync(ClassAttendancePayment payment, CancellationToken ct = default)
        {
            Writes.Add("payment");
            Payments.Add(payment);
            return Task.CompletedTask;
        }

        public Task<bool> SetStatusAsync(
            int businessId, int bookingId, BookingStatus status, DateTime now, CancellationToken ct = default)
        {
            StatusWriteAttempts++;
            throw new InvalidOperationException("El cobro posterior no cambia el estado de la asistencia.");
        }

        public Task<bool> InsertNoteAsync(
            int businessId, int clientId, int appointmentId, string note, DateTime now,
            CancellationToken ct = default)
        {
            NoteWriteAttempts++;
            throw new InvalidOperationException("El cobro posterior no escribe observaciones.");
        }
    }

    /// <summary>A clock that advances one day per read, so a second read changes the day and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }

        public DateTime Now => first.AddDays(ReadCount++);
    }
}
