using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// Which of the two RF-30 notices a period is due. <see cref="Expiring"/> is the reminder three days before the
/// expiry; <see cref="Expired"/> is the notice at expiry when the student has not renewed yet. Both stages share one
/// paid period and each has its own deduplication key, so a period can produce both, in that order, exactly once.
/// </summary>
public enum MembershipExpiryStage : byte
{
    Expiring = 1,
    Expired = 2
}

/// <summary>
/// One membership period the expiry notice (RF-30) may reach, reduced to what the email needs so the core never reads
/// storage twice. <see cref="ExpiresOn"/> is the first day without coverage (RF-29 step 2). <see cref="Recipient"/> is
/// the client record email (<c>Clients.Email</c>) and the only destination the notice may use: the account email, the
/// contact form and any address the student may have typed elsewhere are never part of this row.
/// <para>
/// <see cref="Status"/> and <see cref="HasLaterPeriod"/> travel with the row so the service can re-apply the
/// "active enrollment, current/latest paid period" predicate even against a permissive reader, and
/// <see cref="ClientName"/> and <see cref="BusinessName"/> are the only personal data the body may show.
/// </para>
/// <para>
/// Every persisted <c>MembershipPeriod</c> is valid monthly coverage. Annulling a payment is a purely financial
/// correction (T18) that keeps the coverage, the seat and the attendance of the period it paid, so a period is never
/// filtered by <c>Payments.IsAnnulled</c> and this row carries no payment-validity flag.
/// </para>
/// </summary>
public sealed record MembershipExpiryCandidate(
    int EnrollmentId,
    int ClientId,
    int BusinessId,
    int PeriodId,
    EnrollmentStatus Status,
    DateOnly ExpiresOn,
    bool HasLaterPeriod,
    string Recipient,
    string ClientName,
    string BusinessName);

/// <summary>
/// One notice to queue together with the facts the guarded enqueue rechecks. <see cref="Notification.CreatedAt"/> is
/// the instant the T23 runner supplied: the guarded insert re-derives the stage window from it, so the core never
/// reads a clock of its own. <see cref="PeriodId"/> and <see cref="ExpiresOn"/> identify the exact paid period the
/// body was composed from, and <see cref="Stage"/> is the RF-30 stage that decides the notification type and the
/// deduplication key.
/// </summary>
public sealed record MembershipExpiryNotice(
    int EnrollmentId,
    int ClientId,
    int BusinessId,
    int PeriodId,
    DateOnly ExpiresOn,
    MembershipExpiryStage Stage,
    Notification Notification);

/// <summary>
/// Persistence boundary of the membership expiry notice (RF-29, RF-30, D-22). Every call is bounded and the enqueue
/// is the only write.
/// <para>
/// <see cref="ListCandidatesAsync"/> returns at most <paramref name="batchSize"/> candidates as of
/// <paramref name="now"/>. Each candidate is the <b>current/latest paid</b> period of an <c>Active</c> enrollment: no
/// pending enrollment without a period, no annulled or terminated enrollment, and no old period when a newer one
/// already exists, so a clock rewind or a missed tick never backfills an obsolete period. A period is returned only
/// when its local stage is due at <paramref name="now"/> — three days before the expiry up to the day before it (the
/// expiring window catches a missed exact day) or exactly on the expiry date (the expired stage) — when its
/// <c>Clients.Email</c> is present and well-formed, and when that stage's key does not exist yet. The expired stage is
/// never served on a later date, and it is suppressed when the enrollment was renewed to a newer period; an expiring
/// notice is suppressed when the expired notice for the same period already exists, so a rewound clock cannot issue
/// the earlier stage after the later one. Ordered by enrollment id and capped so one tick stays bounded.
/// </para>
/// <para>
/// <see cref="TryEnqueueNoticeAsync"/> is <b>one short SQL transaction</b>: it re-reads the enrollment and its periods
/// and inserts the notice only when the enrollment still exists, still belongs to the same business and client, is
/// still <c>Active</c>, its client record email is still the one the body was composed for, the same period is still
/// the current/latest one with the same <see cref="MembershipExpiryCandidate.ExpiresOn"/>, no newer period appeared,
/// the composed instant still falls inside <see cref="MembershipExpiryNotice.Stage"/>'s window, the type and key are
/// the canonical ones, no later stage was already written for the same period, and the exact key does not exist. The
/// insert is guarded by <c>UQ_Notifications_DedupKey</c>, so a concurrent tick or a retried one is a <c>false</c>
/// no-op rather than a second email. It never writes <see cref="Notification.LastError"/>.
/// </para>
/// <para>
/// The row predicate above is a scan obligation, not a trust boundary: the service independently re-derives it from
/// the candidate before composing, so a reader that over-returns an inactive enrollment or a superseded period cannot
/// leak it into a body.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake cannot prove the atomic guard or the unique-key race; only a SQL fixture can. The fake
/// proves the service contract, never the runtime transaction.
/// </para>
/// </summary>
public interface IMembershipExpiryRepository
{
    /// <summary>
    /// Returns at most <paramref name="batchSize"/> eligible candidates as of <paramref name="now"/>, each one the
    /// current/latest paid period of an active enrollment. The predicate is documented on the interface.
    /// </summary>
    Task<IReadOnlyList<MembershipExpiryCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Atomically rechecks the guard described on the interface and inserts
    /// <see cref="MembershipExpiryNotice.Notification"/> when the period is still eligible. Returns <c>true</c> only
    /// when a row was inserted; <c>false</c> when the enrollment vanished, was terminated or annulled, changed its
    /// client record email, was renewed to a newer period, the composed instant left the stage window, the type
    /// or key is not canonical, a later stage already exists for the period, or the exact key already exists.
    /// </summary>
    Task<bool> TryEnqueueNoticeAsync(MembershipExpiryNotice notice, CancellationToken ct = default);
}
