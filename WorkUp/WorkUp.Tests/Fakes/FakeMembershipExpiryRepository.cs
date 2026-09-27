using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory membership expiry port (T25, RF-29, RF-30). It mirrors the SQL contract the scheduled notice depends on:
/// the scan returns only the current/latest paid period of an active enrollment, only when its stage is due and
/// its client record email is usable, and only when the stage key does not exist yet — with the expired stage
/// suppressed when a newer period exists and the expiring stage suppressed when the expired notice for the same
/// period already exists, so a clock rewind cannot issue the earlier stage after the later one. Every candidate is
/// built from the latest period, never from an old one. Every persisted period is valid coverage: annulling a payment
/// is a purely financial correction (T18) that keeps the coverage of the period it paid, so <see cref="FakeMembershipPeriod.PaymentAnnulled"/> is
/// metadata the scan deliberately ignores.
/// <para>
/// <see cref="TryEnqueueNoticeAsync"/> re-runs the whole guard under a lock against the <em>current</em> rows —
/// business/client identity, active status, client record email, latest period and expiry, renewal state, the
/// stage window at the composed instant, canonical type/key, the no-later-stage rule and dedup — so a renewal,
/// termination or email change between the scan and the insert is rejected instead of mailed.
/// </para>
/// <para>
/// <see cref="IgnoreBatchCap"/>, <see cref="OverReturnIneligible"/> and <see cref="ForceLaterPeriod"/> break the
/// read half on purpose so a test can prove the service still rejects the extras and caps the batch on its own; they
/// never relax the enqueue guards.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake holds no SQL row lock and no unique-key race, so it cannot prove atomicity; it only
/// proves the service contract. A SQL fixture is the runtime proof.
/// </para>
/// </summary>
public sealed class FakeMembershipExpiryRepository : IMembershipExpiryRepository
{
    private readonly List<FakeMembershipEnrollment> _enrollments = [];
    private readonly List<Notification> _notifications = [];
    private readonly object _gate = new();

    /// <summary>Runs right after a scan produced its snapshot, so a test can mutate state before the enqueue.</summary>
    public Action? AfterList { get; set; }

    /// <summary>When true the scan ignores <c>batchSize</c>, over-returning so the service must cap the batch itself.</summary>
    public bool IgnoreBatchCap { get; set; }

    /// <summary>When true the scan skips every eligibility and dedup filter, so the service must reject the extras.</summary>
    public bool OverReturnIneligible { get; set; }

    /// <summary>When true every built candidate claims a newer valid period exists, so the service must reject it.</summary>
    public bool ForceLaterPeriod { get; set; }

    public int ListCount { get; private set; }

    public int LastBatchSize { get; private set; }

    public int LastScanCandidateCount { get; private set; }

    public int EnqueueAttempts { get; private set; }

    public MembershipExpiryNotice? LastAttemptedNotice { get; private set; }

    public IReadOnlyList<FakeMembershipEnrollment> Enrollments => _enrollments;

    public IReadOnlyList<Notification> Notifications
    {
        get
        {
            lock (_gate)
                return _notifications.ToArray();
        }
    }

    public FakeMembershipEnrollment AddEnrollment(
        int enrollmentId = 1,
        int clientId = 10,
        int businessId = 1,
        EnrollmentStatus status = EnrollmentStatus.Active,
        string? recipient = "alumno@example.com",
        string clientName = "Ana",
        string businessName = "Estudio",
        string accountEmail = "cuenta@example.com")
    {
        var enrollment = new FakeMembershipEnrollment
        {
            EnrollmentId = enrollmentId,
            ClientId = clientId,
            BusinessId = businessId,
            Status = status,
            Recipient = recipient,
            ClientName = clientName,
            BusinessName = businessName,
            AccountEmail = accountEmail
        };
        _enrollments.Add(enrollment);
        return enrollment;
    }

    /// <summary>Adds a paid period to an enrollment. Every period is valid coverage; the optional annulled-payment
    /// metadata is recorded but never used to filter, because annulment is purely financial (T18).</summary>
    public FakeMembershipPeriod AddPeriod(
        FakeMembershipEnrollment enrollment,
        int periodId,
        DateOnly coverageStart,
        DateOnly expiresOn,
        bool paymentAnnulled = false)
    {
        var period = new FakeMembershipPeriod
        {
            PeriodId = periodId,
            CoverageStart = coverageStart,
            ExpiresOn = expiresOn,
            PaymentAnnulled = paymentAnnulled
        };
        enrollment.Periods.Add(period);
        return period;
    }

