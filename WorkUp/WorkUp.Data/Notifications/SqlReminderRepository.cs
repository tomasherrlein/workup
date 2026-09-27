using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Notifications;

/// <summary>
/// ADO.NET persistence of the scheduled reminder port (T24, RF-17). Every command runs on the request connection and
/// joins the caller's transaction if one exists; this repository never opens a transaction of its own, so no
/// transaction can span the scan, the batch and the send.
/// <para>
/// <see cref="ListCandidatesAsync"/> is the global, bounded read half: one <c>TOP (batchSize)</c> statement ordered by
/// the appointment start then the booking id, joining every table with the tenant predicate on the same
/// <c>BusinessId</c> so a candidate can never carry a ficha, service or worker of another business. It returns the
/// strict complement of the immediate producers after commit <c>4223b63</c>: only a booking created strictly outside
/// the business' own anticipation (<c>CreatedAt &lt; StartsAt - leadHours</c>) whose lead instant already arrived
/// (<c>StartsAt - leadHours &lt;= now &lt; StartsAt</c>), on a scheduled appointment, still <c>Pending</c> or
/// <c>Confirmed</c>, of an individual (<c>Modality IS NULL</c>) or drop-in (<c>Modality = DropIn</c>) shape with no
/// enrollment, with neither the scheduled key <c>reminder:booking:{id}</c> nor, for an individual booking, the
/// immediate key <c>booking:{id}</c> typed <see cref="NotificationType.AppointmentReminder"/> already present. A
/// separate bounded statement then loads the live private links of exactly those bookings, so the core can detect zero
/// or multiple live links. A <see cref="NotificationType.BookingCreated"/> under <c>booking:{id}</c> never suppresses
/// the reminder.
/// </para>
/// <para>
/// <see cref="TryEnqueueReminderAsync"/> is <b>one</b> guarded <c>INSERT ... SELECT</c>, never a transaction: the same
/// predicates are re-derived against the <em>current</em> rows at the notice instant and the row is inserted only when
/// every guard still holds — business scope, appointment kind against booking modality, no fixed enrollment, status,
/// suspension, the lead window, the immediate key, and, for an individual booking, that exactly <b>one</b> live private
/// link remains and that it is the <b>same</b> <see cref="ReminderNotice.Link"/> identity the body was built from. A
/// drop-in carries no link and passes the link guard when <see cref="ReminderNotice.Link"/> is <c>null</c>. The
/// scheduled key is re-derived from the booking id, so the canonical dedup cannot be bypassed with an altered key, and
/// <c>UQ_Notifications_DedupKey</c> is the final race guard: a genuinely concurrent producer surfaces as a duplicate
/// key error (<c>2601</c>/<c>2627</c>) that is converted into a <c>false</c> no-op, never into a second email. No other
/// <see cref="SqlException"/> is swallowed.
/// </para>
/// <para>
/// Nothing here logs and <see cref="Notification.LastError"/> is never written. The raw token lives only inside
/// <see cref="Notification.BodyHtml"/>, and the token hash travels as a <c>BINARY(32)</c> parameter, never as text.
/// </para>
/// </summary>
public sealed class SqlReminderRepository(DbSession session) : SqlRepositoryBase(session), IReminderRepository
{
    private const string ScheduledKeyPrefix = "reminder:booking:";
    private const string ImmediateKeyPrefix = "booking:";

