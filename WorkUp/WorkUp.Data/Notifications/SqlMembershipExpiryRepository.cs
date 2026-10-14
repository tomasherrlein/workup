using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Notifications;

/// <summary>
/// ADO.NET persistence of the membership expiry port (T25, RF-29, RF-30, D-22, D-23). Every command runs on the
/// request connection and joins the caller's transaction if one exists; this repository never opens a transaction of
/// its own, so no transaction spans the scan, the batch and the enqueue.
/// <para>
/// <see cref="ListCandidatesAsync"/> is the global, bounded read half: one <c>TOP (batchSize)</c> statement ordered by
/// enrollment id. For each <c>Active</c> enrollment it selects the latest persisted <c>MembershipPeriod</c> by
/// <c>(ExpiresOn DESC, Id DESC)</c> through a <c>CROSS APPLY</c>, so a superseded period is never returned and the
/// projected <see cref="MembershipExpiryCandidate.HasLaterPeriod"/> is always <c>false</c>. Both the enrollment and the
/// period must have been created no later than the scan instant, so a T26 clock rewind never reaches a row that did
/// not exist yet. The recipient is the same-tenant client record email (<c>Clients.Email</c>, joined on business and
/// client): the account email is never read, and a forged enrollment that points at another business' ficha does not
/// join at all. No period is ever filtered by payment annulment: annulling a payment is a purely financial correction
/// (T18) that keeps the coverage of the period it paid. A period is due only when the local date at <paramref name="now"/>
/// falls in the reminder range <c>[ExpiresOn - 3, ExpiresOn)</c> or is exactly <c>ExpiresOn</c> for the at-expiry
/// stage; the exact stage key must still be absent, and after a rewind the earlier reminder is also suppressed when the
/// at-expiry key of the same period already exists, so the expired notice can never be followed by the reminder. The
/// email <em>shape</em> is the core's job (<c>EmailFormat.IsValid</c>): SQL applies only the presence guard, so a blank
/// recipient is never scanned while a merely malformed one is skipped by the service before composing.
/// </para>
/// <para>
/// <see cref="TryEnqueueNoticeAsync"/> is <b>one</b> guarded <c>INSERT ... SELECT</c>, never a transaction: the same
/// guards are re-derived against the <em>current</em> rows at the notice instant — the enrollment still exists, still
/// belongs to the same business and client, is still <c>Active</c> and existed at the instant, its client record email
/// still matches the composed one exactly (never under the database's case-insensitive collation), the same period is
/// still the latest one with the same <see cref="MembershipExpiryCandidate.ExpiresOn"/>, the composed instant still
/// falls inside the canonical <see cref="MembershipExpiryStage"/> window, the type and the canonical key over the live
/// enrollment and period match, the exact key is still absent and no later stage of the same period exists. The row
/// count is the atomic answer, and <c>UQ_Notifications_DedupKey</c> is the final race guard: a genuinely concurrent or
/// retried producer surfaces as a duplicate key error (<c>2601</c>/<c>2627</c>) converted into a <c>false</c> no-op,
/// never into a second email. No other <see cref="SqlException"/> is swallowed.
/// </para>
/// <para>
/// Nothing here logs and <see cref="WorkUp.Domain.Entities.Notification.LastError"/> is never written: the notice is
/// either inserted whole or not at all. No payment, period or enrollment row is ever mutated.
/// </para>
/// </summary>
public sealed class SqlMembershipExpiryRepository(DbSession session)
    : SqlRepositoryBase(session), IMembershipExpiryRepository
{
    private const string KeyPrefix = "membership:enrollment:";
    private const string PeriodMarker = ":period:";
    private const string ExpiringKey = "expiring";
    private const string ExpiredKey = "expired";

    /// <summary>
    /// Global bounded enrollment scan. The <c>TOP</c> keeps one tick small, <c>e.Id</c> is the deterministic order the
    /// service relies on, the <c>CROSS APPLY</c> selects the one latest period (never a superseded one) whose creation
    /// did not postdate the scan, and the two <c>NOT EXISTS</c> clauses are the stage key plus the no-later-stage
    /// rewind guard. The stage is computed once and reused by both guards.
    /// </summary>
    private const string CandidateSql = """
        SELECT TOP (@batchSize)
               e.Id AS EnrollmentId,
               e.ClientId AS ClientId,
               e.BusinessId AS BusinessId,
               latest.PeriodId AS PeriodId,
               e.Status AS Status,
               latest.ExpiresOn AS ExpiresOn,
               c.Email AS Recipient,
               CONCAT(c.FirstName, N' ', c.LastName) AS ClientName,
               b.Name AS BusinessName
          FROM Enrollments AS e
          JOIN Businesses AS b ON b.Id = e.BusinessId
          JOIN Clients AS c ON c.Id = e.ClientId AND c.BusinessId = e.BusinessId
         CROSS APPLY (
              SELECT TOP (1) p.Id AS PeriodId, p.ExpiresOn AS ExpiresOn
                FROM MembershipPeriods AS p
               WHERE p.EnrollmentId = e.Id
                 AND p.CreatedAt <= @now
               ORDER BY p.ExpiresOn DESC, p.Id DESC
         ) AS latest
         CROSS APPLY (
              SELECT CASE
                       WHEN CAST(@now AS date) = latest.ExpiresOn THEN @stageExpired
                       WHEN CAST(@now AS date) >= DATEADD(day, -@threshold, latest.ExpiresOn)
                            AND CAST(@now AS date) < latest.ExpiresOn THEN @stageExpiring
                       ELSE 0
                     END AS Stage
         ) AS due
         WHERE e.Status = @active
           AND e.CreatedAt <= @now
           AND c.Email IS NOT NULL
           AND LTRIM(RTRIM(c.Email)) <> N''
           AND due.Stage <> 0
           AND NOT EXISTS (
                SELECT 1
                  FROM Notifications AS existing
                 WHERE existing.DedupKey =
                       @keyPrefix + CAST(e.Id AS NVARCHAR(10)) + @periodMarker + CAST(latest.PeriodId AS NVARCHAR(10)) + N':' +
                       (CASE due.Stage WHEN @stageExpiring THEN @expiringKey ELSE @expiredKey END))
           AND (due.Stage <> @stageExpiring OR NOT EXISTS (
                SELECT 1
                  FROM Notifications AS laterStage
                 WHERE laterStage.Type = @membershipExpired
                   AND laterStage.BusinessId = e.BusinessId
                   AND laterStage.DedupKey =
                       @keyPrefix + CAST(e.Id AS NVARCHAR(10)) + @periodMarker + CAST(latest.PeriodId AS NVARCHAR(10)) + N':' + @expiredKey))
         ORDER BY e.Id;
        """;

    /// <summary>
    /// The one guarded write. It inserts a single row or none, so the row count is the atomic answer. The same period
    /// must still be the latest one created no later than the composed instant and carry the same expiry, the live
    /// recipient and status must still match, the stage window and the canonical type are re-derived, the canonical key
    /// over the live enrollment and period is compared to the composed one, and the no-later-stage rule is re-applied.
    /// A notice whose stored key was altered cannot bypass the canonical dedup.
    /// </summary>
    private const string EnqueueSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, CreatedAt)
        SELECT @businessId, @type, @recipient, @subject, @bodyHtml,
               @keyPrefix + CAST(e.Id AS NVARCHAR(10)) + @periodMarker + CAST(latest.PeriodId AS NVARCHAR(10)) + N':' +
               (CASE @stage WHEN @stageExpiring THEN @expiringKey ELSE @expiredKey END),
               @notificationPending, 0, @createdAt
          FROM Enrollments AS e
          JOIN Clients AS c ON c.Id = e.ClientId AND c.BusinessId = e.BusinessId
         CROSS APPLY (
              SELECT TOP (1) p.Id AS PeriodId, p.ExpiresOn AS ExpiresOn
                FROM MembershipPeriods AS p
               WHERE p.EnrollmentId = e.Id
                 AND p.CreatedAt <= @createdAt
               ORDER BY p.ExpiresOn DESC, p.Id DESC
         ) AS latest
         WHERE e.Id = @enrollmentId
           AND e.BusinessId = @businessId
           AND e.ClientId = @clientId
           AND e.Status = @active
           AND e.CreatedAt <= @createdAt
           AND c.Email IS NOT NULL
           AND LTRIM(RTRIM(c.Email)) <> N''
           AND c.Email = @recipient COLLATE Latin1_General_BIN2
           AND latest.PeriodId = @periodId
           AND latest.ExpiresOn = @expiresOn
           AND @type = CASE @stage WHEN @stageExpiring THEN @membershipExpiring ELSE @membershipExpired END
           AND (
                (@stage = @stageExpired AND CAST(@createdAt AS date) = @expiresOn)
             OR (@stage = @stageExpiring
                 AND CAST(@createdAt AS date) >= DATEADD(day, -@threshold, @expiresOn)
                 AND CAST(@createdAt AS date) < @expiresOn)
           )
           AND @keyPrefix + CAST(e.Id AS NVARCHAR(10)) + @periodMarker + CAST(latest.PeriodId AS NVARCHAR(10)) + N':' +
               (CASE @stage WHEN @stageExpiring THEN @expiringKey ELSE @expiredKey END) = @expectedDedupKey
           AND NOT EXISTS (
                SELECT 1
                  FROM Notifications AS existing
                 WHERE existing.DedupKey = @expectedDedupKey)
           AND (@stage <> @stageExpiring OR NOT EXISTS (
                SELECT 1
                  FROM Notifications AS laterStage
                 WHERE laterStage.Type = @membershipExpired
                   AND laterStage.BusinessId = e.BusinessId
                   AND laterStage.DedupKey =
                       @keyPrefix + CAST(e.Id AS NVARCHAR(10)) + @periodMarker + CAST(latest.PeriodId AS NVARCHAR(10)) + N':' + @expiredKey));
        """;

    public async Task<IReadOnlyList<MembershipExpiryCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default)
    {
        if (batchSize <= 0)
            return Array.Empty<MembershipExpiryCandidate>();

        return await QueryAsync(
            CandidateSql,
            MapCandidateRow,
            p => p
                .AddDateTime2("@now", now)
                .AddValue("@batchSize", batchSize)
                .AddValue("@active", EnrollmentStatus.Active)
                .AddValue("@stageExpiring", MembershipExpiryStage.Expiring)
                .AddValue("@stageExpired", MembershipExpiryStage.Expired)
                .AddValue("@membershipExpired", NotificationType.MembershipExpired)
                .AddValue("@threshold", MembershipListRules.ExpiresSoonThresholdDays)
                .AddValue("@keyPrefix", KeyPrefix)
                .AddValue("@periodMarker", PeriodMarker)
                .AddValue("@expiringKey", ExpiringKey)
                .AddValue("@expiredKey", ExpiredKey),
            ct);
    }

    public async Task<bool> TryEnqueueNoticeAsync(MembershipExpiryNotice notice, CancellationToken ct = default)
    {
        try
        {
            var inserted = await ExecuteAsync(
                EnqueueSql,
                p => p
                    .AddValue("@enrollmentId", notice.EnrollmentId)
                    .AddValue("@clientId", notice.ClientId)
                    .AddValue("@businessId", notice.BusinessId)
                    .AddValue("@periodId", notice.PeriodId)
                    .AddValue("@expiresOn", notice.ExpiresOn)
                    .AddValue("@stage", notice.Stage)
                    .AddValue("@type", notice.Notification.Type)
                    .AddValue("@recipient", notice.Notification.Recipient)
                    .AddValue("@subject", notice.Notification.Subject)
                    .AddValue("@bodyHtml", notice.Notification.BodyHtml)
                    .AddDateTime2("@createdAt", notice.Notification.CreatedAt)
                    .AddValue("@notificationPending", NotificationStatus.Pending)
                    .AddValue("@active", EnrollmentStatus.Active)
                    .AddValue("@stageExpiring", MembershipExpiryStage.Expiring)
                    .AddValue("@stageExpired", MembershipExpiryStage.Expired)
                    .AddValue("@membershipExpiring", NotificationType.MembershipExpiring)
                    .AddValue("@membershipExpired", NotificationType.MembershipExpired)
                    .AddValue("@threshold", MembershipListRules.ExpiresSoonThresholdDays)
                    .AddValue("@keyPrefix", KeyPrefix)
                    .AddValue("@periodMarker", PeriodMarker)
                    .AddValue("@expiringKey", ExpiringKey)
                    .AddValue("@expiredKey", ExpiredKey)
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

    private static MembershipExpiryCandidate MapCandidateRow(SqlDataReader reader) => new(
        reader.Get<int>("EnrollmentId"),
        reader.Get<int>("ClientId"),
        reader.Get<int>("BusinessId"),
        reader.Get<int>("PeriodId"),
        reader.GetEnum<EnrollmentStatus>("Status"),
        reader.Get<DateOnly>("ExpiresOn"),
        // The latest period was selected, so no newer period can exist for this candidate.
        HasLaterPeriod: false,
        reader.Get<string>("Recipient"),
        reader.Get<string>("ClientName"),
        reader.Get<string>("BusinessName"));
}
