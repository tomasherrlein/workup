using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// One private link of a candidate booking, reduced to what the scheduled reminder (RF-17) needs: the protected token
/// to reconstruct the URL, the hash that proves the reconstructed token is the stored one, and the liveness fields.
/// The raw token is deliberately absent from this row: it exists only after <c>ILinkTokenProtector.Unprotect</c> and
/// must never be carried around or logged. <see cref="TokenHash"/> is treated as untrusted: a malformed row can carry
/// <c>null</c> or a hash of the wrong length, and both must be a skip, never a null reference.
/// </summary>
public sealed record ReminderLink(
    int LinkId,
    byte[] TokenHash,
    string ProtectedToken,
    DateTime ExpiresAt,
    DateTime? RevokedAt)
{
    /// <summary>
    /// Live means neither revoked nor expired at <paramref name="now"/>, the exact rule of
    /// <see cref="AppointmentLink.IsUsable"/>.
    /// </summary>
    public bool IsLiveAt(DateTime now) => RevokedAt is null && now < ExpiresAt;
}

/// <summary>
/// Identity of the exact private link a notice was composed from. The guarded enqueue must require that this same
/// link is the single live one at the run instant; a replacement that appears between the scan and the insert is a
/// different identity, so its URL must never be the one already written into the body.
/// </summary>
public sealed record ReminderLinkIdentity(int LinkId, byte[] TokenHash);

/// <summary>
/// Snapshot of one booking the scheduled reminder (RF-17) may reach, with everything the notice needs so the core
/// never reads storage twice. <see cref="Kind"/> and <see cref="EnrollmentId"/> let the service reject a malformed
/// candidate the scan should never have returned — a fixed enrollment, or a class session carrying no modality —
/// which is the same shape the storage layer must guard, so the two layers cannot drift apart.
/// </summary>
public sealed record ReminderCandidate(
    int BookingId,
    int BusinessId,
    int AppointmentId,
    AppointmentKind Kind,
    BookingModality? Modality,
    int? EnrollmentId,
    BookingStatus Status,
    AppointmentStatus AppointmentStatus,
    DateTime StartsAt,
    DateTime CreatedAt,
    int ReminderLeadHours,
    string Recipient,
    string ClientFirstName,
    string BusinessName,
    string ServiceName,
    string WorkerName,
    IReadOnlyList<ReminderLink> Links)
{
    /// <summary>An individual turn has no modality; a drop-in carries <see cref="BookingModality.DropIn"/>.</summary>
    public bool IsIndividual => Modality is null;
}

/// <summary>
/// One reminder to queue together with the facts the guarded enqueue rechecks.
/// <see cref="Notification.CreatedAt"/> is the run instant the T23 runner supplied: the guarded insert re-derives the
/// lead window from it, so the core never reads a clock of its own. The scheduled key is
/// <c>reminder:booking:{BookingId}</c>. <see cref="Link"/> is the exact private link an individual body was built
/// from and stays <c>null</c> for a drop-in, which carries no link at all.
/// </summary>
public sealed record ReminderNotice(
    int BookingId,
    int AppointmentId,
    int ReminderLeadHours,
    ReminderLinkIdentity? Link,
    Notification Notification);

/// <summary>
/// Persistence boundary of the scheduled reminder (RF-17). Every call is bounded and the enqueue is the only write.
/// <para>
/// <see cref="ListCandidatesAsync"/> is the read half of the strict complement of the immediate producers after
/// commit <c>4223b63</c>: it returns only bookings whose owner email the immediate path did <b>not</b> already take,
/// i.e. those created strictly outside the configured window
/// (<c>CreatedAt &lt; StartsAt - leadHours</c>) whose lead instant already arrived
/// (<c>StartsAt - leadHours &lt;= now &lt; StartsAt</c>). A booking created exactly on the boundary is immediate and
/// must never appear here. The scan must also exclude fixed enrollments (<c>EnrollmentId</c> not null or
/// <c>Modality = Fija</c>), cancelled/attended/absent bookings, suspended appointments, individual bookings that
/// already own an immediate <see cref="NotificationType.AppointmentReminder"/> under key <c>booking:{id}</c>, and
/// every booking that already owns the scheduled key <c>reminder:booking:{id}</c>. A
/// <see cref="NotificationType.BookingCreated"/> under key <c>booking:{id}</c> must <b>not</b> suppress the later
/// scheduled notice. Ordered by start, then id, and capped at <paramref name="batchSize"/> so one tick stays bounded.
/// </para>
/// <para>
/// <see cref="TryEnqueueReminderAsync"/> is <b>one short SQL transaction</b>: it re-derives the same predicates
/// against the <em>current</em> rows — business scope, appointment kind against booking modality, no fixed
/// enrollment, status, suspension, the lead window at <see cref="Notification.CreatedAt"/>, the immediate-reminder
/// type above, and, for an individual booking, that the booking still has <b>exactly one</b> live private link and
/// that it is the <b>same</b> <see cref="ReminderNotice.Link"/> the body was built from — and inserts the notice only
/// when all of them still hold. The insert is guarded by <c>UQ_Notifications_DedupKey</c>, so a concurrent producer
/// or a retried tick is a <c>false</c> no-op rather than a second email. It never writes
/// <see cref="Notification.LastError"/>.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake cannot prove the atomic guard or the unique-key race; only a SQL fixture can. The
/// fake proves the service contract, never the runtime transaction.
/// </para>
/// </summary>
public interface IReminderRepository
{
    /// <summary>
    /// Returns at most <paramref name="batchSize"/> eligible candidates as of <paramref name="now"/>. The predicate is
    /// documented on the interface: bookings created strictly outside the configured anticipation, inside the lead
    /// instant, still upcoming, not fixed, not cancelled/attended/absent, on a scheduled appointment, with no
    /// scheduled reminder yet and no individual immediate reminder for the same booking.
    /// </summary>
    Task<IReadOnlyList<ReminderCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Atomically rechecks the guard described on the interface and inserts
    /// <see cref="ReminderNotice.Notification"/> when the booking is still eligible. Returns <c>true</c> only when a
    /// row was inserted; <c>false</c> when any guard fails, the scheduled key already exists, an individual immediate
    /// reminder appeared after the scan, or the individual's single live link is no longer the reviewed identity.
    /// </summary>
    Task<bool> TryEnqueueReminderAsync(ReminderNotice notice, CancellationToken ct = default);
}
