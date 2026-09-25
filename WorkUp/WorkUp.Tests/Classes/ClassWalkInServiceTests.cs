using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Classes;

// T16 unit 3 (RF-11, RF-12, RF-22, Regla 15, D-07, D-20, D-25): at-arrival registration of a client who shows up
// without a booking into an already generated class session. The whole operation — the `Suelta` annotation, the
// `Atendido` mark and the optional drop-in charge and observation — is one transaction under one clock read, reuses
// the `ClassCapacity` rule and the guarded last-place insert of T14, and never queues an email (D-07).
public class ClassWalkInServiceTests
{
    private const int BusinessId = 1;
    private const int SessionId = 900;
    private const int ServiceId = 7;
    private const int WorkerId = 3;
    private const int ClientId = 11;
    private const int BookingId = 777;
    private const decimal ListPrice = 5000m;

    private static readonly DateTime ClassStartsAt = new(2026, 3, 12, 18, 0, 0); // Thursday
    private static readonly DateTime ClassEndsAt = new(2026, 3, 12, 19, 0, 0);
    private static readonly DateTime ClockNow = new(2026, 3, 12, 18, 10, 0);

    private readonly FakeClassDropInRepository _dropIns = new();
    private readonly FakeClassAttendanceRepository _attendance = new();
    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeUnitOfWork _unit = new();
    private readonly FakeClock _clock = new() { Now = ClockNow };
    private readonly ClassWalkInService _service;

    public ClassWalkInServiceTests()
    {
        SeedBusiness(allowsWalkIns: true);
        _service = new ClassWalkInService(_dropIns, _attendance, _businesses, _unit, _clock);
    }

    /// <summary>
    /// The fake runs its insert synchronously, so the business exists before the first line of every test. The first
    /// inserted business is id 1, which is what the suite's <see cref="BusinessId"/> names.
    /// </summary>
    private void SeedBusiness(bool allowsWalkIns) =>
        _businesses.InsertAsync(new Business
        {
            Name = "Estudio",
            Slug = "estudio",
            AllowsWalkIns = allowsWalkIns
        }).GetAwaiter().GetResult();

    /// <summary>An existing client of the business, the only shape the owner-assisted path accepts (RF-11).</summary>
    private void AddClient(int clientId = ClientId, int businessId = BusinessId) =>
        _dropIns.Clients.Add(new FakeClassDropInClient(
            businessId, new DropInClient(clientId, "ana@example.invalid", "Ana", "Gómez")));

    private void AddSession(
        DateTime? startsAt = null,
        DateTime? endsAt = null,
        int maxCapacity = 8,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        int businessId = BusinessId,
        AppointmentKind kind = AppointmentKind.ClassSession,
        IReadOnlyList<ClassSessionBooking>? bookings = null,
        int appointmentId = SessionId) =>
        _dropIns.Sessions.Add(new FakeClassDropInSession(
            businessId,
            new ClassSessionRow(
                appointmentId,
                ServiceId,
                WorkerId,
                startsAt ?? ClassStartsAt,
                maxCapacity,
                kind,
                status,
                bookings ?? [],
                endsAt ?? ClassEndsAt)));

    /// <summary>
    /// The annotation the attendance lock reads back for the booking this walk-in just inserted. The defaults
    /// describe the fresh drop-in the insert wrote: pending, drop-in modality, class session of the tenant.
    /// </summary>
    private void LockReturnsBooking(
        int bookingId,
        BookingStatus status = BookingStatus.Pending,
        BookingModality? modality = BookingModality.DropIn,
        AppointmentKind kind = AppointmentKind.ClassSession,
        int appointmentId = SessionId,
        int clientId = ClientId,
        decimal listPrice = ListPrice,
        int businessId = BusinessId) =>
        _attendance.Booking = new ClassAttendanceBookingState(
            businessId, bookingId, clientId, appointmentId, ClassStartsAt, kind, status, modality,
            null, null, listPrice, []);

    /// <summary>
    /// The permissive case every test narrows: an existing client, a scheduled class the arrival can land in, and the
    /// annotation lock answer for the booking the guarded insert produces.
    /// </summary>
    private void SeedHappyPath()
    {
        AddClient();
        AddSession();
        _dropIns.NextBookingId = BookingId;
        LockReturnsBooking(BookingId);
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
        Assert.Equal(0, _dropIns.LockCallCount);
    }

