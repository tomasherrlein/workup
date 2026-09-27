using System.Globalization;
using System.Net;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// Business core of the daily owner agenda summary (RF-18, plan assumption 3). It is invoked once per tick with the
/// instant the T23 runner read from <c>IClock</c>; the service never reads a clock of its own, so the real clock and
/// the simulated T26 clock work without changes. The instant arrives as local business time (assumption 12) and is
/// used as-is: no timezone conversion happens here.
/// <para>
/// One business is served at most once per target day. The summary of the day <c>D+1</c> belongs to the calendar day
/// <c>D</c>: as soon as the configured <c>DailySummaryTime</c> has been reached, <c>now</c> on day <c>D</c> composes
/// it. A tick that missed the exact minute is still served later that same day, and midnight ends the opportunity — a
/// summary that was never sent for <c>D+1</c> is <b>not</b> backfilled the next day, because on <c>D+1</c> the target
/// is <c>D+2</c>. The once-per-day guarantee is the key
/// <c>summary:business:{BusinessId}:{yyyyMMdd}</c> over the <b>target</b> day.
/// </para>
/// <para>
/// The recipient is the business notice email and only it: a missing or malformed email skips the business, and the
/// contact phone is never a fallback (RF-18 speaks of a phone, but WhatsApp/SMS is out of scope; assumption 3 routes
/// owner notices to email). The body carries tomorrow's scheduled turns and class sessions with their active bookings
/// encoded for HTML; notes, payment details and private links are not part of the contract at all and therefore never
/// appear. Nothing here logs: no raw body or recipient reaches an error channel, and
/// <see cref="Notification.LastError"/> is never written.
/// </para>
/// <para>
/// The service trusts the scan only for the missing target key: it enforces its own eligibility and recipient rules
/// before composing, and the guarded enqueue rechecks the live rows in one short transaction, so a recipient or
/// configured time edited, or a summary already produced, between the scan and the insert is a no-op instead of a
/// stale or duplicate email.
/// </para>
/// </summary>
public sealed class DailySummaryService(IDailySummaryRepository repository)
{
    /// <summary>Most businesses one tick processes, so a large tenant set never blocks the scheduler loop.</summary>
    public const int BatchSize = 20;

    /// <summary>Subject prefix of the summary; the target date is appended.</summary>
    public const string SubjectPrefix = "Resumen de agenda";

