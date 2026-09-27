using System.Globalization;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Notifications;

/// <summary>
/// ADO.NET persistence of the stock-review port (T25, RF-19). Every command runs on the request connection and joins
/// the caller's transaction if one exists; this repository never opens a transaction of its own, so no transaction can
/// span the scan, the batch and the enqueue.
/// <para>
/// <see cref="ListCandidatesAsync"/> is the global, bounded read half: one <c>TOP (batchSize)</c> statement ordered by
/// business id, then one bounded statement loading only the active supplies of exactly those businesses. A business is
/// only a candidate when its notice email is present and not blank, its current period is already due, and its
/// current-bucket key <c>stock:business:{id}:on:{yyyyMMdd}</c> is still absent while no <b>later</b> canonical bucket
/// exists. The anchor is the permanent <b>local calendar date</b> of <c>Businesses.CreatedAt</c>: the first review is
/// due after <c>StockReviewEveryDays</c> calendar days and every N days afterwards, so the current bucket is
/// <c>anchor + floor(diffDays / N) * N</c> — a missed exact day still yields one review for the current period, and
/// the periods the host never observed are not backfilled one by one. The supplied instant never reaches a bucket that
/// predates the anchor, because a non-positive elapsed day count is filtered out. The email <em>shape</em> is the
/// core's job (<c>EmailFormat.IsValid</c>): SQL applies only the presence guard, so a blank recipient can never be
/// scanned while a merely malformed one is skipped by the service before composing.
/// </para>
/// <para>
/// The supply half repeats the business-id predicate on every row, so a candidate can never carry a supply of another
/// business, and only <c>IsActive = 1</c> rows are read; a business with zero registered supplies is still a candidate
/// with an empty list, because RF-19 asks the owner to keep the declared stock current. The rows arrive ordered by
/// business id and supply id, so the body never depends on the reader's ordering.
/// </para>
/// <para>
/// <see cref="TryEnqueueReviewAsync"/> is <b>one</b> guarded <c>INSERT ... SELECT</c>, never a transaction: the same
/// guards are re-derived against the <em>current</em> business row at the notice instant — the row still exists, the
/// recipient is still the one the body was composed for (compared exactly, never under the database's
/// case-insensitive collation), the anchor calendar date and <c>StockReviewEveryDays</c> are unchanged, the composed
/// instant still falls inside the notice's due bucket under the live anchor and cadence, and the canonical
/// <see cref="NotificationType.StockReview"/> key over the current bucket matches the composed one and is still
/// absent while no later canonical bucket exists. The row count is the atomic answer, and
/// <c>UQ_Notifications_DedupKey</c> is the final race guard: a genuinely concurrent or retried producer surfaces as a
/// duplicate key error (<c>2601</c>/<c>2627</c>) converted into a <c>false</c> no-op, never into a second email. No
/// other <see cref="SqlException"/> is swallowed.
/// </para>
/// <para>
/// The later-bucket guard is restricted to rows of the same <see cref="NotificationType.StockReview"/>,
/// <c>BusinessId</c> and the canonical key shape <c>stock:business:{id}:on:{yyyyMMdd}</c>, so a foreign tenant, a
/// different type, or a malformed key can never suppress a legitimate review. Comparing the fixed-width canonical keys
/// as text is a date comparison over buckets, which is what makes a T26 clock rewind refuse to resurrect an obsolete
/// period.
/// </para>
/// <para>
/// Nothing here logs and <see cref="WorkUp.Domain.Entities.Notification.LastError"/> is never written: the notice is
/// either inserted whole or not at all. Stock is never written either: the review only reports.
/// </para>
/// </summary>
public sealed class SqlStockReviewRepository(DbSession session) : SqlRepositoryBase(session), IStockReviewRepository
{
    private const string KeyPrefix = "stock:business:";
    private const string OnMarker = ":on:";

    /// <summary>
    /// Exactly the canonical eight-digit <c>yyyyMMdd</c> suffix, no more and no less, so a malformed or truncated
    /// <c>DedupKey</c> can never be mistaken for a real bucket of the same business.
    /// </summary>
    private const string DatePattern = "[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]";

