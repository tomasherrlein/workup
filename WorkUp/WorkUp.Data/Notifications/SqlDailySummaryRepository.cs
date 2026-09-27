using System.Globalization;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Notifications;

/// <summary>
/// ADO.NET persistence of the daily owner agenda summary port (T25, RF-18). Every command runs on the request
/// connection and joins the caller's transaction if one exists; this repository never opens a transaction of its own,
/// so no transaction can span the scan, the batch and the enqueue.
/// <para>
/// <see cref="ListCandidatesAsync"/> is the global, bounded read half: one <c>TOP (batchSize)</c> statement ordered by
/// business id, then one bounded statement loading the target day's rows of exactly those businesses. A business is
/// only a candidate when its notice email is present and not blank, its <c>DailySummaryTime</c> already arrived at the
/// composing instant on the same local day, it existed at that instant (a rewound simulated clock never reaches a
/// business created later), and its <c>summary:business:{id}:{yyyyMMdd}</c> key over the <b>next</b> day is still
/// absent — the key of an older day therefore never suppresses tomorrow's summary, and the last representable
/// calendar day has no next day at all. The email <em>shape</em> is the core's job
/// (<c>EmailFormat.IsValid</c>): SQL applies the presence guard, so a blank recipient can never be scanned while a
/// merely malformed one is skipped by the service before composing.
/// </para>
/// <para>
/// The row half repeats the tenant predicate on every join, so a candidate can never carry a service, a worker or a
/// ficha of another business: only the target day's <c>Scheduled</c> rows are read, only <c>Pendiente</c>/<c>Confirmado</c>
/// bookings survive, and a booking whose client belongs to another tenant is not a booking at all. An <b>individual</b>
/// turn left with no active booking is dropped — a turn with nobody annotated is not part of the agenda — while a
/// <b>class session</b> is kept even with none. Nothing but names, hours and states is projected: health notes, owner
/// notes, payments and private links have no column in this contract.
/// </para>
/// <para>
/// <see cref="TryEnqueueSummaryAsync"/> is <b>one</b> guarded <c>INSERT ... SELECT</c>, never a transaction: the same
/// guards are re-derived against the <em>current</em> business row at the notice instant — the row still exists, the
/// recipient still matches the composed one exactly, <c>DailySummaryTime</c> is unchanged and already reached, the
/// business existed at the notice instant, the canonical key over the next day matches the composed key and is still
/// absent. The row count is the atomic answer, and <c>UQ_Notifications_DedupKey</c> is the final race guard: a
/// genuinely concurrent or retried producer surfaces as a duplicate key error (<c>2601</c>/<c>2627</c>) converted into
/// a <c>false</c> no-op, never into a second email. No other <see cref="SqlException"/> is swallowed.
/// </para>
/// <para>
/// Nothing here logs and <see cref="WorkUp.Domain.Entities.Notification.LastError"/> is never written: the notice is
/// either inserted whole or not at all.
/// </para>
/// </summary>
public sealed class SqlDailySummaryRepository(DbSession session) : SqlRepositoryBase(session), IDailySummaryRepository
{
    private const string DedupKeyPrefix = "summary:business:";

    /// <summary>
    /// Global bounded business scan. The <c>TOP</c> keeps one tick small, <c>bu.Id</c> is the deterministic order the
    /// service relies on, and the <c>NOT EXISTS</c> clause is the once-per-target-day half.
    /// </summary>
    private const string CandidateSql = """
        SELECT TOP (@batchSize)
               bu.Id AS BusinessId,
               bu.NotificationEmail AS Recipient,
               bu.Name AS BusinessName,
               bu.DailySummaryTime AS ConfiguredTime
          FROM Businesses AS bu
         WHERE bu.NotificationEmail IS NOT NULL
           AND LTRIM(RTRIM(bu.NotificationEmail)) <> N''
           AND @nowTimeOfDay >= bu.DailySummaryTime
           AND bu.CreatedAt <= @now
           AND NOT EXISTS (
                SELECT 1
                  FROM Notifications AS existing
                 WHERE existing.DedupKey = @dedupKeyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @targetSuffix)
         ORDER BY bu.Id;
        """;

    /// <summary>
    /// Target-day rows of the bounded candidate set. Every join repeats the tenant predicate, the booking join keeps
    /// only the two active states, and the client join keeps only a ficha of the same business, so a row of another
    /// tenant can neither appear nor be counted as an active booking.
    /// </summary>
    private const string AppointmentSql = """
        SELECT a.BusinessId,
               a.Id AS AppointmentId,
               a.StartsAt,
               a.EndsAt,
               a.Kind,
               a.Status AS AppointmentStatus,
               s.Name AS ServiceName,
               CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName,
               b.Id AS BookingId,
               b.Status AS BookingStatus,
               CASE WHEN c.Id IS NULL THEN NULL ELSE CONCAT(c.FirstName, N' ', c.LastName) END AS ClientName
          FROM Appointments AS a
          JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
          JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
          LEFT JOIN Bookings AS b ON b.AppointmentId = a.Id AND b.BusinessId = a.BusinessId
               AND b.Status IN (@bookingPending, @bookingConfirmed)
          LEFT JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
         WHERE a.BusinessId IN ({0})
           AND a.Status = @scheduled
           AND a.StartsAt >= @targetStart AND a.StartsAt < @targetEndExclusive
         ORDER BY a.BusinessId, a.StartsAt, a.Id, b.Id;
        """;