    /// <summary>Marks an enrollment terminated between the scan and the insert (RF-29 step 6).</summary>
    public void Terminate(FakeMembershipEnrollment enrollment) => enrollment.Status = EnrollmentStatus.Terminated;

    /// <summary>Annuls an enrollment between the scan and the insert (RF-20).</summary>
    public void Annul(FakeMembershipEnrollment enrollment) => enrollment.Status = EnrollmentStatus.Annulled;

    /// <summary>Changes the client record email between the scan and the insert.</summary>
    public void ChangeRecipient(FakeMembershipEnrollment enrollment, string recipient) =>
        enrollment.Recipient = recipient;

    /// <summary>Removes an enrollment, modelling a deletion between the scan and the insert.</summary>
    public void RemoveEnrollment(int enrollmentId) =>
        _enrollments.RemoveAll(row => row.EnrollmentId == enrollmentId);

    /// <summary>
    /// Inserts a competing notice for the exact stage key, modelling a concurrent producer that committed after the
    /// scan returned its snapshot, so the guarded enqueue must reject the duplicate.
    /// </summary>
    public Notification SeedNotice(
        int enrollmentId, int periodId, MembershipExpiryStage stage, DateTime createdAt)
    {
        var notification = new Notification
        {
            BusinessId = 1,
            Type = MembershipExpiryService.TypeFor(stage),
            Recipient = "otro@example.com",
            Subject = "aviso en curso",
            BodyHtml = "<p>aviso en curso</p>",
            DedupKey = MembershipExpiryService.DedupKey(enrollmentId, periodId, stage),
            CreatedAt = createdAt
        };
        lock (_gate)
            _notifications.Add(notification);
        return notification;
    }

    public Task<IReadOnlyList<MembershipExpiryCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ListCount++;
        LastBatchSize = batchSize;
        var eligible = _enrollments
            .Where(enrollment => OverReturnIneligible || IsEligibleForScan(enrollment, now))
            .OrderBy(enrollment => enrollment.EnrollmentId);
        var limited = IgnoreBatchCap ? eligible : eligible.Take(batchSize);
        var candidates = limited
            .Where(enrollment => LatestPeriod(enrollment) is not null)
            .Select(enrollment => BuildCandidate(enrollment, LatestPeriod(enrollment)!))
            .ToArray();

