using System.Globalization;
using System.Net;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// Business core of the membership expiry notice (RF-29, RF-30, D-22, D-23). It is invoked once per tick with the
/// instant the T23 runner read from <c>IClock</c>; the service never reads a clock of its own, so the real clock and
/// the simulated T26 clock work without changes. The instant arrives as local business time and is used as-is: no
/// timezone conversion happens here.
/// <para>
/// Only the current/latest paid period of an <c>Active</c> enrollment is noticed. A pending enrollment with no
/// paid period, an annulled or terminated enrollment, and an old period that a newer one already superseded are
/// never reached, so a clock rewind or a missed tick cannot resurrect an obsolete period. The expiry date is the first
/// day without coverage (RF-29 step 2), so the period covers up to the day before it. Every persisted period is valid
/// coverage: annulling a payment is a purely financial correction (T18) that keeps the coverage of the period it paid,
/// so no notice depends on <c>Payments.IsAnnulled</c>.
/// </para>
/// <para>
/// Two stages, each once per period. The <c>Expiring</c> stage covers the local date range
/// <c>[ExpiresOn - 3, ExpiresOn)</c>, which is the three days RF-30 step 1 warns about plus a late tick that missed the
/// exact day while the period is still in force. The <c>Expired</c> stage is due on the local expiry date and on that
/// date only: a missed minute is caught up the same day, and a later day never serves it, so the expired email is never
/// a catch-up for a long-past period. It is refused when the enrollment was renewed to a newer period. Each stage has
/// its own key <c>membership:enrollment:{EnrollmentId}:period:{PeriodId}:expiring|expired</c>, so a period can produce
/// both notices in order and never a daily repetition. An expired notice for a period also suppresses the earlier
/// expiring one, so a rewound clock cannot mail the pre-expiry reminder after the expiry notice.
/// </para>
/// <para>
/// The recipient is the client record email (<c>Clients.Email</c>) of the same business and only it: an account email,
/// a contact-form address or any other address is never a destination. The body carries the student name, the expiry
/// date and the business name, HTML-encoded; it never includes a private link, payment or health data, and it never
/// announces an automatic release of the fixed seat, because conservation and release are manual under RF-29. Nothing
/// here logs: no raw body or recipient reaches an error channel, and <see cref="Notification.LastError"/> is never
/// written.
/// </para>
/// <para>
/// The service trusts the scan only for the missing key: it enforces its own eligibility and stage rules before
/// composing, and the guarded enqueue rechecks the live rows in one short transaction, so a renewal, termination or
/// email change between the scan and the insert is a no-op instead of a stale or duplicate email.
/// </para>
/// </summary>
public sealed class MembershipExpiryService(IMembershipExpiryRepository repository)
{
    /// <summary>Most enrollments one tick processes, so a large tenant set never blocks the scheduler loop.</summary>
    public const int BatchSize = 20;

    /// <summary>Subject of the three-days-before reminder (RF-30 step 1).</summary>
    public const string ExpiringSubject = "Tu abono vence pronto";

    /// <summary>Subject of the at-expiry notice (RF-30 step 2).</summary>
    public const string ExpiredSubject = "Tu abono venció";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Processes the batch emitted for <paramref name="now"/> and returns how many notices were enqueued. A period
    /// whose client record email is blank or malformed is skipped: an unusable recipient must produce nothing rather
    /// than the wrong email.
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

