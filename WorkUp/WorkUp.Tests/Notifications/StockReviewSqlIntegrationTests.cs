using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlStockReviewRepository"/> (T25, RF-19) driven through the
/// real <see cref="StockReviewService"/>. It requires a fresh, dedicated database supplied through
/// <c>WORKUP_T25_STOCK_SQL_CONNECTION</c>; without it every SQL test returns N/A and no connection is opened. Each test
/// derives its own database name from the supplied one, so the whole class runs in a single command, the preflight
/// asserts <c>DB_ID</c> is absent before migrating, and nothing is torn down: the targets are retained on purpose and
/// never reused. No keyring and no web host take part: the fixture drives the port and the service directly.
/// <para>
/// The in-memory fake and the service tests cannot prove what SQL Server does with the global bounded scan, the tenant
/// joins, the permanent calendar anchor or a concurrent insert. This fixture is the runtime proof of the port: one
/// bounded, id-ordered scan across all businesses that only returns a business whose notice email is present and not
/// blank, whose current period is already due — first review anchor date + N days, then every N days — and whose
/// current <c>stock:business:{id}:on:{yyyyMMdd}</c> key is still absent while no <b>later</b> canonical key exists, so a
/// clock rewind never backfills an obsolete period; carrying only the current <b>active</b> supplies of each business,
/// ordered by supply id, including a business with zero supplies; and one guarded <c>INSERT ... SELECT</c> that
/// rechecks the live business, the exact recipient, the calendar anchor and cadence, the derived due bucket at the
/// composed instant and the canonical type/key, so an owner edit, a repeated tick or a forced two-session overlap is a
/// no-op instead of a stale or duplicate email.
/// </para>
/// </summary>
public sealed class StockReviewSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T25_STOCK_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT25StockSql";
    private const int DefaultEveryDays = 7;

    private static readonly Regex SafeDatabaseName =
        new(@"\AWorkUpDemoT25StockSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>The permanent cadence anchor every scenario starts from: the local date 2026-01-01.</summary>
    private static readonly DateTime Anchor = new(2026, 1, 1, 8, 0, 0);

    /// <summary>The single composing instant most scenarios use: 2026-01-08, the first due day at N = 7.</summary>
    private static readonly DateTime Now = new(2026, 1, 8, 9, 0, 0);

    /// <summary>The bucket a tick on <see cref="Now"/> belongs to at the default cadence.</summary>
    private static readonly DateOnly FirstBucket = new(2026, 1, 8);

    // ---- The permanent calendar anchor and the due bucket ----

    [Fact]
    public async Task Before_the_first_due_day_nothing_is_enqueued()
    {
        var target = await PrepareAsync("_Before");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var businessId = await SeedBusinessAsync(connection, "t25-before", "t25.before@example.invalid");
            await SeedSupplyAsync(connection, businessId, "Shampoo");
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);

        // 2026-01-07 is only six days after the 2026-01-01 anchor: the first review is not due yet.
        var before = new DateTime(2026, 1, 7, 9, 0, 0);
        Assert.Empty(await repository.ListCandidatesAsync(before, StockReviewService.BatchSize));
        Assert.Equal(0, await new StockReviewService(repository).RunAsync(before));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Before-first-due assertions passed: on 2026-01-07 the scan was empty and nothing enqueued.");
    }

    [Fact]
    public async Task Exactly_on_the_first_due_day_enqueues_once_with_the_bucket_key()
    {
        var target = await PrepareAsync("_At");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t25-at", "t25.at@example.invalid");
            await SeedSupplyAsync(connection, businessId, "Shampoo", currentStock: 12.5m, minimumStock: 3m);
            await SeedSupplyAsync(connection, businessId, "Jabon", currentStock: 4m, minimumStock: 1m, isActive: false);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);
        var service = new StockReviewService(repository);

        // The scan already carries the first due bucket and only the active supply.
        var candidate = Assert.Single(await repository.ListCandidatesAsync(Now, StockReviewService.BatchSize));
        Assert.Equal(businessId, candidate.BusinessId);
        Assert.Equal("t25.at@example.invalid", candidate.Recipient);
        Assert.Equal(DefaultEveryDays, candidate.StockReviewEveryDays);
        Assert.Equal(new DateTime(2026, 1, 1), candidate.AnchorCreatedAt);
        Assert.Equal(FirstBucket, candidate.DueDate);
        Assert.Equal("Shampoo", Assert.Single(candidate.Supplies).Name);

        Assert.Equal(1, await service.RunAsync(Now));

        var key = Key(businessId, FirstBucket);
        var notice = await ReadNotificationAsync(target.ConnectionString, key);
        Assert.Equal(businessId, notice.BusinessId);
        Assert.Equal(NotificationType.StockReview, notice.Type);
        Assert.Equal("t25.at@example.invalid", notice.Recipient);
        Assert.Contains(StockReviewService.SubjectPrefix, notice.Subject, StringComparison.Ordinal);
        Assert.Contains("08/01/2026", notice.Subject, StringComparison.Ordinal);
        Assert.Equal(NotificationStatus.Pending, notice.Status);
        Assert.Equal(0, notice.Attempts);
        Assert.Null(notice.LastError);
        Assert.Equal(Now, notice.CreatedAt);
        Assert.Equal(key, notice.DedupKey);
        Assert.Contains("Shampoo", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Jabon", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, key));

        // A repeated tick inside the same period is a no-op rather than a second email.
        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, key));

        // The review only reports: the declared stock is never deducted.
        Assert.Equal(
            12.5m,
            await ScalarDecimalAsync(
                target.ConnectionString,
                "SELECT CurrentStock FROM Supplies WHERE BusinessId = @business AND Name = N'Shampoo'",
                ("@business", businessId)));

        output.WriteLine(
            "First-due assertions passed: the 2026-01-08 bucket fired once with the canonical key, a repeated tick " +
            "was a no-op and the declared stock was untouched.");
    }

    [Fact]
    public async Task A_missed_due_day_is_served_once_for_the_current_bucket()
    {
        var target = await PrepareAsync("_Missed");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
            businessId = await SeedBusinessAsync(connection, "t25-missed", "t25.missed@example.invalid");

        await using var session = new DbSession(target.ConnectionString);
        var service = new StockReviewService(new SqlStockReviewRepository(session));

        // 2026-01-25 is 24 days after the anchor: floor(24 / 7) = 3, so the current bucket is 2026-01-22. The missed
        // 2026-01-08 and 2026-01-15 periods are obsolete and must not each get their own email.
        var missed = new DateTime(2026, 1, 25, 9, 0, 0);
        Assert.Equal(1, await service.RunAsync(missed));

        var notice = await ReadNotificationAsync(target.ConnectionString, Key(businessId, new DateOnly(2026, 1, 22)));
        Assert.Equal(missed, notice.CreatedAt);
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Missed-due assertions passed: the 2026-01-22 current bucket fired once and no obsolete period was " +
            "backfilled.");
    }

    [Fact]
    public async Task A_one_day_period_fires_once_per_day_and_never_twice()
    {
        var target = await PrepareAsync("_Daily");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
            businessId = await SeedBusinessAsync(
                connection, "t25-daily", "t25.daily@example.invalid", everyDays: 1);

        await using var session = new DbSession(target.ConnectionString);
        var service = new StockReviewService(new SqlStockReviewRepository(session));

        Assert.Equal(1, await service.RunAsync(new DateTime(2026, 1, 2, 9, 0, 0)));
        Assert.Equal(1, await service.RunAsync(new DateTime(2026, 1, 3, 9, 0, 0)));
        Assert.Equal(0, await service.RunAsync(new DateTime(2026, 1, 3, 23, 0, 0)));

        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, Key(businessId, new DateOnly(2026, 1, 2))));
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, Key(businessId, new DateOnly(2026, 1, 3))));
        Assert.Equal(2, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Daily-period assertions passed: N = 1 fired on 2026-01-02 and 2026-01-03 and the same-day repeat was a " +
            "no-op.");
    }

    // ---- Dedup: the current bucket and the later-bucket rewind guard ----

    [Fact]
    public async Task An_already_reviewed_current_bucket_is_not_notified_again()
    {
        var target = await PrepareAsync("_Dedup");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t25-dedup", "t25.dedup@example.invalid");
            await InsertNotificationAsync(
                connection, businessId, NotificationType.StockReview, Key(businessId, FirstBucket), Now);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(Now, StockReviewService.BatchSize));
        Assert.Equal(0, await new StockReviewService(repository).RunAsync(Now));
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, Key(businessId, FirstBucket)));

        output.WriteLine(
            "Dedup assertions passed: the composed bucket was already present, so the scan returned no candidate.");
    }

    [Fact]
    public async Task A_later_bucket_review_suppresses_an_obsolete_bucket_after_a_rewind()
    {
        var target = await PrepareAsync("_Rewind");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t25-rewind", "t25.rewind@example.invalid");
            // A later period was already reviewed. The T26 clock is then rewound to 2026-01-08: the obsolete
            // 2026-01-08 bucket must not be resurrected even though its key is still absent.
            await InsertNotificationAsync(
                connection, businessId, NotificationType.StockReview,
                Key(businessId, new DateOnly(2026, 1, 15)), Now);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(Now, StockReviewService.BatchSize));
        Assert.Equal(0, await new StockReviewService(repository).RunAsync(Now));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Rewind assertions passed: an existing later bucket blocked the obsolete 2026-01-08 bucket.");
    }

    [Fact]
    public async Task Only_a_canonical_stockreview_later_key_suppresses_a_bucket()
    {
        var target = await PrepareAsync("_Canonical");
        if (target is null)
            return;

        int otherType;
        int nonCanonical;
        int canonicalLater;
        int foreignKey;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            // A later StockReview *type* key of another type must not suppress anything: only a StockReview row counts.
            otherType = await SeedBusinessAsync(connection, "t25-canonical-othertype", "t25.canonical.other@example.invalid");
            await InsertNotificationAsync(
                connection, otherType, NotificationType.DailySummary,
                $"stock:business:{otherType}:on:20260201", Now);

            // A canonical-prefix key with a malformed (non 8-digit) suffix is not a canonical stock review key.
            nonCanonical = await SeedBusinessAsync(connection, "t25-canonical-shortkey", "t25.canonical.short@example.invalid");
            await InsertNotificationAsync(
                connection, nonCanonical, NotificationType.StockReview,
                $"stock:business:{nonCanonical}:on:202602", Now);

            // A true canonical later key suppresses its own business.
            canonicalLater = await SeedBusinessAsync(connection, "t25-canonical-later", "t25.canonical.later@example.invalid");
            await InsertNotificationAsync(
                connection, canonicalLater, NotificationType.StockReview,
                $"stock:business:{canonicalLater}:on:20260201", Now);

            // A key whose business id inside the text belongs to another tenant is never a later key of this one.
            foreignKey = await SeedBusinessAsync(connection, "t25-canonical-foreign", "t25.canonical.foreign@example.invalid");
            await InsertNotificationAsync(
                connection, foreignKey, NotificationType.StockReview,
                $"stock:business:{foreignKey + 1000}:on:20260201", Now);
        }

        await using var session = new DbSession(target.ConnectionString);
        var candidates = await new SqlStockReviewRepository(session)
            .ListCandidatesAsync(Now, StockReviewService.BatchSize);

        Assert.Equal(
            new[] { otherType, nonCanonical, foreignKey },
            candidates.Select(candidate => candidate.BusinessId).ToArray());

        output.WriteLine(
            "Canonical-key assertions passed: only a canonical same-tenant StockReview key with a later bucket " +
            "suppressed a business.");
    }

    // ---- Supplies: active only, zero supplies, tenant scope ----

    [Fact]
    public async Task Only_active_supplies_are_projected_and_zero_supplies_is_still_a_candidate()
    {
        var target = await PrepareAsync("_Supplies");
        if (target is null)
            return;

        int empty;
        int allInactive;
        int mixed;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            empty = await SeedBusinessAsync(connection, "t25-supplies-empty", "t25.supplies.empty@example.invalid");

            allInactive = await SeedBusinessAsync(connection, "t25-supplies-inactive", "t25.supplies.inactive@example.invalid");
            await SeedSupplyAsync(connection, allInactive, "Inactivo Uno", isActive: false);
            await SeedSupplyAsync(connection, allInactive, "Inactivo Dos", isActive: false);

            mixed = await SeedBusinessAsync(connection, "t25-supplies-mixed", "t25.supplies.mixed@example.invalid");
            await SeedSupplyAsync(connection, mixed, "Activo A", currentStock: 5m, minimumStock: 1m);
            await SeedSupplyAsync(connection, mixed, "Inactivo Tercero", isActive: false);
            await SeedSupplyAsync(connection, mixed, "Activo B", currentStock: 9m, minimumStock: 2m);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);
        var candidates = await repository.ListCandidatesAsync(Now, StockReviewService.BatchSize);

        Assert.Equal(
            new[] { empty, allInactive, mixed },
            candidates.Select(candidate => candidate.BusinessId).ToArray());
        Assert.Empty(candidates[0].Supplies);
        Assert.Empty(candidates[1].Supplies);
        Assert.Equal(
            new[] { "Activo A", "Activo B" },
            candidates[2].Supplies.Select(supply => supply.Name).ToArray());
        Assert.All(candidates[2].Supplies, supply => Assert.True(supply.IsActive));

        Assert.Equal(3, await new StockReviewService(repository).RunAsync(Now));

        var emptyNotice = await ReadNotificationAsync(target.ConnectionString, Key(empty, FirstBucket));
        var inactiveNotice = await ReadNotificationAsync(target.ConnectionString, Key(allInactive, FirstBucket));
        var mixedNotice = await ReadNotificationAsync(target.ConnectionString, Key(mixed, FirstBucket));
        Assert.Contains(StockReviewService.EmptyInventoryText, emptyNotice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains(StockReviewService.EmptyInventoryText, inactiveNotice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Activo A", mixedNotice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Activo B", mixedNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Inactivo Tercero", mixedNotice.BodyHtml, StringComparison.Ordinal);

        output.WriteLine(
            "Supply assertions passed: the empty and the all-inactive inventories were still reviewed with the " +
            "explicit empty wording, and only active supplies were listed in id order.");
    }

    [Fact]
    public async Task Cross_tenant_scans_never_mix_businesses_or_borrow_foreign_supplies()
    {
        var target = await PrepareAsync("_Tenant");
        if (target is null)
            return;

        int own;
        int foreign;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            own = await SeedBusinessAsync(connection, "t25-tenant-own", "t25.tenant.own@example.invalid");
            foreign = await SeedBusinessAsync(connection, "t25-tenant-foreign", "t25.tenant.foreign@example.invalid");
            await SeedSupplyAsync(connection, own, "Shampoo Propio");
            await SeedSupplyAsync(connection, foreign, "Jabon Ajeno");
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);
        var candidates = await repository.ListCandidatesAsync(Now, StockReviewService.BatchSize);

        Assert.Equal(new[] { own, foreign }, candidates.Select(candidate => candidate.BusinessId).ToArray());
        Assert.Equal("Shampoo Propio", Assert.Single(candidates[0].Supplies).Name);
        Assert.Equal("Jabon Ajeno", Assert.Single(candidates[1].Supplies).Name);

        Assert.Equal(2, await new StockReviewService(repository).RunAsync(Now));

        var ownNotice = await ReadNotificationAsync(target.ConnectionString, Key(own, FirstBucket));
        var foreignNotice = await ReadNotificationAsync(target.ConnectionString, Key(foreign, FirstBucket));
        Assert.Equal("t25.tenant.own@example.invalid", ownNotice.Recipient);
        Assert.Equal("t25.tenant.foreign@example.invalid", foreignNotice.Recipient);
        Assert.Contains("Shampoo Propio", ownNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Jabon Ajeno", ownNotice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Jabon Ajeno", foreignNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Shampoo Propio", foreignNotice.BodyHtml, StringComparison.Ordinal);

        output.WriteLine(
            "Tenant assertions passed: each candidate carried only its own active supplies and no body named " +
            "another tenant's supply.");
    }

    // ---- No usable notice email means no review at all ----

    [Fact]
    public async Task A_business_without_a_notice_email_is_never_a_candidate()
    {
        var target = await PrepareAsync("_NoEmail");
        if (target is null)
            return;

        const string phone = "+54 11 5555-0000";
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var absent = await SeedBusinessAsync(
                connection, "t25-noemail", notificationEmail: null, contactPhone: phone);
            await SeedSupplyAsync(connection, absent, "SinCorreo");

            var blank = await SeedBusinessAsync(
                connection, "t25-blankemail", notificationEmail: "   ", contactPhone: phone);
            await SeedSupplyAsync(connection, blank, "Blanco");
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(Now, StockReviewService.BatchSize));
        Assert.Equal(0, await new StockReviewService(repository).RunAsync(Now));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));
        Assert.Equal(
            0,
            await CountAsync(
                target.ConnectionString,
                "SELECT COUNT(*) FROM Notifications WHERE Recipient = @phone",
                ("@phone", phone)));

        output.WriteLine(
            "No-email assertions passed: neither a NULL nor a blank notice email produced a candidate and the " +
            "contact phone was never used.");
    }

    [Fact]
    public async Task A_malformed_notice_email_is_scanned_but_never_notified()
    {
        var target = await PrepareAsync("_Malformed");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
            await SeedBusinessAsync(connection, "t25-malformed", "no-es-un-email");

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);

        // SQL applies presence only: the malformed address is scanned, and the core's shape check rejects it.
        Assert.Single(await repository.ListCandidatesAsync(Now, StockReviewService.BatchSize));
        Assert.Equal(0, await new StockReviewService(repository).RunAsync(Now));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Malformed-email assertions passed: SQL returned the present address and the service refused to compose " +
            "for it.");
    }

    // ---- Guarded enqueue: an edit between the scan and the insert is a no-op ----

    [Fact]
    public async Task A_recipient_or_cadence_changed_after_the_scan_is_not_notified()
    {
        var target = await PrepareAsync("_Changed");
        if (target is null)
            return;

        int recipientBusiness;
        int cadenceBusiness;
        const string editedRecipient = "t25.changed.nuevo@example.invalid";
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            recipientBusiness = await SeedBusinessAsync(
                connection, "t25-changed-recipient", "t25.changed.viejo@example.invalid");
            cadenceBusiness = await SeedBusinessAsync(
                connection, "t25-changed-cadence", "t25.changed.cadence@example.invalid");
        }

        await using var session = new DbSession(target.ConnectionString);
        var scanned = 0;
        var service = new StockReviewService(
            new HookedStockReviewRepository(
                new SqlStockReviewRepository(session),
                async candidates =>
                {
                    // The owner edits the recipient and the cadence right after the scan, before the insert.
                    scanned = candidates.Count;
                    await ExecuteAsync(
                        target.ConnectionString,
                        "UPDATE Businesses SET NotificationEmail = @email WHERE Id = @business",
                        ("@email", editedRecipient), ("@business", recipientBusiness));
                    await ExecuteAsync(
                        target.ConnectionString,
                        "UPDATE Businesses SET StockReviewEveryDays = @everyDays WHERE Id = @business",
                        ("@everyDays", 30), ("@business", cadenceBusiness));
                }));

        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(2, scanned);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        // The change only invalidated the notice composed before it: a fresh scan notices the new recipient and finds
        // the second business no longer due at 2026-01-08 under a 30-day cadence.
        Assert.Equal(1, await new StockReviewService(new SqlStockReviewRepository(session)).RunAsync(Now));
        var notice = await ReadNotificationAsync(target.ConnectionString, Key(recipientBusiness, FirstBucket));
        Assert.Equal(editedRecipient, notice.Recipient);
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, Key(cadenceBusiness, FirstBucket)));

        output.WriteLine(
            "Guard assertions passed: the edits refused the pre-edit notices with no row written, while a fresh scan " +
            "composed against the live recipient only.");
    }

    [Fact]
    public async Task An_anchor_date_changed_after_the_scan_is_not_notified()
    {
        var target = await PrepareAsync("_Anchor");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
            businessId = await SeedBusinessAsync(connection, "t25-anchor", "t25.anchor@example.invalid");

        await using var session = new DbSession(target.ConnectionString);
        var service = new StockReviewService(
            new HookedStockReviewRepository(
                new SqlStockReviewRepository(session),
                _ => ExecuteAsync(
                    target.ConnectionString,
                    "UPDATE Businesses SET CreatedAt = @createdAt WHERE Id = @business",
                    ("@createdAt", Anchor.AddDays(1)), ("@business", businessId))));

        // Moving the anchor one day later leaves only six elapsed days: the composed bucket no longer matches.
        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Anchor assertions passed: the anchor edited after the scan invalidated the composed notice.");
    }

    [Fact]
    public async Task A_business_removed_after_the_scan_is_not_notified()
    {
        var target = await PrepareAsync("_Removed");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
            businessId = await SeedBusinessAsync(connection, "t25-removed", "t25.removed@example.invalid");

        await using var session = new DbSession(target.ConnectionString);
        var service = new StockReviewService(
            new HookedStockReviewRepository(
                new SqlStockReviewRepository(session),
                _ => ExecuteAsync(
                    target.ConnectionString,
                    "DELETE FROM Businesses WHERE Id = @business",
                    ("@business", businessId))));

        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Removal assertions passed: the business deleted after the scan produced no notice.");
    }

    [Fact]
    public async Task The_guarded_enqueue_refuses_a_non_canonical_notice()
    {
        var target = await PrepareAsync("_CanonicalNotice");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
            businessId = await SeedBusinessAsync(connection, "t25-canonical-notice", "t25.canonical.notice@example.invalid");

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlStockReviewRepository(session);

        // A notice whose stored key names a different bucket than its own due date is not the canonical key, so the
        // guarded insert must refuse it instead of writing a row under a key the contract never produced.
        var notice = new StockReviewNotice(
            businessId,
            Anchor,
            DefaultEveryDays,
            FirstBucket,
            new Notification
            {
                BusinessId = businessId,
                Type = NotificationType.StockReview,
                Recipient = "t25.canonical.notice@example.invalid",
                Subject = "revisión fuera de clave",
                BodyHtml = "<p>revisión fuera de clave</p>",
                DedupKey = $"stock:business:{businessId}:on:20260109",
                CreatedAt = Now
            });

        Assert.False(await repository.TryEnqueueReviewAsync(notice));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Canonical-notice assertions passed: the enqueue refused a key that did not match the notice's bucket.");
    }

    // ---- HTML safety ----

    [Fact]
    public async Task The_body_encodes_business_and_supply_names()
    {
        var target = await PrepareAsync("_Html");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(
                connection, "t25-html", "t25.html@example.invalid", businessName: "<b>Estudio</b>");
            await SeedSupplyAsync(connection, businessId, "<b>Shampoo</b> & Spa");
        }

        await using var session = new DbSession(target.ConnectionString);
        Assert.Equal(1, await new StockReviewService(new SqlStockReviewRepository(session)).RunAsync(Now));

        var notice = await ReadNotificationAsync(target.ConnectionString, Key(businessId, FirstBucket));
        Assert.Contains("&lt;b&gt;Estudio&lt;/b&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Shampoo&lt;/b&gt; &amp; Spa", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Shampoo</b>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Estudio</b>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("http", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Null(notice.LastError);

        output.WriteLine(
            "HTML assertions passed: the business and supply names reached the body HTML-encoded and no link was " +
            "present.");
    }

    // ---- The scan is global, bounded and id-ordered ----

    [Fact]
    public async Task The_scan_is_bounded_to_the_batch_size_and_ordered_by_business_id()
    {
        var target = await PrepareAsync("_Batch");
        if (target is null)
            return;

        var seeded = new List<int>();
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            for (var index = 0; index < StockReviewService.BatchSize + 1; index++)
            {
                var businessId = await SeedBusinessAsync(
                    connection, $"t25-batch-{index}", $"t25.batch.{index}@example.invalid");
                seeded.Add(businessId);
            }
        }

        await using var session = new DbSession(target.ConnectionString);
        var candidates = await new SqlStockReviewRepository(session)
            .ListCandidatesAsync(Now, StockReviewService.BatchSize);

        Assert.Equal(StockReviewService.BatchSize, candidates.Count);
        Assert.Equal(seeded.Take(StockReviewService.BatchSize), candidates.Select(row => row.BusinessId));

        output.WriteLine(
            "Batch assertions passed: 21 eligible businesses produced exactly the 20 lowest ids in ascending order.");
    }

    // ---- Two independent sessions racing the same business produce exactly one review ----

    [Fact]
    public async Task Two_concurrent_sessions_enqueue_exactly_one_review()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
            businessId = await SeedBusinessAsync(connection, "t25-race", "t25.race@example.invalid");

        // The barrier forces both sessions to finish their scan before either inserts, so the unique dedup key — not a
        // lucky ordering — is what decides the winner.
        using var startGate = new Barrier(participantCount: 2);

        Task<int> RunAsync() => Task.Run(async () =>
        {
            await using var session = new DbSession(target.ConnectionString);
            var service = new StockReviewService(
                new HookedStockReviewRepository(
                    new SqlStockReviewRepository(session),
                    _ =>
                    {
                        startGate.SignalAndWait();
                        return Task.CompletedTask;
                    }));
            return await service.RunAsync(Now);
        });

        var results = await Task.WhenAll(RunAsync(), RunAsync());

        Assert.Equal(1, results.Sum());
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, Key(businessId, FirstBucket)));

        output.WriteLine(
            "Race assertions passed: two independent sessions with forced overlap enqueued exactly one review; this " +
            "is one forced overlap, not a universal no-deadlock claim.");
    }

    // --- Service and repository wiring ------------------------------------------------------------------------

    private static string Key(int businessId, DateOnly dueDate) => StockReviewService.DedupKey(businessId, dueDate);

    /// <summary>Delegates to the real port and runs <paramref name="afterList"/> between the scan and the enqueue.</summary>
    private sealed class HookedStockReviewRepository(
        IStockReviewRepository inner,
        Func<IReadOnlyList<StockReviewCandidate>, Task> afterList) : IStockReviewRepository
    {
        public async Task<IReadOnlyList<StockReviewCandidate>> ListCandidatesAsync(
            DateTime now, int batchSize, CancellationToken ct = default)
        {
            var candidates = await inner.ListCandidatesAsync(now, batchSize, ct);
            await afterList(candidates);
            return candidates;
        }

        public Task<bool> TryEnqueueReviewAsync(StockReviewNotice notice, CancellationToken ct = default) =>
            inner.TryEnqueueReviewAsync(notice, ct);
    }

    // --- Seeding ------------------------------------------------------------------------------------------------

    private static Task<int> SeedBusinessAsync(
        SqlConnection connection, string slug, string? notificationEmail,
        int everyDays = DefaultEveryDays, string? contactPhone = null, string businessName = "Synthetic T25",
        DateTime? createdAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt, StockReviewEveryDays)
            OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @createdAt, @everyDays)
            """,
            ("@name", businessName), ("@slug", slug), ("@phone", contactPhone ?? "0000000000"),
            ("@email", notificationEmail), ("@createdAt", createdAt ?? Anchor), ("@everyDays", everyDays));

    private static Task<int> SeedSupplyAsync(
        SqlConnection connection, int businessId, string name, decimal currentStock = 10m,
        decimal minimumStock = 2m, bool isActive = true, DateTime? lastDeclaredAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Supplies (BusinessId, Name, CurrentStock, MinimumStock, LastDeclaredAt, IsActive)
            OUTPUT INSERTED.Id VALUES (@business, @name, @current, @minimum, @lastDeclared, @isActive)
            """,
            ("@business", businessId), ("@name", name), ("@current", currentStock),
            ("@minimum", minimumStock), ("@lastDeclared", lastDeclaredAt ?? Now), ("@isActive", isActive));

    private static Task<int> InsertNotificationAsync(
        SqlConnection connection, int businessId, NotificationType type, string dedupKey, DateTime createdAt) =>
        InsertAsync(connection,
            """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @type, 'seed@example.invalid', 'seed', '<p>seed</p>', @dedupKey, @status, 0, @createdAt)
            """,
            ("@business", businessId), ("@type", type), ("@dedupKey", dedupKey),
            ("@status", NotificationStatus.Pending), ("@createdAt", createdAt));

    // --- Reads -------------------------------------------------------------------------------------------------

    private static async Task<StoredNotification> ReadNotificationAsync(string connectionString, string dedupKey)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(
            """
            SELECT BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt
              FROM Notifications WHERE DedupKey = @dedupKey
            """,
            connection);
        command.Parameters.Add("@dedupKey", SqlDbType.NVarChar, 150).Value = dedupKey;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Notification '{dedupKey}' was not found.");
        return new StoredNotification(
            reader.GetInt32(0),
            (NotificationType)reader.GetByte(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            (NotificationStatus)reader.GetByte(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetDateTime(9));
    }

    private static Task<int> CountNotificationsAsync(string connectionString, string dedupKey) => CountAsync(
        connectionString, "SELECT COUNT(*) FROM Notifications WHERE DedupKey = @dedupKey", ("@dedupKey", dedupKey));

    private static async Task<int> CountAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.Add(Parameter(name, value));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<decimal> ScalarDecimalAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.Add(Parameter(name, value));
        return Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.Add(Parameter(name, value));
        await command.ExecuteNonQueryAsync();
    }

    // --- Opt-in target ------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves this test's opt-in target. <c>null</c> means the variable is absent: the test returns N/A without
    /// opening any connection or touching any server. Every created database is retained and never reused or dropped.
    /// </summary>
    private async Task<SqlTarget?> PrepareAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"N/A: {EnvVar} absent; no SQL connection opened.");
            return null;
        }

        var target = ValidateTarget(supplied, testSuffix);

        var master = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            TrustServerCertificate = true
        };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@database)", connection);
            check.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = target.DatabaseName;
            var existing = await check.ExecuteScalarAsync();
            Assert.True(existing is null or DBNull,
                $"Refusing existing database {target.DatabaseName}; the target must be new and is never reused or dropped.");
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        Assert.Contains("001_schema.sql", scripts);
        Assert.Contains("002_client_account_pending_names.sql", scripts);
        Assert.Contains("003_outbox_dispatch.sql", scripts);
        output.WriteLine(
            $"Target {target.DatabaseName}: DB_ID absent before migration; applied {scripts.Count} schema script(s); retained permanently without teardown.");
        return target;
    }

    /// <summary>
    /// Derives this test's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog, so
    /// one <c>WORKUP_T25_STOCK_SQL_CONNECTION</c> value serves every fact. Only the validated LocalDB instance and the
    /// derived database survive into the connection string.
    /// </summary>
    private static SqlTarget ValidateTarget(string suppliedConnectionString, string testSuffix)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{EnvVar} could not be parsed; no SQL connection was opened.");
        }

        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && databaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal)
            && SafeDatabaseName.IsMatch(databaseName);
        if (!string.Equals(supplied.DataSource, LocalDb, StringComparison.OrdinalIgnoreCase)
            || !supplied.IntegratedSecurity
            || !safeName
            || !string.IsNullOrEmpty(supplied.UserID)
            || !string.IsNullOrEmpty(supplied.Password)
            || !string.IsNullOrEmpty(supplied.AttachDBFilename)
            || supplied.UserInstance
            || !string.IsNullOrEmpty(supplied.FailoverPartner))
        {
            throw new InvalidOperationException(
                $"{EnvVar} must target {LocalDb} with Integrated Security and a safe {DatabasePrefix}* database; credentials, attached files and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T25 stock review SQL integration test",
            TrustServerCertificate = true,
            MultipleActiveResultSets = false
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    // --- Plain SQL helpers --------------------------------------------------------------------------------------

    private static async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> InsertAsync(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.Add(Parameter(name, value));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlParameter Parameter(string name, object? value) => value switch
    {
        null => new SqlParameter(name, DBNull.Value),
        DateTime dateTime => new SqlParameter(name, SqlDbType.DateTime2) { Value = dateTime },
        Enum enumValue => new SqlParameter(name, Convert.ToByte(enumValue, CultureInfo.InvariantCulture)),
        _ => new SqlParameter(name, value)
    };

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record StoredNotification(
        int BusinessId, NotificationType Type, string Recipient, string Subject, string BodyHtml, string DedupKey,
        NotificationStatus Status, int Attempts, string? LastError, DateTime CreatedAt);
}