    /// <summary>
    /// Global bounded business scan. The <c>TOP</c> keeps one tick small, <c>bu.Id</c> is the deterministic order the
    /// service relies on, and the two <c>NOT EXISTS</c> clauses are the once-per-bucket half plus the later-bucket
    /// rewind guard. The bucket is computed once through the chained <c>CROSS APPLY</c> and reused by both guards.
    /// </summary>
    private const string CandidateSql = """
        SELECT TOP (@batchSize)
               bu.Id AS BusinessId,
               bu.NotificationEmail AS Recipient,
               bu.Name AS BusinessName,
               CAST(bu.CreatedAt AS date) AS AnchorDate,
               bu.StockReviewEveryDays AS EveryDays,
               due.DueDate AS DueDate
          FROM Businesses AS bu
         CROSS APPLY (
              SELECT DATEDIFF(day, CAST(bu.CreatedAt AS date), CAST(@now AS date)) AS DiffDays
         ) AS diff
         CROSS APPLY (
              SELECT DATEADD(day,
                     (diff.DiffDays / NULLIF(bu.StockReviewEveryDays, 0)) * bu.StockReviewEveryDays,
                     CAST(bu.CreatedAt AS date)) AS DueDate
         ) AS due
         WHERE bu.NotificationEmail IS NOT NULL
           AND LTRIM(RTRIM(bu.NotificationEmail)) <> N''
           AND bu.StockReviewEveryDays >= 1
           AND due.DueDate IS NOT NULL
           AND diff.DiffDays >= bu.StockReviewEveryDays
           AND NOT EXISTS (
                SELECT 1
                  FROM Notifications AS existing
                 WHERE existing.DedupKey =
                       @keyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @onMarker + CONVERT(NVARCHAR(8), due.DueDate, 112))
           AND NOT EXISTS (
                SELECT 1
                  FROM Notifications AS later
                 WHERE later.Type = @stockReview
                   AND later.BusinessId = bu.Id
                   AND later.DedupKey LIKE @keyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @onMarker + @datePattern
                   AND later.DedupKey >
                       @keyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @onMarker + CONVERT(NVARCHAR(8), due.DueDate, 112))
         ORDER BY bu.Id;
        """;

    /// <summary>
    /// Active supplies of the bounded candidate set. The business-id predicate is the tenant scope; only
    /// <c>IsActive = 1</c> rows survive, and the order by business and supply id is the order the body uses.
    /// </summary>
    private const string SupplySql = """
        SELECT s.BusinessId,
               s.Id AS SupplyId,
               s.Name,
               s.CurrentStock,
               s.MinimumStock,
               s.IsActive
          FROM Supplies AS s
         WHERE s.BusinessId IN ({0})
           AND s.IsActive = 1
         ORDER BY s.BusinessId, s.Id;
        """;