    [Fact]
    public async Task A_walk_in_inside_the_window_books_attends_and_charges_in_one_transaction()
    {
        SeedHappyPath();

        var result = await _service.RegisterAsync(
            BusinessId,
            new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(450m, "Clase suelta con descuento"), "  Alergia al amoníaco  "));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(SessionId, result.Value.AppointmentId);
        Assert.Equal(BookingId, result.Value.BookingId);
        Assert.Equal(ClientId, result.Value.ClientId);
        Assert.Equal(ClassStartsAt, result.Value.StartsAt);
        Assert.Equal(450m, result.Value.ChargedAmount);

        // The booking was written against the session the caller named, of the caller's business, at the single
        // instant the operation read.
        var insert = Assert.Single(_dropIns.Inserts);
        Assert.Equal(BusinessId, insert.BusinessId);
        Assert.Equal(SessionId, insert.AppointmentId);
        Assert.Equal(ClientId, insert.ClientId);
        Assert.Equal(ClockNow, insert.CreatedAt);
        Assert.Equal(BusinessId, _dropIns.LockBusinessId);
        Assert.Equal(SessionId, _dropIns.LockedAppointmentId);
        Assert.Equal(1, _dropIns.FindClientCallCount);

        // The attendance and the charge travel with it: note first, then the guarded status, then the payment.
        Assert.Equal(new[] { "note", "status", "payment" }, _attendance.WriteOrder);
        Assert.Equal(BookingStatus.Attended, _attendance.LastWrittenStatus);
        Assert.Null(_attendance.LastWrittenAbsenceSource);
        Assert.Equal(ClockNow, _attendance.LastWrittenAt);
        var note = Assert.Single(_attendance.Notes);
        Assert.Equal(BusinessId, note.BusinessId);
        Assert.Equal(ClientId, note.ClientId);
        Assert.Equal(SessionId, note.AppointmentId);
        Assert.Equal("Alergia al amoníaco", note.Text);
        Assert.Equal(ClockNow, note.CreatedAt);
        var payment = Assert.Single(_attendance.Payments);
        Assert.Equal(BusinessId, payment.BusinessId);
        Assert.Equal(ClientId, payment.ClientId);
        Assert.Equal(BookingId, payment.BookingId);
        Assert.Equal(PaymentConcept.DropInClass, payment.Concept);
        Assert.Equal(450m, payment.Amount);
        Assert.Equal(ListPrice, payment.ListPrice);
        Assert.Equal("Clase suelta con descuento", payment.PriceReason);
        Assert.Equal(ClockNow, payment.PaidAt);

