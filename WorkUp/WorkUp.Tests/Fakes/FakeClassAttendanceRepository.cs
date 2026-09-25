using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>One observation landed on the client's record, tied to the session it was taken in (RF-11 step 4).</summary>
public sealed record FakeClientNote(int BusinessId, int ClientId, int AppointmentId, string Text, DateTime CreatedAt);

/// <summary>
/// In-memory stand-in for the per-student attendance port. It emulates the tenant scope and the guarded status
/// statement exactly like the SQL predicates: a booking registered under another business is invisible, only an
/// annotation that is still open changes status, and a suspended class never changes status. It also models the class
/// session lock the real port takes first: <see cref="SessionLockHeld"/> is held from <see cref="LockAsync"/> to the
/// guarded status write, and <see cref="TrySuspendSession"/> — the competing suspension — respects it. It exposes the
/// instant and the absence source the last guarded write persisted, so a test can prove the single clock read is what
/// got stored, and the order of the writes, so a test can pin that the fallible observation precedes the status write
/// and the charge.
/// <para>
/// The fake does not model the annulment table: any payment of the booking counts as effective, which is the same
/// answer for every case the unit tests exercise. It does not model the transaction rollback either, so a test that
/// proves atomicity asserts the transaction counters (no commit, every begin rolled back) instead of the in-memory
/// state; <c>ClassAttendanceSqlIntegrationTests</c> is what proves on real SQL that a rolled-back unit erases the rows
/// and that a failed note leaves nothing behind even inside an outer transaction its opener commits.
/// </para>
/// </summary>
public sealed class FakeClassAttendanceRepository : IClassAttendanceRepository
{
    public ClassAttendanceBookingState? Booking { get; set; }

    /// <summary>The status of the booking's session, as the lock would read it. Scheduled unless a test says otherwise.</summary>
    public AppointmentStatus SessionStatus { get; set; } = AppointmentStatus.Scheduled;

    public List<ClassAttendancePayment> Payments { get; } = [];

    public List<FakeClientNote> Notes { get; } = [];

    /// <summary>The writes the service asked for, in order, so a test can pin which one goes first.</summary>
    public List<string> WriteOrder { get; } = [];

    /// <summary>When set, the note refuses to land so the caller's rollback can be observed.</summary>
    public bool FailNoteInsert { get; set; }

    /// <summary>When set, the guarded status write reports "no row changed", like a lost race.</summary>
    public bool RefuseStatusWrite { get; set; }

    /// <summary>
    /// Invoked right after a note lands and before the operation's next write, so a test can pin the interleaving the
    /// composition report named: a suspension arriving exactly between the observation and the guarded status write.
    /// </summary>
    public Action? OnNoteInserted { get; set; }

    /// <summary>
    /// Simulates a concurrent suspension of this annotation's class (RF-10) arriving while an operation is in flight.
    /// The real suspension writes the <c>Appointments</c> row, the very row the port takes an update lock on in
    /// <see cref="LockAsync"/> and holds until the unit ends, so while that lock is held the suspension cannot land.
    /// This returns whether it applied instead of forcing the session state, and when it applies it leaves the session
    /// suspended, which is exactly what the guarded status write then refuses.
    /// </summary>
    public bool TrySuspendSession()
    {
        if (SessionLockHeld)
            return false;

        SessionStatus = AppointmentStatus.Suspended;
        return true;
    }

    public int LockCallCount { get; private set; }

    /// <summary>
    /// Whether the class session lock is held, mirroring the update lock the real port takes on the
    /// <c>Appointments</c> row in <see cref="LockAsync"/> and keeps until the unit ends. It is what
    /// <see cref="TrySuspendSession"/> respects. The fake releases it on the guarded status write, which is the last
    /// step of the window the lock has to cover: the read of the session state through the guarded write.
    /// </summary>
    public bool SessionLockHeld { get; private set; }

    public int? LockedBusinessId { get; private set; }

    public int? LockedBookingId { get; private set; }

    public int PaymentCheckCount { get; private set; }

    public BookingStatus? LastWrittenStatus { get; private set; }

    public AbsenceSource? LastWrittenAbsenceSource { get; private set; }

    public DateTime? LastWrittenAt { get; private set; }

    public Task<ClassAttendanceLock?> LockAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        LockCallCount++;
        LockedBusinessId = businessId;
        LockedBookingId = bookingId;

        var booking = Booking;
        var resolved = booking is not null && booking.BusinessId == businessId && booking.BookingId == bookingId;
        // The lock is acquired only for an annotation this business can see: a foreign or missing booking returns no
        // lock, exactly like a lock statement that matches no row. Taking it here is what a session-first statement
        // does before the annotation row.
        SessionLockHeld = resolved;
        return Task.FromResult(
            resolved ? new ClassAttendanceLock(booking!, SessionStatus) : null);
    }

    public Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default)
    {
        PaymentCheckCount++;
        return Task.FromResult(Payments.Any(payment => payment.BookingId == bookingId));
    }

    public Task<bool> SetStatusAsync(
        int businessId, int bookingId, BookingStatus status, DateTime now, CancellationToken ct = default)
    {
        WriteOrder.Add("status");

        // The guarded write is the last step the session lock covers; the fake releases it here either way.
        SessionLockHeld = false;

        if (RefuseStatusWrite
            || SessionStatus == AppointmentStatus.Suspended
            || Booking is null
            || Booking.BusinessId != businessId
            || Booking.BookingId != bookingId
            || Booking.BookingStatus is not (BookingStatus.Pending or BookingStatus.Confirmed))
            return Task.FromResult(false);

        Booking = Booking with { BookingStatus = status };
        LastWrittenStatus = status;
        LastWrittenAbsenceSource = status == BookingStatus.Absent ? AbsenceSource.Owner : null;
        LastWrittenAt = now;
        return Task.FromResult(true);
    }

    public Task InsertPaymentAsync(ClassAttendancePayment payment, CancellationToken ct = default)
    {
        WriteOrder.Add("payment");
        Payments.Add(payment);
        return Task.CompletedTask;
    }

    public Task<bool> InsertNoteAsync(
        int businessId, int clientId, int appointmentId, string note, DateTime now, CancellationToken ct = default)
    {
        WriteOrder.Add("note");
        if (FailNoteInsert)
            return Task.FromResult(false);

        Notes.Add(new FakeClientNote(businessId, clientId, appointmentId, note, now));
        OnNoteInserted?.Invoke();
        return Task.FromResult(true);
    }
}
