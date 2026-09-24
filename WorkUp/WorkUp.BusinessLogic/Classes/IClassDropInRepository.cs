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
    /// Dated class sessions of the business whose start falls inside the inclusive range,
    /// <paramref name="from"/>..<paramref name="to"/>, optionally narrowed to one service, each with the status of
    /// every one of its bookings, cancelled included, so the caller counts the places with its own rule. The query
    /// does not filter by status or occupancy: the bookable window is a business rule, not a query shape.
    /// </summary>
    Task<IReadOnlyList<ClassSessionRow>> ListSessionsAsync(
        int businessId, DateTime from, DateTime to, int? serviceId, CancellationToken ct = default);

    /// <summary>
    /// Locks the appointment row of the session inside the caller's transaction, like
    /// <c>SELECT ... WITH (UPDLOCK, ROWLOCK)</c>, and returns it with the live statuses of its bookings. It returns
    /// <c>null</c> when the session does not belong to the business. Holding that row is what serializes two
    /// bookings competing for the last place: the count and the insert happen under the same lock.
    /// </summary>
    Task<ClassSessionRow?> LockSessionAsync(int businessId, int appointmentId, CancellationToken ct = default);

    /// <summary>
    /// Returns the client identified by <paramref name="email"/> inside the business, creating it without a password
    /// when it does not exist yet (the T09 flow, RF-07 step 5). An existing client is returned as it is, never
    /// duplicated and never updated. Tenant-scoped: the same email in another business is a different client.
    /// </summary>
    Task<DropInClient> IdentifyClientAsync(
        int businessId, string email, string firstName, string lastName, string? phone, CancellationToken ct = default);

    /// <summary>
    /// Whether the client already belongs to the business. The owner-assisted path only accepts its own clients
    /// (RF-11), so it never creates one by mistake.
    /// </summary>
    Task<bool> ClientExistsAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>
    /// Guarded drop-in insert: it must insert the booking only while the session still has a free place, and must do
    /// it atomically under the lock taken by <see cref="LockSessionAsync"/>. It returns the new booking id, or
    /// <c>null</c> when it did not insert because no place remained, which is how a lost race for the last place is
    /// reported back. It runs inside the caller's transaction, which owns the commit.
    /// </summary>
    Task<int?> TryInsertDropInAsync(DropInInsert insert, CancellationToken ct = default);

    /// <summary>
    /// Queues an email in the outbox inside the caller's transaction. It never sends the email: delivery belongs to
    /// the notification work (RF-17), and the unique <see cref="DropInNotification.DedupKey"/> stops a retry from
    /// duplicating it.
    /// </summary>
    Task EnqueueNotificationAsync(DropInNotification notification, CancellationToken ct = default);
}