    /// <summary>
    /// The one guarded write. It inserts a single row or none, so the row count is the atomic answer. The key is
    /// re-derived from the business id and the target day in both the guard and the inserted value, so a notice whose
    /// stored key was altered cannot bypass the canonical dedup, and the recipient is compared exactly — never under
    /// the database's case-insensitive collation — because it is the identity the body was composed for.
    /// </summary>
    private const string EnqueueSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, CreatedAt)
        SELECT @businessId, @type, @recipient, @subject, @bodyHtml,
               @dedupKeyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @targetSuffix, @notificationPending, 0, @createdAt
          FROM Businesses AS bu
         WHERE bu.Id = @businessId
           AND @type = @dailySummary
           AND bu.NotificationEmail IS NOT NULL
           AND bu.NotificationEmail = @recipient COLLATE Latin1_General_BIN2
           AND bu.DailySummaryTime = @configuredTime
           AND @createdAtTimeOfDay >= bu.DailySummaryTime
           AND bu.CreatedAt <= @createdAt
           AND @dedupKeyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @targetSuffix = @expectedDedupKey
           AND NOT EXISTS (
                SELECT 1
                  FROM Notifications AS existing
                 WHERE existing.DedupKey = @dedupKeyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @targetSuffix);
        """;

    public async Task<IReadOnlyList<DailySummaryCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default)
    {
        if (batchSize <= 0)
            return Array.Empty<DailySummaryCandidate>();

        // The last representable calendar day has no next day to summarize, so there is nothing to scan for it.
        var nowDay = DateOnly.FromDateTime(now);
        if (nowDay == DateOnly.MaxValue)
            return Array.Empty<DailySummaryCandidate>();

        var target = nowDay.AddDays(1);
        // The exclusive upper bound of the target day; the last representable day is bounded by its own start only,
        // because DateTime.MaxValue is the largest instant DATETIME2(0) can hold.
        var targetEndExclusive = target == DateOnly.MaxValue
            ? DateTime.MaxValue
            : target.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var businesses = await QueryAsync(
            CandidateSql,
            MapCandidateRow,
            p => p
                .AddDateTime2("@now", now)
                .AddValue("@nowTimeOfDay", TimeOnly.FromDateTime(now))
                .AddValue("@batchSize", batchSize)
                .AddValue("@dedupKeyPrefix", DedupKeyPrefix)
                .AddValue("@targetSuffix", TargetSuffix(target)),
            ct);

        if (businesses.Count == 0)
            return Array.Empty<DailySummaryCandidate>();

        var businessIds = businesses.Select(business => business.BusinessId).ToArray();
        var rows = await QueryAsync(
            BuildAppointmentSql(businessIds.Length),
            MapAppointmentRow,
            p =>
            {
                p.AddDateTime2("@targetStart", target.ToDateTime(TimeOnly.MinValue))
                    .AddDateTime2("@targetEndExclusive", targetEndExclusive)
                    .AddValue("@scheduled", AppointmentStatus.Scheduled)
                    .AddValue("@bookingPending", BookingStatus.Pending)
                    .AddValue("@bookingConfirmed", BookingStatus.Confirmed);
                for (var index = 0; index < businessIds.Length; index++)
                    p.AddValue($"@business{index}", businessIds[index]);
            },
            ct);

        var appointmentsByBusiness = GroupAppointments(rows);

        var candidates = new List<DailySummaryCandidate>(businesses.Count);
        foreach (var business in businesses)
        {
            var appointments = appointmentsByBusiness.TryGetValue(business.BusinessId, out var grouped)
                ? (IReadOnlyList<DailySummaryAppointment>)grouped
                : Array.Empty<DailySummaryAppointment>();
            candidates.Add(new DailySummaryCandidate(
                business.BusinessId,
                business.Recipient,
                business.BusinessName,
                business.ConfiguredTime,
                appointments));
        }

        return candidates;
    }

    public async Task<bool> TryEnqueueSummaryAsync(DailySummaryNotice notice, CancellationToken ct = default)
    {
        var createdAt = notice.Notification.CreatedAt;
        var createdDay = DateOnly.FromDateTime(createdAt);
        // No next day means no canonical key: the notice cannot be the summary of an agenda day.
        if (createdDay == DateOnly.MaxValue)
            return false;

        try
        {
            var inserted = await ExecuteAsync(
                EnqueueSql,
                p => p
                    .AddValue("@businessId", notice.BusinessId)
                    .AddValue("@type", notice.Notification.Type)
                    .AddValue("@recipient", notice.Notification.Recipient)
                    .AddValue("@subject", notice.Notification.Subject)
                    .AddValue("@bodyHtml", notice.Notification.BodyHtml)
                    .AddDateTime2("@createdAt", createdAt)
                    .AddValue("@createdAtTimeOfDay", TimeOnly.FromDateTime(createdAt))
                    .AddValue("@configuredTime", notice.ConfiguredTime)
                    .AddValue("@notificationPending", NotificationStatus.Pending)
                    .AddValue("@dailySummary", NotificationType.DailySummary)
                    .AddValue("@dedupKeyPrefix", DedupKeyPrefix)
                    .AddValue("@targetSuffix", TargetSuffix(createdDay.AddDays(1)))
                    .AddValue("@expectedDedupKey", notice.Notification.DedupKey),
                ct);

            return inserted == 1;
        }
        catch (SqlException exception) when (IsDedupKeyDuplicate(exception))
        {
            // The unique dedup key is the final race guard: a concurrent producer or a retried tick is a no-op.
            return false;
        }
    }

    /// <summary>
    /// Only a duplicate of <c>UQ_Notifications_DedupKey</c> is a benign race. Any other unique violation — or any
    /// other <see cref="SqlException"/> — propagates instead of being hidden as a false no-op.
    /// </summary>
    private static bool IsDedupKeyDuplicate(SqlException exception) =>
        exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is 2601 or 2627
                && error.Message.Contains("UQ_Notifications_DedupKey", StringComparison.Ordinal));

    /// <summary>Suffix of the canonical key that fixes the target day: <c>:{yyyyMMdd}</c>.</summary>
    private static string TargetSuffix(DateOnly target) =>
        ":" + target.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Builds the <c>IN</c> placeholder list; the ids themselves are always parameters.</summary>
    private static string BuildAppointmentSql(int businessCount)
    {
        var placeholders = string.Join(
            ", ", Enumerable.Range(0, businessCount).Select(index => $"@business{index}"));
        return string.Format(AppointmentSql, placeholders);
    }

    /// <summary>
    /// Folds the flat row set into candidates' appointments. The rows arrive ordered by business, start and
    /// appointment id, so each appointment is one contiguous run; an <b>individual</b> turn is kept only while it has
    /// at least one active booking, while a <b>class session</b> is kept even with none.
    /// </summary>
    private static Dictionary<int, List<DailySummaryAppointment>> GroupAppointments(IReadOnlyList<AppointmentRow> rows)
    {
        var byBusiness = new Dictionary<int, List<DailySummaryAppointment>>();
        var index = 0;

        while (index < rows.Count)
        {
            var head = rows[index];
            var bookings = new List<DailySummaryBooking>();

            while (index < rows.Count
                && rows[index].BusinessId == head.BusinessId
                && rows[index].AppointmentId == head.AppointmentId)
            {
                var row = rows[index];
                // A booking only counts when its client belongs to the same business: a foreign ficha is not a booking
                // this tenant may even acknowledge.
                if (row.BookingId is int bookingId
                    && row.ClientName is not null
                    && row.BookingStatus is BookingStatus.Pending or BookingStatus.Confirmed)
                {
                    bookings.Add(new DailySummaryBooking(bookingId, row.ClientName, row.BookingStatus.Value));
                }

                index++;
            }

            if (head.Kind == AppointmentKind.ClassSession || bookings.Count > 0)
            {
                if (!byBusiness.TryGetValue(head.BusinessId, out var appointments))
                    byBusiness[head.BusinessId] = appointments = [];
                appointments.Add(new DailySummaryAppointment(
                    head.AppointmentId,
                    head.StartsAt,
                    head.EndsAt,
                    head.Kind,
                    head.Status,
                    head.ServiceName,
                    head.WorkerName,
                    bookings));
            }
        }

        return byBusiness;
    }

    private static BusinessRow MapCandidateRow(SqlDataReader reader) => new(
        reader.Get<int>("BusinessId"),
        reader.Get<string>("Recipient"),
        reader.Get<string>("BusinessName"),
        reader.Get<TimeOnly>("ConfiguredTime"));

    private static AppointmentRow MapAppointmentRow(SqlDataReader reader) => new(
        reader.Get<int>("BusinessId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("EndsAt"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.Get<string>("ServiceName"),
        reader.Get<string>("WorkerName"),
        reader.GetNullable<int>("BookingId"),
        reader.GetNullableEnum<BookingStatus>("BookingStatus"),
        reader.GetNullableString("ClientName"));

    private sealed record BusinessRow(
        int BusinessId, string Recipient, string BusinessName, TimeOnly ConfiguredTime);

    private sealed record AppointmentRow(
        int BusinessId,
        int AppointmentId,
        DateTime StartsAt,
        DateTime EndsAt,
        AppointmentKind Kind,
        AppointmentStatus Status,
        string ServiceName,
        string WorkerName,
        int? BookingId,
        BookingStatus? BookingStatus,
        string? ClientName);
}
