using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory stock-review port (T25, RF-19). It mirrors the SQL contract the scheduled review depends on: the scan
/// returns only businesses whose current cadence bucket is already due at the supplied instant, whose notice email is
/// present and well-formed and whose current-bucket key does not exist yet — and none for a later bucket, so a clock
/// rewind cannot backfill an obsolete period — carrying only their current active supplies; and
/// <see cref="TryEnqueueReviewAsync"/> re-runs the whole guard under a lock against the <em>current</em> rows —
/// business scope, recipient, anchor and cadence, eligibility at the composed instant, canonical type/key and dedup —
/// so a cadence or recipient edited between the scan and the insert is rejected instead of mailed.
/// <para>
/// <see cref="IgnoreBatchCap"/>, <see cref="OverReturnIneligible"/> and <see cref="OverReturnSupplies"/> deliberately
/// break the read half so a test can prove the service still rejects the extras and caps the batch on its own; they
/// never relax the enqueue guards.
/// </para>
/// <para>
/// LIMITATION: an in-memory fake holds no SQL row lock and no unique-key race, so it cannot prove atomicity; it only
/// proves the service contract. A SQL fixture is the runtime proof.
/// </para>
/// </summary>
public sealed class FakeStockReviewRepository : IStockReviewRepository
{
    private static readonly DateTime DefaultAnchor = new(2026, 1, 1, 8, 0, 0);

    private readonly List<FakeStockReviewBusiness> _businesses = [];
    private readonly List<Notification> _notifications = [];
    private readonly object _gate = new();

    /// <summary>Runs right after a scan produced its snapshot, so a test can mutate state before the enqueue.</summary>
    public Action? AfterList { get; set; }

    /// <summary>When true the scan ignores <c>batchSize</c>, over-returning so the service must cap the batch itself.</summary>
    public bool IgnoreBatchCap { get; set; }

    /// <summary>When true the scan skips every eligibility and dedup filter, so the service must reject the extras.</summary>
    public bool OverReturnIneligible { get; set; }

    /// <summary>
    /// When true the scan carries every seeded supply as-is, including inactive ones, so the service must re-apply the
    /// active-only predicate and drop them from the body.
    /// </summary>
    public bool OverReturnSupplies { get; set; }

    public int ListCount { get; private set; }

    public int LastBatchSize { get; private set; }

    public int LastScanCandidateCount { get; private set; }

    public int EnqueueAttempts { get; private set; }

    public StockReviewNotice? LastAttemptedNotice { get; private set; }

    public IReadOnlyList<FakeStockReviewBusiness> Businesses => _businesses;

    public IReadOnlyList<Notification> Notifications
    {
        get
        {
            lock (_gate)
                return _notifications.ToArray();
        }
    }

    public FakeStockReviewBusiness AddBusiness(
        int businessId = 1,
        string name = "Estudio",
        string? recipient = "duena@example.com",
        DateTime? createdAt = null,
        int everyDays = 7,
        string contactPhone = "+54 11 5555-0000")
    {
        var business = new FakeStockReviewBusiness
        {
            BusinessId = businessId,
            Name = name,
            Recipient = recipient,
            CreatedAt = createdAt ?? DefaultAnchor,
            EveryDays = everyDays,
            ContactPhone = contactPhone
        };
        _businesses.Add(business);
        return business;
    }

    /// <summary>Adds a supply to a business; active by default so a test opts into the inactive case explicitly.</summary>
    public FakeStockReviewSupply AddSupply(
        FakeStockReviewBusiness business,
        int supplyId,
        string name = "Shampoo",
        decimal currentStock = 10m,
        decimal minimumStock = 2m,
        bool isActive = true,
        DateTime? lastDeclaredAt = null)
    {
        var supply = new FakeStockReviewSupply
        {
            SupplyId = supplyId,
            Name = name,
            CurrentStock = currentStock,
            MinimumStock = minimumStock,
            IsActive = isActive,
            LastDeclaredAt = lastDeclaredAt ?? business.CreatedAt
        };
        business.Supplies.Add(supply);
        return supply;
    }

    public Task<IReadOnlyList<StockReviewCandidate>> ListCandidatesAsync(
        DateTime now, int batchSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ListCount++;
        LastBatchSize = batchSize;
        var eligible = _businesses
            .Where(business => OverReturnIneligible || IsEligibleForScan(business, now))
            .OrderBy(business => business.BusinessId);
        var limited = IgnoreBatchCap ? eligible : eligible.Take(batchSize);
        var candidates = limited
            .Select(business => BuildCandidate(business, now))
            .ToArray();

        LastScanCandidateCount = candidates.Length;
        AfterList?.Invoke();
        return Task.FromResult<IReadOnlyList<StockReviewCandidate>>(candidates);
    }

