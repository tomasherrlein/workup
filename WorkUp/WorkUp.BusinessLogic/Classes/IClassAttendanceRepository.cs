using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// The annotation the lock returned together with the status of the session it belongs to. Suspension is a property
/// of the class, not of the annotation, so it travels beside the booking state instead of inside it; the lock reads
/// both in the same statement, so a suspended class is refused from the single locked read and before any write.
/// </summary>
public sealed record ClassAttendanceLock(ClassAttendanceBookingState Annotation, AppointmentStatus SessionStatus);

/// <summary>
/// Persistence boundary for the per-student attendance and absence of a generated class session (RF-11, RF-12, D-16,
/// D-20, D-23). Every method is scoped by <c>businessId</c>: a booking of another business is invisible and
/// indistinguishable from one that does not exist. Every command joins the caller's ambient transaction, so the lock,
/// the optional note, the status write and the optional charge are one unit that the service commits exactly once.
/// <para>
/// The implementations must let the same shape serve both operations: attendance and absence differ only in the
/// status they write and in the coverage rule the service applies, never in storage. The optional note is mandatory
/// to land: a note that could not be written answers <c>false</c> so the caller fails the whole operation instead of
/// losing the observation silently, and the service asks for it before every other write so that failure leaves
/// nothing persistent behind even when the unit is composed inside an outer transaction.
/// </para>
/// </summary>
public interface IClassAttendanceRepository
{
    /// <summary>
    /// Locks the class session row of the business — the row the README calls the class lock — and then the annotation
    /// row, in that fixed order, inside the caller's transaction, and reads the annotation with its session, the
    /// session status, its client, the enrollment status and every paid period the coverage rule needs. The lock is
    /// what serializes two operations on the same booking — a double attendance with a charge, for instance — and it
    /// is held until the caller commits or rolls back. The session is locked first, so no writer of that same
    /// <c>Appointments</c> row can commit between the read of the session state and the guarded status write: it waits
    /// for the unit instead, and the suspension path in <c>ClassDateActionsService</c> — which now exists — takes the
    /// same row first, so it is serialized by this lock like every other writer of the session. It returns <c>null</c>
    /// when
    /// the booking does not belong to the business, when it does not exist, or when it is missing its session: a
    /// foreign and a missing booking are the same answer, so nothing about another tenant's rows leaks. The kind and
    /// the modality are not filtered here: the service decides what to do with a booking that is not a class
    /// annotation, so the discriminant is never hidden by the port. The session status travels beside the state, read
    /// under the session lock, so the suspension rule is judged from a read that no concurrent writer of that row can
    /// invalidate before the write (RF-10).
    /// </summary>
    Task<ClassAttendanceLock?> LockAsync(int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>
    /// Whether the annotation already has a payment that is still in force, so a second charge is never written. An
    /// annulled payment does not count: it is no longer a real charge (RF-26, D-20, and the same rule the individual
    /// path uses). The read is taken under the caller's lock, so it cannot be raced by a concurrent charge of the same
    /// booking.
    /// </summary>
    Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>
    /// Writes the status of the annotation together with the instant it was recorded, inside the caller's transaction.
    /// The guarded statement repeats every storage-expressible rule — the discriminant, the modality, the open status,
    /// the absent cancellation and the recorded instant, plus the session status — and changes no row when the
    /// annotation is no longer open or its class is suspended, so a double submission or a suspended class reports
    /// <c>false</c> instead of overwriting the first record or reviving a cancellation. The coverage and charge rules
    /// are the service's, not the statement's. It returns whether exactly one row changed.
    /// </summary>
    Task<bool> SetStatusAsync(
        int businessId, int bookingId, BookingStatus status, DateTime now,
        CancellationToken ct = default);

    /// <summary>
    /// Writes one charge. It joins the caller's transaction, so the payment commits with the attendance it
    /// accompanies and disappears with a rollback. The caller has already checked that no effective payment exists:
    /// this statement is the write, not the guard.
    /// </summary>
    Task InsertPaymentAsync(ClassAttendancePayment payment, CancellationToken ct = default);

    /// <summary>
    /// Adds one observation to the client's record, tied to the session it was taken in, exactly as the individual
    /// path writes it (RF-11 step 4). It returns whether the note landed: a tenant-scoped write that matches no client
    /// or no session answers <c>false</c>, and the caller fails the whole operation, so the observation is never lost
    /// silently. The service asks for this write before the status write and the charge, because it is the only write
    /// that can refuse for a reason the lock does not already rule out: a refusal must leave no persistent write
    /// behind, not even inside an outer transaction that its caller later commits.
    /// </summary>
    Task<bool> InsertNoteAsync(
        int businessId, int clientId, int appointmentId, string note, DateTime now, CancellationToken ct = default);
}
