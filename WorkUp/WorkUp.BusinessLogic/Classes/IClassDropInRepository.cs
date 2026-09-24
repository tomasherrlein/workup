using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Persistence boundary for booking a drop-in into an already generated class session (RF-07 cupo &gt; 1, RF-22,
/// D-25). Every method is scoped by <c>businessId</c>: a session or a client of another business is invisible to the
/// caller and is never leaked. The write methods run inside the caller's ambient transaction, which owns the commit,
/// so the whole booking is one unit and returning without committing rolls it back.
/// </summary>
public interface IClassDropInRepository
{
    /// <summary>
    /// Dated <c>ClassSession</c> rows of the business whose start day falls inside the inclusive date range
    /// <paramref name="fromInclusive"/>..<paramref name="toInclusive"/> (both ends included), optionally narrowed to
    /// one service, each with the client and status of every one of its bookings, cancelled included, so the caller
    /// counts the places with its own rule. The implementation must filter <c>a.Kind = @classSession</c>: an individual
    /// turn has capacity 1 and can also be <c>Programado</c>, so without the discriminant it would be offered as a
    /// class. The query does not filter by status or occupancy: the bookable window is a business rule, not a query
    /// shape.
    /// </summary>
    /// <remarks>
    /// The dates are inclusive on both ends, but <c>DATETIME2(0)</c> has no inclusive maximum instant for a whole
    /// day, so the SQL implementation must compare half-open instants instead:
    /// <c>StartsAt &gt;= fromInclusive 00:00</c> and <c>StartsAt &lt; (toInclusive + 1 day) 00:00</c>. The exclusive
    /// bound must be computed, never the day's last instant, which would widen the range.
    /// <para>
    /// That exclusive bound exists only while <paramref name="toInclusive"/> is a real day, so
    /// <see cref="DateOnly.MaxValue"/> is not a valid end date for any implementation. The service rejects it with
    /// <c>drop_in_invalid_request</c> before calling this method (see
    /// <c>ClassDropInService.ListOptionsAsync</c>); an implementation that still receives it must not widen the range
    /// silently.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ClassSessionRow>> ListSessionsAsync(
        int businessId, DateOnly fromInclusive, DateOnly toInclusive, int? serviceId, CancellationToken ct = default);

    /// <summary>
    /// Locks the appointment row of the class session inside the caller's transaction, like
    /// <c>SELECT ... WITH (UPDLOCK, ROWLOCK)</c>, and returns it with the live client and status of its bookings. The
    /// lock is taken on <c>a.Kind = @classSession</c> as well, so an individual turn can never be locked as a class.
    /// It returns <c>null</c> when the session does not belong to the business. Holding that row is what serializes
    /// two bookings competing for the last place: the count and the insert happen under the same lock.
    /// </summary>
    Task<ClassSessionRow?> LockSessionAsync(int businessId, int appointmentId, CancellationToken ct = default);

    /// <summary>
    /// Returns the client identified by <paramref name="email"/> inside the business, creating it without a password
    /// when it does not exist yet (the T09 flow, RF-07 step 5). An existing client is returned as it is, never
    /// duplicated and never updated: its stored email is the recipient of the drop-in notice. Tenant-scoped: the same
    /// email in another business is a different client, and it must never return a client of another business.
    /// <para>
    /// Two collisions are decided by the implementation, not by its caller. A concurrent creation of the same email
    /// must lose to the unique key on the business and email without throwing: the implementation catches the
    /// duplicate-key <c>SqlException</c> 2601/2627, re-reads the row it just failed to insert and returns that stored
    /// client, so the caller always receives a client from a case it asked to resolve, never an unhandled exception.
    /// And a client stored with a differently cased email than the one requested is still that same client: the
    /// returned <c>Email</c> is the stored value, which is what the caller must use as recipient.
    /// </para>
    /// </summary>
    Task<DropInClient> IdentifyClientAsync(
        int businessId, string email, string firstName, string lastName, string? phone, CancellationToken ct = default);

    /// <summary>
    /// Returns the client of the business identified by <paramref name="clientId"/>, or <c>null</c> when no such
    /// client exists in it. The owner-assisted path only accepts its own clients (RF-11), so it never creates one by
    /// mistake; and it needs the stored email and name, not only the id, to compose the drop-in notice.
    /// </summary>
    Task<DropInClient?> FindClientAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>
    /// Guarded drop-in insert: it must insert the booking only while the session is a <c>ClassSession</c> that still
    /// has a free place, and must do it atomically under the lock taken by <see cref="LockSessionAsync"/>. It writes
    /// the booking as <c>Modality = DropIn</c> and <c>Status = Pending</c>. The three-way result distinguishes an
    /// inserted booking (with its new id), a lost race for the last place, and the unique key
    /// <c>UX_Bookings_ActiveClient</c> firing because the same client already holds a non-cancelled booking, so the
    /// caller never receives a raw unique-key exception. It runs inside the caller's transaction, which owns the commit.
    /// </summary>
    Task<DropInInsertResult> TryInsertDropInAsync(DropInInsert insert, CancellationToken ct = default);

    /// <summary>
    /// Queues an email in the outbox inside the caller's transaction. It never sends the email: delivery belongs to
    /// the notification work (RF-17), and the unique <see cref="Notification.DedupKey"/> stops a retry from
    /// duplicating it. The caller composes the full <see cref="Notification"/> (recipient, subject and body): the
    /// <c>Notifications.Recipient</c>, <c>Subject</c> and <c>BodyHtml</c> columns are not nullable, so the data layer
    /// only stores what the service composed.
    /// </summary>
    Task EnqueueNotificationAsync(Notification notification, CancellationToken ct = default);
}
