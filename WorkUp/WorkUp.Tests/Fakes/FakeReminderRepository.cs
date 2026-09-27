using WorkUp.BusinessLogic.Notifications;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory reminder port (T24). It mirrors the SQL contract the scheduled reminder depends on: the scan returns
/// only eligible candidates with no scheduled key yet, and <see cref="TryEnqueueReminderAsync"/> re-runs the whole
/// eligibility against the <em>current</em> rows under a lock — business scope, kind/modality, lead, dedup and the
/// exact still-live private link identity — and only then inserts, so a booking cancelled, or an immediate reminder
/// produced, or a link revoked/replaced between the scan and the insert is rejected instead of duplicated or pointed
/// at a stale URL.
/// <para>
/// <see cref="OverReturnIneligible"/> and <see cref="IgnoreBatchCap"/> deliberately break the read half so a test can
/// prove the service still rejects the extras on its own; they never relax the enqueue guards.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake holds no SQL row lock and no unique-key race, so it cannot prove atomicity; it only
/// proves the service contract. A SQL fixture is the runtime proof.
/// </para>
/// </summary>
public sealed class FakeReminderRepository : IReminderRepository
{
    private readonly List<FakeReminderBooking> _bookings = [];
    private readonly List<Notification> _notifications = [];
    private readonly object _gate = new();
    private int _nextLinkId = 1;

    /// <summary>Runs right after a scan produced its snapshot, so a test can mutate state before the enqueue.</summary>
    public Action? AfterList { get; set; }

    /// <summary>When true the scan ignores <c>batchSize</c>, over-returning so the service must cap the batch itself.</summary>
    public bool IgnoreBatchCap { get; set; }

    /// <summary>When true the scan skips every eligibility and dedup filter, so the service must reject malformed rows.</summary>
    public bool OverReturnIneligible { get; set; }

    public int ListCount { get; private set; }

    public int EnqueueAttempts { get; private set; }

    public int LastBatchSize { get; private set; }

    public int LastScanCandidateCount { get; private set; }

    public ReminderNotice? LastAttemptedNotice { get; private set; }

    public IReadOnlyList<FakeReminderBooking> Bookings => _bookings;

    public IReadOnlyList<Notification> Notifications
    {
        get
        {
            lock (_gate)
                return _notifications.ToArray();
        }
    }

    public FakeReminderBooking AddIndividual(
        int bookingId,
        DateTime startsAt,
        DateTime createdAt,
        int reminderLeadHours = 24,
        BookingStatus status = BookingStatus.Pending,
        AppointmentStatus appointmentStatus = AppointmentStatus.Scheduled,
        int businessId = 1,
        string recipient = "ana@example.com",
        string clientFirstName = "Ana",
        string businessName = "Estudio",
        string serviceName = "Masaje",
        string workerName = "Sol Díaz",
        AppointmentKind kind = AppointmentKind.Individual,
        int? enrollmentId = null)
    {
        var booking = new FakeReminderBooking
        {
            BookingId = bookingId,
            BusinessId = businessId,
            AppointmentId = 1000 + bookingId,
            Kind = kind,
            Modality = null,
            EnrollmentId = enrollmentId,
            Status = status,
            AppointmentStatus = appointmentStatus,
            StartsAt = startsAt,
            CreatedAt = createdAt,
            ReminderLeadHours = reminderLeadHours,
            Recipient = recipient,
            ClientFirstName = clientFirstName,
            BusinessName = businessName,
            ServiceName = serviceName,
            WorkerName = workerName
        };
        _bookings.Add(booking);
        return booking;
    }

    public FakeReminderBooking AddDropIn(
        int bookingId,
        DateTime startsAt,
        DateTime createdAt,
        int reminderLeadHours = 24,
        BookingStatus status = BookingStatus.Pending,
        AppointmentStatus appointmentStatus = AppointmentStatus.Scheduled,
        int businessId = 1,
        string recipient = "ana@example.com",
        string clientFirstName = "Ana",
        string businessName = "Estudio",
        string serviceName = "Clase suelta",
        string workerName = "Sol Díaz")
    {
        var booking = new FakeReminderBooking
        {
            BookingId = bookingId,
            BusinessId = businessId,
            AppointmentId = 1000 + bookingId,
            Kind = AppointmentKind.ClassSession,
            Modality = BookingModality.DropIn,
            EnrollmentId = null,
            Status = status,
            AppointmentStatus = appointmentStatus,
            StartsAt = startsAt,
            CreatedAt = createdAt,
            ReminderLeadHours = reminderLeadHours,
            Recipient = recipient,
            ClientFirstName = clientFirstName,
            BusinessName = businessName,
            ServiceName = serviceName,
            WorkerName = workerName
        };
        _bookings.Add(booking);
        return booking;
    }