    public Task<bool> TryEnqueueReviewAsync(StockReviewNotice notice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnqueueAttempts++;
        LastAttemptedNotice = notice;
        lock (_gate)
        {
            var business = _businesses.FirstOrDefault(row => row.BusinessId == notice.BusinessId);
            if (business is null)
                return Task.FromResult(false);

            // The notice is only valid while the business still has the same usable recipient.
            if (!EmailFormat.IsValid(business.Recipient)
                || !string.Equals(business.Recipient, notice.Notification.Recipient, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            // A cadence or anchor edited after the scan invalidates the notice the body was composed from.
            if (business.CreatedAt != notice.AnchorCreatedAt || business.EveryDays != notice.StockReviewEveryDays)
                return Task.FromResult(false);

            // The composed instant must still fall inside the notice's bucket under the current anchor and cadence.
            var due = StockReviewService.CurrentDueDate(business.CreatedAt, business.EveryDays, notice.Notification.CreatedAt);
            if (due is null || due.Value != notice.DueDate)
                return Task.FromResult(false);

            // The type and key must be the canonical ones for this business and bucket.
            if (notice.Notification.Type != NotificationType.StockReview)
                return Task.FromResult(false);
            if (notice.Notification.DedupKey != StockReviewService.DedupKey(notice.BusinessId, notice.DueDate))
                return Task.FromResult(false);

            if (_notifications.Any(existing => existing.DedupKey == notice.Notification.DedupKey))
                return Task.FromResult(false);
            if (HasLaterReview(business.BusinessId, notice.DueDate))
                return Task.FromResult(false);

            _notifications.Add(notice.Notification);
            return Task.FromResult(true);
        }
    }

    private bool IsEligibleForScan(FakeStockReviewBusiness business, DateTime now)
    {
        var due = StockReviewService.CurrentDueDate(business.CreatedAt, business.EveryDays, now);
        if (due is null)
            return false;
        if (!EmailFormat.IsValid(business.Recipient))
            return false;
        if (HasReview(business.BusinessId, due.Value))
            return false;
        return !HasLaterReview(business.BusinessId, due.Value);
    }

    private bool HasReview(int businessId, DateOnly due)
    {
        lock (_gate)
            return _notifications.Any(notification =>
                notification.DedupKey == StockReviewService.DedupKey(businessId, due));
    }

    private bool HasLaterReview(int businessId, DateOnly due)
    {
        lock (_gate)
            return _notifications.Any(notification =>
                TryParseReviewBucket(notification.DedupKey, businessId, out var bucket) && bucket > due);
    }

    /// <summary>
    /// Parses <c>stock:business:{id}:on:{yyyyMMdd}</c> for the given business. A foreign key returns <c>false</c> so it
    /// can never suppress a legitimate review.
    /// </summary>
    private static bool TryParseReviewBucket(string dedupKey, int businessId, out DateOnly bucket)
    {
        bucket = default;
        var prefix = $"stock:business:{businessId}:on:";
        if (!dedupKey.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return DateOnly.TryParseExact(
            dedupKey[prefix.Length..], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out bucket);
    }

    /// <summary>
    /// Inserts a competing review row for the exact bucket key, modelling a concurrent producer that committed after
    /// the scan returned its snapshot, so the guarded enqueue must reject the duplicate.
    /// </summary>
    public Notification SeedReview(int businessId, DateOnly due, DateTime createdAt)
    {
        var notification = new Notification
        {
            BusinessId = businessId,
            Type = NotificationType.StockReview,
            Recipient = "otro@example.com",
            Subject = "revisión en curso",
            BodyHtml = "<p>revisión en curso</p>",
            DedupKey = StockReviewService.DedupKey(businessId, due),
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
    /// Tenant-scoped projection of one business. The bucket is re-derived from the current anchor, cadence and
    /// supplied instant, and <see cref="OverReturnSupplies"/> returns every seeded supply untouched so a test can
    /// prove the service re-applies the active-only predicate on its own. The default path routes through
    /// <see cref="StockReviewService.VisibleSupplies"/> so the fake scan and the service cannot drift apart.
    /// </summary>
    private StockReviewCandidate BuildCandidate(FakeStockReviewBusiness business, DateTime now)
    {
        var due = StockReviewService.CurrentDueDate(business.CreatedAt, business.EveryDays, now);
        var raw = new StockReviewCandidate(
            business.BusinessId,
            business.Recipient ?? "",
            business.Name,
            business.CreatedAt,
            business.EveryDays,
            due ?? DateOnly.FromDateTime(business.CreatedAt),
            business.Supplies
                .OrderBy(supply => supply.SupplyId)
                .Select(supply => new StockReviewSupply(
                    supply.SupplyId, supply.Name, supply.CurrentStock, supply.MinimumStock, supply.IsActive))
                .ToArray());

        return OverReturnSupplies
            ? raw
            : raw with { Supplies = StockReviewService.VisibleSupplies(raw) };
    }
}

/// <summary>A seeded business the fake scans; mutable so a test can model an edit between the scan and the enqueue.</summary>
public sealed class FakeStockReviewBusiness
{
    public int BusinessId { get; init; }
    public string Name { get; set; } = "Estudio";
    public string? Recipient { get; set; } = "duena@example.com";

    /// <summary>Permanent cadence anchor: the local calendar date of <c>Businesses.CreatedAt</c>.</summary>
    public DateTime CreatedAt { get; set; } = new(2026, 1, 1, 8, 0, 0);

    /// <summary>Configured review period in days (<c>Businesses.StockReviewEveryDays</c>).</summary>
    public int EveryDays { get; set; } = 7;

    /// <summary>Contact datum only: no review may ever be delivered here (plan assumption 3).</summary>
    public string ContactPhone { get; set; } = "";

    public List<FakeStockReviewSupply> Supplies { get; } = [];
}

/// <summary>A seeded supply; mutable so a test can model a declaration between the scan and the insert.</summary>
public sealed class FakeStockReviewSupply
{
    public int SupplyId { get; init; }
    public string Name { get; set; } = "Shampoo";
    public decimal CurrentStock { get; set; } = 10m;
    public decimal MinimumStock { get; set; } = 2m;
    public bool IsActive { get; set; } = true;
    public DateTime LastDeclaredAt { get; set; } = new(2026, 1, 1, 8, 0, 0);
}
