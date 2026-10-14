using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Persistence boundary for the two owner actions over a generated class date (RF-10, RF-22): cancelling one
/// annotation of that date and suspending the whole class session. Every method is scoped by <c>businessId</c>: a
/// booking, a session or a client of another business is invisible and indistinguishable from one that does not
/// exist. Every command joins the caller's ambient transaction, so the locks, the guarded writes and the queued
/// notices are one unit that the service commits exactly once.
/// <para>
/// <b>Lock order.</b> Every action of this port takes the <c>Appointments</c> row of the class first and only then
/// touches the <c>Bookings</c> rows, the order the README prescribes for the class lock: cancelling locks the session
/// row and then the annotation row, and suspending locks the session row and then reads the annotations under the same
/// lock. That single order is what a cancellation and a suspension of the same class share with each other and with
/// <c>ClassAttendanceService</c>, so a suspension and a per-student attendance serialize on the same row instead of
/// interleaving. It is not a global no-deadlock guarantee: the T14 drop-in cancellation
/// (<c>SqlClassDropInRepository</c>) covers the annotation row first and reaches the session row afterwards, the
/// opposite order, so a two-writer inversion between that path and this port is not excluded by this port's order.
/// </para>
/// </summary>
public interface IClassDateActionsRepository
{
    /// <summary>
    /// Locks the class session row of the business and then the annotation row, in that fixed order, inside the
    /// caller's transaction, and reads the annotation with its session facts. It returns <c>null</c> when the booking
    /// does not belong to the business, when it does not exist, or when its session cannot be resolved: a foreign and
    /// a missing booking are the same answer, so nothing about another tenant's rows leaks. The kind and the modality
    /// are not filtered here: the service decides what to do with a booking that is not a class annotation, so the
    /// discriminant is never hidden by the port. The session lock is held until the caller commits or rolls back, so
    /// the class state judged by the caller cannot move under it — a competing suspension of that same class waits for
    /// this unit instead.
    /// </summary>
    Task<ClassBookingCancellationState?> LockBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>
    /// Writes the cancellation of one annotation inside the caller's transaction: <c>Cancelado</c>,
    /// <c>CancelledBy = Owner</c> and the instant of the single clock read of the operation. The guarded statement
    /// repeats every storage-expressible rule — the tenant, the discriminant, the modality, the annotation still
    /// being open, the class still <c>Programado</c> and the class not started at <paramref name="now"/> — so a
    /// double submission or a class that started in the meantime changes no row instead of overwriting the original
    /// instant. It writes nothing else: the enrollment, the grid, the membership periods and every payment stay
    /// exactly as they were (RF-22, D-24). It returns whether exactly one row changed.
    /// </summary>
    Task<bool> CancelBookingAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Locks the class session row of the business — the class lock of the README — and reads it with every
    /// annotation it holds, cancelled ones included, each with its client's name and email. The lock is taken before
    /// the annotations are read and is held until the caller commits, so a cancellation of one of those annotations
    /// cannot land between the read and the suspension write: it waits for this unit. It returns <c>null</c> when the
    /// session does not belong to the business or does not exist, so a foreign and a missing session are the same
    /// answer. The kind is not filtered here: the service refuses a session that is not a class, so the discriminant
    /// is never hidden by the port.
    /// </summary>
    Task<ClassSuspensionSession?> LockSessionAsync(
        int businessId, int appointmentId, CancellationToken ct = default);

    /// <summary>
    /// Writes the suspension of the class inside the caller's transaction: <c>Suspendido</c>, <c>SuspendedAt</c> and
    /// the trimmed reason. The guarded statement repeats the tenant, the discriminant, the state and the start of the
    /// session, so a second suspension or a class that started in the meantime changes no row. It writes the session
    /// row only: every annotation, enrollment and payment stays untouched, because suspending a class never recovers
    /// the class, never charges, never refunds and never credits anything (RF-10, Regla 17, D-15, D-24). It returns
    /// whether exactly one row changed.
    /// </summary>
    Task<bool> SuspendAsync(
        int businessId, int appointmentId, string reason, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Queues one email in the outbox, inside the caller's transaction, so the notice commits with the suspension it
    /// announces and disappears with a rollback. A notice whose unique <see cref="Notification.DedupKey"/> is already
    /// stored is a deduplication hit: it is not stored again and the method answers <c>false</c>, which is how a
    /// technical retry never sends the same suspension notice twice for the same student and the same class.
    /// </summary>
    Task<bool> EnqueueNotificationAsync(Notification notification, CancellationToken ct = default);
}
