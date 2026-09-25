using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Persistence boundary for the per-student attendance and absence of a generated class session (RF-11, RF-12, D-16,
/// D-20, D-23). Every method is scoped by <c>businessId</c>: a booking of another business is invisible and
/// indistinguishable from one that does not exist. Every command joins the caller's ambient transaction, so the lock,
/// the status write, the optional charge and the optional note are one unit that the service commits exactly once.
/// <para>
/// The implementations must let the same shape serve both operations: attendance and absence differ only in the
/// status they write and in the coverage rule the service applies, never in storage. The optional note is mandatory
/// to land: a note that could not be written answers <c>false</c> so the caller fails the whole operation and rolls
/// back instead of losing the observation silently.
/// </para>
/// </summary>
public interface IClassAttendanceRepository
{
    /// <summary>
    /// Locks the annotation row of the business inside the caller's transaction and reads it with its session, its
    /// client, the enrollment status and every paid period the coverage rule needs. The lock is what serializes two
    /// operations on the same booking — a double attendance with a charge, for instance — and it is held until the
    /// caller commits or rolls back. It returns <c>null</c> when the booking does not belong to the business, when it
    /// does not exist, or when it is missing its session: a foreign and a missing booking are the same answer, so
    /// nothing about another tenant's rows leaks. The kind and the modality are not filtered here: the service decides
    /// what to do with a booking that is not a class annotation, so the discriminant is never hidden by the port.
    /// </summary>
    Task<ClassAttendanceBookingState?> LockAsync(int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>
    /// Whether the annotation already has a payment that is still in force, so a second charge is never written. An
    /// annulled payment does not count: it is no longer a real charge (RF-26, D-20, and the same rule the individual
    /// path uses). The read is taken under the caller's lock, so it cannot be raced by a concurrent charge of the same
    /// booking.
    /// </summary>
    Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>
    /// Writes the status of the annotation together with the instant it was recorded, inside the caller's transaction.
    /// The guarded statement repeats every rule and changes no row when the annotation is no longer open, so a double
    /// submission reports <c>false</c> instead of overwriting the first record or reviving a cancellation. It returns
    /// whether exactly one row changed.
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
    /// or no session answers <c>false</c>, and the caller fails the whole operation and rolls back, so the observation
    /// is never lost silently.
    /// </summary>
    Task<bool> InsertNoteAsync(
        int businessId, int clientId, int appointmentId, string note, DateTime now, CancellationToken ct = default);
}
