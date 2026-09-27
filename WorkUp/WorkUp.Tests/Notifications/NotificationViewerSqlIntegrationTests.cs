using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlNotificationViewerRepository"/> (T26, plan assumptions
/// 10 and 11), plus the default-run contract facts that need no server. It requires a fresh, dedicated database
/// supplied through <c>WORKUP_T26_VIEWER_SQL_CONNECTION</c>; without it every SQL test returns N/A and no connection is
/// opened. Each test derives its own database name from the supplied one, so the whole class runs in a single command,
/// the preflight asserts <c>DB_ID</c> is absent before migrating <c>001</c>+<c>002</c>+<c>003</c>, and nothing is torn
/// down: every target is retained on purpose and never reused.
/// <para>
/// The viewer is a read path over the very table the dispatcher writes, and the two demo businesses share one simulated
/// clock (plan assumption 11). Sharing the clock moves both tenants in time at once, so tenant isolation cannot depend
/// on time, on an unguessable id or on the recipient address: it must come from <c>Notifications.BusinessId</c>
/// compared against the caller's authenticated business, exactly as this fixture proves. What the in-memory fake cannot
/// prove, and this fixture does: that the list reads only the eight summary columns and never the body, the safe reason
/// or the dispatch claim; that the newest-first page is a strict keyset (<c>Id &lt; beforeId</c>) with a hard bound and
/// no offset; that the body is reachable only through the one-row detail of the caller's own business; that a notice
/// with no business at all — an account verification or password reset that can carry the owner's own notice address —
/// is invisible and never inferred from that address; and that reading changes nothing on the row.
/// </para>
/// <para>
/// The default-run facts at the end of the class need no database and are the shared-contract evidence of the unit: the
/// list projection has no member able to carry a body, a reason or a claim; both adapters answer an invalid identifier
/// generically and without touching storage; and the fake mirrors the bounded keyset and the tenant filter. They must
/// never be presented as proof of the SQL behavior, which only the opt-in facts below can show.
/// </para>
/// </summary>
public sealed class NotificationViewerSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T26_VIEWER_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT26ViewerSql";
    private const string OwnerNoticeEmail = "avisos@duena.example";
    private const string Subject = "Turno confirmado";
    private const string DefaultBodyHtml = "<p>Cuerpo del aviso</p>";

    /// <summary>A body like the queued ones: it can embed a single-use private appointment link.</summary>
    private const string PrivateLinkToken = "EnlacePrivadoDeUnSoloUso123456";

    private const string PrivateBodyHtml =
        "<p><a href=\"https://workup.invalid/turno/" + PrivateLinkToken + "\">Ver mi turno</a></p>";

    private const string NeverOpenableConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT26ViewerGuardNeverExists;Integrated Security=true;Connect Timeout=1";

    private static readonly Regex SafeDatabaseName =
        new(@"\AWorkUpDemoT26ViewerSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>The instant every seeded notice was created at, and the sent/claimed instant of the seeded rows.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    // --- Shared contract, no server needed ---------------------------------------------------------------------

    /// <summary>
    /// Structural proof of the list projection. The list is rendered for many rows at once, so it must be unable to
    /// carry the notice body (which can embed a single-use private link or a reset token), the stored error, the
    /// dispatcher's claim (<c>ClaimedAt</c>, <c>DispatchKey</c>), the retry gate or the dedup key. Equality against the
    /// exact member set fails on any added name, so no forbidden field can be introduced unnoticed.
    /// </summary>
    [Fact]
    public void The_list_projection_carries_no_body_error_claim_or_dedup_member_and_the_detail_adds_only_the_body()
    {
        Assert.Equal(
            new[] { "Attempts", "CreatedAt", "Id", "Recipient", "SentAt", "Status", "Subject", "Type" },
            PublicPropertyNames(typeof(NotificationViewerListItem)));

        // The detail projection is the only read that carries the body, and not even it carries the dispatcher's claim
        // or the stored error: the viewer shows the notice, it is not a window into dispatch internals.
        Assert.Equal(
            new[] { "Attempts", "BodyHtml", "CreatedAt", "Id", "Recipient", "SentAt", "Status", "Subject", "Type" },
            PublicPropertyNames(typeof(NotificationViewerDetail)));
    }

    /// <summary>
    /// The page is hard bounded by <see cref="NotificationViewerBounds.MaxPageSize"/>, so no caller can turn the viewer
    /// into a broad sweep of the outbox, and a nonsensical page size is clamped instead of reaching a storage engine as
    /// a negative or unbounded <c>TOP</c>.
    /// </summary>
    [Fact]
    public void Every_request_is_clamped_into_one_bounded_page()
    {
        Assert.Equal(50, NotificationViewerBounds.MaxPageSize);
        Assert.Equal(1, NotificationViewerBounds.ClampPageSize(0));
        Assert.Equal(1, NotificationViewerBounds.ClampPageSize(-7));
        Assert.Equal(20, NotificationViewerBounds.ClampPageSize(20));
        Assert.Equal(50, NotificationViewerBounds.ClampPageSize(50));
        Assert.Equal(50, NotificationViewerBounds.ClampPageSize(5_000));
    }

    /// <summary>
    /// An invalid tenant or notice identifier is answered exactly like a missing or foreign row — an empty page or
    /// <c>null</c> — and <b>nothing</b> is read. The SQL adapter is built on a connection string that can never be
    /// opened: a missing guard would raise a <see cref="SqlException"/> from the attempt to connect, so reaching the
    /// neutral answer is the proof that no command and no connection exist. The fake is checked with its own read
    /// counters for the same reason.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task An_invalid_tenant_or_notice_identifier_is_answered_like_a_missing_row_without_a_read(
        int invalidId)
    {
        await using var session = new DbSession(NeverOpenableConnectionString);
        var sql = new SqlNotificationViewerRepository(session);

        Assert.Empty(await sql.ListRecentAsync(invalidId, beforeId: null, pageSize: 20));
        Assert.Null(await sql.GetDetailAsync(invalidId, notificationId: 1));
        Assert.Null(await sql.GetDetailAsync(businessId: 1, invalidId));

        var fake = new FakeNotificationViewerRepository();
        fake.Seed(businessId: 1);
        Assert.Empty(await fake.ListRecentAsync(invalidId, beforeId: null, pageSize: 20));
        Assert.Null(await fake.GetDetailAsync(invalidId, notificationId: 1));
        Assert.Null(await fake.GetDetailAsync(businessId: 1, invalidId));
        Assert.Equal(0, fake.ListCallCount);
        Assert.Equal(0, fake.DetailCallCount);

        output.WriteLine(
            $"Default-run guard assertions passed: identifier {invalidId} produced the neutral answer on both adapters " +
            "with no connection attempt and no read.");
    }

    /// <summary>
    /// The fake used by the owner controller and view tests must mirror the persistence contract, not approximate it:
    /// newest first by <c>Id</c>, a strict cursor, the hard page bound, the caller's tenant and nothing else. A notice
    /// of the other business and a notice with no business at all stay invisible even when they repeat the tenant's own
    /// recipient address and subject, because the tenant is never inferred from the addressee.
    /// </summary>
    [Fact]
    public async Task The_fake_mirrors_the_tenant_isolation_the_strict_keyset_and_the_page_bound_of_the_contract()
    {
        var fake = new FakeNotificationViewerRepository();

        // Fifty-five own notices, then one more own notice with a sent body: more than one page either way, so the
        // bound is observable.
        var own = new List<int>();
        for (var index = 0; index < 55; index++)
            own.Add(fake.Seed(businessId: 1, createdAt: FixedNow.AddSeconds(index)).Id);

        var ownSent = fake.Seed(
            businessId: 1, status: NotificationStatus.Sent, attempts: 1, sentAt: FixedNow, bodyHtml: PrivateBodyHtml);
        var foreign = fake.Seed(businessId: 2, recipient: OwnerNoticeEmail);
        var ownerlessReset = fake.Seed(
            businessId: null, type: NotificationType.PasswordReset, recipient: OwnerNoticeEmail);

        var mine = own.Append(ownSent.Id).OrderByDescending(id => id).ToArray();

        var firstPage = await fake.ListRecentAsync(businessId: 1, beforeId: null, pageSize: 5_000);
        Assert.Equal(mine.Take(NotificationViewerBounds.MaxPageSize), firstPage.Select(item => item.Id));
        Assert.Contains(ownSent.Id, firstPage.Select(item => item.Id));

        var secondPage = await fake.ListRecentAsync(
            businessId: 1, beforeId: firstPage[^1].Id, pageSize: NotificationViewerBounds.MaxPageSize);
        Assert.Equal(mine.Skip(NotificationViewerBounds.MaxPageSize), secondPage.Select(item => item.Id));

        // A non-positive cursor can never be a real identity value, so the strict keyset returns nothing.
        Assert.Empty(await fake.ListRecentAsync(businessId: 1, beforeId: 0, pageSize: 20));
        Assert.Empty(await fake.ListRecentAsync(businessId: 1, beforeId: mine[^1], pageSize: 20));

        // Only the other tenant's own row is visible to it, whatever the addresses say.
        var foreignPage = await fake.ListRecentAsync(businessId: 2, beforeId: null, pageSize: 20);
        Assert.Equal(new[] { foreign.Id }, foreignPage.Select(item => item.Id));
        Assert.DoesNotContain(ownerlessReset.Id, firstPage.Select(item => item.Id));
        Assert.DoesNotContain(ownerlessReset.Id, foreignPage.Select(item => item.Id));

        var detail = await fake.GetDetailAsync(businessId: 1, ownSent.Id);
        Assert.NotNull(detail);
        Assert.Equal(NotificationStatus.Sent, detail!.Status);
        Assert.Equal(1, detail.Attempts);
        Assert.Equal(FixedNow, detail.SentAt);
        Assert.Equal(OwnerNoticeEmail, detail.Recipient);
        Assert.Contains(PrivateLinkToken, detail.BodyHtml, StringComparison.Ordinal);

        Assert.Null(await fake.GetDetailAsync(businessId: 1, foreign.Id));
        Assert.Null(await fake.GetDetailAsync(businessId: 1, ownerlessReset.Id));
        Assert.Null(await fake.GetDetailAsync(businessId: 2, ownSent.Id));
        Assert.Null(await fake.GetDetailAsync(businessId: 1, ownSent.Id + 100_000));
        Assert.True(fake.ListCallCount > 0 && fake.DetailCallCount > 0);
    }

    // --- The outbox viewer against real SQL -------------------------------------------------------------------

    [Fact]
    public async Task Own_notices_of_every_status_are_listed_newest_first_while_other_tenants_stay_invisible()
    {
        var target = await PrepareAsync("_Statuses");
        if (target is null)
            return;

        int businessId;
        int foreignBusinessId;
        int ownPending, ownSent, ownReview, ownDispatching;
        int foreignFirst, foreignSecond;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t26-viewer-statuses-own", "Peluquería de Marina");
            foreignBusinessId = await SeedBusinessAsync(connection, "t26-viewer-statuses-foreign", "Estudio de Yoga de Lucía");

            ownPending = await SeedNotificationAsync(
                connection, businessId, NotificationType.BookingCreated, "viewer:status:pending", createdAt: FixedNow);
            ownSent = await SeedNotificationAsync(
                connection, businessId, NotificationType.AppointmentReminder, "viewer:status:sent",
                status: NotificationStatus.Sent, attempts: 1, createdAt: FixedNow, sentAt: FixedNow);
            ownReview = await SeedNotificationAsync(
                connection, businessId, NotificationType.DailySummary, "viewer:status:review",
                status: NotificationStatus.ReviewRequired, attempts: 3, lastError: OutboxDispatchService.UnknownReason,
                createdAt: FixedNow, claimedAt: FixedNow, dispatchKey: Guid.NewGuid());
            ownDispatching = await SeedNotificationAsync(
                connection, businessId, NotificationType.StockReview, "viewer:status:dispatching",
                status: NotificationStatus.Dispatching, attempts: 1, createdAt: FixedNow, claimedAt: FixedNow,
                dispatchKey: Guid.NewGuid());

            // The other demo business repeats the very same recipient address and subject, so only the tenant column can
            // keep its rows out of this page. A viewer that matched by addressee would leak them here.
            foreignFirst = await SeedNotificationAsync(
                connection, foreignBusinessId, NotificationType.BookingCreated, "viewer:foreign:1", createdAt: FixedNow);
            foreignSecond = await SeedNotificationAsync(
                connection, foreignBusinessId, NotificationType.MembershipExpired, "viewer:foreign:2",
                status: NotificationStatus.Sent, attempts: 1, createdAt: FixedNow, sentAt: FixedNow);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlNotificationViewerRepository(session);

        var page = await repository.ListRecentAsync(businessId, beforeId: null, pageSize: 50);

        // Newest first by Notification.Id descending, and exactly the four statuses the viewer must show.
        Assert.Equal(new[] { ownDispatching, ownReview, ownSent, ownPending }, page.Select(item => item.Id));

        var byId = page.ToDictionary(item => item.Id);
        Assert.Equal(NotificationStatus.Pending, byId[ownPending].Status);
        Assert.Equal(0, byId[ownPending].Attempts);
        Assert.Null(byId[ownPending].SentAt);
        Assert.Equal(NotificationType.BookingCreated, byId[ownPending].Type);

        Assert.Equal(NotificationStatus.Sent, byId[ownSent].Status);
        Assert.Equal(1, byId[ownSent].Attempts);
        Assert.Equal(FixedNow, byId[ownSent].SentAt);
        Assert.Equal(NotificationType.AppointmentReminder, byId[ownSent].Type);

        Assert.Equal(NotificationStatus.ReviewRequired, byId[ownReview].Status);
        Assert.Equal(3, byId[ownReview].Attempts);
        Assert.Null(byId[ownReview].SentAt);
        Assert.Equal(NotificationType.DailySummary, byId[ownReview].Type);

        Assert.Equal(NotificationStatus.Dispatching, byId[ownDispatching].Status);
        Assert.Equal(1, byId[ownDispatching].Attempts);

        Assert.All(page, item =>
        {
            Assert.Equal(OwnerNoticeEmail, item.Recipient);
            Assert.Equal(Subject, item.Subject);
            Assert.Equal(FixedNow, item.CreatedAt);
        });
        Assert.DoesNotContain(page, item => item.Id == foreignFirst || item.Id == foreignSecond);

        output.WriteLine(
            "SQL status assertions passed: the four own statuses were listed newest first with their attempts and sent " +
            "instant, and the other business kept out of the page despite repeating recipient and subject.");
    }

    [Fact]
    public async Task The_detail_is_the_only_read_that_carries_the_private_body_and_only_for_the_own_tenant()
    {
        var target = await PrepareAsync("_Detail");
        if (target is null)
            return;

        int businessId;
        int foreignBusinessId;
        int ownSent, foreignSent;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t26-viewer-detail-own", "Peluquería de Marina");
            foreignBusinessId = await SeedBusinessAsync(connection, "t26-viewer-detail-foreign", "Estudio de Yoga de Lucía");

            ownSent = await SeedNotificationAsync(
                connection, businessId, NotificationType.BookingCreated, "viewer:detail:own",
                status: NotificationStatus.Sent, attempts: 1, createdAt: FixedNow, sentAt: FixedNow,
                bodyHtml: PrivateBodyHtml);
            foreignSent = await SeedNotificationAsync(
                connection, foreignBusinessId, NotificationType.BookingCreated, "viewer:detail:foreign",
                status: NotificationStatus.Sent, attempts: 1, createdAt: FixedNow, sentAt: FixedNow,
                bodyHtml: "<p>Aviso del otro negocio</p>");
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlNotificationViewerRepository(session);

        var own = await repository.GetDetailAsync(businessId, ownSent);

        Assert.NotNull(own);
        Assert.Equal(ownSent, own!.Id);
        Assert.Equal(NotificationType.BookingCreated, own.Type);
        Assert.Equal(NotificationStatus.Sent, own.Status);
        Assert.Equal(OwnerNoticeEmail, own.Recipient);
        Assert.Equal(Subject, own.Subject);
        Assert.Equal(1, own.Attempts);
        Assert.Equal(FixedNow, own.CreatedAt);
        Assert.Equal(FixedNow, own.SentAt);
        Assert.Contains(PrivateLinkToken, own.BodyHtml, StringComparison.Ordinal);

        // The same row through the list carries no body: the list projection has no member for it (default-run fact),
        // and a mapper that tried to read an unselected column would fail the call itself.
        var page = await repository.ListRecentAsync(businessId, beforeId: null, pageSize: 50);
        Assert.Equal(ownSent, Assert.Single(page).Id);

        // A cross-tenant id probe and a missing id are answered with null, and the body never travels with them.
        Assert.Null(await repository.GetDetailAsync(businessId, foreignSent));
        Assert.Null(await repository.GetDetailAsync(foreignBusinessId, ownSent));
        Assert.Null(await repository.GetDetailAsync(businessId, ownSent + foreignSent + 100_000));

        output.WriteLine(
            "SQL detail assertions passed: the private body came back only for the caller's own notice, while the " +
            "cross-tenant probe and the missing id were both null.");
    }

    [Fact]
    public async Task The_keyset_cursor_walks_from_newest_to_oldest_without_an_offset_and_the_page_is_bounded()
    {
        var target = await PrepareAsync("_Keyset");
        if (target is null)
            return;

        int businessId;
        List<int> seeded;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t26-viewer-keyset", "Peluquería de Marina");
            seeded = await SeedBulkNotificationsAsync(connection, businessId, count: 55);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlNotificationViewerRepository(session);

        var ordered = seeded.OrderByDescending(id => id).ToArray();

        var firstPage = await repository.ListRecentAsync(businessId, beforeId: null, pageSize: 50);
        Assert.Equal(ordered.Take(NotificationViewerBounds.MaxPageSize), firstPage.Select(item => item.Id));
        Assert.True(
            firstPage.Select(item => item.Id).SequenceEqual(firstPage.Select(item => item.Id).OrderByDescending(id => id)),
            "The page must be ordered by Notification.Id descending.");

        // A request above the bound is clamped: the page is never an unbounded sweep of the outbox.
        var clamped = await repository.ListRecentAsync(businessId, beforeId: null, pageSize: 5_000);
        Assert.Equal(NotificationViewerBounds.MaxPageSize, clamped.Count);
        Assert.Equal(firstPage.Select(item => item.Id), clamped.Select(item => item.Id));

        // The cursor is strictly older than the last id of the previous page: no offset, no repeat, no gap.
        var cursor = firstPage[^1].Id;
        Assert.Equal(ordered[NotificationViewerBounds.MaxPageSize - 1], cursor);
        var secondPage = await repository.ListRecentAsync(businessId, beforeId: cursor, pageSize: 50);
        Assert.Equal(ordered.Skip(NotificationViewerBounds.MaxPageSize), secondPage.Select(item => item.Id));

        // Walking one step past the oldest row ends the sequence instead of looping or restarting.
        Assert.Empty(await repository.ListRecentAsync(businessId, beforeId: ordered[^1], pageSize: 50));
        // A non-positive cursor can never be a real identity value, so the strict keyset returns nothing at all.
        Assert.Empty(await repository.ListRecentAsync(businessId, beforeId: 0, pageSize: 50));

        output.WriteLine(
            "SQL keyset assertions passed: fifty newest rows, then the strict cursor returned the remaining five, and " +
            "the exhausted and non-positive cursors returned nothing.");
    }

    [Fact]
    public async Task A_notice_without_a_business_is_never_returned_even_when_it_looks_like_an_account_email()
    {
        var target = await PrepareAsync("_Ownerless");
        if (target is null)
            return;

        int businessId;
        int ownId, ownerlessVerification, ownerlessReset;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t26-viewer-ownerless", "Peluquería de Marina");

            // Account notices carry no tenant, and the owner's own notice address can be the very address they use. The
            // viewer must never guess the tenant from that address.
            ownerlessVerification = await SeedNotificationAsync(
                connection, businessId: null, NotificationType.EmailVerification, "viewer:ownerless:verify",
                recipient: OwnerNoticeEmail, bodyHtml: PrivateBodyHtml);
            ownerlessReset = await SeedNotificationAsync(
                connection, businessId: null, NotificationType.PasswordReset, "viewer:ownerless:reset",
                recipient: OwnerNoticeEmail, bodyHtml: "<p>Restablecer la contraseña</p>");

            ownId = await SeedNotificationAsync(
                connection, businessId, NotificationType.BookingCreated, "viewer:ownerless:own", createdAt: FixedNow);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlNotificationViewerRepository(session);

        var page = await repository.ListRecentAsync(businessId, beforeId: null, pageSize: 50);
        Assert.Equal(new[] { ownId }, page.Select(item => item.Id));

        Assert.Null(await repository.GetDetailAsync(businessId, ownerlessVerification));
        Assert.Null(await repository.GetDetailAsync(businessId, ownerlessReset));

        output.WriteLine(
            "SQL ownerless assertions passed: the account verification and the password reset with a NULL BusinessId " +
            "stayed invisible to the owner whose notice address they repeat.");
    }

    [Fact]
    public async Task Listing_and_reading_a_notice_never_modify_its_stored_row()
    {
        var target = await PrepareAsync("_ReadOnly");
        if (target is null)
            return;

        int businessId;
        int reviewId;
        List<int> others;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t26-viewer-readonly", "Peluquería de Marina");
            reviewId = await SeedNotificationAsync(
                connection, businessId, NotificationType.AccountInvitation, "viewer:readonly:review",
                status: NotificationStatus.ReviewRequired, attempts: 2, lastError: OutboxDispatchService.UnknownReason,
                createdAt: FixedNow.AddMinutes(-5), claimedAt: FixedNow, dispatchKey: Guid.NewGuid(),
                bodyHtml: PrivateBodyHtml);
            others = await SeedBulkNotificationsAsync(connection, businessId, count: 3);
        }

        var before = await ReadNotificationAsync(target.ConnectionString, reviewId);

        await using (var session = new DbSession(target.ConnectionString))
        {
            var repository = new SqlNotificationViewerRepository(session);
            for (var round = 0; round < 3; round++)
            {
                await repository.ListRecentAsync(businessId, beforeId: null, pageSize: 50);
                await repository.ListRecentAsync(businessId, beforeId: others.Min(), pageSize: 50);
                Assert.NotNull(await repository.GetDetailAsync(businessId, reviewId));
            }
        }

        // Every column of the row is byte-for-byte what it was: the viewer is a read path over the dispatcher's table
        // and may never touch the status, the attempt count, the body, the safe reason or the claim.
        var after = await ReadNotificationAsync(target.ConnectionString, reviewId);
        Assert.Equal(before, after);

        output.WriteLine(
            "SQL read-only assertions passed: after three list and detail rounds the row kept its status, attempts, " +
            "body and claim unchanged.");
    }

    // --- Shared contract helpers -------------------------------------------------------------------------------

    private static string[] PublicPropertyNames(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => property.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    // --- Seeding ----------------------------------------------------------------------------------------------

    private static Task<int> SeedBusinessAsync(
        SqlConnection connection, string slug, string name, DateTime? createdAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @notice, @createdAt)
            """,
            ("@name", name), ("@slug", slug), ("@phone", "0000000000"), ("@notice", OwnerNoticeEmail),
            ("@createdAt", createdAt ?? FixedNow.AddDays(-30)));

    private static Task<int> SeedNotificationAsync(
        SqlConnection connection, int? businessId, NotificationType type, string dedupKey,
        NotificationStatus status = NotificationStatus.Pending, string recipient = OwnerNoticeEmail,
        string subject = Subject, string bodyHtml = DefaultBodyHtml, int attempts = 0, DateTime? createdAt = null,
        DateTime? sentAt = null, string? lastError = null, DateTime? claimedAt = null, Guid? dispatchKey = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt, ClaimedAt, DispatchKey)
            OUTPUT INSERTED.Id
            VALUES (@business, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, @attempts, @lastError, @createdAt, @sentAt, @claimedAt, @dispatchKey)
            """,
            ("@business", businessId), ("@type", type), ("@recipient", recipient), ("@subject", subject),
            ("@bodyHtml", bodyHtml), ("@dedupKey", dedupKey), ("@status", status), ("@attempts", attempts),
            ("@lastError", lastError), ("@createdAt", createdAt ?? FixedNow), ("@sentAt", sentAt),
            ("@claimedAt", claimedAt), ("@dispatchKey", dispatchKey));

    /// <summary>
    /// Seeds <paramref name="count"/> notices of one business in a single statement and returns their identity values,
    /// so a fact can seed more rows than one page and still assert the hard bound.
    /// </summary>
    private static async Task<List<int>> SeedBulkNotificationsAsync(SqlConnection connection, int businessId, int count)
    {
        const string sql = """
            DECLARE @ids TABLE (Id INT);
            ;WITH numbers AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                  FROM sys.all_columns)
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt, ClaimedAt, DispatchKey)
            OUTPUT INSERTED.Id INTO @ids
            SELECT @business, @type, @recipient, @subject, @bodyHtml,
                   'viewer:bulk:' + CAST(n AS NVARCHAR(10)), @status, 0, NULL,
                   DATEADD(SECOND, n, @createdAt), NULL, NULL, NULL
              FROM numbers;
            SELECT Id FROM @ids ORDER BY Id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(Parameter("@count", count));
        command.Parameters.Add(Parameter("@business", businessId));
        command.Parameters.Add(Parameter("@type", NotificationType.BookingCreated));
        command.Parameters.Add(Parameter("@recipient", OwnerNoticeEmail));
        command.Parameters.Add(Parameter("@subject", Subject));
        command.Parameters.Add(Parameter("@bodyHtml", DefaultBodyHtml));
        command.Parameters.Add(Parameter("@status", NotificationStatus.Pending));
        command.Parameters.Add(Parameter("@createdAt", FixedNow));

        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<int>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetInt32(0));
        Assert.Equal(count, ids.Count);
        return ids;
    }

    // --- Plain reads of the persisted row ---------------------------------------------------------------------

    private sealed record StoredNotification(
        int Id, int? BusinessId, byte Type, byte Status, int Attempts, string Recipient, string Subject,
        string BodyHtml, string DedupKey, string? LastError, DateTime CreatedAt, DateTime? SentAt,
        DateTime? NextAttemptAt, DateTime? ClaimedAt, Guid? DispatchKey);

    private static async Task<StoredNotification> ReadNotificationAsync(string connectionString, int id)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(
            """
            SELECT Id, BusinessId, Type, Status, Attempts, Recipient, Subject, BodyHtml, DedupKey, LastError,
                   CreatedAt, SentAt, NextAttemptAt, ClaimedAt, DispatchKey
              FROM Notifications
             WHERE Id = @id
            """,
            connection);
        command.Parameters.Add(Parameter("@id", id));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Notification {id} was not found.");
        return new StoredNotification(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetByte(2),
            reader.GetByte(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetDateTime(10),
            reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            reader.IsDBNull(12) ? null : reader.GetDateTime(12),
            reader.IsDBNull(13) ? null : reader.GetDateTime(13),
            reader.IsDBNull(14) ? null : reader.GetGuid(14));
    }

    // --- Opt-in target ----------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves this test's opt-in target. <c>null</c> means the variable is absent: the test returns N/A without
    /// opening any connection or touching any server. The created database is retained and never reused or dropped.
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
    /// one <c>WORKUP_T26_VIEWER_SQL_CONNECTION</c> value serves every fact. Only the validated LocalDB instance and the
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
            ApplicationName = "WorkUp T26 email viewer SQL integration test",
            TrustServerCertificate = true,
            MultipleActiveResultSets = false
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    // --- Plain SQL helpers ------------------------------------------------------------------------------------

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
        Guid guid => new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = guid },
        Enum enumValue => new SqlParameter(name, Convert.ToByte(enumValue, CultureInfo.InvariantCulture)),
        _ => new SqlParameter(name, value)
    };

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
