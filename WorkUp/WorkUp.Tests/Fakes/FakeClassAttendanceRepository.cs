using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>One observation landed on the client's record, tied to the session it was taken in (RF-11 step 4).</summary>
public sealed record FakeClientNote(int BusinessId, int ClientId, int AppointmentId, string Text, DateTime CreatedAt);

/// <summary>
/// In-memory stand-in for the per-student attendance port. It emulates the tenant scope and the guarded status
/// statement exactly like the SQL predicates: a booking registered under another business is invisible, and only an
/// annotation that is still open changes status. It also exposes the instant and the absence source the last guarded
/// write persisted, so a test can prove the single clock read is what got stored.
/// <para>
/// The fake does not model the annulment table: any payment of the booking counts as effective, which is the same
/// answer for every case the unit tests exercise. It does not model the transaction rollback either, so a test that
/// proves atomicity asserts the transaction counters (no commit, every begin rolled back) instead of the in-memory
/// state; the opt-in SQL fixture is what proves the rolled-back unit erases the rows.
/// </para>
/// </summary>
public sealed class FakeClassAttendanceRepository : IClassAttendanceRepository
{
    public ClassAttendanceBookingState? Booking { get; set; }

    public List<ClassAttendancePayment> Payments { get; } = [];

    public List<FakeClientNote> Notes { get; } = [];

    /// <summary>When set, the note refuses to land so the caller's rollback can be observed.</summary>
    public bool FailNoteInsert { get; set; }

    /// <summary>When set, the guarded status write reports "no row changed", like a lost race.</summary>
    public bool RefuseStatusWrite { get; set; }

    public int LockCallCount { get; private set; }

    public int? LockedBusinessId { get; private set; }

    public int? LockedBookingId { get; private set; }

    public int PaymentCheckCount { get; private set; }

    public BookingStatus? LastWrittenStatus { get; private set; }

    public AbsenceSource? LastWrittenAbsenceSource { get; private set; }

    public DateTime? LastWrittenAt { get; private set; }

    public Task<ClassAttendanceBookingState?> LockAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        LockCallCount++;
        LockedBusinessId = businessId;
        LockedBookingId = bookingId;

        var booking = Booking;
        return Task.FromResult(
            booking is not null && booking.BusinessId == businessId && booking.BookingId == bookingId
                ? booking
                : null);
    }

    public Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default)
    {
        PaymentCheckCount++;
        return Task.FromResult(Payments.Any(payment => payment.BookingId == bookingId));
    }

    public Task<bool> SetStatusAsync(
        int businessId, int bookingId, BookingStatus status, DateTime now, CancellationToken ct = default)
    {
        if (RefuseStatusWrite
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
        Payments.Add(payment);
        return Task.CompletedTask;
    }

    public Task<bool> InsertNoteAsync(
        int businessId, int clientId, int appointmentId, string note, DateTime now, CancellationToken ct = default)
    {
        if (FailNoteInsert)
            return Task.FromResult(false);

        Notes.Add(new FakeClientNote(businessId, clientId, appointmentId, note, now));
        return Task.FromResult(true);
    }
}
