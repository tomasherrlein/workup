using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Data;
using WorkUp.Data.Notifications;
using WorkUp.Domain.Enums;
using Xunit.Abstractions;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlDailySummaryRepository"/> (T25, RF-18) driven through
/// the real <see cref="DailySummaryService"/>. It requires a fresh, dedicated database supplied through
/// <c>WORKUP_T25_DAILY_SQL_CONNECTION</c>; without it every SQL test returns N/A and no connection is opened. Each test
/// derives its own database name from the supplied one, so the whole class runs in a single command, the preflight
/// asserts <c>DB_ID</c> is absent before migrating, and nothing is torn down: the targets are retained on purpose and
/// never reused. No keyring and no web host take part: the fixture drives the port and the service directly.
/// <para>
/// The in-memory fake and the service tests cannot prove what SQL Server does with the global bounded scan, the tenant
/// joins, the target-day key or a concurrent insert. This fixture is the runtime proof of the port: one bounded,
/// id-ordered scan across all businesses that only returns a business whose notice email is present, whose configured
/// time already arrived on the same local day and whose <c>summary:business:{id}:{yyyyMMdd}</c> key over the
/// <b>next</b> day is still absent, carrying only the target day's scheduled rows with their active bookings of the
/// same tenant; and one guarded <c>INSERT ... SELECT</c> that rechecks the live business, the exact recipient, the
/// configured time, the due instant and the canonical key, so an owner edit, a repeated tick or a forced two-session
/// overlap is a no-op instead of a stale or duplicate email.
/// </para>
/// </summary>
public sealed class DailySummarySqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T25_DAILY_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT25DailySql";

    private static readonly Regex SafeDatabaseName =
        new(@"\AWorkUpDemoT25DailySql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>The single clock instant every scenario starts from: 2026-03-12 21:00, the default summary time.</summary>
    private static readonly DateTime Now = new(2026, 3, 12, 21, 0, 0);

    /// <summary>The day a run on <see cref="Now"/> summarizes: the next local day.</summary>
    private static readonly DateOnly Target = new(2026, 3, 13);

    private static readonly TimeOnly DefaultSummaryTime = new(21, 0);

    // ---- The configured hour: before is too early, equal and a missed minute both fire once ----

    [Fact]
    public async Task Before_the_configured_hour_nothing_is_enqueued()
    {
        var target = await PrepareAsync("_Before");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var businessId = await SeedBusinessAsync(connection, "t25-before", "t25.before@example.invalid");
            await SeedAgendaAsync(connection, businessId, SeedIndividual(On(Target, 9), "Ana", "Antes"));
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlDailySummaryRepository(session);

        // The scan itself refuses the business: the configured time has not arrived on this local day.
        Assert.Empty(await repository.ListCandidatesAsync(Now.AddMinutes(-1), DailySummaryService.BatchSize));

        Assert.Equal(0, await new DailySummaryService(repository).RunAsync(Now.AddMinutes(-1)));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Before-hour assertions passed: at 20:59 the scan returned no candidate and nothing was enqueued.");
    }

    [Fact]
    public async Task A_business_created_after_the_composing_instant_is_not_a_candidate()
    {
        var target = await PrepareAsync("_Rewind");
        if (target is null)
            return;

        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            // A rewound T26 clock lands one hour before the business exists: eligibility is judged at the supplied
            // instant, so a business created later must not receive a summary for a day it could not have planned.
            var businessId = await SeedBusinessAsync(
                connection, "t25-rewind", "t25.rewind@example.invalid", createdAt: Now.AddHours(1));
            await SeedAgendaAsync(connection, businessId, SeedIndividual(On(Target, 9), "Ana", "Futura"));
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlDailySummaryRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(Now, DailySummaryService.BatchSize));
        Assert.Equal(0, await new DailySummaryService(repository).RunAsync(Now));
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        output.WriteLine(
            "Rewind assertions passed: the business created one hour after the composing instant was invisible.");
    }

    [Fact]
    public async Task Exactly_at_the_configured_hour_enqueues_once_for_the_next_day_only()
    {
        var target = await PrepareAsync("_At");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t25-at", "t25.at@example.invalid");
            await SeedAgendaAsync(
                connection, businessId,
                SeedIndividual(On(Target, 9), "Ana", "Justo"),
                SeedClass(On(Target, 18)),
                SeedClass(On(Target, 20)));
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlDailySummaryRepository(session);
        var service = new DailySummaryService(repository);

        Assert.Equal(1, await service.RunAsync(Now));

        var notice = await ReadNotificationAsync(target.ConnectionString, Key(businessId));
        Assert.Equal(businessId, notice.BusinessId);
        Assert.Equal(NotificationType.DailySummary, notice.Type);
        Assert.Equal("t25.at@example.invalid", notice.Recipient);
        Assert.Contains(DailySummaryService.SubjectPrefix, notice.Subject, StringComparison.Ordinal);
        Assert.Contains("13/03/2026", notice.Subject, StringComparison.Ordinal);
        Assert.Equal(NotificationStatus.Pending, notice.Status);
        Assert.Equal(0, notice.Attempts);
        Assert.Null(notice.LastError);
        Assert.Equal(Now, notice.CreatedAt);
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, Key(businessId)));

        // A repeated tick on the same day finds the target key already taken and adds nothing.
        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, Key(businessId)));

        // The key is scoped to the *next* day: the scan drops the business with its key, and an older key of another
        // business — for today, produced yesterday — never suppresses tomorrow's summary.
        Assert.Empty(await repository.ListCandidatesAsync(Now, DailySummaryService.BatchSize));

        int staleKeyBusiness;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            staleKeyBusiness = await SeedBusinessAsync(connection, "t25-at-stale", "t25.at.stale@example.invalid");
            await InsertNotificationAsync(
                connection, staleKeyBusiness, NotificationType.DailySummary,
                DailySummaryService.DedupKey(staleKeyBusiness, Target.AddDays(-1)), Now.AddDays(-1));
        }

        var remaining = await repository.ListCandidatesAsync(Now, DailySummaryService.BatchSize);
        var candidate = Assert.Single(remaining);
        Assert.Equal(staleKeyBusiness, candidate.BusinessId);
        Assert.Equal(DefaultSummaryTime, candidate.ConfiguredTime);
        Assert.Equal("t25.at.stale@example.invalid", candidate.Recipient);
        Assert.Empty(candidate.Appointments);

        output.WriteLine(
            "At-hour assertions passed: 21:00 enqueued exactly one Pending DailySummary keyed on the next day, a " +
            "repeated tick was a no-op and a stale key for today did not suppress tomorrow.");
    }

    [Fact]
    public async Task A_missed_minute_still_serves_the_same_target_later_that_day()
    {
        var target = await PrepareAsync("_Missed");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t25-missed", "t25.missed@example.invalid");
            await SeedAgendaAsync(connection, businessId, SeedIndividual(On(Target, 10), "Ana", "Tarde"));
        }

        await using var session = new DbSession(target.ConnectionString);
        var missed = Now.AddMinutes(40); // 21:40: the scheduled minute was missed.
        Assert.Equal(1, await new DailySummaryService(new SqlDailySummaryRepository(session)).RunAsync(missed));

        var notice = await ReadNotificationAsync(target.ConnectionString, Key(businessId));
        Assert.Equal(missed, notice.CreatedAt);
        Assert.Equal(Key(businessId), notice.DedupKey);
        Assert.Contains("13/03/2026", notice.Subject, StringComparison.Ordinal);

        output.WriteLine(
            "Missed-minute assertions passed: the 21:40 tick produced the day-13 summary with its own created " +
            "instant and the same target key a punctual tick would have used.");
    }

    // ---- The agenda of the target day only ----

    [Fact]
    public async Task Only_the_target_day_agenda_is_reported()
    {
        var target = await PrepareAsync("_DayScope");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t25-dayscope", "t25.dayscope@example.invalid");
            await SeedAgendaAsync(
                connection, businessId,
                SeedIndividual(On(Target.AddDays(-1), 10), "Hoy", "DeHoy"),
                SeedIndividual(On(Target, 9), "Manana", "DeManana"),
                SeedIndividual(On(Target.AddDays(1), 9), "Pasado", "DePasado"));
        }

        await using var session = new DbSession(target.ConnectionString);
        var service = new DailySummaryService(new SqlDailySummaryRepository(session));

        Assert.Equal(1, await service.RunAsync(Now));

        var notice = await ReadNotificationAsync(target.ConnectionString, Key(businessId));
        Assert.Contains("Manana", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("DeHoy", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("DePasado", notice.BodyHtml, StringComparison.Ordinal);

        output.WriteLine(
            "Day-scope assertions passed: only the target day's turn appeared; today's and the day after were " +
            "invisible to the scan.");
    }

    // ---- Individual turns need an active booking; class sessions survive empty ----

    [Fact]
    public async Task An_individual_without_an_active_booking_is_dropped_while_an_empty_class_survives()
    {
        var target = await PrepareAsync("_Shape");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t25-shape", "t25.shape@example.invalid");
            await SeedAgendaAsync(
                connection, businessId,
                // Kept: an individual turn with a still-active booking, even alongside a cancelled one.
                SeedIndividualBookings(
                    On(Target, 9), AppointmentStatus.Scheduled,
                    ("Ana", "Activa", BookingStatus.Pending),
                    ("Beto", "Inactivo", BookingStatus.Cancelled)),
                // Dropped: an individual turn whose only booking was cancelled.
                SeedIndividualBookings(
                    On(Target, 10), AppointmentStatus.Scheduled,
                    ("Caro", "Cancelada", BookingStatus.Cancelled)),
                // Kept: a class session with no bookings at all.
                SeedClass(On(Target, 18)),
                // Kept: a class session with one confirmed and one cancelled booking.
                SeedClass(On(Target, 19), [("Dora", "Confirmada", BookingStatus.Confirmed), ("Eva", "Baja", BookingStatus.Cancelled)]),
                // Dropped: a suspended class session, even with an active booking.
                SeedClass(On(Target, 20), [("Flor", "Suspendida", BookingStatus.Pending)], AppointmentStatus.Suspended));
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlDailySummaryRepository(session);
        var service = new DailySummaryService(repository);

        // The scan already carries exactly the three visible rows, in start order.
        var candidate = Assert.Single(await repository.ListCandidatesAsync(Now, DailySummaryService.BatchSize));
        Assert.Equal(
            new[] { 9, 18, 19 },
            candidate.Appointments.Select(appointment => appointment.StartsAt.Hour).ToArray());
        Assert.Equal(
            new[] { AppointmentKind.Individual, AppointmentKind.ClassSession, AppointmentKind.ClassSession },
            candidate.Appointments.Select(appointment => appointment.Kind).ToArray());
        Assert.Equal("Ana Activa", candidate.Appointments[0].Bookings.Single().ClientName);
        Assert.Empty(candidate.Appointments[1].Bookings);
        Assert.Equal("Dora Confirmada", candidate.Appointments[2].Bookings.Single().ClientName);

        Assert.Equal(1, await service.RunAsync(Now));

        var notice = await ReadNotificationAsync(target.ConnectionString, Key(businessId));
        Assert.Contains("Ana Activa", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("sin anotaciones", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Dora Confirmada", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Caro Cancelada", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Beto Inactivo", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Eva Baja", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Flor Suspendida", notice.BodyHtml, StringComparison.Ordinal);

        output.WriteLine(
            "Shape assertions passed: the individual without an active booking and the suspended class were absent, " +
            "the empty class stayed, and only active bookings reached the body.");
    }

    // ---- No usable notice email means no summary at all ----

    [Fact]
    public async Task A_business_without_a_notice_email_never_falls_back_to_its_phone()
    {
        var target = await PrepareAsync("_NoEmail");
        if (target is null)
            return;

        const string phone = "+54 11 5555-0000";
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            var absent = await SeedBusinessAsync(
                connection, "t25-noemail", notificationEmail: null, contactPhone: phone);
            await SeedAgendaAsync(connection, absent, SeedIndividual(On(Target, 9), "Ana", "SinCorreo"));

            var blank = await SeedBusinessAsync(
                connection, "t25-blankemail", notificationEmail: "   ", contactPhone: phone);
            await SeedAgendaAsync(connection, blank, SeedIndividual(On(Target, 9), "Beto", "Blanco"));
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlDailySummaryRepository(session);

        Assert.Empty(await repository.ListCandidatesAsync(Now, DailySummaryService.BatchSize));
        Assert.Equal(0, await new DailySummaryService(repository).RunAsync(Now));

        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));
        Assert.Equal(
            0,
            await CountAsync(
                target.ConnectionString,
                "SELECT COUNT(*) FROM Notifications WHERE Recipient = @phone",
                ("@phone", phone)));

        output.WriteLine(
            "No-email assertions passed: neither the NULL nor the blank notice email produced a candidate and the " +
            "contact phone was never used as a recipient.");
    }

    [Fact]
    public async Task An_empty_agenda_says_so_explicitly_instead_of_sending_an_empty_body()
    {
        var target = await PrepareAsync("_Empty");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
            businessId = await SeedBusinessAsync(connection, "t25-empty", "t25.empty@example.invalid");

        await using var session = new DbSession(target.ConnectionString);
        Assert.Equal(1, await new DailySummaryService(new SqlDailySummaryRepository(session)).RunAsync(Now));

        var notice = await ReadNotificationAsync(target.ConnectionString, Key(businessId));
        Assert.Contains(DailySummaryService.EmptyAgendaText, notice.BodyHtml, StringComparison.Ordinal);

        output.WriteLine(
            "Empty-agenda assertions passed: the summary was still produced and named the empty agenda explicitly.");
    }

    // ---- Tenant safety ----

    [Fact]
    public async Task Cross_tenant_scans_never_mix_businesses_or_borrow_a_foreign_client()
    {
        var target = await PrepareAsync("_Tenant");
        if (target is null)
            return;

        int ownBusiness;
        int foreignBusiness;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            ownBusiness = await SeedBusinessAsync(connection, "t25-tenant-own", "t25.tenant.own@example.invalid");
            foreignBusiness = await SeedBusinessAsync(connection, "t25.tenant-foreign", "t25.tenant.foreign@example.invalid");

            var ownService = await SeedServiceAsync(connection, ownBusiness);
            var ownWorker = await SeedWorkerAsync(connection, ownBusiness);
            var ownClient = await SeedClientAsync(connection, ownBusiness, "own", "Ana", "Propia");
            var foreignClient = await SeedClientAsync(connection, foreignBusiness, "foreign", "Bety", "Ajena");

            // The owner's own turn with two bookings: its own client, plus a booking whose ficha belongs to the other
            // tenant. The tenant join must hide the foreign ficha instead of displaying it.
            var ownAppointment = await SeedAppointmentAsync(
                connection, ownBusiness, AppointmentKind.Individual, ownService, ownWorker, On(Target, 9));
            await SeedBookingAsync(connection, ownBusiness, ownAppointment, ownClient, BookingStatus.Pending);
            await SeedBookingAsync(connection, ownBusiness, ownAppointment, foreignClient, BookingStatus.Confirmed);

            // An inconsistent booking of the foreign tenant pointing at the owner's appointment, and the same shape on
            // the foreign tenant's own appointment: neither may surface in the other tenant's agenda.
            var foreignService = await SeedServiceAsync(connection, foreignBusiness);
            var foreignWorker = await SeedWorkerAsync(connection, foreignBusiness);
            var foreignAppointment = await SeedAppointmentAsync(
                connection, foreignBusiness, AppointmentKind.Individual, foreignService, foreignWorker, On(Target, 10));
            await SeedBookingAsync(connection, ownBusiness, foreignAppointment, ownClient, BookingStatus.Pending);
            await SeedBookingAsync(connection, foreignBusiness, foreignAppointment, foreignClient, BookingStatus.Pending);
        }

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlDailySummaryRepository(session);

        // Both tenants are eligible at 21:00; each candidate only carries its own row and its own ficha.
        var candidates = await repository.ListCandidatesAsync(Now, DailySummaryService.BatchSize);
        Assert.Equal(new[] { ownBusiness, foreignBusiness }, candidates.Select(row => row.BusinessId).ToArray());
        var ownCandidate = candidates[0];
        Assert.Equal("t25.tenant.own@example.invalid", ownCandidate.Recipient);
        var ownRow = Assert.Single(ownCandidate.Appointments);
        Assert.Equal("Ana Propia", ownRow.Bookings.Single().ClientName);
        var foreignCandidate = candidates[1];
        Assert.Equal("t25.tenant.foreign@example.invalid", foreignCandidate.Recipient);
        Assert.Equal("Bety Ajena", Assert.Single(foreignCandidate.Appointments).Bookings.Single().ClientName);

        Assert.Equal(2, await new DailySummaryService(repository).RunAsync(Now));

        var ownNotice = await ReadNotificationAsync(target.ConnectionString, Key(ownBusiness));
        var foreignNotice = await ReadNotificationAsync(target.ConnectionString, Key(foreignBusiness));
        Assert.Equal("t25.tenant.own@example.invalid", ownNotice.Recipient);
        Assert.Equal("t25.tenant.foreign@example.invalid", foreignNotice.Recipient);
        Assert.Contains("Ana Propia", ownNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Bety Ajena", ownNotice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Bety Ajena", foreignNotice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana Propia", foreignNotice.BodyHtml, StringComparison.Ordinal);

        output.WriteLine(
            "Tenant assertions passed: the foreign ficha was invisible inside the owner's turn and no body ever named " +
            "another tenant's client or recipient.");
    }

    // ---- Guarded enqueue: an owner edit between the scan and the insert is a no-op ----

    [Fact]
    public async Task A_recipient_or_configured_time_changed_after_the_scan_is_not_notified()
    {
        var target = await PrepareAsync("_Changed");
        if (target is null)
            return;

        int recipientBusiness;
        int timeBusiness;
        const string editedRecipient = "t25.changed.nuevo@example.invalid";
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            recipientBusiness = await SeedBusinessAsync(connection, "t25-changed-recipient", "t25.changed.viejo@example.invalid");
            await SeedAgendaAsync(connection, recipientBusiness, SeedIndividual(On(Target, 9), "Ana", "Editada"));

            timeBusiness = await SeedBusinessAsync(connection, "t25-changed-time", "t25.changed.time@example.invalid");
            await SeedAgendaAsync(connection, timeBusiness, SeedIndividual(On(Target, 10), "Beto", "Hora"));
        }

        await using var session = new DbSession(target.ConnectionString);
        var scanned = 0;
        var service = new DailySummaryService(
            new HookedDailySummaryRepository(
                new SqlDailySummaryRepository(session),
                async candidates =>
                {
                    // The owner edits the recipient and the configured time right after the scan, before the insert.
                    scanned = candidates.Count;
                    await ExecuteAsync(
                        target.ConnectionString,
                        "UPDATE Businesses SET NotificationEmail = @email WHERE Id = @business",
                        ("@email", editedRecipient), ("@business", recipientBusiness));
                    await ExecuteAsync(
                        target.ConnectionString,
                        "UPDATE Businesses SET DailySummaryTime = @time WHERE Id = @business",
                        ("@time", new TimeOnly(23, 0)), ("@business", timeBusiness));
                }));

        Assert.Equal(0, await service.RunAsync(Now));
        Assert.Equal(2, scanned);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Notifications"));

        // The change only invalidated the notice composed before it: a fresh run re-scans, notices the new recipient of
        // the first business and finds the second one still not due at 21:00 (its time is now 23:00).
        Assert.Equal(1, await new DailySummaryService(new SqlDailySummaryRepository(session)).RunAsync(Now));
        var notice = await ReadNotificationAsync(target.ConnectionString, Key(recipientBusiness));
        Assert.Equal(editedRecipient, notice.Recipient);
        Assert.Equal(0, await CountNotificationsAsync(target.ConnectionString, Key(timeBusiness)));

        output.WriteLine(
            "Guard assertions passed: the notice composed before the edit was refused with no row written, while a " +
            "fresh scan produced the summary against the live recipient only.");
    }

    // ---- Two independent sessions racing the same business produce exactly one summary ----

    [Fact]
    public async Task Two_concurrent_sessions_enqueue_exactly_one_summary()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(connection, "t25-race", "t25.race@example.invalid");
            await SeedAgendaAsync(connection, businessId, SeedIndividual(On(Target, 9), "Ana", "Carrera"));
        }

        // The barrier forces both sessions to finish their scan before either inserts, so the unique dedup key — not a
        // lucky ordering — is what decides the winner.
        using var startGate = new Barrier(participantCount: 2);

        Task<int> RunAsync() => Task.Run(async () =>
        {
            await using var session = new DbSession(target.ConnectionString);
            var service = new DailySummaryService(
                new HookedDailySummaryRepository(
                    new SqlDailySummaryRepository(session),
                    _ =>
                    {
                        startGate.SignalAndWait();
                        return Task.CompletedTask;
                    }));
            return await service.RunAsync(Now);
        });

        var results = await Task.WhenAll(RunAsync(), RunAsync());

        Assert.Equal(1, results.Sum());
        Assert.Equal(1, await CountNotificationsAsync(target.ConnectionString, Key(businessId)));

        output.WriteLine(
            "Race assertions passed: two independent sessions with forced overlap enqueued exactly one summary; this " +
            "is one forced overlap, not a universal no-deadlock claim.");
    }

    // ---- The body carries encoded names and no note, payment or link ----

    [Fact]
    public async Task The_body_encodes_names_and_never_carries_notes_payments_or_links()
    {
        var target = await PrepareAsync("_Private");
        if (target is null)
            return;

        int businessId;
        await using (var connection = await OpenAsync(target.ConnectionString))
        {
            businessId = await SeedBusinessAsync(
                connection, "t25-private", "t25.private@example.invalid", businessName: "<b>Estudio</b>");
            var serviceId = await SeedServiceAsync(connection, businessId);
            var workerId = await SeedWorkerAsync(connection, businessId);
            var clientId = await SeedClientAsync(
                connection, businessId, "private", "<Ana>", "Rios", healthNotes: "ALERGIA-MARCADOR-123");
            var appointmentId = await SeedAppointmentAsync(
                connection, businessId, AppointmentKind.Individual, serviceId, workerId, On(Target, 9));
            var bookingId = await SeedBookingAsync(
                connection, businessId, appointmentId, clientId, BookingStatus.Pending);

            // An owner-only note on the turn and a payment of the booking: neither belongs to the summary.
            await InsertAsync(connection,
                "INSERT INTO ClientNotes (ClientId, AppointmentId, Text, CreatedAt) VALUES (@client, @appointment, 'NOTA-PRIVADA-999', @now)",
                ("@client", clientId), ("@appointment", appointmentId), ("@now", Now));
            await InsertAsync(connection,
                """
                INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, Amount, PaidAt, RecordedAt)
                VALUES (@business, @client, @concept, @booking, 12345.67, @now, @now)
                """,
                ("@business", businessId), ("@client", clientId), ("@concept", PaymentConcept.IndividualAppointment),
                ("@booking", bookingId), ("@now", Now));
        }

        await using var session = new DbSession(target.ConnectionString);
        Assert.Equal(1, await new DailySummaryService(new SqlDailySummaryRepository(session)).RunAsync(Now));

        var notice = await ReadNotificationAsync(target.ConnectionString, Key(businessId));
        Assert.Contains("&lt;Ana&gt; Rios", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Ana>", notice.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Estudio&lt;/b&gt;", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("ALERGIA-MARCADOR-123", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTA-PRIVADA-999", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("http", notice.BodyHtml, StringComparison.OrdinalIgnoreCase);

        output.WriteLine(
            "Privacy assertions passed: names reached the body HTML-encoded while the health note, the owner note, " +
            "the payment amount and any link stayed out.");
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
            for (var index = 0; index < DailySummaryService.BatchSize + 1; index++)
            {
                var businessId = await SeedBusinessAsync(
                    connection, $"t25-batch-{index}", $"t25.batch.{index}@example.invalid");
                seeded.Add(businessId);
            }
        }

        await using var session = new DbSession(target.ConnectionString);
        var candidates = await new SqlDailySummaryRepository(session)
            .ListCandidatesAsync(Now, DailySummaryService.BatchSize);

        Assert.Equal(DailySummaryService.BatchSize, candidates.Count);
        Assert.Equal(seeded.Take(DailySummaryService.BatchSize), candidates.Select(row => row.BusinessId));

        output.WriteLine(
            "Batch assertions passed: 21 eligible businesses produced exactly the 20 lowest ids in ascending order.");
    }

    // --- Service and repository wiring ------------------------------------------------------------------------

    private static string Key(int businessId) => DailySummaryService.DedupKey(businessId, Target);

    private static DateTime On(DateOnly day, int hour, int minute = 0) => day.ToDateTime(new TimeOnly(hour, minute));

    /// <summary>Delegates to the real port and runs <paramref name="afterList"/> between the scan and the enqueue.</summary>
    private sealed class HookedDailySummaryRepository(
        IDailySummaryRepository inner,
        Func<IReadOnlyList<DailySummaryCandidate>, Task> afterList) : IDailySummaryRepository
    {
        public async Task<IReadOnlyList<DailySummaryCandidate>> ListCandidatesAsync(
            DateTime now, int batchSize, CancellationToken ct = default)
        {
            var candidates = await inner.ListCandidatesAsync(now, batchSize, ct);
            await afterList(candidates);
            return candidates;
        }

        public Task<bool> TryEnqueueSummaryAsync(DailySummaryNotice notice, CancellationToken ct = default) =>
            inner.TryEnqueueSummaryAsync(notice, ct);
    }

    // --- Seeding ------------------------------------------------------------------------------------------------

    /// <summary>Declarative shape of one seeded appointment, resolved against its business at seed time.</summary>
    private sealed record AppointmentSeed(
        AppointmentKind Kind,
        DateTime StartsAt,
        AppointmentStatus Status,
        IReadOnlyList<(string FirstName, string LastName, BookingStatus Status)> Bookings);

    /// <summary>An individual turn annotated by a single booking at <paramref name="bookingStatus"/>.</summary>
    private static AppointmentSeed SeedIndividual(
        DateTime startsAt,
        string firstName,
        string lastName,
        BookingStatus bookingStatus = BookingStatus.Pending,
        AppointmentStatus status = AppointmentStatus.Scheduled) =>
        new(AppointmentKind.Individual, startsAt, status, [(firstName, lastName, bookingStatus)]);

    /// <summary>An individual turn with its exact bookings, so a turn without any active booking can be seeded.</summary>
    private static AppointmentSeed SeedIndividualBookings(
        DateTime startsAt,
        AppointmentStatus status,
        params (string FirstName, string LastName, BookingStatus Status)[] bookings) =>
        new(AppointmentKind.Individual, startsAt, status, bookings);

    private static AppointmentSeed SeedClass(
        DateTime startsAt,
        IReadOnlyList<(string FirstName, string LastName, BookingStatus Status)>? bookings = null,
        AppointmentStatus status = AppointmentStatus.Scheduled) =>
        new(AppointmentKind.ClassSession, startsAt, status, bookings ?? []);

    /// <summary>Seeds one tenant, its service and its worker, then the appointments and bookings it declares.</summary>
    private static async Task SeedAgendaAsync(
        SqlConnection connection, int businessId, params AppointmentSeed[] appointments)
    {
        var serviceId = await SeedServiceAsync(connection, businessId);
        var workerId = await SeedWorkerAsync(connection, businessId);
        var clients = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var appointment in appointments)
        {
            var appointmentId = await SeedAppointmentAsync(
                connection, businessId, appointment.Kind, serviceId, workerId, appointment.StartsAt,
                appointment.Status);

            foreach (var booking in appointment.Bookings)
            {
                var key = booking.FirstName + " " + booking.LastName;
                if (!clients.TryGetValue(key, out var clientId))
                {
                    clientId = await SeedClientAsync(
                        connection, businessId, $"c{clients.Count + 1}", booking.FirstName, booking.LastName);
                    clients[key] = clientId;
                }

                await SeedBookingAsync(connection, businessId, appointmentId, clientId, booking.Status);
            }
        }
    }

    private static Task<int> SeedBusinessAsync(
        SqlConnection connection, string slug, string? notificationEmail,
        TimeOnly? summaryTime = null, string? contactPhone = null, string businessName = "Synthetic T25",
        DateTime? createdAt = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, DailySummaryTime, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @summaryTime, @createdAt)
            """,
            ("@name", businessName), ("@slug", slug), ("@phone", contactPhone ?? "0000000000"),
            ("@email", notificationEmail), ("@summaryTime", summaryTime ?? DefaultSummaryTime),
            ("@createdAt", createdAt ?? Now.AddDays(-30)));

    private static Task<int> SeedServiceAsync(SqlConnection connection, int businessId) =>
        InsertAsync(connection,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Service', 1000.00, 30, 8)",
            ("@business", businessId));

    private static Task<int> SeedWorkerAsync(SqlConnection connection, int businessId) =>
        InsertAsync(connection,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Worker', '0000000000', @cuil)",
            ("@business", businessId), ("@cuil", "20" + businessId.ToString("D9", CultureInfo.InvariantCulture)));

    private static Task<int> SeedClientAsync(
        SqlConnection connection, int businessId, string suffix, string firstName, string lastName,
        string? healthNotes = null) =>
        InsertAsync(connection,
            """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, HealthNotes, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @firstName, @lastName, @email, @healthNotes, @now)
            """,
            ("@business", businessId), ("@firstName", firstName), ("@lastName", lastName),
            ("@email", $"{suffix}.{businessId.ToString(CultureInfo.InvariantCulture)}@example.invalid"),
            ("@healthNotes", healthNotes), ("@now", Now));

    private static Task<int> SeedAppointmentAsync(
        SqlConnection connection, int businessId, AppointmentKind kind, int serviceId, int workerId,
        DateTime startsAt, AppointmentStatus status = AppointmentStatus.Scheduled)
    {
        // CK_Appointments_KindShape: an individual turn carries its origin and capacity 1; a class has no origin.
        object? origin = kind == AppointmentKind.Individual ? (byte)AppointmentOrigin.Reserved : DBNull.Value;
        var capacity = kind == AppointmentKind.Individual ? 1 : 8;
        return InsertAsync(connection,
            """
            INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
            OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @startsAt, @endsAt, @capacity, @origin, @status, @now)
            """,
            ("@business", businessId), ("@kind", kind), ("@service", serviceId), ("@worker", workerId),
            ("@startsAt", startsAt), ("@endsAt", startsAt.AddHours(1)), ("@capacity", capacity),
            ("@origin", origin), ("@status", status), ("@now", Now));
    }

    private static Task<int> SeedBookingAsync(
        SqlConnection connection, int businessId, int appointmentId, int clientId, BookingStatus status,
        BookingModality? modality = null, int? enrollmentId = null) =>
        // CK_Bookings_Cancelled requires CancelledAt exactly when the status is Cancelled.
        InsertAsync(connection,
            """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt)
            OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, @enrollment, @modality, @status, @createdAt, @cancelledAt)
            """,
            ("@business", businessId), ("@appointment", appointmentId), ("@client", clientId),
            ("@enrollment", enrollmentId), ("@modality", modality), ("@status", status),
            ("@createdAt", Now.AddDays(-5)),
            ("@cancelledAt", status == BookingStatus.Cancelled ? (object)Now.AddDays(-1) : DBNull.Value));

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
    /// Derives this test's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog,
    /// so one <c>WORKUP_T25_DAILY_SQL_CONNECTION</c> value serves every fact. Only the validated LocalDB instance and
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
            ApplicationName = "WorkUp T25 daily summary SQL integration test",
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