    public FakeReminderBooking AddFixed(
        int bookingId,
        DateTime startsAt,
        DateTime createdAt,
        int reminderLeadHours = 24,
        BookingStatus status = BookingStatus.Pending,
        AppointmentStatus appointmentStatus = AppointmentStatus.Scheduled,
        int businessId = 1)
    {
        var booking = new FakeReminderBooking
        {
            BookingId = bookingId,
            BusinessId = businessId,
            AppointmentId = 1000 + bookingId,
            Kind = AppointmentKind.ClassSession,
            Modality = BookingModality.Fixed,
            EnrollmentId = 42,
            Status = status,
            AppointmentStatus = appointmentStatus,
            StartsAt = startsAt,
            CreatedAt = createdAt,
            ReminderLeadHours = reminderLeadHours
        };
        _bookings.Add(booking);
        return booking;
    }

    public ReminderLink AddLink(
        FakeReminderBooking booking,
        string token,
        DateTime expiresAt,
        DateTime? revokedAt = null,
        string? protectedToken = null) =>
        AddLinkWithHash(booking, SecureToken.Hash(token), protectedToken ?? "protected:" + token, expiresAt, revokedAt);

    public ReminderLink AddLinkWithHash(
        FakeReminderBooking booking,
        byte[] tokenHash,
        string protectedToken,
        DateTime expiresAt,
        DateTime? revokedAt = null)
    {
        var link = new ReminderLink(_nextLinkId++, tokenHash, protectedToken, expiresAt, revokedAt);
        booking.Links.Add(link);
        return link;
    }

    /// <summary>Marks an already seeded link revoked by the owner between the scan and the insert.</summary>
    public void RevokeLink(FakeReminderBooking booking, ReminderLink link, DateTime revokedAt)
    {
        var index = booking.Links.FindIndex(candidate => candidate.LinkId == link.LinkId);
        if (index >= 0)
            booking.Links[index] = booking.Links[index] with { RevokedAt = revokedAt };
    }

    /// <summary>Revokes the reviewed link and inserts a fresh one, the owner "replace link" path (RF-23).</summary>
    public ReminderLink ReplaceLink(
        FakeReminderBooking booking, ReminderLink link, string newToken, DateTime revokedAt, DateTime newExpiresAt)
    {
        RevokeLink(booking, link, revokedAt);
        return AddLink(booking, newToken, newExpiresAt);
    }

    /// <summary>Seeds the immediate individual reminder the booking path queues inside the window (key <c>booking:{id}</c>).</summary>
    public Notification AddImmediateAppointmentReminder(FakeReminderBooking booking, DateTime createdAt) =>
        AddNotification(booking, NotificationType.AppointmentReminder, $"booking:{booking.BookingId}", createdAt);

    /// <summary>Seeds the ordinary booking confirmation (key <c>booking:{id}</c>) that must not suppress the reminder.</summary>
    public Notification AddBookingCreated(FakeReminderBooking booking, DateTime createdAt) =>
        AddNotification(booking, NotificationType.BookingCreated, $"booking:{booking.BookingId}", createdAt);

    /// <summary>Seeds the immediate drop-in email, which already owns the scheduled key (key <c>reminder:booking:{id}</c>).</summary>
    public Notification AddImmediateDropInReminder(FakeReminderBooking booking, DateTime createdAt) =>
        AddNotification(booking, NotificationType.DropInReminder, $"reminder:booking:{booking.BookingId}", createdAt);

