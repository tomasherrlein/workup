using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory daily-summary port (T25, RF-18). It mirrors the SQL contract the scheduled summary depends on: the scan
/// returns only businesses whose configured time already arrived, whose notice email is present and well-formed and
/// whose target-day key does not exist yet, carrying only the target day's <c>Scheduled</c> appointments and their
/// <c>Pendiente</c>/<c>Confirmado</c> bookings; and <see cref="TryEnqueueSummaryAsync"/> re-runs the whole guard under a
/// lock against the <em>current</em> rows — business scope, recipient, configured time, eligibility at the composed
/// instant and dedup — so a recipient or time edited between the scan and the insert is rejected instead of mailed.
/// <para>
/// <see cref="OverReturnIneligible"/> and <see cref="IgnoreBatchCap"/> deliberately break the read half so a test can
/// prove the service still rejects the extras and caps the batch on its own; they never relax the enqueue guards.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake holds no SQL row lock and no unique-key race, so it cannot prove atomicity; it only
/// proves the service contract. A SQL fixture is the runtime proof.
/// </para>
/// </summary>
public sealed class FakeDailySummaryRepository : IDailySummaryRepository
{
    private readonly List<FakeDailySummaryBusiness> _businesses = [];
    private readonly List<Notification> _notifications = [];
    private readonly object _gate = new();

    /// <summary>Runs right after a scan produced its snapshot, so a test can mutate state before the enqueue.</summary>
    public Action? AfterList { get; set; }

    /// <summary>When true the scan ignores <c>batchSize</c>, over-returning so the service must cap the batch itself.</summary>
    public bool IgnoreBatchCap { get; set; }

    /// <summary>When true the scan skips every eligibility and dedup filter, so the service must reject the extras.</summary>
    public bool OverReturnIneligible { get; set; }

    /// <summary>
    /// When true the scan skips every row filter, returning suspended appointments, rows outside the target day and
    /// inactive bookings as seeded, so the service must re-apply the predicate and drop them from the body.
    /// </summary>
    public bool OverReturnRows { get; set; }

    public int ListCount { get; private set; }

    public int LastBatchSize { get; private set; }

    public int LastScanCandidateCount { get; private set; }

    public int EnqueueAttempts { get; private set; }

    public DailySummaryNotice? LastAttemptedNotice { get; private set; }

    public IReadOnlyList<FakeDailySummaryBusiness> Businesses => _businesses;

    public IReadOnlyList<Notification> Notifications
    {
        get
        {
            lock (_gate)
                return _notifications.ToArray();
        }
    }

    public FakeDailySummaryBusiness AddBusiness(
        int businessId = 1,
        string name = "Estudio",
        string? recipient = "duena@example.com",
        TimeOnly? configuredTime = null,
        string contactPhone = "+54 11 5555-0000")
    {
        var business = new FakeDailySummaryBusiness
        {
            BusinessId = businessId,
            Name = name,
            Recipient = recipient,
            ConfiguredTime = configuredTime ?? new TimeOnly(21, 0),
            ContactPhone = contactPhone
        };
        _businesses.Add(business);
        return business;
    }

    /// <summary>Adds an individual turn with its single booking; the default booking id is mirrored from the turn.</summary>
    public FakeDailySummaryAppointment AddIndividual(
        FakeDailySummaryBusiness business,
        int appointmentId,
        DateTime startsAt,
        string serviceName = "Corte",
        string workerName = "Sol Díaz",
        AppointmentStatus status = AppointmentStatus.Scheduled,
        string clientName = "Ana",
        BookingStatus bookingStatus = BookingStatus.Pending,
        int? bookingId = null) =>
        AddAppointment(business, appointmentId, startsAt, AppointmentKind.Individual, serviceName, workerName, status)
            .WithBooking(bookingId ?? appointmentId, clientName, bookingStatus);

    /// <summary>Adds a class session with no bookings; a test annotates it explicitly.</summary>
    public FakeDailySummaryAppointment AddClassSession(
        FakeDailySummaryBusiness business,
        int appointmentId,
        DateTime startsAt,
        string serviceName = "Yoga",
        string workerName = "Mar Acosta",
        AppointmentStatus status = AppointmentStatus.Scheduled) =>
        AddAppointment(business, appointmentId, startsAt, AppointmentKind.ClassSession, serviceName, workerName, status);

    public Task<IReadOnlyList<DailySummaryCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ListCount++;
        LastBatchSize = batchSize;
        var target = DailySummaryService.TargetDate(now);
        var eligible = _businesses
            .Where(business => OverReturnIneligible
                || (DailySummaryService.IsEligible(BuildCandidate(business, now), now)
                    && !HasSummary(business.BusinessId, target)))
            .OrderBy(business => business.BusinessId);
        var limited = IgnoreBatchCap ? eligible : eligible.Take(batchSize);
        var candidates = limited
            .Select(business => BuildCandidate(business, now))
            .ToArray();