            var stage = StageAt(candidate.ExpiresOn, now)!.Value;
            if (await repository.TryEnqueueNoticeAsync(Compose(candidate, now, stage), ct))
                enqueued++;
        }

        return enqueued;
    }

    /// <summary>
    /// Builds the notice. <see cref="Notification.CreatedAt"/> is the run instant the T23 runner supplied and the key
    /// is derived from the period and stage, never from the date alone, so a late tick inside the same window and a
    /// punctual one share it. The notice carries the period id and expiry the body was composed from, so the guarded
    /// enqueue can reject a renewal or email change made between the scan and the insert.
    /// </summary>
    private static MembershipExpiryNotice Compose(
        MembershipExpiryCandidate candidate, DateTime now, MembershipExpiryStage stage) =>
        new(
            candidate.EnrollmentId,
            candidate.ClientId,
            candidate.BusinessId,
            candidate.PeriodId,
            candidate.ExpiresOn,
            stage,
            new Notification
            {
                BusinessId = candidate.BusinessId,
                Type = TypeFor(stage),
                Recipient = candidate.Recipient,
                Subject = stage == MembershipExpiryStage.Expiring ? ExpiringSubject : ExpiredSubject,
                BodyHtml = BuildBody(candidate, stage),
                DedupKey = DedupKey(candidate.EnrollmentId, candidate.PeriodId, stage),
                CreatedAt = now
            });

    /// <summary>
    /// Renders the student-facing body from the re-derived fields. Every user-influenced value is HTML-encoded. The
    /// body carries only the student name, the expiry date and the business name: no private link, no payment or
    /// health datum, and no wording about an automatic release of the fixed seat, because conservation and release are
    /// manual under RF-29.
    /// </summary>
    private static string BuildBody(MembershipExpiryCandidate candidate, MembershipExpiryStage stage)
    {
        var safeName = WebUtility.HtmlEncode(candidate.ClientName);
        var safeBusiness = WebUtility.HtmlEncode(candidate.BusinessName);
        var date = WebUtility.HtmlEncode(candidate.ExpiresOn.ToString("dd/MM/yyyy", Spanish));
        var lead = stage == MembershipExpiryStage.Expiring
            ? $"Tu abono vence el {date}."
            : $"Tu abono venció el {date}.";

        return $"<p>Hola {safeName},</p><p>{lead}</p><p>{safeBusiness}</p>";
    }

    /// <summary>
    /// RF-30 stage of a period at <paramref name="now"/>, or <c>null</c> when it is not due. The expiring window is the
    /// local date range <c>[ExpiresOn - 3, ExpiresOn)</c>. The expired stage is due on the local expiry date and on that
    /// date only: it starts exactly at <paramref name="expiresOn"/> and ends when the calendar day ends, so a missed
    /// minute is caught up the same day and a later day never serves it. The arithmetic uses
    /// <see cref="DateOnly.DayNumber"/> only, so neither the last nor the first representable day can overflow.
    /// </summary>
    public static MembershipExpiryStage? StageAt(DateOnly expiresOn, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (today.DayNumber == expiresOn.DayNumber)
            return MembershipExpiryStage.Expired;
        if (today.DayNumber < expiresOn.DayNumber
            && expiresOn.DayNumber - today.DayNumber <= MembershipListRules.ExpiresSoonThresholdDays)
        {
            return MembershipExpiryStage.Expiring;
        }

        return null;
    }

    /// <summary>
    /// Unique scheduled key of one stage of one paid period:
    /// <c>membership:enrollment:{enrollmentId}:period:{periodId}:expiring|expired</c>. The period id, not the date, is
    /// the identity, so a period that expires on the same day as another still gets its own key.
    /// </summary>
    public static string DedupKey(int enrollmentId, int periodId, MembershipExpiryStage stage) =>
        $"membership:enrollment:{enrollmentId}:period:{periodId}:{StageKey(stage)}";

    /// <summary>The canonical notification type of a stage: <c>MembershipExpiring</c> or <c>MembershipExpired</c>.</summary>
    public static NotificationType TypeFor(MembershipExpiryStage stage) => stage == MembershipExpiryStage.Expiring
        ? NotificationType.MembershipExpiring
        : NotificationType.MembershipExpired;

    /// <summary>
    /// Whether a candidate is eligible at <paramref name="now"/> without touching storage: a real, still active
    /// enrollment whose current/latest period has not been superseded, whose client record email is usable, and
    /// whose stage is due. This is the same predicate the scan applies, so the two layers cannot drift apart, and it
    /// rejects a permissive reader that over-returns an inactive enrollment or a stale period.
    /// </summary>
    public static bool IsEligible(MembershipExpiryCandidate candidate, DateTime now)
    {
        if (candidate.EnrollmentId <= 0 || candidate.ClientId <= 0 || candidate.BusinessId <= 0)
            return false;
        if (candidate.PeriodId <= 0)
            return false;
        if (candidate.Status != EnrollmentStatus.Active)
            return false;
        if (candidate.HasLaterPeriod)
            return false;
        if (!EmailFormat.IsValid(candidate.Recipient))
            return false;
        return StageAt(candidate.ExpiresOn, now) is not null;
    }

    private static string StageKey(MembershipExpiryStage stage) =>
        stage == MembershipExpiryStage.Expiring ? "expiring" : "expired";
}