        // One unit, committed once.
        Assert.Equal(1, _unit.BeginCount);
        Assert.Equal(1, _unit.CommitCount);
        Assert.Equal(0, _unit.RolledBackCount);
        Assert.Equal(1, _attendance.LockCallCount);
    }

    [Theory]
    [InlineData(18, 0)] // the exact start: the client arrives as the class begins
    [InlineData(19, 0)] // the exact end: both ends of the window are included
    public async Task A_walk_in_at_the_exact_start_and_at_the_exact_end_is_allowed(int hour, int minute)
    {
        SeedHappyPath();
        _clock.Now = new DateTime(2026, 3, 12, hour, minute, 0);

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(BookingStatus.Attended, _attendance.LastWrittenStatus);
    }

    [Fact]
    public async Task A_walk_in_before_the_class_starts_is_refused()
    {
        // The window is the arrival window: nobody walks into a class that has not started yet.
        SeedHappyPath();
        _clock.Now = ClassStartsAt.AddMinutes(-1);

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_outside_window");
        Assert.Equal(0, _dropIns.InsertCallCount);
        Assert.Empty(_attendance.WriteOrder);
    }

    [Fact]
    public async Task A_walk_in_after_the_class_ends_is_refused()
    {
        SeedHappyPath();
        _clock.Now = ClassEndsAt.AddSeconds(1);

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_outside_window");
        Assert.Equal(0, _dropIns.InsertCallCount);
    }

    [Fact]
    public async Task A_walk_in_is_refused_when_the_business_does_not_admit_them()
    {
        // Regla 15: a business that publishes its offer on a grid may not admit the attention without a booking. The
        // refusal happens before the client, the session and the seat are even looked at.
        SeedHappyPath();
        _businesses.All.Single().AllowsWalkIns = false;

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_disabled");
        Assert.Equal(0, _dropIns.FindClientCallCount);
        Assert.Equal(0, _dropIns.LockCallCount);
        Assert.Equal(0, _dropIns.InsertCallCount);
    }

    [Fact]
    public async Task A_missing_business_is_not_found()
    {
        SeedHappyPath();
        _businesses.All.Single().Id = 2;

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_not_found");
        Assert.Equal(0, _dropIns.InsertCallCount);
    }

    [Fact]
    public async Task A_suspended_class_refuses_the_walk_in()
    {
        // RF-10: a suspended class did not happen, so nobody is added to it and nobody pays for it.
        SeedHappyPath();
        _dropIns.Sessions.Clear();
        AddSession(status: AppointmentStatus.Suspended);

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_not_available");
        Assert.Equal(0, _dropIns.InsertCallCount);
        Assert.Empty(_attendance.WriteOrder);
    }

    [Fact]
    public async Task A_foreign_or_missing_class_is_not_found()
    {
        // Foreign: the same session id under another business is invisible.
        SeedHappyPath();
        _dropIns.Sessions.Clear();
        AddSession(businessId: 2);
        var foreign = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        // Missing: the port returns null.
        _dropIns.Sessions.Clear();
        var missing = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(foreign, "class_walk_in_not_found");
        AssertRolledBack(missing, "class_walk_in_not_found");
        Assert.Equal(0, _dropIns.InsertCallCount);
    }

    [Fact]
    public async Task An_individual_turn_is_not_a_class()
    {
        // An individual turn has capacity 1 and can also be `Programado`, so a repository that forgot the
        // discriminant must never let it receive a walk-in as if it were a class.
        SeedHappyPath();
        _dropIns.Sessions.Clear();
        AddSession(kind: AppointmentKind.Individual, maxCapacity: 1);

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_not_found");
        Assert.Equal(0, _dropIns.InsertCallCount);
    }

    [Fact]
    public async Task A_foreign_or_missing_client_is_not_found()
    {
        // The owner-assisted path never creates a client by mistake: an unknown one is refused, and the client of
        // another business is as unknown as a missing one.
        SeedHappyPath();
        _dropIns.Clients.Clear();
        var missing = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        _dropIns.Clients.Clear();
        AddClient(businessId: 2);
        var foreign = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(missing, "class_walk_in_client_not_found");
        AssertRolledBack(foreign, "class_walk_in_client_not_found");
        Assert.Equal(0, _dropIns.LockCallCount);
        Assert.Equal(0, _dropIns.InsertCallCount);
    }

    [Fact]
    public async Task A_full_class_refuses_the_walk_in()
    {
        SeedHappyPath();
        _dropIns.Sessions.Clear();
        AddSession(maxCapacity: 1, bookings: [new ClassSessionBooking(99, BookingStatus.Pending)]);

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_full");
        Assert.Equal(0, _dropIns.InsertCallCount);
        Assert.Empty(_attendance.WriteOrder);
    }

    [Fact]
    public async Task A_client_who_already_holds_a_place_hears_that_first()
    {
        // The client's own booking is decided before the occupancy: she must hear that she is already booked even
        // when her own place is what makes the class look full.
        SeedHappyPath();
        _dropIns.Sessions.Clear();
        AddSession(maxCapacity: 1, bookings: [new ClassSessionBooking(ClientId, BookingStatus.Pending)]);

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_already_booked");
        Assert.Equal(0, _dropIns.InsertCallCount);
    }

    [Fact]
    public async Task A_lost_last_place_race_is_reported_as_full_without_attending()
    {
        // The guarded insert is the real protection of the last place: it reports the losing outcome without writing,
        // so the loser rolls back instead of overbooking the class or charging for a place it never got.
        SeedHappyPath();
        _dropIns.TryInsertReturnsNoPlaceLeft = true;

        var result = await _service.RegisterAsync(
            BusinessId, new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(450m)));

        AssertRolledBack(result, "class_walk_in_full");
        Assert.Equal(1, _dropIns.InsertCallCount);
        Assert.Equal(0, _attendance.LockCallCount);
        Assert.Empty(_attendance.WriteOrder);
    }

    [Fact]
    public async Task A_duplicate_insert_is_reported_as_already_booked()
    {
        SeedHappyPath();
        _dropIns.TryInsertReturnsAlreadyBooked = true;

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_already_booked");
        Assert.Equal(0, _attendance.LockCallCount);
    }

    [Fact]
    public async Task An_undefined_insert_outcome_fails_loudly()
    {
        // The enum knows exactly three outcomes and has no zero member: any other value is a broken implementation
        // and must never be read as a successful insert with a zero booking id.
        SeedHappyPath();
        _dropIns.TryInsertResultOverride = new DropInInsertResult((DropInInsertResultType)99, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId)));

        Assert.Equal(0, _unit.CommitCount);
        Assert.Equal(1, _unit.RolledBackCount);
    }

    [Fact]
    public async Task A_walk_in_without_a_charge_attends_without_writing_a_payment()
    {
        // Attendance and payment are independent (D-20, Regla 21): the owner can register the arrival and forget the
        // money without blocking the class.
        SeedHappyPath();

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Null(result.Value.ChargedAmount);
        Assert.Empty(_attendance.Payments);
        Assert.Equal(BookingStatus.Attended, _attendance.LastWrittenStatus);
        Assert.Equal(new[] { "status" }, _attendance.WriteOrder);
        Assert.Equal(1, _unit.CommitCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(10000000000)]
    public async Task Invalid_amounts_are_rejected_without_touching_storage(decimal amount)
    {
        SeedHappyPath();

        var result = await _service.RegisterAsync(
            BusinessId, new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(amount)));

        AssertRejectedBeforeStorage(result, "class_walk_in_invalid_charge");
    }

    [Fact]
    public async Task A_long_price_reason_is_rejected_without_touching_storage()
    {
        SeedHappyPath();

        var result = await _service.RegisterAsync(
            BusinessId,
            new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(450m, new string('x', 201))));

        AssertRejectedBeforeStorage(result, "class_walk_in_invalid_charge");
    }

    [Fact]
    public async Task A_long_note_is_rejected_without_touching_storage()
    {
        SeedHappyPath();

        var result = await _service.RegisterAsync(
            BusinessId,
            new ClassWalkInRequest(SessionId, ClientId, Note: new string('x', 1001)));

        AssertRejectedBeforeStorage(result, "class_walk_in_invalid_note");
    }

    [Theory]
    [InlineData(0, SessionId, ClientId)]
    [InlineData(BusinessId, 0, ClientId)]
    [InlineData(BusinessId, SessionId, 0)]
    public async Task Invalid_ids_are_rejected_without_touching_storage(int businessId, int appointmentId, int clientId)
    {
        // A missing identifier is answered like a missing record: the caller must not be able to tell "you did not
        // name it" from "it is not yours".
        SeedHappyPath();

        var result = await _service.RegisterAsync(
            businessId, new ClassWalkInRequest(appointmentId, clientId));

        AssertRejectedBeforeStorage(result, "class_walk_in_invalid_request");
    }

    [Fact]
    public async Task A_note_that_cannot_land_rolls_back_the_booking_and_the_charge()
    {
        // The observation is mandatory to land: a refused note fails the whole operation, and because the walk-in owns
        // its transaction, the annotation the guarded insert already wrote is rolled back with it. The fake does not
        // model the SQL rollback, so the transaction counters and the untouched payment are the observable proof here.
        SeedHappyPath();
        _attendance.FailNoteInsert = true;

        var result = await _service.RegisterAsync(
            BusinessId, new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(450m), "Alergia"));

        AssertRolledBack(result, "class_walk_in_note_failed");
        Assert.Equal(new[] { "note" }, _attendance.WriteOrder);
        Assert.Empty(_attendance.Payments);
        Assert.Null(_attendance.LastWrittenStatus);
    }

    [Fact]
    public async Task A_guarded_status_write_that_changes_no_row_rolls_back()
    {
        SeedHappyPath();
        _attendance.RefuseStatusWrite = true;

        var result = await _service.RegisterAsync(
            BusinessId, new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(450m)));

        AssertRolledBack(result, "class_walk_in_unavailable");
        Assert.Empty(_attendance.Payments);
    }

    [Fact]
    public async Task An_annotation_the_attendance_lock_cannot_resolve_rolls_back()
    {
        // The booking the guarded insert wrote must be visible to the attendance lock of the same transaction; if it
        // is not, the state is broken and the operation must fail instead of attending an unknown annotation.
        SeedHappyPath();
        _attendance.Booking = null;

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_unavailable");
        Assert.Equal(1, _dropIns.InsertCallCount);
        Assert.Empty(_attendance.WriteOrder);
    }

    [Fact]
    public async Task A_client_whose_previous_booking_was_cancelled_can_walk_in_again()
    {
        // D-25: cancelling an annotation frees its place and stops it counting as a booking, so the same client who
        // booked, cancelled and then showed up is registered as attended instead of hearing that she is already
        // booked. The fake models that freed place; the opt-in SQL fixture proves the stored row.
        SeedHappyPath();
        _dropIns.Sessions.Clear();
        AddSession(bookings: [new ClassSessionBooking(ClientId, BookingStatus.Cancelled)]);

        var result = await _service.RegisterAsync(
            BusinessId, new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(450m)));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(1, _dropIns.InsertCallCount);
        var insert = Assert.Single(_dropIns.Inserts);
        Assert.Equal(ClientId, insert.ClientId);
        Assert.Equal(SessionId, insert.AppointmentId);
        Assert.Equal(BookingId, result.Value.BookingId);
        Assert.Equal(BookingStatus.Attended, _attendance.LastWrittenStatus);
        Assert.Equal(1, _unit.CommitCount);
    }

    [Fact]
    public async Task The_walk_in_reads_the_clock_once_and_persists_that_instant()
    {
        // A clock that advances one day per read: a second read would be the next day and could not go unnoticed.
        var clock = new AdvancingClock(ClockNow);
        var service = new ClassWalkInService(_dropIns, _attendance, _businesses, _unit, clock);
        SeedHappyPath();

        var result = await service.RegisterAsync(
            BusinessId,
            new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(450m), "Nota de una sola lectura"));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(ClockNow, _dropIns.Inserts.Single().CreatedAt);
        Assert.Equal(ClockNow, _attendance.LastWrittenAt);
        Assert.Equal(ClockNow, _attendance.Payments.Single().PaidAt);
        Assert.Equal(ClockNow, _attendance.Notes.Single().CreatedAt);
    }

    [Fact]
    public async Task A_lock_wait_that_spans_the_end_of_the_class_refuses_the_walk_in()
    {
        // The class session row is the lock: while a transaction waits for it, wall time keeps moving. The arrival
        // window must be judged at the instant the lock is held, not at the instant the call began, so a walk-in that
        // waited past the end of the class is refused instead of accepted with a stale arrival time.
        SeedHappyPath();
        _dropIns.WhileWaitingForLock = () => _clock.Now = ClassEndsAt.AddSeconds(1);

        var result = await _service.RegisterAsync(BusinessId, new ClassWalkInRequest(SessionId, ClientId));

        AssertRolledBack(result, "class_walk_in_outside_window");
        Assert.Equal(0, _dropIns.InsertCallCount);
        Assert.Empty(_attendance.WriteOrder);
    }

    [Fact]
    public async Task The_instant_persisted_is_the_one_read_after_the_lock()
    {
        // A walk-in that waits for the lock but still arrives inside the window must persist the post-wait instant
        // everywhere — booking, attendance, note and payment — because that is the instant the window was judged at.
        SeedHappyPath();
        var afterLock = ClockNow.AddMinutes(7);
        _dropIns.WhileWaitingForLock = () => _clock.Now = afterLock;

        var result = await _service.RegisterAsync(
            BusinessId,
            new ClassWalkInRequest(SessionId, ClientId, new ClassAttendanceCharge(450m), "Nota"));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(afterLock, _dropIns.Inserts.Single().CreatedAt);
        Assert.Equal(afterLock, _attendance.LastWrittenAt);
        Assert.Equal(afterLock, _attendance.Payments.Single().PaidAt);
        Assert.Equal(afterLock, _attendance.Notes.Single().CreatedAt);
    }

    /// <summary>A clock that advances one day per read, so a second read changes the day and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }

        public DateTime Now => first.AddDays(ReadCount++);
    }
}
