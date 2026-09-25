using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the two owner actions over a class date (RF-10, RF-22). It emulates the tenant scope, the
/// lock order — the class session row of <c>Appointments</c> first and the annotation rows of <c>Bookings</c>
/// afterwards — and the guarded statements exactly like the SQL predicates: only a <c>Programado</c> class that has
/// not started admits both writes, a cancellation never rewrites an already cancelled annotation, and a suspension
/// touches the session row only.
/// <para>
/// <see cref="SessionLockHeld"/> models the update lock the real port takes on the <c>Appointments</c> row and holds
/// until the unit ends; <see cref="TrySuspendSession"/> is the competing suspension, and it respects that lock. The
/// fake releases the lock on the guarded write, which is the last step the lock has to cover in these operations.
/// </para>
/// <para>
/// The outbox models <c>UQ_Notifications_DedupKey</c>: a notice whose deduplication key is already stored is not
/// stored again and <see cref="EnqueueNotificationAsync"/> answers <c>false</c>, like the SQL insert that swallows
/// the unique-key violation. The fake does not model the transaction rollback, so a test that proves atomicity asserts
/// the transaction counters (no commit, every begin rolled back) instead of the in-memory state.
/// </para>
/// </summary>
public sealed class FakeClassDateActionsRepository : IClassDateActionsRepository
{
    /// <summary>The class session the two actions act on. It is the <c>Appointments</c> row, so no action ever sees it
    /// through another business. <c>null</c> means the session does not exist.</summary>
    public FakeClassDateSession? Session { get; set; }

    /// <summary>The annotations of that session, in storage order by booking id.</summary>
    public List<FakeClassDateBooking> Bookings { get; } = [];

    /// <summary>The queued notices, unique by deduplication key like the outbox table.</summary>
    public List<Notification> Outbox { get; } = [];

    /// <summary>The rows locked or read by the last operation, in order, so a test can pin the class lock first.</summary>
    public List<string> LockOrder { get; } = [];

    public int LockBookingCallCount { get; private set; }

    public int CancelWriteCallCount { get; private set; }

    public int LockSessionCallCount { get; private set; }

    public int SuspendWriteCallCount { get; private set; }

    public int EnqueueCallCount { get; private set; }

    /// <summary>When set, the guarded cancellation reports "no row changed", like a lost race.</summary>
    public bool RefuseCancelWrite { get; set; }

    /// <summary>When set, the guarded suspension reports "no row changed", like a lost race.</summary>
    public bool RefuseSuspendWrite { get; set; }

    /// <summary>
    /// Whether the class session lock is held, mirroring the update lock the real port takes on the
    /// <c>Appointments</c> row and keeps until the unit ends. <see cref="TrySuspendSession"/> respects it, and the
    /// fake releases it on the guarded write of the operation that took it.
    /// </summary>
    public bool SessionLockHeld { get; private set; }

    /// <summary>
    /// Invoked right after the annotation row is locked and before the guarded cancellation write, so a test can pin
    /// the interleaving the class lock rules out: a suspension of the same class arriving while the cancellation is
    /// in flight.
    /// </summary>
    public Action? OnBookingLocked { get; set; }

    /// <summary>
    /// Simulates a competing suspension of the class (RF-10) arriving while an operation is in flight. The real
    /// suspension writes the <c>Appointments</c> row, the very row both actions lock first, so while that lock is held
    /// the suspension cannot land. It returns whether it applied instead of forcing the session state.
    /// </summary>
    public bool TrySuspendSession()
    {
        if (SessionLockHeld || Session is null)
            return false;

        Session.Status = AppointmentStatus.Suspended;
        return true;
    }

    public FakeClassDateBooking? FindBooking(int businessId, int bookingId) =>
        Bookings.FirstOrDefault(booking =>
            booking.BusinessId == businessId && booking.BookingId == bookingId);

    public Task<ClassBookingCancellationState?> LockBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        LockBookingCallCount++;
        SessionLockHeld = false;

        var booking = FindBooking(businessId, bookingId);
        if (booking is null || Session is null || Session.AppointmentId != booking.AppointmentId)
            return Task.FromResult<ClassBookingCancellationState?>(null);

        // The class session row is locked first and the annotation second: the single order the README prescribes and
        // the one the attendance read already uses.
        LockOrder.Add("appointments");
        SessionLockHeld = true;
        LockOrder.Add("booking");
        OnBookingLocked?.Invoke();