    /// <summary>Explicit wording of an agenda with no scheduled appointment, never an empty body.</summary>
    public const string EmptyAgendaText = "sin turnos ni clases";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Processes the batch emitted for <paramref name="now"/> and returns how many notices were enqueued. A business
    /// whose notice email is blank or malformed is skipped: RF-18 has no phone or fallback channel, so an unusable
    /// recipient must produce nothing rather than the wrong email. The last representable calendar day returns
    /// <c>0</c> without touching the repository, because it has no next day to summarize.
    /// </summary>
    public async Task<int> RunAsync(DateTime now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // RF-18 summarizes the next local day, so the last representable calendar day has nothing to summarize. The
        // repository is not scanned at all: no amount of over-return could produce a valid target day from it.
        if (DateOnly.FromDateTime(now) == DateOnly.MaxValue)
            return 0;

        var candidates = await repository.ListCandidatesAsync(now, BatchSize, ct);
        var enqueued = 0;

        // The cap is enforced here too: a broken or permissive reader must not turn one tick into an unbounded write.
        foreach (var candidate in candidates.Take(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            if (!IsEligible(candidate, now))
                continue;

            if (await repository.TryEnqueueSummaryAsync(Compose(candidate, now), ct))
                enqueued++;
        }

        return enqueued;
    }

    /// <summary>
    /// Agenda day a run is about: the day after the calendar day of <paramref name="now"/>. RF-18 summarizes "el día
    /// siguiente", so a run on <c>D</c> prepares <c>D+1</c> and a run on <c>D+1</c> prepares <c>D+2</c> — the missed
    /// <c>D+1</c> agenda is never resurrected. The last representable calendar day has no next day, so the target
    /// saturates at <see cref="DateOnly.MaxValue"/> instead of overflowing; <see cref="RunAsync"/> refuses to scan that
    /// instant, so the saturated value never becomes a real notice.
    /// </summary>
    public static DateOnly TargetDate(DateTime now) =>
        DateOnly.FromDateTime(now.Date == DateTime.MaxValue.Date ? now.Date : now.Date.AddDays(1));

    /// <summary>Unique scheduled key of one business summary for a target day: <c>summary:business:{id}:{yyyyMMdd}</c>.</summary>
    public static string DedupKey(int businessId, DateOnly target) =>
        $"summary:business:{businessId}:{target.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Whether a candidate is eligible at <paramref name="now"/> without touching storage: a real business with a
    /// usable notice email whose configured time already arrived on the calendar day of <paramref name="now"/>. This is
    /// the same predicate the scan applies, so the two layers cannot drift apart.
    /// </summary>
    public static bool IsEligible(DailySummaryCandidate candidate, DateTime now)
    {
        if (candidate.BusinessId <= 0)
            return false;
        if (!EmailFormat.IsValid(candidate.Recipient))
            return false;
        return TimeOnly.FromDateTime(now) >= candidate.ConfiguredTime;
    }

    /// <summary>
    /// The rows the body may show, re-derived from the candidate so a permissive reader cannot inject a row that is
    /// not part of the agenda: only a <c>Scheduled</c> appointment on the target day is kept, an <b>individual</b> turn
    /// additionally needs at least one <c>Pendiente</c>/<c>Confirmado</c> booking — an individual row with nobody
    /// annotated is not part of the day — a class session is kept even with no booking because the class itself is
    /// still planned, and every appointment keeps only its active bookings. Ordered by start, then id, so the body
    /// never depends on the reader's ordering.
    /// </summary>
    public static IReadOnlyList<DailySummaryAppointment> VisibleAppointments(
        DailySummaryCandidate candidate, DateTime now)
    {
        var target = TargetDate(now);
        return candidate.Appointments
            .Where(appointment => appointment.Status == AppointmentStatus.Scheduled)
            .Where(appointment => DateOnly.FromDateTime(appointment.StartsAt) == target)
            .Select(appointment => appointment with
            {
                Bookings = appointment.Bookings
                    .Where(booking => booking.Status is BookingStatus.Pending or BookingStatus.Confirmed)
                    .OrderBy(booking => booking.BookingId)
                    .ToArray()
            })
            .Where(appointment => appointment.Kind == AppointmentKind.ClassSession || appointment.Bookings.Count > 0)
            .OrderBy(appointment => appointment.StartsAt)
            .ThenBy(appointment => appointment.AppointmentId)
            .ToArray();
    }

    /// <summary>
    /// Builds the notice. <see cref="Notification.CreatedAt"/> is the run instant the T23 runner supplied; the key is
    /// derived from the target day, never from the business time, so a late same-day tick and a punctual one share it.
    /// </summary>
    private DailySummaryNotice Compose(DailySummaryCandidate candidate, DateTime now)
    {
        var target = TargetDate(now);
        return new DailySummaryNotice(
            candidate.BusinessId,
            candidate.ConfiguredTime,
            new Notification
            {
                BusinessId = candidate.BusinessId,
                Type = NotificationType.DailySummary,
                Recipient = candidate.Recipient,
                Subject = $"{SubjectPrefix}: {target.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}",
                BodyHtml = BuildBody(candidate, now),
                DedupKey = DedupKey(candidate.BusinessId, target),
                CreatedAt = now
            });
    }

    /// <summary>
    /// Renders the owner-facing body from the re-derived rows. Every name is HTML-encoded because it is business data
    /// a client can influence; an agenda with no visible row says so explicitly instead of sending an empty body.
    /// </summary>
    private static string BuildBody(DailySummaryCandidate candidate, DateTime now)
    {
        var visible = VisibleAppointments(candidate, now);
        var safeBusinessName = WebUtility.HtmlEncode(candidate.BusinessName);
        var when = WebUtility.HtmlEncode(TargetDate(now).ToString("dddd d 'de' MMMM", Spanish));
        var lines = visible.Count == 0
            ? $"<p>{EmptyAgendaText}</p>"
            : string.Concat(visible.Select(RenderAppointment));

        return $"<p>Agenda del {when}.</p>{lines}<p>{safeBusinessName}</p>";
    }

    /// <summary>Renders one appointment: hour, kind, service, worker and the active client names of that row.</summary>
    private static string RenderAppointment(DailySummaryAppointment appointment)
    {
        var time = WebUtility.HtmlEncode(appointment.StartsAt.ToString("HH:mm", CultureInfo.InvariantCulture));
        var serviceName = WebUtility.HtmlEncode(appointment.ServiceName);
        var workerName = WebUtility.HtmlEncode(appointment.WorkerName);
        var kind = appointment.Kind == AppointmentKind.ClassSession ? "Clase" : "Turno";
        var clients = appointment.Bookings.Count == 0
            ? "sin anotaciones"
            : string.Join(", ", appointment.Bookings.Select(booking => WebUtility.HtmlEncode(booking.ClientName)));

        return $"<p><strong>{time}</strong> {kind}: {serviceName} con {workerName} — {clients}</p>";
    }
}
