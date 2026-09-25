namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Persistence boundary for the read-only detail of one class session (RF-10, RF-11, RF-12, RF-22, D-15, D-16,
/// D-23). Every method is scoped by <c>businessId</c>: a session of another business is invisible to the caller and is
/// indistinguishable from one that does not exist. The port only reads; it never writes, so it takes no transaction
/// and the whole detail is a pure projection of the stored rows.
/// </summary>
public interface IClassDetailRepository
{
    /// <summary>
    /// Returns the class session of the business identified by <paramref name="appointmentId"/> together with every
    /// booking it holds, cancelled ones included, each with the client's name and email, the modality and — for the
    /// fixed ones — the enrollment status and its membership periods. It returns <c>null</c> when the session does not
    /// belong to the business, when it does not exist, or when it is not a class session, so a foreign, a missing and
    /// an individual appointment are all the same answer: not found.
    /// <para>
    /// Cancelled annotations travel with their status; the implementation must not filter them out, because the owner
    /// has to see who released a place and the place count has to know what frees it. The read carries the
    /// discriminant <c>Kind = ClassSession</c>, and the tenant rides on the session, the booking, the client and the
    /// enrollment joins. The membership-periods join is the one exception: <c>MembershipPeriods</c> has no
    /// <c>BusinessId</c> of its own, so it inherits the scope of the business-scoped enrollment it hangs off instead
    /// of carrying the business on both sides. A booking whose client row cannot be resolved still comes back with an
    /// empty name and an empty email instead of being dropped from the answer; the email is what the per-student
    /// suspension notice is sent to.
    /// </para>
    /// </summary>
    Task<ClassDetailSessionRow?> GetSessionAsync(
        int businessId, int appointmentId, CancellationToken ct = default);
}