    public Task<IReadOnlyList<ReminderCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default)
    {
        ListCount++;
        LastBatchSize = batchSize;
        var eligible = _bookings
            .Where(booking => OverReturnIneligible
                || (ReminderService.IsEligible(ToCandidate(booking), now)
                    && !HasScheduledReminder(booking.BookingId)
                    && !HasImmediateIndividualReminder(booking)))
            .OrderBy(booking => booking.StartsAt)
            .ThenBy(booking => booking.BookingId);
        var limited = IgnoreBatchCap ? eligible : eligible.Take(batchSize);
        var candidates = limited.Select(ToCandidate).ToArray();

        LastScanCandidateCount = candidates.Length;
        AfterList?.Invoke();
        return Task.FromResult<IReadOnlyList<ReminderCandidate>>(candidates);
    }

    public Task<bool> TryEnqueueReminderAsync(ReminderNotice notice, CancellationToken ct = default)
    {
        EnqueueAttempts++;
        LastAttemptedNotice = notice;
        lock (_gate)
        {
            var now = notice.Notification.CreatedAt;
            var booking = _bookings.FirstOrDefault(row => row.BookingId == notice.BookingId);
            if (booking is null
                // Business scope: the live row must still belong to the tenant the notice was composed for.
                || booking.BusinessId != notice.Notification.BusinessId
                || !ReminderService.IsEligible(ToCandidate(booking), now)
                || HasScheduledReminder(booking.BookingId)
                || HasImmediateIndividualReminder(booking)
                || !HasMatchingLiveLink(booking, notice, now))
            {
                return Task.FromResult(false);
            }

            if (_notifications.Any(existing => existing.DedupKey == notice.Notification.DedupKey))
                return Task.FromResult(false);

            _notifications.Add(notice.Notification);
            return Task.FromResult(true);
        }
    }

    private Notification AddNotification(
        FakeReminderBooking booking, NotificationType type, string dedupKey, DateTime createdAt)
    {
        var notification = new Notification
        {
            BusinessId = booking.BusinessId,
            Type = type,
            Recipient = booking.Recipient,
            Subject = "aviso previo",
            BodyHtml = "<p>aviso previo</p>",
            DedupKey = dedupKey,
            CreatedAt = createdAt
        };
        lock (_gate)
            _notifications.Add(notification);
        return notification;
    }

    private bool HasScheduledReminder(int bookingId) =>
        _notifications.Any(notification => notification.DedupKey == $"reminder:booking:{bookingId}");

    /// <summary>
    /// An individual notice must still re-present the exact link it was built from as the single live link at the run
    /// instant; a revoked, expired, multiplied or replaced link is a different state and must not be linked silently.
    /// A drop-in carries no link and is unaffected.
    /// </summary>
    private static bool HasMatchingLiveLink(FakeReminderBooking booking, ReminderNotice notice, DateTime now)
    {
        if (booking.Modality is not null)
            return notice.Link is null;
        if (notice.Link is null || notice.Link.TokenHash is null)
            return false;

        var live = booking.Links.Where(link => link.IsLiveAt(now)).ToArray();
        return live.Length == 1
            && live[0].LinkId == notice.Link.LinkId
            && live[0].TokenHash is not null
            && live[0].TokenHash.AsSpan().SequenceEqual(notice.Link.TokenHash);
    }

    private bool HasImmediateIndividualReminder(FakeReminderBooking booking) =>
        booking.Modality is null && _notifications.Any(notification =>
            notification.DedupKey == $"booking:{booking.BookingId}"
            && notification.Type == NotificationType.AppointmentReminder);

    private static ReminderCandidate ToCandidate(FakeReminderBooking booking) => new(
        booking.BookingId,
        booking.BusinessId,
        booking.AppointmentId,
        booking.Kind,
        booking.Modality,
        booking.EnrollmentId,
        booking.Status,
        booking.AppointmentStatus,
        booking.StartsAt,
        booking.CreatedAt,
        booking.ReminderLeadHours,
        booking.Recipient,
        booking.ClientFirstName,
        booking.BusinessName,
        booking.ServiceName,
        booking.WorkerName,
        booking.Links.ToArray());
}

/// <summary>A seeded booking the fake scans; mutable so a test can model a race between the scan and the enqueue.</summary>
public sealed class FakeReminderBooking
{
    public int BookingId { get; init; }
    public int BusinessId { get; set; } = 1;
    public int AppointmentId { get; init; }
    public AppointmentKind Kind { get; init; } = AppointmentKind.Individual;
    public BookingModality? Modality { get; init; }
    public int? EnrollmentId { get; init; }
    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public AppointmentStatus AppointmentStatus { get; set; } = AppointmentStatus.Scheduled;
    public DateTime StartsAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public int ReminderLeadHours { get; init; } = 24;
    public string Recipient { get; init; } = "ana@example.com";
    public string ClientFirstName { get; init; } = "Ana";
    public string BusinessName { get; init; } = "Estudio";
    public string ServiceName { get; init; } = "Masaje";
    public string WorkerName { get; init; } = "Sol Díaz";
    public List<ReminderLink> Links { get; } = [];
}