        return Task.FromResult<ClassBookingCancellationState?>(new ClassBookingCancellationState(
            booking.BusinessId,
            booking.BookingId,
            booking.AppointmentId,
            Session.StartsAt,
            Session.Kind,
            Session.Status,
            booking.Status,
            booking.Modality));
    }

    public Task<bool> CancelBookingAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default)
    {
        CancelWriteCallCount++;

        // The guarded write is the last step the session lock covers; the fake releases it here either way.
        SessionLockHeld = false;

        var booking = FindBooking(businessId, bookingId);
        if (RefuseCancelWrite
            || booking is null
            || Session is null
            || Session.AppointmentId != booking.AppointmentId
            || Session.Kind != AppointmentKind.ClassSession
            || Session.Status != AppointmentStatus.Scheduled
            || Session.StartsAt <= now
            || booking.Modality is not (BookingModality.Fixed or BookingModality.DropIn)
            || booking.Status == BookingStatus.Cancelled
            || booking.CancelledAt is not null)
            return Task.FromResult(false);

        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = now;
        booking.CancelledBy = CancelledBy.Owner;
        return Task.FromResult(true);
    }

    public Task<ClassSuspensionSession?> LockSessionAsync(
        int businessId, int appointmentId, CancellationToken ct = default)
    {
        LockSessionCallCount++;
        SessionLockHeld = false;

        if (Session is null || Session.BusinessId != businessId || Session.AppointmentId != appointmentId)
            return Task.FromResult<ClassSuspensionSession?>(null);

        LockOrder.Add("appointments");
        SessionLockHeld = true;
        LockOrder.Add("bookings");

        var bookings = Bookings
            .Where(booking => booking.BusinessId == businessId && booking.AppointmentId == appointmentId)
            .OrderBy(booking => booking.BookingId)
            .Select(booking => new ClassSuspensionBooking(
                booking.BookingId,
                booking.ClientId,
                booking.ClientEmail,
                booking.ClientName,
                booking.Modality,
                booking.Status))
            .ToArray();

        return Task.FromResult<ClassSuspensionSession?>(new ClassSuspensionSession(
            Session.BusinessId, Session.AppointmentId, Session.StartsAt, Session.Kind, Session.Status, bookings));
    }

    public Task<bool> SuspendAsync(
        int businessId, int appointmentId, string reason, DateTime now, CancellationToken ct = default)
    {
        SuspendWriteCallCount++;

        // The guarded write is the last step the session lock covers; the fake releases it here either way.
        SessionLockHeld = false;

        if (RefuseSuspendWrite
            || Session is null
            || Session.BusinessId != businessId
            || Session.AppointmentId != appointmentId
            || Session.Kind != AppointmentKind.ClassSession
            || Session.Status != AppointmentStatus.Scheduled
            || Session.StartsAt <= now)
            return Task.FromResult(false);

        Session.Status = AppointmentStatus.Suspended;
        Session.SuspendedAt = now;
        Session.SuspensionReason = reason;
        return Task.FromResult(true);
    }

    public Task<bool> EnqueueNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        EnqueueCallCount++;

        // UQ_Notifications_DedupKey: the same student and the same class never get a second notice.
        if (Outbox.Any(queued => queued.DedupKey == notification.DedupKey))
            return Task.FromResult(false);

        Outbox.Add(notification);
        return Task.FromResult(true);
    }
}

/// <summary>One <c>Appointments</c> row of a class date as the fake stores it (RF-10, RF-22).</summary>
public sealed class FakeClassDateSession(
    int businessId,
    int appointmentId,
    DateTime startsAt,
    AppointmentKind kind,
    AppointmentStatus status)
{
    public int BusinessId { get; } = businessId;

    public int AppointmentId { get; } = appointmentId;

    public DateTime StartsAt { get; init; } = startsAt;

    public AppointmentKind Kind { get; init; } = kind;

    public AppointmentStatus Status { get; set; } = status;

    public DateTime? SuspendedAt { get; set; }

    public string? SuspensionReason { get; set; }
}

/// <summary>One <c>Bookings</c> row of that class as the fake stores it (RF-10, RF-22).</summary>
public sealed class FakeClassDateBooking(
    int businessId,
    int bookingId,
    int appointmentId,
    int clientId,
    string clientEmail,
    string clientName,
    BookingModality? modality,
    BookingStatus status)
{
    public int BusinessId { get; } = businessId;

    public int BookingId { get; } = bookingId;

    public int AppointmentId { get; } = appointmentId;

    public int ClientId { get; } = clientId;

    public string ClientEmail { get; } = clientEmail;

    public string ClientName { get; } = clientName;

    public BookingModality? Modality { get; } = modality;

    public BookingStatus Status { get; set; } = status;

    public DateTime? CancelledAt { get; set; }

    public CancelledBy? CancelledBy { get; set; }
}
