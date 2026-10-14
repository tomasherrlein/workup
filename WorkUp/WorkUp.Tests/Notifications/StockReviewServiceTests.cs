using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

// T25 stock review core (RF-19, D-03, plan assumption 3). The review is anchored to the local calendar DATE of
// Businesses.CreatedAt: the first one is due after StockReviewEveryDays calendar days and every N days afterwards,
// for every business including one with no supplies. A later supply declaration never restarts that calendar. If the
// host missed the exact due day the review is still issued once for the current period on a later day; obsolete
// buckets are never backfilled. The recipient is the business notice email and nothing else, and the key is once per
// business per due bucket.
public class StockReviewServiceTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 8, 0, 0);
    private static readonly DateTime FirstDue = new(2026, 1, 8, 9, 0, 0);

    private readonly FakeStockReviewRepository _repository = new();
    private readonly StockReviewService _service;

    public StockReviewServiceTests() => _service = new StockReviewService(_repository);

    private FakeStockReviewBusiness AddBusiness(
        int businessId = 1,
        string name = "Estudio",
        string? recipient = "duena@example.com",
        DateTime? createdAt = null,
        int everyDays = 7)
        => _repository.AddBusiness(businessId, name, recipient, createdAt ?? Anchor, everyDays);

    // ---- The cadence anchor: permanent local date of Businesses.CreatedAt ----

    [Fact]
    public async Task Before_the_first_due_day_nothing_is_enqueued()
    {
        AddBusiness();
        _repository.AddSupply(_repository.Businesses[0], 1);

        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 1, 7, 9, 0, 0)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Exactly_on_the_first_due_day_the_review_is_enqueued_once()
    {
        AddBusiness();
        _repository.AddSupply(_repository.Businesses[0], 1);

        Assert.Equal(1, await _service.RunAsync(FirstDue));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(NotificationType.StockReview, notice.Type);
        Assert.Equal("stock:business:1:on:20260108", notice.DedupKey);
        Assert.Equal(FirstDue, notice.CreatedAt);
        Assert.Equal(1, notice.BusinessId);
        Assert.Equal("duena@example.com", notice.Recipient);
        Assert.Contains(StockReviewService.SubjectPrefix, notice.Subject, StringComparison.Ordinal);
        Assert.Null(notice.LastError);
    }

    [Fact]
    public async Task A_different_periodicity_shifts_the_first_due_day()
    {
        AddBusiness(everyDays: 14);

        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 1, 14, 9, 0, 0)));
        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 15, 9, 0, 0)));
        Assert.Equal("stock:business:1:on:20260115", Assert.Single(_repository.Notifications).DedupKey);
    }

    [Fact]
    public async Task A_periodicity_of_one_day_is_due_every_single_day()
    {
        AddBusiness(everyDays: 1);

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 2, 9, 0, 0)));
        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 3, 9, 0, 0)));

        var keys = _repository.Notifications.Select(notice => notice.DedupKey).OrderBy(key => key).ToArray();
        Assert.Equal(["stock:business:1:on:20260102", "stock:business:1:on:20260103"], keys);
    }

    // ---- A missed due day is served once for the current period; obsolete buckets are not backfilled ----

    [Fact]
    public async Task A_due_day_missed_by_the_host_is_still_served_later_for_that_period()
    {
        AddBusiness();

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 9, 9, 0, 0)));

        Assert.Equal("stock:business:1:on:20260108", Assert.Single(_repository.Notifications).DedupKey);
    }

    [Fact]
    public async Task Many_missed_periods_produce_one_notice_for_the_current_bucket_only()
    {
        AddBusiness();

        // 2026-01-25 is 24 days after the anchor: floor(24 / 7) = 3, so the current bucket is 2026-01-22. The missed
        // 2026-01-08 and 2026-01-15 periods are obsolete and must not each get their own email.
        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 25, 9, 0, 0)));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal("stock:business:1:on:20260122", notice.DedupKey);
    }

    [Fact]
    public async Task A_repeated_tick_inside_the_same_period_never_enqueues_a_second_review()
    {
        AddBusiness();

        Assert.Equal(1, await _service.RunAsync(FirstDue));
        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 1, 9, 9, 0, 0)));
        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 1, 14, 23, 0, 0)));
        Assert.Single(_repository.Notifications);
    }

    // ---- Period rollover and clock rewind ----

    [Fact]
    public async Task A_period_rollover_enqueues_a_new_review_under_a_new_key()
    {
        AddBusiness();

        Assert.Equal(1, await _service.RunAsync(FirstDue));
        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 15, 9, 0, 0)));

        var keys = _repository.Notifications.Select(notice => notice.DedupKey).OrderBy(key => key).ToArray();
        Assert.Equal(["stock:business:1:on:20260108", "stock:business:1:on:20260115"], keys);
    }

    [Fact]
    public async Task A_clock_rewind_inside_the_same_period_does_not_duplicate()
    {
        AddBusiness();

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 10, 9, 0, 0)));
        // Rewound to the due day itself: same bucket, same key, no second email.
        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Single(_repository.Notifications);
    }

    [Fact]
    public async Task A_clock_rewound_before_the_first_due_day_enqueues_nothing()
    {
        AddBusiness();

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 10, 9, 0, 0)));
        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 1, 7, 9, 0, 0)));
        Assert.Single(_repository.Notifications);
    }

    [Fact]
    public async Task A_clock_rewound_before_the_creation_date_enqueues_nothing()
    {
        AddBusiness();

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 10, 9, 0, 0)));
        Assert.Equal(0, await _service.RunAsync(new DateTime(2025, 12, 31, 9, 0, 0)));
        Assert.Single(_repository.Notifications);
    }

    [Fact]
    public async Task A_clock_rewind_never_backfills_an_obsolete_bucket()
    {
        AddBusiness();

        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 22, 9, 0, 0)));
        Assert.Equal("stock:business:1:on:20260122", Assert.Single(_repository.Notifications).DedupKey);

        // The current period already advanced past 2026-01-08; a rewind must not resurrect that obsolete period.
        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Single(_repository.Notifications);
    }

    // ---- Later supply declarations never restart the cadence ----

    [Fact]
    public async Task Declaring_a_supply_later_does_not_restart_the_schedule()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1, lastDeclaredAt: new DateTime(2026, 1, 5, 10, 0, 0));

        // The declaration on 2026-01-05 must not re-anchor the calendar: the 2026-01-08 bucket is still the one served
        // on 2026-01-11, and a restart on the declaration date would have pushed the first review to 2026-01-12.
        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 11, 9, 0, 0)));
        Assert.Equal("stock:business:1:on:20260108", Assert.Single(_repository.Notifications).DedupKey);

        // The next bucket is still anchored on 2026-01-01 as well.
        Assert.Equal(1, await _service.RunAsync(new DateTime(2026, 1, 15, 9, 0, 0)));
        Assert.Equal("stock:business:1:on:20260115", _repository.Notifications[1].DedupKey);
    }

    // ---- Supplies: active only, listed with current and minimum quantities ----

    [Fact]
    public async Task A_business_with_no_supplies_still_receives_the_review_with_explicit_empty_wording()
    {
        AddBusiness(name: "Estudio");

        Assert.Equal(1, await _service.RunAsync(FirstDue));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains(StockReviewService.EmptyInventoryText, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Estudio", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_supplies_are_listed_with_their_current_and_minimum_quantities()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1, name: "Shampoo", currentStock: 12.5m, minimumStock: 3m);

        Assert.Equal(1, await _service.RunAsync(FirstDue));

        var notice = Assert.Single(_repository.Notifications);
        Assert.DoesNotContain(StockReviewService.EmptyInventoryText, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Shampoo", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("12,5", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("3", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("mínimo", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inactive_supplies_are_excluded_from_the_body()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1, name: "Shampoo Activo", currentStock: 12.5m, minimumStock: 3m);
        _repository.AddSupply(
            business, 2, name: "Acondicionador Inactivo", currentStock: 5m, minimumStock: 1m, isActive: false);

        Assert.Equal(1, await _service.RunAsync(FirstDue));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("Shampoo Activo", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Acondicionador Inactivo", notice.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_review_never_deducts_stock_automatically()
    {
        var business = AddBusiness();
        var supply = _repository.AddSupply(business, 1, currentStock: 12.5m, minimumStock: 3m);

        Assert.Equal(1, await _service.RunAsync(FirstDue));

        Assert.Equal(12.5m, supply.CurrentStock);
        Assert.Equal(3m, supply.MinimumStock);
    }

    [Fact]
    public async Task The_body_carries_no_link_credentials_or_client_data()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1);

        Assert.Equal(1, await _service.RunAsync(FirstDue));

        var notice = Assert.Single(_repository.Notifications);
        Assert.DoesNotContain("http", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clave", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cliente", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task Each_business_receives_only_its_own_supplies_and_recipient()
    {
        var first = AddBusiness(1, "Primero", "primero@example.com");
        _repository.AddSupply(first, 1, name: "Shampoo del Primero");
        var second = AddBusiness(2, "Segundo", "segundo@example.com");
        _repository.AddSupply(second, 2, name: "Jabon del Segundo");

        Assert.Equal(2, await _service.RunAsync(FirstDue));

        var firstNotice = Assert.Single(_repository.Notifications, n => n.BusinessId == 1);
        Assert.Equal("primero@example.com", firstNotice.Recipient);
        Assert.Equal("stock:business:1:on:20260108", firstNotice.DedupKey);
        Assert.Contains("Shampoo del Primero", firstNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Jabon del Segundo", firstNotice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Primero", firstNotice.BodyHtml, StringComparison.Ordinal);

        var secondNotice = Assert.Single(_repository.Notifications, n => n.BusinessId == 2);
        Assert.Equal("segundo@example.com", secondNotice.Recipient);
        Assert.Equal("stock:business:2:on:20260108", secondNotice.DedupKey);
        Assert.Contains("Jabon del Segundo", secondNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Shampoo del Primero", secondNotice.BodyHtml, StringComparison.Ordinal);
    }

    // ---- Key format and global dedup ----

    [Fact]
    public void The_dedup_key_is_the_business_and_the_due_bucket_date()
    {
        Assert.Equal("stock:business:7:on:20260108", StockReviewService.DedupKey(7, new DateOnly(2026, 1, 8)));
    }

    [Fact]
    public async Task A_concurrent_review_committed_after_the_scan_is_not_duplicated()
    {
        AddBusiness();
        Notification? competing = null;
        _repository.AfterList = () => competing = _repository.SeedReview(1, new DateOnly(2026, 1, 8), FirstDue);

        Assert.Equal(0, await _service.RunAsync(FirstDue));

        Assert.Equal(1, _repository.EnqueueAttempts);
        Assert.Same(competing, Assert.Single(_repository.Notifications));
    }

    // ---- The guarded enqueue: an edit between the scan and the insert invalidates the notice ----

    [Fact]
    public async Task A_recipient_changed_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1);
        _repository.AfterList = () => business.Recipient = "nueva@example.com";

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
        Assert.Equal(1, _repository.EnqueueAttempts);
    }

    [Fact]
    public async Task A_cadence_changed_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1);
        _repository.AfterList = () => business.EveryDays = 30;

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
        Assert.Equal(1, _repository.EnqueueAttempts);
    }

    [Fact]
    public async Task An_anchor_date_changed_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1);
        _repository.AfterList = () => business.CreatedAt = Anchor.AddDays(1);

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
        Assert.Equal(1, _repository.EnqueueAttempts);
    }

    [Fact]
    public async Task A_recipient_cleared_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1);
        _repository.AfterList = () => business.Recipient = "";

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_business_removed_between_the_scan_and_the_insert_is_not_notified()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1);
        _repository.AfterList = () => _repository.RemoveBusiness(1);

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
    }

    // ---- The notice email is the only destination; the phone is never a fallback ----

    [Fact]
    public async Task A_business_without_a_notice_email_is_skipped_and_never_falls_back_to_the_phone()
    {
        var business = AddBusiness(recipient: null);
        _repository.AddSupply(business, 1);

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_malformed_notice_email_is_skipped_instead_of_being_used()
    {
        var business = AddBusiness(recipient: "no-es-un-email");
        _repository.AddSupply(business, 1);

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
    }

    // ---- HTML safety ----

    [Fact]
    public async Task Supply_and_business_names_are_html_encoded()
    {
        var business = AddBusiness(name: "<i>Estudio</i>");
        _repository.AddSupply(business, 1, name: "<b>Shampoo</b> & Spa");

        Assert.Equal(1, await _service.RunAsync(FirstDue));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("&lt;i&gt;Estudio&lt;/i&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Shampoo&lt;/b&gt; &amp; Spa", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Shampoo</b>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>Estudio</i>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Null(notice.LastError);
    }

    // ---- Bounded batch and a permissive reader that cannot force an ineligible write ----

    [Fact]
    public async Task The_batch_is_bounded_to_twenty_businesses_per_tick()
    {
        for (var index = 1; index <= 25; index++)
            AddBusiness(index, $"Negocio {index}", $"duena{index}@example.com");
        _repository.IgnoreBatchCap = true;

        Assert.Equal(StockReviewService.BatchSize, await _service.RunAsync(FirstDue));

        Assert.Equal(25, _repository.LastScanCandidateCount);
        Assert.Equal(StockReviewService.BatchSize, _repository.Notifications.Count);
        Assert.Equal(StockReviewService.BatchSize, _repository.LastBatchSize);
    }

    [Fact]
    public async Task A_permissive_reader_cannot_force_a_review_before_the_first_due_day()
    {
        AddBusiness();
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(new DateTime(2026, 1, 3, 9, 0, 0)));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_permissive_reader_cannot_force_a_review_without_a_usable_recipient()
    {
        AddBusiness(recipient: null);
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task A_permissive_reader_cannot_force_a_non_positive_cadence()
    {
        AddBusiness(everyDays: 0);
        _repository.OverReturnIneligible = true;

        Assert.Equal(0, await _service.RunAsync(FirstDue));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task The_service_defends_against_a_permissive_reader_that_over_returns_inactive_supplies()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1, name: "Shampoo Activo");
        _repository.AddSupply(business, 2, name: "Acondicionador Inactivo", isActive: false);
        _repository.OverReturnSupplies = true;

        Assert.Equal(1, await _service.RunAsync(FirstDue));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Contains("Shampoo Activo", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Acondicionador Inactivo", notice.BodyHtml, StringComparison.Ordinal);
    }

    // ---- The service never reads a clock of its own ----

    [Fact]
    public async Task The_service_uses_the_supplied_instant_and_never_reads_a_clock()
    {
        AddBusiness();
        var supplied = new DateTime(2026, 1, 8, 22, 15, 0);

        Assert.Equal(1, await _service.RunAsync(supplied));

        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(supplied, notice.CreatedAt);
        Assert.Equal(StockReviewService.DedupKey(1, new DateOnly(2026, 1, 8)), notice.DedupKey);
    }

    // ---- DateOnly extremes ----

    [Fact]
    public void No_bucket_exists_before_the_anchor_date()
    {
        Assert.Null(StockReviewService.CurrentDueDate(Anchor, 7, Anchor.AddDays(-1)));
    }

    [Fact]
    public void The_last_representable_day_does_not_overflow()
    {
        var last = new DateTime(9999, 12, 25, 8, 0, 0);

        Assert.Null(StockReviewService.CurrentDueDate(last, 7, DateTime.MaxValue));
        Assert.Equal(DateOnly.MaxValue, StockReviewService.CurrentDueDate(last, 6, DateTime.MaxValue));
    }

    // ---- Cancellation propagates and never leaves a partial notice ----

    [Fact]
    public async Task Cancellation_before_the_run_propagates_and_enqueues_nothing()
    {
        AddBusiness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RunAsync(FirstDue, cancellation.Token));
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Cancellation_after_the_scan_propagates_without_enqueuing()
    {
        var business = AddBusiness();
        _repository.AddSupply(business, 1);
        using var cancellation = new CancellationTokenSource();
        _repository.AfterList = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RunAsync(FirstDue, cancellation.Token));
        Assert.Empty(_repository.Notifications);
    }
}
