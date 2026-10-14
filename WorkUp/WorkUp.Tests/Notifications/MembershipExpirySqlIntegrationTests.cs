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
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlMembershipExpiryRepository"/> (T25, RF-29, RF-30)
/// driven through the real <see cref="MembershipExpiryService"/>. It requires a fresh, dedicated database supplied
/// through <c>WORKUP_T25_MEMBERSHIP_SQL_CONNECTION</c>; without it every SQL test returns N/A and no connection is
/// opened. Each test derives its own database name from the supplied one, so the whole class runs in a single command,
/// the preflight asserts <c>DB_ID</c> is absent before migrating, and nothing is torn down: the targets are retained
/// on purpose and never reused.
/// <para>
/// The in-memory fake and the service tests cannot prove what SQL Server does with the global bounded scan, the
/// tenant joins, the latest-period selection or a concurrent insert. This fixture is the runtime proof of the port:
/// one <c>TOP (batchSize)</c>, enrollment-id-ordered scan that returns only the latest persisted
/// <c>MembershipPeriod</c> of an <c>Active</c> enrollment created no later than the scan instant, selected by
/// <c>ExpiresOn DESC, Id DESC</c> and never filtered by payment annulment (T18), carrying the same-tenant
/// <c>Clients.Email</c> — never <c>ClientAccounts.Email</c> — as recipient, and only when its stage is due at the scan
/// instant (<c>[ExpiresOn-3, ExpiresOn)</c> for the reminder and the local expiry date only for the at-expiry notice),
/// its exact stage key is absent and, after a rewound clock, the later expired key of the same period is absent too;
/// and one guarded <c>INSERT ... SELECT</c> that rechecks the live enrollment, the same business/client, the exact
/// recipient, the active status, the same period still being the latest with the same expiry, the stage window at the
/// composed instant, the canonical type/key and the no-later-stage rule, so a renewal, termination, client or email
/// change, a repeated tick or a forced two-session overlap is a no-op instead of a stale or duplicate email.
/// </para>
/// </summary>
public sealed class MembershipExpirySqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T25_MEMBERSHIP_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT25MembershipSql";

    private static readonly Regex SafeDatabaseName =
        new(@"\AWorkUpDemoT25MembershipSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>The instant every seeded enrollment, client and period was created at, before every scan instant.</summary>
    private static readonly DateTime SeedCreatedAt = new(2026, 1, 1, 8, 0, 0);

    /// <summary>The expiry date most scenarios use: the first day without coverage (RF-29 step 2).</summary>
    private static readonly DateOnly Expiry = new(2026, 3, 10);

    private static readonly DateTime FourDaysBefore = new(2026, 3, 6, 9, 0, 0);
    private static readonly DateTime ThreeDaysBefore = new(2026, 3, 7, 9, 0, 0);
    private static readonly DateTime TwoDaysBefore = new(2026, 3, 8, 9, 0, 0);
    private static readonly DateTime OneDayBefore = new(2026, 3, 9, 9, 0, 0);
    private static readonly DateTime ExpiryDay = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime OneDayAfter = new(2026, 3, 11, 9, 0, 0);
    private static readonly DateTime MonthsAfter = new(2026, 9, 10, 9, 0, 0);

    // ---- The latest period, the client record recipient and the two stages ----

    [Fact]
    public async Task The_scan_returns_the_latest_period_of_an_active_enrollment_and_its_client_record_recipient()
    {
        var target = await PrepareAsync("_Latest");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var businessId = await SeedBusinessAsync(connection, "t25-latest", "Synthetic T25");
            var clientId = await SeedClientAsync(connection, businessId, "ficha@example.com", "Ana", "Perez");
            // Decoy account email: the notice must never travel to the account (RF-30 recipient is the client record).
            await SeedAccountAsync(connection, businessId, clientId, "cuenta@example.com");
            var enrollmentId = await SeedEnrollmentAsync(
                connection, businessId, clientId, EnrollmentStatus.Active, SeedCreatedAt);
            await SeedPeriodAsync(
                connection, businessId, clientId, enrollmentId, Expiry.AddMonths(-1), Expiry, SeedCreatedAt);
            var laterPeriodId = await SeedPeriodAsync(
                connection, businessId, clientId, enrollmentId, Expiry, Expiry.AddMonths(1), SeedCreatedAt);
            seed = new MembershipSeed(businessId, clientId, enrollmentId, laterPeriodId);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);
        // 2026-04-08 is two days before the newest expiry, inside the reminder window.
        var laterWindow = new DateTime(2026, 4, 8, 9, 0, 0);

        var candidate = Assert.Single(await repository.ListCandidatesAsync(laterWindow, MembershipExpiryService.BatchSize));
        Assert.Equal(seed.EnrollmentId, candidate.EnrollmentId);
        Assert.Equal(seed.ClientId, candidate.ClientId);
        Assert.Equal(seed.BusinessId, candidate.BusinessId);
        Assert.Equal(seed.PeriodId, candidate.PeriodId);
        Assert.Equal(EnrollmentStatus.Active, candidate.Status);
        Assert.Equal(new DateOnly(2026, 4, 10), candidate.ExpiresOn);
        Assert.False(candidate.HasLaterPeriod);
        Assert.Equal("ficha@example.com", candidate.Recipient);
        Assert.Equal("Ana Perez", candidate.ClientName);
        Assert.Equal("Synthetic T25", candidate.BusinessName);

        Assert.Equal(1, await new MembershipExpiryService(repository).RunAsync(laterWindow));

        var notice = await ReadNotificationAsync(
            target.ConnectionString,
            MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring));
        Assert.Equal(NotificationType.MembershipExpiring, notice.Type);
        Assert.Equal("ficha@example.com", notice.Recipient);
        Assert.NotEqual("cuenta@example.com", notice.Recipient);
        Assert.Equal(laterWindow, notice.CreatedAt);
        Assert.Null(notice.LastError);

        output.WriteLine(
            "Latest/recipient assertions passed: the scan carried the newest period and the client ficha email, never " +
            "the account email.");
    }

    [Fact]
    public async Task The_two_stages_fire_once_each_on_their_own_local_windows()
    {
        var target = await PrepareAsync("_Stages");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
            seed = await SeedActiveAsync(connection, "t25-stages", Expiry);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);
        var service = new MembershipExpiryService(repository);

        // The reminder window is [ExpiresOn - 3, ExpiresOn): four days before is too early, the three days before and
        // the day before are due, and a late tick the same window is caught up.
        Assert.Equal(0, await service.RunAsync(FourDaysBefore));
        Assert.Equal(1, await service.RunAsync(ThreeDaysBefore));
        Assert.Equal(0, await service.RunAsync(TwoDaysBefore));
        Assert.Equal(0, await service.RunAsync(OneDayBefore));
        // The at-expiry stage is due on the local expiry date and that date only; a later day is never a catch-up.
        Assert.Equal(1, await service.RunAsync(ExpiryDay));
        Assert.Equal(0, await service.RunAsync(OneDayAfter));
        Assert.Equal(0, await service.RunAsync(MonthsAfter));

        Assert.Equal(2, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));
        Assert.Equal(
            1,
            await CountNotificationsAsync(
                target.ConnectionString,
                MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring)));
        Assert.Equal(
            1,
            await CountNotificationsAsync(
                target.ConnectionString,
                MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expired)));

        output.WriteLine(
            "Stage-window assertions passed: -4/-3/-2/-1/expiry/+1/months produced exactly the expiring and the " +
            "expired notice, once each.");
    }

    // ---- Renewal, ties and ineligible enrollments ----

    [Fact]
    public async Task An_early_renewal_suppresses_the_superseded_periods_notices()
    {
        var target = await PrepareAsync("_EarlyRenewal");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var seed = await SeedActiveAsync(connection, "t25-early", Expiry);
            // Advance renewal (RF-29 step 3): the new coverage starts at the old expiry, so the old period stops being
            // the latest and neither of its stages may fire.
            await SeedPeriodAsync(
                connection, seed.BusinessId, seed.ClientId, seed.EnrollmentId,
                Expiry, Expiry.AddMonths(1), SeedCreatedAt);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);
        var service = new MembershipExpiryService(repository);

        Assert.Empty(await repository.ListCandidatesAsync(TwoDaysBefore, MembershipExpiryService.BatchSize));
        Assert.Equal(0, await service.RunAsync(TwoDaysBefore));
        Assert.Equal(0, await service.RunAsync(ExpiryDay));
        Assert.Equal(0, await service.RunAsync(OneDayAfter));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Early-renewal assertions passed: the superseded period produced neither the reminder nor the at-expiry " +
            "notice.");
    }

    [Fact]
    public async Task A_late_renewal_never_backfills_the_old_period()
    {
        var target = await PrepareAsync("_LateRenewal");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var seed = await SeedActiveAsync(connection, "t25-late", Expiry);
            // Late renewal (RF-29 step 4): coverage restarts on the payment day, leaving a gap. The gap day is neither
            // the old expiry nor the new window, so nothing may be mailed.
            await SeedPeriodAsync(
                connection, seed.BusinessId, seed.ClientId, seed.EnrollmentId,
                new DateOnly(2026, 3, 20), new DateOnly(2026, 4, 20), SeedCreatedAt);
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = new MembershipExpiryService(new SqlMembershipExpiryRepository(session));

        Assert.Equal(0, await service.RunAsync(new DateTime(2026, 3, 25, 9, 0, 0)));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Late-renewal assertions passed: the gap day never backfilled the old period.");
    }

    [Fact]
    public async Task The_latest_period_wins_a_tie_broken_by_the_highest_period_id()
    {
        var target = await PrepareAsync("_Tie");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            seed = await SeedActiveAsync(connection, "t25-tie", Expiry);
            // Same expiry date, higher id: the latest is the one with the highest id (ExpiresOn DESC, Id DESC).
            var tiePeriodId = await SeedPeriodAsync(
                connection, seed.BusinessId, seed.ClientId, seed.EnrollmentId,
                Expiry.AddMonths(-1), Expiry, SeedCreatedAt);
            seed = seed with { PeriodId = tiePeriodId };
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        var candidate = Assert.Single(await repository.ListCandidatesAsync(ThreeDaysBefore, MembershipExpiryService.BatchSize));
        Assert.Equal(seed.PeriodId, candidate.PeriodId);

        Assert.Equal(1, await new MembershipExpiryService(repository).RunAsync(ThreeDaysBefore));
        Assert.Equal(1, await CountNotificationsAsync(
            target.ConnectionString,
            MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring)));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Tie assertions passed: the latest period by (ExpiresOn DESC, Id DESC) was the one notified.");
    }

    [Fact]
    public async Task Terminated_pending_and_periodless_enrollments_are_never_candidates()
    {
        var target = await PrepareAsync("_Ineligible");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var businessId = await SeedBusinessAsync(connection, "t25-ineligible", "Synthetic T25");

            var terminatedClient = await SeedClientAsync(connection, businessId, "terminated@example.com", "Baja", "Uno");
            var terminated = await SeedEnrollmentAsync(
                connection, businessId, terminatedClient, EnrollmentStatus.Terminated, SeedCreatedAt);
            await SeedPeriodAsync(
                connection, businessId, terminatedClient, terminated, Expiry.AddMonths(-1), Expiry, SeedCreatedAt);

            var pendingClient = await SeedClientAsync(connection, businessId, "pending@example.com", "Pendiente", "Dos");
            var pending = await SeedEnrollmentAsync(
                connection, businessId, pendingClient, EnrollmentStatus.PendingPayment, SeedCreatedAt);
            await SeedPeriodAsync(
                connection, businessId, pendingClient, pending, Expiry.AddMonths(-1), Expiry, SeedCreatedAt);

            var annulledClient = await SeedClientAsync(connection, businessId, "annulled@example.com", "Anulada", "Tres");
            var annulled = await SeedEnrollmentAsync(
                connection, businessId, annulledClient, EnrollmentStatus.Annulled, SeedCreatedAt);
            await SeedPeriodAsync(
                connection, businessId, annulledClient, annulled, Expiry.AddMonths(-1), Expiry, SeedCreatedAt);

            // Active but with no paid period at all.
            await SeedEnrollmentAsync(connection, businessId, annulledClient, EnrollmentStatus.Active, SeedCreatedAt);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(ThreeDaysBefore, MembershipExpiryService.BatchSize));
        Assert.Equal(0, await new MembershipExpiryService(repository).RunAsync(ThreeDaysBefore));
        Assert.Equal(0, await new MembershipExpiryService(repository).RunAsync(ExpiryDay));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Ineligible assertions passed: terminated, pending, annulled and periodless enrollments produced no " +
            "candidate.");
    }

    [Fact]
    public async Task An_annulled_payment_keeps_the_period_covered_and_noticed()
    {
        var target = await PrepareAsync("_Annulled");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var businessId = await SeedBusinessAsync(connection, "t25-annulled", "Synthetic T25");
            var clientId = await SeedClientAsync(connection, businessId, "alumno@example.com", "Ana", "Perez");
            var enrollmentId = await SeedEnrollmentAsync(
                connection, businessId, clientId, EnrollmentStatus.Active, SeedCreatedAt);
            // T18: annulling the payment is a purely financial correction that keeps the monthly coverage, so the period
            // must still be selected and must still produce its notice.
            var periodId = await SeedPeriodAsync(
                connection, businessId, clientId, enrollmentId, Expiry.AddMonths(-1), Expiry, SeedCreatedAt, annulled: true);
            seed = new MembershipSeed(businessId, clientId, enrollmentId, periodId);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        var candidate = Assert.Single(await repository.ListCandidatesAsync(TwoDaysBefore, MembershipExpiryService.BatchSize));
        Assert.Equal(seed.PeriodId, candidate.PeriodId);

        Assert.Equal(1, await new MembershipExpiryService(repository).RunAsync(TwoDaysBefore));
        Assert.Equal(1, await CountNotificationsAsync(
            target.ConnectionString,
            MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring)));

        output.WriteLine("Annulled-payment assertions passed: the annulled period was still noticed.");
    }

    // ---- Tenant scope ----

    [Fact]
    public async Task A_client_from_another_tenant_is_never_borrowed_as_recipient()
    {
        var target = await PrepareAsync("_Forged");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var own = await SeedBusinessAsync(connection, "t25-forged-own", "Propio");
            var foreign = await SeedBusinessAsync(connection, "t25-forged-foreign", "Ajeno");
            // A forged enrollment claims to belong to the first business but points at the second business' ficha.
            var foreignClient = await SeedClientAsync(connection, foreign, "ajeno@example.com", "Ajena", "Ficha");
            var forged = await SeedEnrollmentAsync(connection, own, foreignClient, EnrollmentStatus.Active, SeedCreatedAt);
            await SeedPeriodAsync(
                connection, own, foreignClient, forged, Expiry.AddMonths(-1), Expiry, SeedCreatedAt);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(ThreeDaysBefore, MembershipExpiryService.BatchSize));
        Assert.Equal(0, await new MembershipExpiryService(repository).RunAsync(ThreeDaysBefore));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Forged-tenant assertions passed: an enrollment whose client belongs to another business was not a " +
            "candidate.");
    }

    [Fact]
    public async Task Each_tenant_carries_only_its_own_client_recipient()
    {
        var target = await PrepareAsync("_Tenant");
        if (target is null)
            return;

        MembershipSeed first;
        MembershipSeed second;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            first = await SeedActiveAsync(connection, "t25-tenant-a", Expiry, "a@example.com", "Estudio A");
            second = await SeedActiveAsync(connection, "t25-tenant-b", Expiry, "b@example.com", "Estudio B");
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);
        var candidates = await repository.ListCandidatesAsync(ThreeDaysBefore, MembershipExpiryService.BatchSize);

        Assert.Equal(
            new[] { first.EnrollmentId, second.EnrollmentId },
            candidates.Select(candidate => candidate.EnrollmentId).ToArray());
        Assert.Equal("a@example.com", candidates[0].Recipient);
        Assert.Equal("Estudio A", candidates[0].BusinessName);
        Assert.Equal("b@example.com", candidates[1].Recipient);
        Assert.Equal("Estudio B", candidates[1].BusinessName);

        Assert.Equal(2, await new MembershipExpiryService(repository).RunAsync(ThreeDaysBefore));
        var a = await ReadNotificationAsync(
            target.ConnectionString,
            MembershipExpiryService.DedupKey(first.EnrollmentId, first.PeriodId, MembershipExpiryStage.Expiring));
        var b = await ReadNotificationAsync(
            target.ConnectionString,
            MembershipExpiryService.DedupKey(second.EnrollmentId, second.PeriodId, MembershipExpiryStage.Expiring));
        Assert.Equal("a@example.com", a.Recipient);
        Assert.Equal("b@example.com", b.Recipient);

        output.WriteLine("Tenant assertions passed: each candidate carried only its own business and client ficha.");
    }

    // ---- The T26 rewind: nothing created after the scan instant is seen ----

    [Fact]
    public async Task Rows_created_after_a_rewound_scan_instant_are_invisible()
    {
        var target = await PrepareAsync("_RewindCreate");
        if (target is null)
            return;

        // The simulated clock is rewound to 2026-03-08, but the whole enrollment and its period were created in April.
        var rewound = TwoDaysBefore;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var businessId = await SeedBusinessAsync(connection, "t25-rewind-create", "Synthetic T25");

            var lateClient = await SeedClientAsync(connection, businessId, "late@example.com", "Nueva", "Inscripcion");
            var lateEnrollment = await SeedEnrollmentAsync(
                connection, businessId, lateClient, EnrollmentStatus.Active, new DateTime(2026, 4, 1, 9, 0, 0));
            await SeedPeriodAsync(
                connection, businessId, lateClient, lateEnrollment,
                new DateOnly(2026, 4, 10), new DateOnly(2026, 5, 10), new DateTime(2026, 4, 1, 9, 0, 0));

            // Enrollment existed before the rewind, but its only period was created after the rewind instant, so the
            // scan sees no eligible period at all.
            var earlyClient = await SeedClientAsync(connection, businessId, "early@example.com", "Vieja", "Inscripcion");
            var earlyEnrollment = await SeedEnrollmentAsync(
                connection, businessId, earlyClient, EnrollmentStatus.Active, SeedCreatedAt);
            await SeedPeriodAsync(
                connection, businessId, earlyClient, earlyEnrollment,
                Expiry.AddMonths(-1), Expiry, new DateTime(2026, 4, 1, 9, 0, 0));
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(rewound, MembershipExpiryService.BatchSize));
        Assert.Equal(0, await new MembershipExpiryService(repository).RunAsync(rewound));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Rewind-creation assertions passed: an enrollment created after the instant and a period created after " +
            "it were both invisible.");
    }

    // ---- Dedup and the rewind of the earlier stage ----

    [Fact]
    public async Task An_already_written_stage_is_never_notified_again()
    {
        var target = await PrepareAsync("_Dedup");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            seed = await SeedActiveAsync(connection, "t25-dedup", Expiry);
            await InsertNotificationAsync(
                connection,
                seed,
                NotificationType.MembershipExpiring,
                MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring),
                ThreeDaysBefore);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(TwoDaysBefore, MembershipExpiryService.BatchSize));
        Assert.Equal(0, await new MembershipExpiryService(repository).RunAsync(TwoDaysBefore));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Dedup assertions passed: the already-written reminder was not repeated.");
    }

    [Fact]
    public async Task An_expired_notice_suppresses_the_earlier_expiring_stage_after_a_rewind()
    {
        var target = await PrepareAsync("_RewindSuppress");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            seed = await SeedActiveAsync(connection, "t25-rewind-suppress", Expiry);
            // The at-expiry notice was already written; the clock is then rewound into the reminder window.
            await InsertNotificationAsync(
                connection,
                seed,
                NotificationType.MembershipExpired,
                MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expired),
                ExpiryDay);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        // The scan refuses the obsolete reminder because the later stage of the same period exists.
        Assert.Empty(await repository.ListCandidatesAsync(TwoDaysBefore, MembershipExpiryService.BatchSize));
        Assert.Equal(0, await new MembershipExpiryService(repository).RunAsync(TwoDaysBefore));

        // The guarded insert refuses it too, even when handed directly: a rewound clock can never mail the reminder
        // after the at-expiry notice.
        var reminder = MembershipExpiryService.DedupKey(
            seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring);
        var notice = new MembershipExpiryNotice(
            seed.EnrollmentId,
            seed.ClientId,
            seed.BusinessId,
            seed.PeriodId,
            Expiry,
            MembershipExpiryStage.Expiring,
            new Notification
            {
                BusinessId = seed.BusinessId,
                Type = NotificationType.MembershipExpiring,
                Recipient = "alumno@example.com",
                Subject = MembershipExpiryService.ExpiringSubject,
                BodyHtml = "<p>recordatorio tardío</p>",
                DedupKey = reminder,
                CreatedAt = TwoDaysBefore
            });
        Assert.False(await repository.TryEnqueueNoticeAsync(notice));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Rewind suppression passed: neither the scan nor the guarded insert issued the earlier reminder after " +
            "the at-expiry notice.");
    }

    // ---- The guarded insert rechecks the live rows ----

    [Fact]
    public async Task A_termination_between_the_scan_and_the_insert_is_a_no_op()
    {
        var target = await PrepareAsync("_Terminated");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
            seed = await SeedActiveAsync(connection, "t25-terminated", Expiry);

        await using var session = new DbSession(target.ConnectionString);
        var scanned = 0;
        var service = new MembershipExpiryService(
            new HookedMembershipExpiryRepository(
                new SqlMembershipExpiryRepository(session),
                async candidates =>
                {
                    scanned = candidates.Count;
                    await ExecuteAsync(
                        target.ConnectionString,
                        "UPDATE Enrollments SET Status = @status WHERE Id = @enrollment",
                        ("@status", EnrollmentStatus.Terminated), ("@enrollment", seed.EnrollmentId));
                }));

        Assert.Equal(0, await service.RunAsync(TwoDaysBefore));
        Assert.Equal(1, scanned);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Termination-between assertions passed: the guarded insert wrote no row.");
    }

    [Fact]
    public async Task A_client_change_between_the_scan_and_the_insert_is_a_no_op()
    {
        var target = await PrepareAsync("_ClientChange");
        if (target is null)
            return;

        MembershipSeed seed;
        int replacementClient;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            seed = await SeedActiveAsync(connection, "t25-client-change", Expiry);
            replacementClient = await SeedClientAsync(
                connection, seed.BusinessId, "otro@example.com", "Otra", "Ficha");
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = new MembershipExpiryService(
            new HookedMembershipExpiryRepository(
                new SqlMembershipExpiryRepository(session),
                _ => ExecuteAsync(
                    target.ConnectionString,
                    "UPDATE Enrollments SET ClientId = @client WHERE Id = @enrollment",
                    ("@client", replacementClient), ("@enrollment", seed.EnrollmentId))));

        Assert.Equal(0, await service.RunAsync(TwoDaysBefore));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Client-change assertions passed: the guarded insert refused the moved enrollment.");
    }

    [Fact]
    public async Task A_recipient_change_between_the_scan_and_the_insert_is_a_no_op()
    {
        var target = await PrepareAsync("_EmailChange");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
            seed = await SeedActiveAsync(connection, "t25-email-change", Expiry, "ficha@example.com");

        await using var session = new DbSession(target.ConnectionString);
        var service = new MembershipExpiryService(
            new HookedMembershipExpiryRepository(
                new SqlMembershipExpiryRepository(session),
                _ => ExecuteAsync(
                    target.ConnectionString,
                    "UPDATE Clients SET Email = @email WHERE Id = @client",
                    ("@email", "nuevo@example.com"), ("@client", seed.ClientId))));

        Assert.Equal(0, await service.RunAsync(TwoDaysBefore));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        // A fresh scan notices the new recipient instead, proving the change only invalidated the stale notice.
        Assert.Equal(1, await new MembershipExpiryService(new SqlMembershipExpiryRepository(session)).RunAsync(TwoDaysBefore));
        var notice = await ReadNotificationAsync(
            target.ConnectionString,
            MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring));
        Assert.Equal("nuevo@example.com", notice.Recipient);

        output.WriteLine(
            "Email-change assertions passed: the pre-edit notice was refused and a fresh scan used the new email.");
    }

    [Fact]
    public async Task A_renewal_between_the_scan_and_the_insert_is_a_no_op()
    {
        var target = await PrepareAsync("_RenewalRace");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
            seed = await SeedActiveAsync(connection, "t25-renewal-race", Expiry);

        await using var session = new DbSession(target.ConnectionString);
        var service = new MembershipExpiryService(
            new HookedMembershipExpiryRepository(
                new SqlMembershipExpiryRepository(session),
                async _ =>
                {
                    await using var connection = await OpenAsync(target.ConnectionString);
                    await SeedPeriodAsync(
                        connection, seed.BusinessId, seed.ClientId, seed.EnrollmentId,
                        Expiry, Expiry.AddMonths(1), SeedCreatedAt);
                }));

        Assert.Equal(0, await service.RunAsync(TwoDaysBefore));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Renewal-between assertions passed: the superseded period was not notified.");
    }

    [Fact]
    public async Task The_guarded_insert_refuses_a_composed_instant_outside_the_stage_window()
    {
        var target = await PrepareAsync("_OffWindow");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
            seed = await SeedActiveAsync(connection, "t25-off-window", Expiry);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        // A reminder composed four days before the expiry is outside [ExpiresOn - 3, ExpiresOn).
        Assert.False(await repository.TryEnqueueNoticeAsync(BuildReminder(seed, FourDaysBefore)));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Off-window assertions passed: an early composed instant was refused.");
    }

    [Fact]
    public async Task The_guarded_insert_refuses_a_non_canonical_key()
    {
        var target = await PrepareAsync("_CanonicalKey");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
            seed = await SeedActiveAsync(connection, "t25-canonical-key", Expiry);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        // Reminder type and window, but the stored key names the later expired stage: the canonical key is derived, so
        // the guarded insert must refuse the mismatched one.
        var notice = new MembershipExpiryNotice(
            seed.EnrollmentId,
            seed.ClientId,
            seed.BusinessId,
            seed.PeriodId,
            Expiry,
            MembershipExpiryStage.Expiring,
            new Notification
            {
                BusinessId = seed.BusinessId,
                Type = NotificationType.MembershipExpiring,
                Recipient = "alumno@example.com",
                Subject = MembershipExpiryService.ExpiringSubject,
                BodyHtml = "<p>recordatorio</p>",
                DedupKey = MembershipExpiryService.DedupKey(
                    seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expired),
                CreatedAt = TwoDaysBefore
            });
        Assert.False(await repository.TryEnqueueNoticeAsync(notice));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine("Canonical-key assertions passed: the mismatched key did not insert.");
    }

    // ---- Recipient shape and HTML safety ----

    [Fact]
    public async Task A_malformed_client_email_is_scanned_but_never_notified()
    {
        var target = await PrepareAsync("_Malformed");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
            await SeedActiveAsync(connection, "t25-malformed", Expiry, "no-es-un-email");

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlMembershipExpiryRepository(session);

        // SQL applies presence only: the malformed address is scanned, and the core's shape check rejects it.
        Assert.Single(await repository.ListCandidatesAsync(TwoDaysBefore, MembershipExpiryService.BatchSize));
        Assert.Equal(0, await new MembershipExpiryService(repository).RunAsync(TwoDaysBefore));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Malformed-email assertions passed: SQL returned the present address and the service refused to compose.");
    }

    [Fact]
    public async Task The_body_encodes_client_and_business_names()
    {
        var target = await PrepareAsync("_Html");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            seed = await SeedActiveAsync(
                connection, "t25-html", Expiry, "ficha@example.com", "<script>x</script>",
                "<b>Ana</b>", "& Co");
        }

        await using var session = new DbSession(target.ConnectionString);
        Assert.Equal(1, await new MembershipExpiryService(new SqlMembershipExpiryRepository(session)).RunAsync(ExpiryDay));

        var notice = await ReadNotificationAsync(
            target.ConnectionString,
            MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expired));
        Assert.Contains("&lt;b&gt;Ana&lt;/b&gt; &amp; Co", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Ana</b>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("http", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Null(notice.LastError);

        output.WriteLine("HTML assertions passed: the client and business names reached the body HTML-encoded.");
    }

    // ---- Bounded scan and the forced race ----

    [Fact]
    public async Task The_scan_is_bounded_to_the_batch_size_and_ordered_by_enrollment_id()
    {
        var target = await PrepareAsync("_Batch");
        if (target is null)
            return;

        var seeded = new List<int>();
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            for (var index = 0; index < MembershipExpiryService.BatchSize + 1; index++)
            {
                var seed = await SeedActiveAsync(
                    connection, $"t25-batch-{index}", Expiry, $"alumno{index}@example.com");
                seeded.Add(seed.EnrollmentId);
            }
        }

        await using var session = new DbSession(target.ConnectionString);
        var candidates = await new SqlMembershipExpiryRepository(session)
            .ListCandidatesAsync(TwoDaysBefore, MembershipExpiryService.BatchSize);

        Assert.Equal(MembershipExpiryService.BatchSize, candidates.Count);
        Assert.Equal(seeded.Take(MembershipExpiryService.BatchSize), candidates.Select(row => row.EnrollmentId));

        output.WriteLine("Batch assertions passed: 21 eligible enrollments produced exactly the 20 lowest ids in order.");
    }

    [Fact]
    public async Task Two_concurrent_sessions_enqueue_exactly_one_notice()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        MembershipSeed seed;
        await using (var connection = await OpenAsync(target.ConnectionString))
            seed = await SeedActiveAsync(connection, "t25-race", Expiry);

        // The barrier forces both sessions to finish their scan before either inserts, so the unique dedup key — not a
        // lucky ordering — is what decides the winner.
        using var startGate = new Barrier(participantCount: 2);

        Task<int> RunAsync() => Task.Run(async () =>
        {
            await using var session = new DbSession(target.ConnectionString);
            var service = new MembershipExpiryService(
                new HookedMembershipExpiryRepository(
                    new SqlMembershipExpiryRepository(session),
                    _ =>
                    {
                        startGate.SignalAndWait();
                        return Task.CompletedTask;
                    }));
            return await service.RunAsync(TwoDaysBefore);
        });

        var results = await Task.WhenAll(RunAsync(), RunAsync());

        Assert.Equal(1, results.Sum());
        Assert.Equal(
            1,
            await CountNotificationsAsync(
                target.ConnectionString,
                MembershipExpiryService.DedupKey(seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring)));

        output.WriteLine(
            "Race assertions passed: two independent sessions with forced overlap enqueued exactly one notice; this " +
            "is one forced overlap, not a universal no-deadlock claim.");
    }

    // --- Test wiring -------------------------------------------------------------------------------------------

    private static MembershipExpiryNotice BuildReminder(MembershipSeed seed, DateTime createdAt) =>
        new(
            seed.EnrollmentId,
            seed.ClientId,
            seed.BusinessId,
            seed.PeriodId,
            Expiry,
            MembershipExpiryStage.Expiring,
            new Notification
            {
                BusinessId = seed.BusinessId,
                Type = NotificationType.MembershipExpiring,
                Recipient = "alumno@example.com",
                Subject = MembershipExpiryService.ExpiringSubject,
                BodyHtml = "<p>recordatorio</p>",
                DedupKey = MembershipExpiryService.DedupKey(
                    seed.EnrollmentId, seed.PeriodId, MembershipExpiryStage.Expiring),
                CreatedAt = createdAt
            });

    /// <summary>Delegates to the real port and runs <paramref name="afterList"/> between the scan and the enqueue.</summary>
    private sealed class HookedMembershipExpiryRepository(
        IMembershipExpiryRepository inner,
        Func<IReadOnlyList<MembershipExpiryCandidate>, Task> afterList) : IMembershipExpiryRepository
    {
        public async Task<IReadOnlyList<MembershipExpiryCandidate>> ListCandidatesAsync(
            DateTime now, int batchSize, CancellationToken ct = default)
        {
            var candidates = await inner.ListCandidatesAsync(now, batchSize, ct);
            await afterList(candidates);
            return candidates;
        }

        public Task<bool> TryEnqueueNoticeAsync(MembershipExpiryNotice notice, CancellationToken ct = default) =>
            inner.TryEnqueueNoticeAsync(notice, ct);
    }

    // --- Seeding -----------------------------------------------------------------------------------------------

    private static Task<int> SeedBusinessAsync(
        SqlConnection connection, string slug, string name = "Synthetic T25", DateTime? createdAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @createdAt)
            """,
            ("@name", name), ("@slug", slug), ("@phone", "0000000000"), ("@createdAt", createdAt ?? SeedCreatedAt));

    private static Task<int> SeedClientAsync(
        SqlConnection connection, int businessId, string email, string firstName = "Ana",
        string lastName = "Perez", DateTime? createdAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @first, @last, @email, @createdAt)
            """,
            ("@business", businessId), ("@first", firstName), ("@last", lastName), ("@email", email),
            ("@createdAt", createdAt ?? SeedCreatedAt));

    private static Task<int> SeedAccountAsync(
        SqlConnection connection, int businessId, int clientId, string email, DateTime? createdAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO ClientAccounts (BusinessId, ClientId, Email, PasswordHash, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @client, @email, @hash, @createdAt)
            """,
            ("@business", businessId), ("@client", clientId), ("@email", email), ("@hash", "synthetic-hash"),
            ("@createdAt", createdAt ?? SeedCreatedAt));

    private static Task<int> SeedEnrollmentAsync(
        SqlConnection connection, int businessId, int clientId,
        EnrollmentStatus status = EnrollmentStatus.Active, DateTime? createdAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @client, 2, @status, 5000, @createdAt)
            """,
            ("@business", businessId), ("@client", clientId), ("@status", status),
            ("@createdAt", createdAt ?? SeedCreatedAt));

    private static async Task<int> SeedPeriodAsync(
        SqlConnection connection, int businessId, int clientId, int enrollmentId,
        DateOnly coverageStart, DateOnly expiresOn, DateTime? createdAt = null, bool annulled = false)
    {
        var at = createdAt ?? SeedCreatedAt;
        var paymentId = await InsertAsync(connection,
            """
            INSERT INTO Payments (BusinessId, ClientId, Concept, EnrollmentId, Amount, PaidAt, RecordedAt)
            OUTPUT INSERTED.Id VALUES (@business, @client, @membership, @enrollment, 5000, @paidAt, @recordedAt)
            """,
            ("@business", businessId), ("@client", clientId), ("@membership", PaymentConcept.Membership),
            ("@enrollment", enrollmentId), ("@paidAt", at), ("@recordedAt", at));
        if (annulled)
        {
            await InsertAsync(connection,
                """
                INSERT INTO PaymentAnnulments (PaymentId, Reason, AnnulledAt, AnnulledBy)
                OUTPUT INSERTED.Id VALUES (@payment, N'corrección financiera', @annulledAt, N'Dueña')
                """,
                ("@payment", paymentId), ("@annulledAt", at));
        }

        return await InsertAsync(connection,
            """
            INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@enrollment, @coverageStart, @expiresOn, @payment, @createdAt)
            """,
            ("@enrollment", enrollmentId), ("@coverageStart", coverageStart), ("@expiresOn", expiresOn),
            ("@payment", paymentId), ("@createdAt", at));
    }

    private static async Task<MembershipSeed> SeedActiveAsync(
        SqlConnection connection, string slug, DateOnly expiresOn, string clientEmail = "alumno@example.com",
        string businessName = "Synthetic T25", string clientFirstName = "Ana",
        string clientLastName = "Perez", string? accountEmail = null)
    {
        var businessId = await SeedBusinessAsync(connection, slug, businessName);
        var clientId = await SeedClientAsync(connection, businessId, clientEmail, clientFirstName, clientLastName);
        if (accountEmail is not null)
            await SeedAccountAsync(connection, businessId, clientId, accountEmail);
        var enrollmentId = await SeedEnrollmentAsync(connection, businessId, clientId, EnrollmentStatus.Active);
        var periodId = await SeedPeriodAsync(
            connection, businessId, clientId, enrollmentId, expiresOn.AddMonths(-1), expiresOn);
        return new MembershipSeed(businessId, clientId, enrollmentId, periodId);
    }

    // --- Notification helpers ----------------------------------------------------------------------------------

    private static Task<int> InsertNotificationAsync(
        SqlConnection connection, MembershipSeed seed, NotificationType type, string dedupKey, DateTime createdAt) =>
        InsertAsync(connection,
            """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @type, 'seed@example.invalid', 'seed', '<p>seed</p>', @dedupKey, @status, 0, @createdAt)
            """,
            ("@business", seed.BusinessId), ("@type", type), ("@dedupKey", dedupKey),
            ("@status", NotificationStatus.Pending), ("@createdAt", createdAt));

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

    private static async Task ExecuteAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.Add(Parameter(name, value));
        await command.ExecuteNonQueryAsync();
    }

    // --- Opt-in target -----------------------------------------------------------------------------------------

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
    /// one <c>WORKUP_T25_MEMBERSHIP_SQL_CONNECTION</c> value serves every fact. Only the validated LocalDB instance and
    /// the derived database survive into the connection string.
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
            ApplicationName = "WorkUp T25 membership expiry SQL integration test",
            TrustServerCertificate = true,
            MultipleActiveResultSets = false
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    // --- Plain SQL helpers -------------------------------------------------------------------------------------

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
        DateOnly date => new SqlParameter(name, SqlDbType.Date) { Value = date.ToDateTime(TimeOnly.MinValue) },
        Enum enumValue => new SqlParameter(name, Convert.ToByte(enumValue, CultureInfo.InvariantCulture)),
        _ => new SqlParameter(name, value)
    };

    private sealed record MembershipSeed(int BusinessId, int ClientId, int EnrollmentId, int PeriodId);

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record StoredNotification(
        int BusinessId, NotificationType Type, string Recipient, string Subject, string BodyHtml, string DedupKey,
        NotificationStatus Status, int Attempts, string? LastError, DateTime CreatedAt);
}