        LastScanCandidateCount = candidates.Length;
        AfterList?.Invoke();
        return Task.FromResult<IReadOnlyList<MembershipExpiryCandidate>>(candidates);
    }

    public Task<bool> TryEnqueueNoticeAsync(MembershipExpiryNotice notice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnqueueAttempts++;
        LastAttemptedNotice = notice;
        lock (_gate)
        {
            var enrollment = _enrollments.FirstOrDefault(row => row.EnrollmentId == notice.EnrollmentId);
            if (enrollment is null)
                return Task.FromResult(false);

            // Business and client identity: the live row must still be the one the notice was composed for.
            if (enrollment.BusinessId != notice.BusinessId || enrollment.ClientId != notice.ClientId)
                return Task.FromResult(false);
            if (enrollment.Status != EnrollmentStatus.Active)
                return Task.FromResult(false);

            // The client record email must still be the exact destination the body was composed for.
            if (!EmailFormat.IsValid(enrollment.Recipient)
                || !string.Equals(enrollment.Recipient, notice.Notification.Recipient, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            // The same period must still be the current/latest one with the same expiry.
            var latest = LatestPeriod(enrollment);
            if (latest is null || latest.PeriodId != notice.PeriodId || latest.ExpiresOn != notice.ExpiresOn)
                return Task.FromResult(false);
            if (HasLaterPeriod(enrollment, notice.ExpiresOn))
                return Task.FromResult(false);

            // The composed instant must still fall inside the stage's window.
            var stage = MembershipExpiryService.StageAt(notice.ExpiresOn, notice.Notification.CreatedAt);
            if (stage is null || stage.Value != notice.Stage)
                return Task.FromResult(false);

            // The type and key must be the canonical ones for this period and stage.
            if (notice.Notification.Type != MembershipExpiryService.TypeFor(notice.Stage))
                return Task.FromResult(false);
            if (notice.Notification.DedupKey
                != MembershipExpiryService.DedupKey(notice.EnrollmentId, notice.PeriodId, notice.Stage))
            {
                return Task.FromResult(false);
            }

            // A rewound clock must not issue the earlier expiring stage after the expired one for the same period.
            if (notice.Stage == MembershipExpiryStage.Expiring
                && HasNotice(notice.EnrollmentId, notice.PeriodId, MembershipExpiryStage.Expired))
            {
                return Task.FromResult(false);
            }

            if (_notifications.Any(existing => existing.DedupKey == notice.Notification.DedupKey))
                return Task.FromResult(false);

            _notifications.Add(notice.Notification);
            return Task.FromResult(true);
        }
    }

    private bool IsEligibleForScan(FakeMembershipEnrollment enrollment, DateTime now)
    {
        var latest = LatestPeriod(enrollment);
        if (latest is null)
            return false;

        var candidate = BuildCandidate(enrollment, latest);
        if (!MembershipExpiryService.IsEligible(candidate, now))
            return false;

        var stage = MembershipExpiryService.StageAt(latest.ExpiresOn, now);
        if (stage is null)
            return false;
        if (HasNotice(enrollment.EnrollmentId, latest.PeriodId, stage.Value))
            return false;

        // The expired notice for a period also suppresses the earlier expiring one.
        return stage.Value != MembershipExpiryStage.Expiring
            || !HasNotice(enrollment.EnrollmentId, latest.PeriodId, MembershipExpiryStage.Expired);
    }

    private bool HasNotice(int enrollmentId, int periodId, MembershipExpiryStage stage)
    {
        lock (_gate)
        {
            return _notifications.Any(notification =>
                notification.DedupKey == MembershipExpiryService.DedupKey(enrollmentId, periodId, stage));
        }
    }

    private static FakeMembershipPeriod? LatestPeriod(FakeMembershipEnrollment enrollment) =>
        enrollment.Periods
            .OrderByDescending(period => period.ExpiresOn)
            .ThenByDescending(period => period.PeriodId)
            .FirstOrDefault();

    private static bool HasLaterPeriod(FakeMembershipEnrollment enrollment, DateOnly expiresOn) =>
        enrollment.Periods.Any(period => period.ExpiresOn > expiresOn);

    private MembershipExpiryCandidate BuildCandidate(
        FakeMembershipEnrollment enrollment, FakeMembershipPeriod latest) =>
        new(
            enrollment.EnrollmentId,
            enrollment.ClientId,
            enrollment.BusinessId,
            latest.PeriodId,
            enrollment.Status,
            latest.ExpiresOn,
            HasLaterPeriod: ForceLaterPeriod,
            // Only the client record email is projected; the account email is deliberately not a destination.
            enrollment.Recipient ?? "",
            enrollment.ClientName,
            enrollment.BusinessName);
}

/// <summary>A seeded enrollment the fake scans; mutable so a test can model a change between the scan and the enqueue.</summary>
public sealed class FakeMembershipEnrollment
{
    public int EnrollmentId { get; init; }
    public int ClientId { get; init; } = 10;
    public int BusinessId { get; init; } = 1;
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Active;

    /// <summary>Client record email (<c>Clients.Email</c>), the only notice destination.</summary>
    public string? Recipient { get; set; } = "alumno@example.com";

    public string ClientName { get; set; } = "Ana";
    public string BusinessName { get; set; } = "Estudio";

    /// <summary>Decoy account email: no notice may ever be delivered here (RF-30 recipient is the client record).</summary>
    public string AccountEmail { get; set; } = "cuenta@example.com";

    public List<FakeMembershipPeriod> Periods { get; } = [];
}

/// <summary>A seeded paid period; mutable so a test can model a later renewal.</summary>
public sealed class FakeMembershipPeriod
{
    public int PeriodId { get; init; }
    public DateOnly CoverageStart { get; set; }
    public DateOnly ExpiresOn { get; set; }

    /// <summary>
    /// Metadata only: an annulled payment keeps the monthly coverage of the period it paid (T18), so the scan never
    /// filters on this flag. It exists so a test can prove the notice is still produced for such a period.
    /// </summary>
    public bool PaymentAnnulled { get; set; }
}
