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
    /// one service, each with the booking id, the client, the client name, the modality and the status of every one of
    /// its bookings, cancelled included, so the caller counts the places with its own rule. The implementation must
    /// filter <c>a.Kind = @classSession</c>: an individual
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
    /// Upcoming roster of the business (RF-22, RF-07 cupo &gt; 1): every <c>ClassSession</c> appointment of the
    /// business that starts strictly after <paramref name="fromExclusive"/>, each with only its drop-in annotations —
    /// cancelled ones included — as <see cref="ClassSessionBooking"/> rows carrying their booking id, modality,
    /// client and client name. An annotation whose modality is not <c>Suelta</c> is never part of the answer: the
    /// query filters <c>b.Modality = @dropIn</c>, so a fixed annotation of the same class can never reach the caller
    /// as if it were a drop-in. A session or a booking of another business is invisible, and so is an individual turn:
    /// the query carries the <c>a.Kind = @classSession</c> discriminant. A session without annotations is returned
    /// with an empty list, never dropped. Ordering is by start, so the caller can present the roster as it comes.
    /// </summary>
    Task<IReadOnlyList<ClassSessionRow>> ListUpcomingRosterAsync(
        int businessId, DateTime fromExclusive, CancellationToken ct = default);

    /// <summary>
    /// Locks the booking row of the business together with its session inside the caller's transaction, like
    /// <c>SELECT ... WITH (UPDLOCK, ROWLOCK)</c>, and returns the facts the cancellation rules read (RF-22). It
    /// returns <c>null</c> when the booking does not belong to the business, so a booking of another tenant and a
    /// booking that does not exist are the same answer and cannot be told apart. It does not filter the modality or
    /// the kind: the caller decides what to do with a booking that is not a drop-in of a class, and it must answer
    /// both cases with the same stable code.
    /// </summary>
    Task<DropInCancellationState?> LockDropInForCancellationAsync(
        int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>
    /// Marks the drop-in annotation <c>Cancelado</c>, with <c>CancelledAt = </c><paramref name="now"/> and
    /// <c>CancelledBy = Owner</c>, and reports whether a row actually changed. It runs inside the caller's transaction
    /// and is scoped by <c>BusinessId</c> and guarded by the same facts the caller just validated: the annotation must
    /// be <c>Modality = Suelta</c> and not cancelled yet, and its session must be a <c>ClassSession</c> that is still
    /// <c>Programado</c> and starts strictly after <paramref name="now"/>. The guard is not decorative: a row that
    /// stopped matching between the lock and this statement (a session suspended in between, for instance) is left
    /// untouched and reported as <c>false</c> instead of being cancelled anyway.
    /// <para>
    /// A concurrent double cancel cannot lose an update. The caller holds the <c>UPDLOCK</c> on the booking row for
    /// the whole transaction, so the second canceller waits and then reads the already cancelled annotation and is
    /// rejected before reaching this statement. And even without that lock the statement is a single atomic update
    /// whose predicate requires the pre-cancellation status: at most one execution can match the row, the loser
    /// changes zero rows and reports <c>false</c>, and <c>Status</c>, <c>CancelledAt</c> and <c>CancelledBy</c> are
    /// written together with no read-modify-write window between them.
    /// </para>
    /// </summary>
    Task<bool> CancelDropInAsync(int businessId, int bookingId, DateTime now, CancellationToken ct = default);

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
