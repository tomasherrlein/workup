using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// One still-active booking of a target-day appointment, reduced to what the owner summary shows: the client name and
/// the booking state. Health notes, payment details and private links are deliberately absent from this row, so the
/// summary is structurally unable to leak them (RF-18).
/// </summary>
public sealed record DailySummaryBooking(int BookingId, string ClientName, BookingStatus Status);

/// <summary>
/// One appointment of the target agenda day: an individual turn or a class session, with its still-active bookings
/// only. The scan already drops suspended appointments, rows outside the target day and every inactive booking, and it
/// additionally drops an <b>individual</b> turn left with no <c>Pendiente</c>/<c>Confirmado</c> booking: an individual
/// row with nobody annotated is not part of the agenda and must not be presented as an empty turn. A class session is
/// kept even when it has no active booking, because the class itself is still a planned row of the day, and it arrives
/// with an empty <see cref="Bookings"/> list. The service re-derives this same predicate through
/// <c>DailySummaryService.VisibleAppointments</c>, so a permissive reader cannot inject a row the day does not have.
/// </summary>
public sealed record DailySummaryAppointment(
    int AppointmentId,
    DateTime StartsAt,
    DateTime EndsAt,
    AppointmentKind Kind,
    AppointmentStatus Status,
    string ServiceName,
    string WorkerName,
    IReadOnlyList<DailySummaryBooking> Bookings);

/// <summary>
/// One business the daily summary (RF-18) may reach, with everything the notice needs so the core never reads storage
/// twice. <see cref="Recipient"/> is the business notice email and the only destination the summary may use: the phone
/// in <c>Businesses.ContactPhone</c> is a contact datum, never a delivery channel (plan assumption 3).
/// <see cref="ConfiguredTime"/> is the business's <c>DailySummaryTime</c> (21:00 by default, RF-01) and
/// <see cref="Appointments"/> holds only the target day's scheduled rows.
/// </summary>
public sealed record DailySummaryCandidate(
    int BusinessId,
    string Recipient,
    string BusinessName,
    TimeOnly ConfiguredTime,
    IReadOnlyList<DailySummaryAppointment> Appointments);

/// <summary>
/// One summary to queue together with the facts the guarded enqueue rechecks:
/// <see cref="Notification.CreatedAt"/> is the instant the T23 runner supplied, <see cref="ConfiguredTime"/> is the
/// business time the notice was composed for, and <see cref="Notification.Recipient"/> is the recipient that must
/// still be the current one. The scheduled key is <c>summary:business:{BusinessId}:{yyyyMMdd}</c> over the
/// <b>target</b> day, so a retried or repeated tick on the same day is a no-op while the next day's key differs.
/// </summary>
public sealed record DailySummaryNotice(
    int BusinessId,
    TimeOnly ConfiguredTime,
    Notification Notification);

/// <summary>
/// Persistence boundary of the daily summary (RF-18). Every call is bounded and the enqueue is the only write.
/// <para>
/// <see cref="ListCandidatesAsync"/> returns at most <paramref name="batchSize"/> businesses whose configured time
/// already arrived on the calendar day of <paramref name="now"/> (<c>now.TimeOfDay &gt;= DailySummaryTime</c>), whose
/// notice email is present and well-formed, and whose target key <c>summary:business:{id}:{yyyyMMdd}</c> for the
/// <b>next</b> day does not exist yet. Each candidate carries the target day's appointments that are still
/// <c>Scheduled</c> and, inside them, only the <c>Pendiente</c>/<c>Confirmado</c> bookings; suspended appointments and
/// cancelled/attended/absent bookings are excluded. On top of that, an <b>individual</b> turn is carried only while it
/// still has at least one active booking — an individual row with nobody annotated is not part of the agenda — while a
/// <b>class session</b> is carried even with no active booking, because the class itself is still planned. The scan is
/// tenant-scoped per business but global across tenants so one tick serves every business. A business without a usable
/// notice email is never returned, because RF-18 has no phone or fallback channel (plan assumption 3). Ordered by
/// business id and capped so one tick stays bounded. A day whose opportunity already passed is never resurrected: a
/// summary for <c>D+1</c> belongs to the calendar day <c>D</c> and only to it, and the last representable calendar
/// day has no next day at all.
/// </para>
/// <para>
/// <see cref="TryEnqueueSummaryAsync"/> is <b>one short SQL transaction</b>: it re-reads the business and inserts the
/// notice only when the recipient is still the one the body was composed for, the configured time is unchanged, the
/// configured time already arrived at <see cref="Notification.CreatedAt"/>, and the target dedup key is still absent.
/// The insert is guarded by <c>UQ_Notifications_DedupKey</c>, so a concurrent tick or a retried one is a
/// <c>false</c> no-op rather than a second email. It never writes <see cref="Notification.LastError"/>.
/// </para>
/// <para>
/// The row predicate above is a scan obligation, not a trust boundary: the service independently re-derives it from
/// the candidate before composing, so a reader that over-returns suspended appointments, rows outside the target day
/// or inactive bookings cannot leak them into a body.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake cannot prove the atomic guard or the unique-key race; only a SQL fixture can. The fake
/// proves the service contract, never the runtime transaction.
/// </para>
/// </summary>
public interface IDailySummaryRepository
{
    /// <summary>
    /// Returns at most <paramref name="batchSize"/> eligible businesses as of <paramref name="now"/>, each with the
    /// target day's scheduled rows and their active bookings. The predicate is documented on the interface: configured
    /// time already reached, usable notice email, no summary yet for the target day.
    /// </summary>
    Task<IReadOnlyList<DailySummaryCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Atomically rechecks the guard described on the interface and inserts
    /// <see cref="DailySummaryNotice.Notification"/> when the business is still eligible. Returns <c>true</c> only when
    /// a row was inserted; <c>false</c> when the business vanished, its recipient or configured time changed after the
    /// scan, the composed instant is before the configured time, or the target key already exists.
    /// </summary>
    Task<bool> TryEnqueueSummaryAsync(DailySummaryNotice notice, CancellationToken ct = default);
}