        LastScanCandidateCount = candidates.Length;
        AfterList?.Invoke();
        return Task.FromResult<IReadOnlyList<DailySummaryCandidate>>(candidates);
    }

    public Task<bool> TryEnqueueSummaryAsync(DailySummaryNotice notice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnqueueAttempts++;
        LastAttemptedNotice = notice;
        lock (_gate)
        {
            var business = _businesses.FirstOrDefault(row => row.BusinessId == notice.BusinessId);
            if (business is null)
                return Task.FromResult(false);

            // The notice is only valid while the business still has the same usable recipient and the same configured
            // time the body was composed for: an edit between the scan and the insert invalidates it.
            if (!EmailFormat.IsValid(business.Recipient)
                || !string.Equals(business.Recipient, notice.Notification.Recipient, StringComparison.Ordinal)
                || business.ConfiguredTime != notice.ConfiguredTime)
            {
                return Task.FromResult(false);
            }

            // Defense in depth: a permissive reader must not date a notice before the configured hour.
            if (TimeOnly.FromDateTime(notice.Notification.CreatedAt) < business.ConfiguredTime)
                return Task.FromResult(false);

            if (_notifications.Any(existing => existing.DedupKey == notice.Notification.DedupKey))
                return Task.FromResult(false);

            _notifications.Add(notice.Notification);
            return Task.FromResult(true);
        }
    }

    private FakeDailySummaryAppointment AddAppointment(
        FakeDailySummaryBusiness business,
        int appointmentId,
        DateTime startsAt,
        AppointmentKind kind,
        string serviceName,
        string workerName,
        AppointmentStatus status)
    {
        var appointment = new FakeDailySummaryAppointment
        {
            AppointmentId = appointmentId,
            StartsAt = startsAt,
            EndsAt = startsAt.AddHours(1),
            Kind = kind,
            Status = status,
            ServiceName = serviceName,
            WorkerName = workerName
        };
        business.Appointments.Add(appointment);
        return appointment;
    }

    private bool HasSummary(int businessId, DateOnly target)
    {
        var key = DailySummaryService.DedupKey(businessId, target);
        lock (_gate)
            return _notifications.Any(notification => notification.DedupKey == key);
    }

    /// <summary>
    /// Inserts a competing summary row for the exact target key, modelling a concurrent producer that committed after
    /// the scan returned its snapshot, so the guarded enqueue must reject the duplicate.
    /// </summary>
    public Notification SeedSummary(int businessId, DateOnly target, DateTime createdAt)
    {
        var notification = new Notification
        {
            BusinessId = businessId,
            Type = NotificationType.DailySummary,
            Recipient = "otro@example.com",
            Subject = "resumen en curso",
            BodyHtml = "<p>resumen en curso</p>",
            DedupKey = DailySummaryService.DedupKey(businessId, target),
            CreatedAt = createdAt
        };
        lock (_gate)
            _notifications.Add(notification);
        return notification;
    }

    /// <summary>Removes a business, modelling a deletion between the scan and the insert.</summary>
    public void RemoveBusiness(int businessId) =>
        _businesses.RemoveAll(row => row.BusinessId == businessId);

    /// <summary>
    /// Tenant-scoped projection of one business. The recipient reflects the current row, so a test that edits it
    /// before the scan is composed against the edit and a test that edits it after the scan hits the guarded enqueue.
    /// <para>
    /// <see cref="OverReturnRows"/> returns every seeded row untouched, so a test can prove the service re-applies
    /// the predicate on its own. The default path is routed through
    /// <see cref="DailySummaryService.VisibleAppointments"/> so the fake scan and the service cannot drift apart: the
    /// target day's <c>Scheduled</c> rows only, an individual turn needs at least one active booking, a class session
    /// is kept even with none, and only active bookings are carried.
    /// </para>
    /// </summary>
    private DailySummaryCandidate BuildCandidate(FakeDailySummaryBusiness business, DateTime now)
    {
        var raw = new DailySummaryCandidate(
            business.BusinessId,
            business.Recipient ?? "",
            business.Name,
            business.ConfiguredTime,
            business.Appointments
                .OrderBy(appointment => appointment.StartsAt)
                .ThenBy(appointment => appointment.AppointmentId)
                .Select(appointment => new DailySummaryAppointment(
                    appointment.AppointmentId,
                    appointment.StartsAt,
                    appointment.EndsAt,
                    appointment.Kind,
                    appointment.Status,
                    appointment.ServiceName,
                    appointment.WorkerName,
                    appointment.Bookings
                        .OrderBy(booking => booking.BookingId)
                        .Select(booking => new DailySummaryBooking(booking.BookingId, booking.ClientName, booking.Status))
                        .ToArray()))
                .ToArray());

        return OverReturnRows
            ? raw
            : raw with { Appointments = DailySummaryService.VisibleAppointments(raw, now) };
    }
}

/// <summary>A seeded business the fake scans; mutable so a test can model an edit between the scan and the enqueue.</summary>
public sealed class FakeDailySummaryBusiness
{
    public int BusinessId { get; init; }
    public string Name { get; set; } = "Estudio";
    public string? Recipient { get; set; } = "duena@example.com";
    public TimeOnly ConfiguredTime { get; set; } = new(21, 0);

    /// <summary>Contact datum only: no summary may ever be delivered here (plan assumption 3).</summary>
    public string ContactPhone { get; set; } = "";

    public List<FakeDailySummaryAppointment> Appointments { get; } = [];
}

/// <summary>A seeded appointment of the target day; mutable so a test can suspend it between the scan and the insert.</summary>
public sealed class FakeDailySummaryAppointment
{
    public int AppointmentId { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
    public AppointmentKind Kind { get; init; } = AppointmentKind.Individual;
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public string ServiceName { get; set; } = "Servicio";
    public string WorkerName { get; set; } = "Sol Díaz";
    public List<FakeDailySummaryBooking> Bookings { get; } = [];

    public FakeDailySummaryAppointment WithBooking(
        int bookingId, string clientName, BookingStatus status = BookingStatus.Pending)
    {
        Bookings.Add(new FakeDailySummaryBooking
        {
            BookingId = bookingId,
            ClientName = clientName,
            Status = status
        });
        return this;
    }
}

/// <summary>A seeded booking of a target-day appointment, with just the two fields the summary shows.</summary>
public sealed class FakeDailySummaryBooking
{
    public int BookingId { get; init; }
    public string ClientName { get; set; } = "Ana";
    public BookingStatus Status { get; set; } = BookingStatus.Pending;
}