    /// <summary>
    /// Global bounded scan. The <c>TOP</c> is what keeps one tick small, and <c>a.StartsAt, b.Id</c> is the
    /// deterministic order the service relies on. Every join repeats the tenant predicate, and the two <c>NOT
    /// EXISTS</c> clauses are the dedup half of the immediate complement.
    /// </summary>
    private const string CandidateSql = """
        SELECT TOP (@batchSize)
               b.Id AS BookingId,
               b.BusinessId,
               b.AppointmentId,
               a.Kind,
               b.Modality,
               b.EnrollmentId,
               b.Status,
               a.Status AS AppointmentStatus,
               a.StartsAt,
               b.CreatedAt,
               bu.ReminderLeadHours,
               c.Email AS Recipient,
               c.FirstName AS ClientFirstName,
               bu.Name AS BusinessName,
               s.Name AS ServiceName,
               CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
          JOIN Businesses AS bu ON bu.Id = b.BusinessId
          JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
          JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = a.BusinessId
          JOIN Workers AS w ON w.Id = a.WorkerId AND w.BusinessId = a.BusinessId
         WHERE a.Status = @appointmentScheduled
           AND b.Status IN (@bookingPending, @bookingConfirmed)
           AND b.EnrollmentId IS NULL
           AND (
                (b.Modality IS NULL AND a.Kind = @individual)
                OR (b.Modality = @dropIn AND a.Kind = @classSession)
           )
           AND a.StartsAt > @now
           AND bu.ReminderLeadHours > 0
           AND DATEADD(hour, -bu.ReminderLeadHours, a.StartsAt) <= @now
           AND b.CreatedAt < DATEADD(hour, -bu.ReminderLeadHours, a.StartsAt)
           AND NOT EXISTS (
                SELECT 1 FROM Notifications AS scheduledNotice
                 WHERE scheduledNotice.DedupKey = @scheduledKeyPrefix + CAST(b.Id AS NVARCHAR(10)))
           AND NOT EXISTS (
                SELECT 1 FROM Notifications AS immediateNotice
                 WHERE b.Modality IS NULL
                   AND immediateNotice.DedupKey = @immediateKeyPrefix + CAST(b.Id AS NVARCHAR(10))
                   AND immediateNotice.Type = @appointmentReminder)
         ORDER BY a.StartsAt, b.Id;
        """;

    /// <summary>
    /// Live private links of the bounded candidate set, with the tenant join kept so a link can only be read through a
    /// booking and appointment of the same business. Only live rows are returned: a revoked or expired link can never
    /// be part of the exactly-one-live-link decision.
    /// </summary>
    private const string LiveLinksSql = """
        SELECT l.BookingId, l.Id AS LinkId, l.TokenHash, l.ProtectedToken, l.ExpiresAt, l.RevokedAt
          FROM AppointmentLinks AS l
          JOIN Bookings AS b ON b.Id = l.BookingId
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE l.BookingId IN ({0})
           AND l.RevokedAt IS NULL
           AND l.ExpiresAt > @now
         ORDER BY l.BookingId, l.Id;
        """;