    /// <summary>
    /// The one guarded write. It inserts a single row or none, so the row count is the atomic answer. The bucket is
    /// re-derived from the live anchor, cadence and notice instant and compared to the notice's own bucket; the key is
    /// re-derived from the business id and that bucket in both the guard and the inserted value, so a notice whose
    /// stored key was altered cannot bypass the canonical dedup. The recipient is compared exactly — never under the
    /// database's case-insensitive collation — because it is the identity the body was composed for, and the
    /// later-bucket guard is restricted to the canonical shape and type of the same tenant.
    /// </summary>
    private const string EnqueueSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, CreatedAt)
        SELECT @businessId, @type, @recipient, @subject, @bodyHtml,
               @keyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @onMarker + @dueSuffix,
               @notificationPending, 0, @createdAt
          FROM Businesses AS bu
         CROSS APPLY (
              SELECT DATEDIFF(day, CAST(bu.CreatedAt AS date), CAST(@createdAt AS date)) AS DiffDays
         ) AS diff
         CROSS APPLY (
              SELECT DATEADD(day,
                     (diff.DiffDays / NULLIF(bu.StockReviewEveryDays, 0)) * bu.StockReviewEveryDays,
                     CAST(bu.CreatedAt AS date)) AS DueDate
         ) AS due
         WHERE bu.Id = @businessId
           AND @type = @stockReview
           AND bu.NotificationEmail IS NOT NULL
           AND bu.NotificationEmail = @recipient COLLATE Latin1_General_BIN2
           AND CAST(bu.CreatedAt AS date) = @anchorDate
           AND bu.StockReviewEveryDays = @everyDays
           AND @everyDays >= 1
           AND diff.DiffDays >= bu.StockReviewEveryDays
           AND due.DueDate = @dueDate
           AND @keyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @onMarker + @dueSuffix = @expectedDedupKey
           AND NOT EXISTS (
                SELECT 1
                  FROM Notifications AS existing
                 WHERE existing.DedupKey =
                       @keyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @onMarker + @dueSuffix)
           AND NOT EXISTS (
                SELECT 1
                  FROM Notifications AS later
                 WHERE later.Type = @stockReview
                   AND later.BusinessId = bu.Id
                   AND later.DedupKey LIKE @keyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @onMarker + @datePattern
                   AND later.DedupKey > @keyPrefix + CAST(bu.Id AS NVARCHAR(10)) + @onMarker + @dueSuffix);
        """;

    public async Task<IReadOnlyList<StockReviewCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default)
    {
        if (batchSize <= 0)
            return Array.Empty<StockReviewCandidate>();

        var businesses = await QueryAsync(
            CandidateSql,
            MapCandidateRow,
            p => p
                .AddDateTime2("@now", now)
                .AddValue("@batchSize", batchSize)
                .AddValue("@stockReview", NotificationType.StockReview)
                .AddValue("@keyPrefix", KeyPrefix)
                .AddValue("@onMarker", OnMarker)
                .AddValue("@datePattern", DatePattern),
            ct);

        if (businesses.Count == 0)
            return Array.Empty<StockReviewCandidate>();

        var businessIds = businesses.Select(business => business.BusinessId).ToArray();
        var supplies = await QueryAsync(
            BuildSupplySql(businessIds.Length),
            MapSupplyRow,
            p =>
            {
                for (var index = 0; index < businessIds.Length; index++)
                    p.AddValue($"@business{index}", businessIds[index]);
            },
            ct);

        var suppliesByBusiness = new Dictionary<int, List<StockReviewSupply>>();
        foreach (var row in supplies)
        {
            if (!suppliesByBusiness.TryGetValue(row.BusinessId, out var rows))
                suppliesByBusiness[row.BusinessId] = rows = [];
            rows.Add(new StockReviewSupply(row.SupplyId, row.Name, row.CurrentStock, row.MinimumStock, row.IsActive));
        }

        var candidates = new List<StockReviewCandidate>(businesses.Count);
        foreach (var business in businesses)
        {
            var businessSupplies = suppliesByBusiness.TryGetValue(business.BusinessId, out var rows)
                ? (IReadOnlyList<StockReviewSupply>)rows
                : Array.Empty<StockReviewSupply>();
            candidates.Add(new StockReviewCandidate(
                business.BusinessId,
                business.Recipient,
                business.BusinessName,
                business.AnchorDate,
                business.EveryDays,
                business.DueDate,
                businessSupplies));
        }

        return candidates;
    }

    public async Task<bool> TryEnqueueReviewAsync(StockReviewNotice notice, CancellationToken ct = default)
    {
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
                    .AddDateTime2("@createdAt", notice.Notification.CreatedAt)
                    .AddValue("@anchorDate", DateOnly.FromDateTime(notice.AnchorCreatedAt))
                    .AddValue("@everyDays", notice.StockReviewEveryDays)
                    .AddValue("@dueDate", notice.DueDate)
                    .AddValue("@dueSuffix", DueSuffix(notice.DueDate))
                    .AddValue("@notificationPending", NotificationStatus.Pending)
                    .AddValue("@stockReview", NotificationType.StockReview)
                    .AddValue("@keyPrefix", KeyPrefix)
                    .AddValue("@onMarker", OnMarker)
                    .AddValue("@datePattern", DatePattern)
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

    /// <summary>Canonical eight-digit suffix of one due bucket: <c>yyyyMMdd</c>.</summary>
    private static string DueSuffix(DateOnly dueDate) =>
        dueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Builds the <c>IN</c> placeholder list; the ids themselves are always parameters.</summary>
    private static string BuildSupplySql(int businessCount)
    {
        var placeholders = string.Join(
            ", ", Enumerable.Range(0, businessCount).Select(index => $"@business{index}"));
        return string.Format(SupplySql, placeholders);
    }

    private static BusinessRow MapCandidateRow(SqlDataReader reader) => new(
        reader.Get<int>("BusinessId"),
        reader.Get<string>("Recipient"),
        reader.Get<string>("BusinessName"),
        reader.Get<DateTime>("AnchorDate"),
        reader.Get<int>("EveryDays"),
        reader.Get<DateOnly>("DueDate"));

    private static SupplyRow MapSupplyRow(SqlDataReader reader) => new(
        reader.Get<int>("BusinessId"),
        reader.Get<int>("SupplyId"),
        reader.Get<string>("Name"),
        reader.Get<decimal>("CurrentStock"),
        reader.Get<decimal>("MinimumStock"),
        reader.Get<bool>("IsActive"));

    private sealed record BusinessRow(
        int BusinessId,
        string Recipient,
        string BusinessName,
        DateTime AnchorDate,
        int EveryDays,
        DateOnly DueDate);

    private sealed record SupplyRow(
        int BusinessId,
        int SupplyId,
        string Name,
        decimal CurrentStock,
        decimal MinimumStock,
        bool IsActive);
}
