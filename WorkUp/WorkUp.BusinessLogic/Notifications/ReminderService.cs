using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// Business core of the scheduled reminder (RF-17, D-22). It is invoked once per tick with the instant the T23 runner
/// read from <c>IClock</c>; the service never reads a clock of its own, so the real clock and the simulated T26 clock
/// work without changes.
/// <para>
/// The reminder is the strict complement of the immediate producers after commit <c>4223b63</c>. A booking created
/// exactly on the lead boundary was already covered by the immediate email, so the scheduled one starts strictly
/// outside it: <c>CreatedAt &lt; StartsAt - leadHours</c> and <c>StartsAt - leadHours &lt;= now &lt; StartsAt</c>. An
/// individual turn includes the current private link and asks for confirmation only while it is still
/// <c>Pendiente</c>; a <c>Confirmado</c> turn gets the same link with an informational body; a drop-in class booking
/// gets date, service and teacher only, never a confirmation ask and never a link.
/// </para>
/// <para>
/// The service trusts the scan only for the missing scheduled key: it enforces its own eligibility and link rules
/// before composing, and the guarded enqueue rechecks the live rows in one short transaction, so a booking cancelled
/// — or an immediate reminder produced — between the scan and the insert is a no-op instead of a duplicate email.
/// Nothing here logs: the raw token lives only inside <see cref="Notification.BodyHtml"/>, and
/// <see cref="Notification.LastError"/> is never written.
/// </para>
/// </summary>
public sealed class ReminderService(
    IReminderRepository repository,
    ILinkTokenProtector linkTokenProtector,
    IPublicAppointmentUrlBuilder appointmentUrlBuilder)
{
    /// <summary>Most candidates one tick processes, so a large backlog never blocks the scheduler loop.</summary>
    public const int BatchSize = 20;

    /// <summary>Subject of an individual reminder that still asks the client to confirm.</summary>
    public const string PendingSubject = "Confirmá tu turno";

    /// <summary>Subject of an individual reminder whose client already confirmed: informational only.</summary>
    public const string ConfirmedSubject = "Recordatorio de tu turno";

    /// <summary>Subject of a drop-in reminder: date and time only, never a confirmation ask.</summary>
    public const string DropInSubject = "Recordatorio de tu clase suelta";

    /// <summary>Phrase that marks the confirmation request of a pending individual reminder.</summary>
    public const string ConfirmationLead = "Confirmá tu intención de asistir";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Processes the batch emitted for <paramref name="now"/> and returns how many notices were enqueued. An
    /// individual booking without exactly one live private link is skipped: a missing, revoked, expired, multiplied or
    /// unprotectable link must never produce an email whose link cannot be used.
    /// </summary>
    public async Task<int> RunAsync(DateTime now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var candidates = await repository.ListCandidatesAsync(now, BatchSize, ct);
        var enqueued = 0;

        // The cap is enforced here too: a broken or permissive reader must not turn one tick into an unbounded write.
        foreach (var candidate in candidates.Take(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            if (!IsEligible(candidate, now))
                continue;

            var notice = Compose(candidate, now);
            if (notice is null)
                continue;

            if (await repository.TryEnqueueReminderAsync(notice, ct))
                enqueued++;
        }

        return enqueued;
    }

    /// <summary>
    /// Whether a candidate is still eligible at <paramref name="now"/> without touching storage. It is the same
    /// predicate the scan applies: a scheduled appointment, a live booking that is <c>Pendiente</c> or
    /// <c>Confirmado</c>, an individual or drop-in shape whose booking modality matches its appointment kind, no
    /// fixed enrollment, an upcoming start, and a creation strictly outside the configured anticipation whose lead
    /// instant already arrived.
    /// </summary>
    public static bool IsEligible(ReminderCandidate candidate, DateTime now)
    {
        if (candidate.BookingId <= 0 || candidate.BusinessId <= 0 || candidate.AppointmentId <= 0)
            return false;
        if (candidate.AppointmentStatus != AppointmentStatus.Scheduled)
            return false;
        if (candidate.Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
            return false;
        if (candidate.Modality is not (null or BookingModality.DropIn))
            return false;
        // Defense in depth: a fixed enrollment is never reminded, and a class booking must not arrive with no
        // modality or a turn that is not an individual appointment. Rejecting the shape here keeps this core in step
        // with the guards the storage layer must apply.
        if (candidate.EnrollmentId is not null)
            return false;
        var expectedKind = candidate.Modality is null
            ? AppointmentKind.Individual
            : AppointmentKind.ClassSession;
        if (candidate.Kind != expectedKind)
            return false;
        if (candidate.ReminderLeadHours <= 0)
            return false;
        if (candidate.StartsAt <= now)
            return false;

        var threshold = candidate.StartsAt - TimeSpan.FromHours(candidate.ReminderLeadHours);
        return now >= threshold && candidate.CreatedAt < threshold;
    }

    /// <summary>
    /// Builds the notice, or null when an individual booking has no exactly-one usable link. The two individual states
    /// share the body but differ in the confirmation request; a drop-in never carries a link or an ask.
    /// </summary>
    private ReminderNotice? Compose(ReminderCandidate candidate, DateTime now)
    {
        if (candidate.IsIndividual)
        {
            var link = SelectSingleLiveLink(candidate, now);
            if (link is null)
                return null;

            var token = TryReconstructToken(link);
            if (token is null)
                return null;

            var asksConfirmation = candidate.Status == BookingStatus.Pending;
            return new ReminderNotice(
                candidate.BookingId,
                candidate.AppointmentId,
                candidate.ReminderLeadHours,
                new ReminderLinkIdentity(link.LinkId, link.TokenHash),
                new Notification
                {
                    BusinessId = candidate.BusinessId,
                    Type = NotificationType.AppointmentReminder,
                    Recipient = candidate.Recipient,
                    Subject = asksConfirmation ? PendingSubject : ConfirmedSubject,
                    BodyHtml = BuildIndividualBody(candidate, appointmentUrlBuilder.Build(token), asksConfirmation),
                    DedupKey = $"reminder:booking:{candidate.BookingId}",
                    CreatedAt = now
                });
        }

        return new ReminderNotice(
            candidate.BookingId,
            candidate.AppointmentId,
            candidate.ReminderLeadHours,
            Link: null,
            new Notification
            {
                BusinessId = candidate.BusinessId,
                Type = NotificationType.DropInReminder,
                Recipient = candidate.Recipient,
                Subject = DropInSubject,
                BodyHtml = BuildDropInBody(candidate),
                DedupKey = $"reminder:booking:{candidate.BookingId}",
                CreatedAt = now
            });
    }

    /// <summary>Returns the single live link, or null when there are none or more than one.</summary>
    private static ReminderLink? SelectSingleLiveLink(ReminderCandidate candidate, DateTime now)
    {
        ReminderLink? single = null;
        foreach (var link in candidate.Links)
        {
            if (!link.IsLiveAt(now))
                continue;
            if (single is not null)
                return null;
            single = link;
        }

        return single;
    }

    /// <summary>
    /// Returns the raw token only when the protected token can be opened and the reconstructed token hashes back to the
    /// stored hash. Any failure returns null: the notice is skipped, and the caught failure text is deliberately
    /// dropped so nothing can leak into a log or an error column. An untrusted row can carry a null or short hash, so
    /// that too is a skip rather than a thrown reference error.
    /// </summary>
    private string? TryReconstructToken(ReminderLink link)
    {
        string token;
        try
        {
            token = linkTokenProtector.Unprotect(link.ProtectedToken);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(token) || link.TokenHash is null || link.TokenHash.Length != 32)
            return null;
        if (!CryptographicOperations.FixedTimeEquals(SecureToken.Hash(token), link.TokenHash))
            return null;

        return token;
    }

    private static string BuildIndividualBody(
        ReminderCandidate candidate, string appointmentUrl, bool asksConfirmation)
    {
        var safeBusinessName = WebUtility.HtmlEncode(candidate.BusinessName);
        var safeFirstName = WebUtility.HtmlEncode(candidate.ClientFirstName);
        var safeServiceName = WebUtility.HtmlEncode(candidate.ServiceName);
        var safeWorkerName = WebUtility.HtmlEncode(candidate.WorkerName);
        var safeUrl = WebUtility.HtmlEncode(appointmentUrl);
        var when = WebUtility.HtmlEncode(candidate.StartsAt.ToString("dddd d 'de' MMMM 'a las' HH:mm", Spanish));
        var lead = asksConfirmation
            ? $"Te recordamos tu turno. {ConfirmationLead} desde el enlace privado."
            : "Te recordamos tu turno. Ya nos confirmaste tu asistencia.";

        return $"<p>Hola {safeFirstName},</p><p>{lead}</p>" +
               $"<p>{safeServiceName} con {safeWorkerName}, {when}.</p>" +
               $"<p><a href=\"{safeUrl}\">Ver mi turno</a></p><p>{safeBusinessName}</p>";
    }

    private static string BuildDropInBody(ReminderCandidate candidate)
    {
        var safeBusinessName = WebUtility.HtmlEncode(candidate.BusinessName);
        var safeFirstName = WebUtility.HtmlEncode(candidate.ClientFirstName);
        var safeServiceName = WebUtility.HtmlEncode(candidate.ServiceName);
        var safeWorkerName = WebUtility.HtmlEncode(candidate.WorkerName);
        var when = WebUtility.HtmlEncode(candidate.StartsAt.ToString("dddd d 'de' MMMM 'a las' HH:mm", Spanish));

        return $"<p>Hola {safeFirstName},</p><p>Te recordamos tu clase suelta.</p>" +
               $"<p>{safeServiceName} con {safeWorkerName}, {when}.</p><p>{safeBusinessName}</p>";
    }
}