    /// <summary>
    /// The one guarded write. It inserts a single row or none, so the row count is the atomic answer, and the unique
    /// dedup key of <c>Notifications</c> is the final race guard. The scheduled key is derived from <c>b.Id</c> in both
    /// the guard and the inserted value, so a notice whose stored key was altered cannot bypass the canonical dedup.
    /// </summary>
    private const string EnqueueSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, CreatedAt)
        SELECT @businessId, @type, @recipient, @subject, @bodyHtml,
               @scheduledKeyPrefix + CAST(b.Id AS NVARCHAR(10)), @notificationPending, 0, @createdAt
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
          JOIN Businesses AS bu ON bu.Id = b.BusinessId
         WHERE b.Id = @bookingId
           AND b.BusinessId = @businessId
           AND b.AppointmentId = @appointmentId
           AND a.Status = @appointmentScheduled
           AND b.Status IN (@bookingPending, @bookingConfirmed)
           AND b.EnrollmentId IS NULL
           AND (
                (b.Modality IS NULL AND a.Kind = @individual)
                OR (b.Modality = @dropIn AND a.Kind = @classSession)
           )
           AND a.StartsAt > @createdAt
           AND bu.ReminderLeadHours > 0
           AND DATEADD(hour, -bu.ReminderLeadHours, a.StartsAt) <= @createdAt
           AND b.CreatedAt < DATEADD(hour, -bu.ReminderLeadHours, a.StartsAt)
           AND NOT EXISTS (
                SELECT 1 FROM Notifications AS scheduledNotice
                 WHERE scheduledNotice.DedupKey = @scheduledKeyPrefix + CAST(b.Id AS NVARCHAR(10)))
           AND NOT EXISTS (
                SELECT 1 FROM Notifications AS immediateNotice
                 WHERE b.Modality IS NULL
                   AND immediateNotice.DedupKey = @immediateKeyPrefix + CAST(b.Id AS NVARCHAR(10))
                   AND immediateNotice.Type = @appointmentReminder)
           AND (
                (b.Modality = @dropIn AND @linkId IS NULL)
                OR (b.Modality IS NULL
                    AND @linkId IS NOT NULL
                    AND @tokenHash IS NOT NULL
                    AND (SELECT COUNT(*) FROM AppointmentLinks AS liveLink
                          WHERE liveLink.BookingId = b.Id
                            AND liveLink.RevokedAt IS NULL
                            AND liveLink.ExpiresAt > @createdAt) = 1
                    AND EXISTS (
                         SELECT 1 FROM AppointmentLinks AS reviewedLink
                          WHERE reviewedLink.BookingId = b.Id
                            AND reviewedLink.Id = @linkId
                            AND reviewedLink.TokenHash = @tokenHash
                            AND reviewedLink.RevokedAt IS NULL
                            AND reviewedLink.ExpiresAt > @createdAt))
           );
        """;

    public async Task<IReadOnlyList<ReminderCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default)
    {
        if (batchSize <= 0)
            return Array.Empty<ReminderCandidate>();

        var rows = await QueryAsync(
            CandidateSql,
            MapCandidateRow,
            p => p
                .AddDateTime2("@now", now)
                .AddValue("@batchSize", batchSize)
                .AddValue("@appointmentScheduled", AppointmentStatus.Scheduled)
                .AddValue("@bookingPending", BookingStatus.Pending)
                .AddValue("@bookingConfirmed", BookingStatus.Confirmed)
                .AddValue("@individual", AppointmentKind.Individual)
                .AddValue("@classSession", AppointmentKind.ClassSession)
                .AddValue("@dropIn", BookingModality.DropIn)
                .AddValue("@appointmentReminder", NotificationType.AppointmentReminder)
                .AddValue("@scheduledKeyPrefix", ScheduledKeyPrefix)
                .AddValue("@immediateKeyPrefix", ImmediateKeyPrefix),
            ct);

        if (rows.Count == 0)
            return Array.Empty<ReminderCandidate>();

        var bookingIds = rows.Select(row => row.BookingId).ToArray();
        var links = await QueryAsync(
            BuildLiveLinksSql(bookingIds.Length),
            row => (row.Get<int>("BookingId"), Link: MapLink(row)),
            p =>
            {
                p.AddDateTime2("@now", now);
                for (var index = 0; index < bookingIds.Length; index++)
                    p.AddValue($"@booking{index}", bookingIds[index]);
            },
            ct);

        var linksByBooking = new Dictionary<int, List<ReminderLink>>();
        foreach (var (bookingId, link) in links)
        {
            if (!linksByBooking.TryGetValue(bookingId, out var bookingLinks))
                linksByBooking[bookingId] = bookingLinks = [];
            bookingLinks.Add(link);
        }

        var candidates = new List<ReminderCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var candidateLinks = linksByBooking.TryGetValue(row.BookingId, out var bookingLinks)
                ? bookingLinks.ToArray()
                : Array.Empty<ReminderLink>();
            candidates.Add(new ReminderCandidate(
                row.BookingId,
                row.BusinessId,
                row.AppointmentId,
                row.Kind,
                row.Modality,
                row.EnrollmentId,
                row.Status,
                row.AppointmentStatus,
                row.StartsAt,
                row.CreatedAt,
                row.ReminderLeadHours,
                row.Recipient,
                row.ClientFirstName,
                row.BusinessName,
                row.ServiceName,
                row.WorkerName,
                candidateLinks));
        }

        return candidates;
    }

    public async Task<bool> TryEnqueueReminderAsync(ReminderNotice notice, CancellationToken ct = default)
    {
        var tokenHash = notice.Link?.TokenHash;

        try
        {
            var inserted = await ExecuteAsync(
                EnqueueSql,
                p =>
                {
                    p.AddValue("@businessId", notice.Notification.BusinessId)
                        .AddValue("@type", notice.Notification.Type)
                        .AddValue("@recipient", notice.Notification.Recipient)
                        .AddValue("@subject", notice.Notification.Subject)
                        .AddValue("@bodyHtml", notice.Notification.BodyHtml)
                        .AddDateTime2("@createdAt", notice.Notification.CreatedAt)
                        .AddValue("@bookingId", notice.BookingId)
                        .AddValue("@appointmentId", notice.AppointmentId)
                        .AddValue("@appointmentScheduled", AppointmentStatus.Scheduled)
                        .AddValue("@bookingPending", BookingStatus.Pending)
                        .AddValue("@bookingConfirmed", BookingStatus.Confirmed)
                        .AddValue("@notificationPending", NotificationStatus.Pending)
                        .AddValue("@individual", AppointmentKind.Individual)
                        .AddValue("@classSession", AppointmentKind.ClassSession)
                        .AddValue("@dropIn", BookingModality.DropIn)
                        .AddValue("@appointmentReminder", NotificationType.AppointmentReminder)
                        .AddValue("@scheduledKeyPrefix", ScheduledKeyPrefix)
                        .AddValue("@immediateKeyPrefix", ImmediateKeyPrefix)
                        .AddValue("@linkId", notice.Link?.LinkId);
                    AddTokenHash(p, tokenHash);
                },
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
    /// Binds the token hash explicitly as <c>BINARY(32)</c> so a <c>null</c> never becomes a string parameter and a
    /// malformed length can never be implicitly converted. The raw token is never a parameter: only its hash travels.
    /// </summary>
    private static void AddTokenHash(SqlParameterCollection parameters, byte[]? tokenHash)
    {
        var parameter = parameters.Add("@tokenHash", SqlDbType.Binary, 32);
        parameter.Value = tokenHash is null ? DBNull.Value : tokenHash;
    }

    /// <summary>
    /// Only a duplicate of <c>UQ_Notifications_DedupKey</c> is a benign race. Any other unique violation — or any other
    /// <see cref="SqlException"/> — propagates instead of being hidden as a false no-op.
    /// </summary>
    private static bool IsDedupKeyDuplicate(SqlException exception) =>
        exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is 2601 or 2627
                && error.Message.Contains("UQ_Notifications_DedupKey", StringComparison.Ordinal));

    /// <summary>Builds the <c>IN</c> placeholder list; the ids themselves are always parameters.</summary>
    private static string BuildLiveLinksSql(int bookingCount)
    {
        var placeholders = string.Join(", ", Enumerable.Range(0, bookingCount).Select(index => $"@booking{index}"));
        return string.Format(LiveLinksSql, placeholders);
    }

    private static CandidateRow MapCandidateRow(SqlDataReader reader) => new(
        reader.Get<int>("BookingId"),
        reader.Get<int>("BusinessId"),
        reader.Get<int>("AppointmentId"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetNullableEnum<BookingModality>("Modality"),
        reader.GetNullable<int>("EnrollmentId"),
        reader.GetEnum<BookingStatus>("Status"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<DateTime>("CreatedAt"),
        reader.Get<int>("ReminderLeadHours"),
        reader.Get<string>("Recipient"),
        reader.Get<string>("ClientFirstName"),
        reader.Get<string>("BusinessName"),
        reader.Get<string>("ServiceName"),
        reader.Get<string>("WorkerName"));

    private static ReminderLink MapLink(SqlDataReader reader) => new(
        reader.Get<int>("LinkId"),
        reader.Get<byte[]>("TokenHash"),
        reader.Get<string>("ProtectedToken"),
        reader.Get<DateTime>("ExpiresAt"),
        reader.GetNullable<DateTime>("RevokedAt"));

    private sealed record CandidateRow(
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
        string WorkerName);
}
