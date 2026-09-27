using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Data;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Enums;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlReminderRepository"/> (T24, RF-17) driven through the
/// real <see cref="ReminderService"/>. It requires a fresh, dedicated database supplied through
/// <c>WORKUP_T24_SQL_CONNECTION</c>; without it every SQL test returns N/A and no connection is opened. Each test
/// derives its own database name from the supplied one, so the whole class runs in a single command, the preflight
/// asserts <c>DB_ID</c> is absent before migrating, and nothing is torn down: the targets are retained on purpose and
/// never reused.
/// <para>
/// The in-memory fake and the service tests cannot prove what SQL Server does with the scan joins, the unique dedup
/// key or a concurrent insert. This fixture is the runtime proof of the port: a global, bounded, tenant-safe scan that
/// honours the business' own anticipation and the strict complement of the immediate producers, and one guarded
/// <c>INSERT ... SELECT</c> that rechecks the live rows — status, kind/shape, the lead window at the notice instant,
/// the immediate-reminder key, and the exact single live link the body was built from — so a race between the scan and
/// the insert is a no-op instead of a duplicate or a stale URL.
/// </para>
/// </summary>
public sealed class ReminderSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T24_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT24Sql";

    private static readonly Regex SafeDatabaseName =
        new(@"\AWorkUpDemoT24Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>The single clock instant every scenario starts from.</summary>
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    private const int DefaultLeadHours = 24;

    // ---- The user decision: pending asks, confirmed informs, drop-in only informs ----

    [Fact]
    public async Task Pending_individual_with_one_live_link_enqueues_the_ask_and_a_second_run_is_idempotent()
    {
        var target = await PrepareAsync("_Pending");
        if (target is null)
            return;

        int bookingId;
        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-pending", DefaultLeadHours, "t24.pending@example.invalid");
            businessId = business.BusinessId;
            bookingId = await SeedIndividualBookingAsync(connection, business, Now.AddHours(20), Now.AddHours(-10));
            await InsertLinkAsync(connection, bookingId, "token-pending", Now.AddHours(48));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        Assert.Equal(1, await service.RunAsync(Now));

        var notice = await ReadNotificationAsync(target.ConnectionString, $"reminder:booking:{bookingId}");
        Assert.Equal(businessId, notice.BusinessId);
        Assert.Equal(NotificationType.AppointmentReminder, notice.Type);
        Assert.Equal("t24.pending@example.invalid", notice.Recipient);
        Assert.Equal(ReminderService.PendingSubject, notice.Subject);
        Assert.Contains(ReminderService.ConfirmationLead, notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("https://workup.test/t/token-pending", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Equal(NotificationStatus.Pending, notice.Status);
        Assert.Equal(0, notice.Attempts);
        Assert.Null(notice.LastError);
        Assert.Equal(Now, notice.CreatedAt);

        // The scheduled key already owns the booking, so a retried tick is a bounded no-op rather than a second email.
        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{bookingId}"));

        output.WriteLine(
            "Pending assertions passed: one live link produced a single Pending AppointmentReminder addressed to the " +
            "ficha with the private URL in the body, and the second tick enqueued nothing.");
    }

    [Fact]
    public async Task Confirmed_individual_is_informational_and_a_drop_in_carries_no_link()
    {
        var target = await PrepareAsync("_ConfirmedDropIn");
        if (target is null)
            return;

        int confirmedBooking;
        int dropInBooking;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-confirmed", DefaultLeadHours, "t24.confirmed@example.invalid");

            confirmedBooking = await SeedIndividualBookingAsync(
                connection, business, Now.AddHours(20), Now.AddHours(-10), BookingStatus.Confirmed);
            await InsertLinkAsync(connection, confirmedBooking, "token-confirmed", Now.AddHours(48));

            dropInBooking = await InsertBookingAsync(
                connection, business.BusinessId, business.ClassSessionId, business.ClientId,
                BookingStatus.Pending, Now.AddHours(-10), BookingModality.DropIn);
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        Assert.Equal(2, await service.RunAsync(Now));

        var confirmedNotice = await ReadNotificationAsync(target.ConnectionString, $"reminder:booking:{confirmedBooking}");
        Assert.Equal(ReminderService.ConfirmedSubject, confirmedNotice.Subject);
        Assert.DoesNotContain(ReminderService.ConfirmationLead, confirmedNotice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("https://workup.test/t/token-confirmed", confirmedNotice.BodyHtml, StringComparison.Ordinal);

        var dropInNotice = await ReadNotificationAsync(target.ConnectionString, $"reminder:booking:{dropInBooking}");
        Assert.Equal(NotificationType.DropInReminder, dropInNotice.Type);
        Assert.Equal(ReminderService.DropInSubject, dropInNotice.Subject);
        Assert.DoesNotContain(ReminderService.ConfirmationLead, dropInNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", dropInNotice.BodyHtml, StringComparison.Ordinal);

        output.WriteLine(
            "Confirmed/drop-in assertions passed: the confirmed turn was informational with its link and the drop-in " +
            "was reminded without a link or an ask.");
    }

    // ---- The configured anticipation is the business' own, and the boundary is strict ----

    [Fact]
    public async Task Configured_lead_hours_of_12_and_48_are_honoured_and_the_boundary_is_strict()
    {
        var target = await PrepareAsync("_Lead");
        if (target is null)
            return;

        int lead12;
        int lead48;
        int boundary;
        int beforeLead;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var shortLead = await SeedBusinessAsync(connection, "synthetic-t24-lead12", 12, "t24.lead12@example.invalid");
            lead12 = await SeedIndividualBookingAsync(connection, shortLead, Now.AddHours(10), Now.AddHours(-5));
            await InsertLinkAsync(connection, lead12, "token-lead12", Now.AddHours(48));

            var longLead = await SeedBusinessAsync(connection, "synthetic-t24-lead48", 48, "t24.lead48@example.invalid");
            lead48 = await SeedIndividualBookingAsync(connection, longLead, Now.AddHours(40), Now.AddHours(-20));
            await InsertLinkAsync(connection, lead48, "token-lead48", Now.AddHours(96));

            // CreatedAt is exactly StartAt - 24h: the immediate producer already covered it, so it must never appear.
            var boundaryLead = await SeedBusinessAsync(connection, "synthetic-t24-boundary", DefaultLeadHours, "t24.boundary@example.invalid");
            boundary = await SeedIndividualBookingAsync(connection, boundaryLead, Now.AddHours(20), Now.AddHours(-4));
            await InsertLinkAsync(connection, boundary, "token-boundary", Now.AddHours(48));

            // CreatedAt is outside the window but the lead instant has not arrived yet.
            var earlyLead = await SeedBusinessAsync(connection, "synthetic-t24-early", DefaultLeadHours, "t24.early@example.invalid");
            beforeLead = await SeedIndividualBookingAsync(connection, earlyLead, Now.AddHours(30), Now.AddHours(-10));
            await InsertLinkAsync(connection, beforeLead, "token-early", Now.AddHours(48));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        Assert.Equal(2, await service.RunAsync(Now));

        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{lead12}"));
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{lead48}"));
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{boundary}"));
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{beforeLead}"));

        output.WriteLine(
            "Lead assertions passed: the 12 h and 48 h businesses were reminded, the boundary booking was excluded by " +
            "the strict CreatedAt < threshold and the booking whose lead instant had not arrived was excluded.");
    }

    // ---- Exclusions ----

    [Fact]
    public async Task Fixed_cancelled_attended_absent_suspended_and_past_bookings_are_never_scanned()
    {
        var target = await PrepareAsync("_Excluded");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-excluded", DefaultLeadHours, "t24.excluded@example.invalid");

            // A fixed enrollment booking: the modality carries an enrollment, so it is not a reminder candidate.
            var enrollmentId = await InsertEnrollmentAsync(connection, business.BusinessId, business.ClientId);
            var fixedSession = await InsertClassSessionAsync(connection, business, Now.AddHours(20));
            await InsertBookingAsync(
                connection, business.BusinessId, fixedSession, business.ClientId,
                BookingStatus.Pending, Now.AddHours(-10), BookingModality.Fixed, enrollmentId);

            var cancelled = await SeedIndividualBookingAsync(
                connection, business, Now.AddHours(20), Now.AddHours(-10), BookingStatus.Cancelled);
            await InsertLinkAsync(connection, cancelled, "token-cancelled", Now.AddHours(48));

            var attended = await SeedIndividualBookingAsync(
                connection, business, Now.AddHours(20), Now.AddHours(-10), BookingStatus.Attended);
            await InsertLinkAsync(connection, attended, "token-attended", Now.AddHours(48));

            var absent = await SeedIndividualBookingAsync(
                connection, business, Now.AddHours(20), Now.AddHours(-10), BookingStatus.Absent);
            await InsertLinkAsync(connection, absent, "token-absent", Now.AddHours(48));

            // Only class sessions can be suspended, so the suspended case is a drop-in booking on a suspended session.
            var suspendedSession = await InsertClassSessionAsync(
                connection, business, Now.AddHours(20), AppointmentStatus.Suspended);
            await InsertBookingAsync(
                connection, business.BusinessId, suspendedSession, business.ClientId,
                BookingStatus.Pending, Now.AddHours(-10), BookingModality.DropIn);

            var past = await SeedIndividualBookingAsync(connection, business, Now.AddHours(-1), Now.AddHours(-30));
            await InsertLinkAsync(connection, past, "token-past", Now.AddHours(48));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Exclusion assertions passed: fixed, cancelled, attended, absent, suspended and already-started bookings " +
            "were all rejected by the scan and no notice was written.");
    }

    // ---- The immediate dedup key: `AppointmentReminder` suppresses, `BookingCreated` does not ----

    [Fact]
    public async Task An_original_booking_created_notice_allows_the_reminder_but_an_immediate_reminder_excludes_it()
    {
        var target = await PrepareAsync("_Dedup");
        if (target is null)
            return;

        int allowed;
        int excluded;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-dedup", DefaultLeadHours, "t24.dedup@example.invalid");

            allowed = await SeedIndividualBookingAsync(
                connection, business, Now.AddHours(20), Now.AddHours(-10), BookingStatus.Confirmed);
            await InsertLinkAsync(connection, allowed, "token-allowed", Now.AddHours(48));
            await InsertNotificationAsync(
                connection, business.BusinessId, NotificationType.BookingCreated,
                $"booking:{allowed}", Now.AddHours(-10));

            excluded = await SeedIndividualBookingAsync(connection, business, Now.AddHours(20), Now.AddHours(-10));
            await InsertLinkAsync(connection, excluded, "token-excluded", Now.AddHours(48));
            await InsertNotificationAsync(
                connection, business.BusinessId, NotificationType.AppointmentReminder,
                $"booking:{excluded}", Now.AddHours(-10));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        Assert.Equal(1, await service.RunAsync(Now));

        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{allowed}"));
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{excluded}"));

        output.WriteLine(
            "Dedup assertions passed: the original BookingCreated notice left the scheduled reminder eligible while " +
            "the immediate AppointmentReminder already owning `booking:{id}` excluded it.");
    }

    // ---- Guarded enqueue: the link the body was built from must still be the single live one ----

    [Fact]
    public async Task A_link_revoked_between_the_scan_and_the_insert_is_not_linked()
    {
        var target = await PrepareAsync("_Revoked");
        if (target is null)
            return;

        int bookingId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-revoked", DefaultLeadHours, "t24.revoked@example.invalid");
            bookingId = await SeedIndividualBookingAsync(connection, business, Now.AddHours(20), Now.AddHours(-10));
            await InsertLinkAsync(connection, bookingId, "token-revoked", Now.AddHours(48));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = new ReminderService(
            new HookedReminderRepository(
                new SqlReminderRepository(session),
                async candidates =>
                {
                    foreach (var candidate in candidates)
                        await RevokeLinksAsync(target.ConnectionString, candidate.BookingId, Now);
                }),
            new PassthroughLinkTokenProtector(),
            new FixedAppointmentUrlBuilder());

        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{bookingId}"));

        output.WriteLine(
            "Revoke assertion passed: the link revoked after the scan failed the single-live-link guard, so no stale " +
            "URL was enqueued.");
    }

    [Fact]
    public async Task A_link_replaced_between_the_scan_and_the_insert_is_not_linked()
    {
        var target = await PrepareAsync("_Replaced");
        if (target is null)
            return;

        int bookingId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-replaced", DefaultLeadHours, "t24.replaced@example.invalid");
            bookingId = await SeedIndividualBookingAsync(connection, business, Now.AddHours(20), Now.AddHours(-10));
            await InsertLinkAsync(connection, bookingId, "token-original", Now.AddHours(48));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = new ReminderService(
            new HookedReminderRepository(
                new SqlReminderRepository(session),
                async candidates =>
                {
                    foreach (var candidate in candidates)
                    {
                        await RevokeLinksAsync(target.ConnectionString, candidate.BookingId, Now);
                        await InsertLinkByConnectionStringAsync(target.ConnectionString, candidate.BookingId, "token-replacement", Now.AddHours(48));
                    }
                }),
            new PassthroughLinkTokenProtector(),
            new FixedAppointmentUrlBuilder());

        // The replacement leaves exactly one live link too, so only the exact identity guard can reject it.
        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{bookingId}"));

        output.WriteLine(
            "Replace assertion passed: exactly one live link remained after the replacement, but it was a different " +
            "identity, so the guarded insert refused it.");
    }

    [Fact]
    public async Task A_second_live_link_appearing_between_the_scan_and_the_insert_is_rejected()
    {
        var target = await PrepareAsync("_SecondLink");
        if (target is null)
            return;

        int bookingId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-secondlink", DefaultLeadHours, "t24.secondlink@example.invalid");
            bookingId = await SeedIndividualBookingAsync(connection, business, Now.AddHours(20), Now.AddHours(-10));
            await InsertLinkAsync(connection, bookingId, "token-first", Now.AddHours(48));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = new ReminderService(
            new HookedReminderRepository(
                new SqlReminderRepository(session),
                async candidates =>
                {
                    foreach (var candidate in candidates)
                        await InsertLinkByConnectionStringAsync(target.ConnectionString, candidate.BookingId, "token-second", Now.AddHours(48));
                }),
            new PassthroughLinkTokenProtector(),
            new FixedAppointmentUrlBuilder());

        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{bookingId}"));

        output.WriteLine(
            "Second-link assertion passed: two live links at insert time failed the exactly-one-live-link guard.");
    }

    [Fact]
    public async Task Two_live_links_at_scan_time_skip_the_booking_while_a_single_link_is_reminded()
    {
        var target = await PrepareAsync("_TwoLinks");
        if (target is null)
            return;

        int doubled;
        int single;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-twolinks", DefaultLeadHours, "t24.twolinks@example.invalid");

            doubled = await SeedIndividualBookingAsync(connection, business, Now.AddHours(20), Now.AddHours(-10));
            await InsertLinkAsync(connection, doubled, "token-double-1", Now.AddHours(48));
            await InsertLinkAsync(connection, doubled, "token-double-2", Now.AddHours(48));

            single = await SeedIndividualBookingAsync(connection, business, Now.AddHours(20), Now.AddHours(-10));
            await InsertLinkAsync(connection, single, "token-single", Now.AddHours(48));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        Assert.Equal(1, await service.RunAsync(Now));
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{doubled}"));
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{single}"));

        output.WriteLine(
            "Exactly-one assertion passed: the booking with two live links was skipped while the booking with one was " +
            "reminded.");
    }

    // ---- Tenant safety ----

    [Fact]
    public async Task Cross_tenant_joins_never_mix_businesses_into_a_candidate()
    {
        var target = await PrepareAsync("_Tenant");
        if (target is null)
            return;

        int validBooking;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var own = await SeedBusinessAsync(connection, "synthetic-t24-tenant-own", DefaultLeadHours, "t24.tenant.own@example.invalid");
            var foreign = await SeedBusinessAsync(connection, "synthetic-t24-tenant-foreign", DefaultLeadHours, "t24.tenant.foreign@example.invalid");

            var validAppointment = await InsertIndividualAppointmentAsync(connection, own, Now.AddHours(20));
            validBooking = await InsertBookingAsync(
                connection, own.BusinessId, validAppointment, own.ClientId, BookingStatus.Pending, Now.AddHours(-10));
            await InsertLinkAsync(connection, validBooking, "token-valid", Now.AddHours(48));

            // A booking of the own tenant pointed at the foreign tenant's appointment: the tenant join must drop it.
            await InsertBookingAsync(
                connection, own.BusinessId, foreign.IndividualAppointmentId, own.ClientId,
                BookingStatus.Pending, Now.AddHours(-10));

            // A booking of the own tenant whose client belongs to the foreign tenant: the recipient join must drop it.
            await InsertBookingAsync(
                connection, own.BusinessId, validAppointment, foreign.ClientId,
                BookingStatus.Pending, Now.AddHours(-10));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        Assert.Equal(1, await service.RunAsync(Now));
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{validBooking}"));
        var recipients = await ReadRecipientsAsync(target.ConnectionString);
        Assert.Equal("t24.tenant.own@example.invalid", Assert.Single(recipients));

        output.WriteLine(
            "Tenant assertions passed: the inconsistent appointment and the foreign ficha were invisible and only the " +
            "own-tenant recipient was ever used.");
    }

    // ---- Two independent sessions racing the same booking produce exactly one notice ----

    [Fact]
    public async Task Two_concurrent_sessions_produce_exactly_one_notification()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        int bookingId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var business = await SeedBusinessAsync(connection, "synthetic-t24-race", DefaultLeadHours, "t24.race@example.invalid");
            bookingId = await SeedIndividualBookingAsync(connection, business, Now.AddHours(20), Now.AddHours(-10));
            await InsertLinkAsync(connection, bookingId, "token-race", Now.AddHours(48));
        }

        // The barrier forces both sessions to finish their scan before either inserts, so the unique dedup key — not a
        // lucky ordering — is what decides the winner.
        using var startGate = new Barrier(participantCount: 2);

        Task<int> RunAsync() => Task.Run(async () =>
        {
            await using var session = new DbSession(target.ConnectionString);
            var service = new ReminderService(
                new HookedReminderRepository(
                    new SqlReminderRepository(session),
                    _ =>
                    {
                        startGate.SignalAndWait();
                        return Task.CompletedTask;
                    }),
                new PassthroughLinkTokenProtector(),
                new FixedAppointmentUrlBuilder());
            return await service.RunAsync(Now);
        });

        var results = await Task.WhenAll(RunAsync(), RunAsync());

        Assert.Equal(1, results.Sum());
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, $"reminder:booking:{bookingId}"));

        output.WriteLine(
            "Race assertions passed: two independent sessions with forced overlap enqueued exactly one notice for the " +
            "booking; this is one forced overlap, not a universal no-deadlock claim.");
    }

    // --- Service and repository wiring ------------------------------------------------------------------------

    private static ReminderService NewService(DbSession session) =>
        new(new SqlReminderRepository(session), new PassthroughLinkTokenProtector(), new FixedAppointmentUrlBuilder());

    /// <summary>Delegates to the real port and runs <paramref name="afterList"/> between the scan and the enqueue.</summary>
    private sealed class HookedReminderRepository(
        IReminderRepository inner,
        Func<IReadOnlyList<ReminderCandidate>, Task> afterList) : IReminderRepository
    {
        public async Task<IReadOnlyList<ReminderCandidate>> ListCandidatesAsync(
            DateTime now, int batchSize, CancellationToken ct = default)
        {
            var candidates = await inner.ListCandidatesAsync(now, batchSize, ct);
            await afterList(candidates);
            return candidates;
        }

        public Task<bool> TryEnqueueReminderAsync(ReminderNotice notice, CancellationToken ct = default) =>
            inner.TryEnqueueReminderAsync(notice, ct);
    }

    // --- Seeding ------------------------------------------------------------------------------------------------

    /// <summary>Seeds one tenant with a service, a worker, a ficha and the two session shapes used by the tests.</summary>
    private static async Task<BusinessSeed> SeedBusinessAsync(
        SqlConnection connection, string slug, int leadHours, string clientEmail)
    {
        var businessId = await InsertAsync(connection,
            """
            INSERT INTO Businesses (Name, Slug, ContactPhone, ReminderLeadHours, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @leadHours, @now)
            """,
            ("@name", "Synthetic T24 " + slug), ("@slug", slug), ("@leadHours", leadHours), ("@now", Now));

        var serviceId = await InsertAsync(connection,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Service', 1000.00, 30, 8)",
            ("@business", businessId));

        // The cuil is unique per business and derived from the identity, so seeding is deterministic and collision-free.
        var workerId = await InsertAsync(connection,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Worker', '0000000000', @cuil)",
            ("@business", businessId), ("@cuil", "20" + businessId.ToString("D9", CultureInfo.InvariantCulture)));

        var clientId = await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Client', @email, @now)",
            ("@business", businessId), ("@email", clientEmail), ("@now", Now));

        var individualAppointmentId = await InsertAppointmentAsync(
            connection, businessId, AppointmentKind.Individual, serviceId, workerId,
            Now.AddHours(20), AppointmentStatus.Scheduled, capacity: 1);
        var classSessionId = await InsertAppointmentAsync(
            connection, businessId, AppointmentKind.ClassSession, serviceId, workerId,
            Now.AddHours(20), AppointmentStatus.Scheduled, capacity: 8);

        return new BusinessSeed(businessId, serviceId, workerId, clientId, individualAppointmentId, classSessionId);
    }

    /// <summary>Seeds one individual appointment plus its booking and returns the booking id.</summary>
    private static async Task<int> SeedIndividualBookingAsync(
        SqlConnection connection, BusinessSeed business, DateTime startsAt, DateTime createdAt,
        BookingStatus status = BookingStatus.Pending)
    {
        var appointmentId = await InsertIndividualAppointmentAsync(connection, business, startsAt);
        return await InsertBookingAsync(
            connection,
            business.BusinessId,
            appointmentId,
            business.ClientId,
            status,
            createdAt,
            cancelledAt: status == BookingStatus.Cancelled ? createdAt : null);
    }

    private static Task<int> InsertIndividualAppointmentAsync(
        SqlConnection connection, BusinessSeed business, DateTime startsAt) =>
        InsertAppointmentAsync(
            connection, business.BusinessId, AppointmentKind.Individual, business.ServiceId, business.WorkerId,
            startsAt, AppointmentStatus.Scheduled, capacity: 1);

    private static Task<int> InsertClassSessionAsync(
        SqlConnection connection, BusinessSeed business, DateTime startsAt,
        AppointmentStatus status = AppointmentStatus.Scheduled) =>
        InsertAppointmentAsync(
            connection, business.BusinessId, AppointmentKind.ClassSession, business.ServiceId, business.WorkerId,
            startsAt, status, capacity: 8);

    private static async Task<int> InsertAppointmentAsync(
        SqlConnection connection, int businessId, AppointmentKind kind, int serviceId, int workerId,
        DateTime startsAt, AppointmentStatus status, int capacity)
    {
        // An individual appointment carries its reserved origin; a class session has no origin (CK_Appointments_KindShape).
        object? origin = kind == AppointmentKind.Individual ? (byte)AppointmentOrigin.Reserved : DBNull.Value;
        return await InsertAsync(connection,
            """
            INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @startsAt, @endsAt, @capacity, @origin, @status, @now)
            """,
            ("@business", businessId), ("@kind", kind), ("@service", serviceId), ("@worker", workerId),
            ("@startsAt", startsAt), ("@endsAt", startsAt.AddHours(1)), ("@capacity", capacity),
            ("@origin", origin), ("@status", status), ("@now", Now));
    }

    private static Task<int> InsertBookingAsync(
        SqlConnection connection, int businessId, int appointmentId, int clientId, BookingStatus status,
        DateTime createdAt, BookingModality? modality = null, int? enrollmentId = null, DateTime? cancelledAt = null)
    {
        // CK_Bookings_Cancelled requires CancelledAt exactly when the status is Cancelled.
        return InsertAsync(connection,
            """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt)
            OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, @enrollment, @modality, @status, @createdAt, @cancelledAt)
            """,
            ("@business", businessId), ("@appointment", appointmentId), ("@client", clientId),
            ("@enrollment", enrollmentId), ("@modality", modality), ("@status", status),
            ("@createdAt", createdAt), ("@cancelledAt", cancelledAt));
    }

    private static Task<int> InsertEnrollmentAsync(SqlConnection connection, int businessId, int clientId) =>
        InsertAsync(connection,
            """
            INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @client, 2, @status, 4000.00, @now)
            """,
            ("@business", businessId), ("@client", clientId), ("@status", EnrollmentStatus.Active), ("@now", Now));

    private static Task<int> InsertLinkAsync(
        SqlConnection connection, int bookingId, string token, DateTime expiresAt, DateTime? revokedAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO AppointmentLinks (BookingId, TokenHash, ProtectedToken, ExpiresAt, RevokedAt, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@booking, @tokenHash, @protectedToken, @expiresAt, @revokedAt, @now)
            """,
            ("@booking", bookingId), ("@tokenHash", SecureToken.Hash(token)),
            ("@protectedToken", "protected:" + token), ("@expiresAt", expiresAt),
            ("@revokedAt", revokedAt), ("@now", Now));

    private static Task<int> InsertNotificationAsync(
        SqlConnection connection, int businessId, NotificationType type, string dedupKey, DateTime createdAt) =>
        InsertAsync(connection,
            """
            INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @type, 'seed@example.invalid', 'seed', '<p>seed</p>', @dedupKey, @status, 0, @createdAt)
            """,
            ("@business", businessId), ("@type", type), ("@dedupKey", dedupKey),
            ("@status", NotificationStatus.Pending), ("@createdAt", createdAt));

    // --- Direct mutation for the scan/insert race and direct reads ---------------------------------------------

    private static async Task RevokeLinksAsync(string connectionString, int bookingId, DateTime revokedAt)
    {
        await using var connection = await OpenAsync(connectionString);
        await InsertAsync(connection,
            "UPDATE AppointmentLinks SET RevokedAt = @revokedAt WHERE BookingId = @booking AND RevokedAt IS NULL",
            ("@revokedAt", revokedAt), ("@booking", bookingId));
    }

    /// <summary>Inserts a second or replacement link on a fresh connection, the owner "replace link" live mutation.</summary>
    private static async Task InsertLinkByConnectionStringAsync(
        string connectionString, int bookingId, string token, DateTime expiresAt)
    {
        await using var connection = await OpenAsync(connectionString);
        await InsertLinkAsync(connection, bookingId, token, expiresAt);
    }

    private static async Task<StoredNotification> ReadNotificationAsync(string connectionString, string dedupKey)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(
            """
            SELECT BusinessId, Type, Recipient, Subject, BodyHtml, Status, Attempts, LastError, CreatedAt
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
            (NotificationStatus)reader.GetByte(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetDateTime(8));
    }

    private static async Task<string[]> ReadRecipientsAsync(string connectionString)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand("SELECT Recipient FROM Notifications ORDER BY Id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var recipients = new List<string>();
        while (await reader.ReadAsync())
            recipients.Add(reader.GetString(0));
        return recipients.ToArray();
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
    /// Derives this test's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog,
    /// so one <c>WORKUP_T24_SQL_CONNECTION</c> value serves every fact. Only the validated LocalDB instance and the
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
            ApplicationName = "WorkUp T24 reminder SQL integration test",
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
        byte[] bytes => new SqlParameter(name, SqlDbType.Binary, bytes.Length) { Value = bytes },
        _ => new SqlParameter(name, value)
    };

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record BusinessSeed(
        int BusinessId, int ServiceId, int WorkerId, int ClientId, int IndividualAppointmentId, int ClassSessionId);

    private sealed record StoredNotification(
        int BusinessId, NotificationType Type, string Recipient, string Subject, string BodyHtml,
        NotificationStatus Status, int Attempts, string? LastError, DateTime CreatedAt);

    /// <summary>Reversible stand-in for the Data Protection protector: the token travels as a prefixed literal.</summary>
    private sealed class PassthroughLinkTokenProtector : ILinkTokenProtector
    {
        private const string Prefix = "protected:";

        public string Protect(string token) => Prefix + token;

        public string Unprotect(string protectedToken) => protectedToken[Prefix.Length..];
    }

    /// <summary>Fixed base URL so the notice body stays deterministic without the application options.</summary>
    private sealed class FixedAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://workup.test/t/" + token;
    }
}
