using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Data;
using WorkUp.Data.Classes;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Classes;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlWeekGenerationRepository"/>. Requires a new, dedicated
/// database supplied through WORKUP_T13_SQL_CONNECTION; without it the fixture opens nothing and never tears a target
/// down. Every test derives its own database name from the supplied one so the whole class runs in a single command.
/// </summary>
public sealed class WeekGenerationSqlIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private static readonly DateOnly WeekStart = new(2026, 3, 9);   // Monday
    private static readonly DateOnly WeekEnd = new(2026, 3, 15);    // Sunday
    private static readonly DateTime FixedNow = new(2026, 3, 9, 8, 0, 0);

    [Fact]
    public async Task Confirming_a_week_creates_each_session_once_and_never_recreates_a_suspension_or_a_cancellation()
    {
        var supplied = Environment.GetEnvironmentVariable("WORKUP_T13_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine("N/A: WORKUP_T13_SQL_CONNECTION absent; no SQL connection opened.");
            return;
        }

        var target = ValidateTarget(supplied, "_WeekGeneration");
        var applied = await CreateTargetAsync(target);
        output.WriteLine($"Target: {target.DatabaseName}; DB_ID absent before creation; applied {applied} schema script(s); retained without teardown.");

        var clock = new FakeClock { Now = FixedNow };
        var ids = await SeedWeekAsync(target.ConnectionString, clock);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlWeekGenerationRepository(session);
        var service = new WeekGenerationService(repository, session, clock);

        // Read scope: the active grid only, the sessions of this week only, no foreign session.
        var snapshot = await repository.GetWeekSnapshotAsync(ids.BusinessA, WeekStart, WeekEnd);
        Assert.Equal(ids.BusinessA, snapshot.BusinessId);
        Assert.Equal(4, snapshot.GridClasses.Count);
        Assert.DoesNotContain(snapshot.GridClasses, grid => grid.GridClassId == ids.GridClassInactive);
        var mondayGrid = Assert.Single(snapshot.GridClasses, grid => grid.GridClassId == ids.GridClassMonday);
        Assert.Equal(60, mondayGrid.DurationMinutes);
        Assert.Equal(8, mondayGrid.MaxCapacity);
        Assert.Equal(2, snapshot.Sessions.Count);
        Assert.DoesNotContain(snapshot.Sessions, row => row.AppointmentId == ids.BusinessBAppointmentId);
        Assert.Equal(7, snapshot.FixedAssignments.Count);
        Assert.DoesNotContain(snapshot.FixedAssignments, assignment => assignment.EnrollmentId == ids.En8);
        Assert.Single(Assert.Single(snapshot.FixedAssignments, assignment => assignment.EnrollmentId == ids.En2).Periods);
        Assert.Empty(Assert.Single(snapshot.FixedAssignments, assignment => assignment.EnrollmentId == ids.En3).Periods);

        // The suspended session is projected as it is, with the booking it already has.
        var suspendedRow = Assert.Single(snapshot.Sessions, row => row.AppointmentId == ids.SuspendedSessionId);
        Assert.Equal(AppointmentStatus.Suspended, suspendedRow.Status);
        Assert.Equal(WeekStart.AddDays(2), suspendedRow.Date);
        Assert.Equal(new TimeOnly(12, 0), suspendedRow.StartTime);
        var preBooked = Assert.Single(suspendedRow.Bookings);
        Assert.Equal(ids.En4, preBooked.EnrollmentId);
        Assert.Equal(BookingStatus.Confirmed, preBooked.Status);
        Assert.Equal("Client T13 4", preBooked.ClientName);

        // First confirm: two missing sessions, one session per active grid class of the week, five missing annotations.
        var first = await service.ConfirmAsync(ids.BusinessA, WeekStart);
        Assert.True(first.Succeeded, first.Error?.Code ?? "The first week generation failed.");
        Assert.Equal(WeekStart, first.Value.WeekStart);
        Assert.Equal(WeekEnd, first.Value.WeekEnd);
        Assert.Equal(2, first.Value.CreatedSessions);
        Assert.Equal(5, first.Value.CreatedFixedBookings);
        Assert.Equal(2, first.Value.ExistingSessions);

        var afterFirst = await ReadStateAsync(target.ConnectionString);
        var sessionsA = afterFirst.Appointments.Where(appointment => appointment.BusinessId == ids.BusinessA).ToArray();
        Assert.Equal(4, sessionsA.Length);
        Assert.DoesNotContain(sessionsA, appointment => appointment.GridClassId == ids.GridClassInactive);

        var mondaySession = Assert.Single(sessionsA, appointment => appointment.GridClassId == ids.GridClassMonday);
        Assert.Equal(WeekStart.ToDateTime(new TimeOnly(10, 0)), mondaySession.StartsAt);
        Assert.Equal(WeekStart.ToDateTime(new TimeOnly(11, 0)), mondaySession.EndsAt); // the 60 minutes of the service
        Assert.Equal(8, mondaySession.MaxCapacity);
        Assert.Equal((byte)AppointmentStatus.Scheduled, mondaySession.Status);
        var tuesdaySession = Assert.Single(sessionsA, appointment => appointment.GridClassId == ids.GridClassTuesday);
        Assert.Equal(WeekStart.AddDays(1).ToDateTime(new TimeOnly(11, 0)), tuesdaySession.StartsAt);
        Assert.Equal(1, sessionsA.Count(appointment => appointment.GridClassId == ids.GridClassWednesday));
        Assert.Equal(1, sessionsA.Count(appointment => appointment.GridClassId == ids.GridClassThursday));

        // An existing suspended session keeps its status and capacity and only receives the missing annotation.
        var suspended = Assert.Single(sessionsA, appointment => appointment.Id == ids.SuspendedSessionId);
        Assert.Equal((byte)AppointmentStatus.Suspended, suspended.Status);
        Assert.Equal(8, suspended.MaxCapacity);
        Assert.Equal(WeekStart.AddDays(2).ToDateTime(new TimeOnly(12, 0)), suspended.StartsAt);
        var suspendedBookings = afterFirst.Bookings.Where(booking => booking.AppointmentId == ids.SuspendedSessionId).ToArray();
        Assert.Equal(2, suspendedBookings.Length);
        var kept = Assert.Single(suspendedBookings, booking => booking.EnrollmentId == ids.En4);
        Assert.Equal((byte)BookingStatus.Confirmed, kept.Status);
        Assert.Contains(suspendedBookings, booking =>
            booking.EnrollmentId == ids.En5 && booking.Status == (byte)BookingStatus.Pending);

        // An existing session gains the missing annotation, keeps the cancelled one and loses nothing.
        var thursdayBookings = afterFirst.Bookings.Where(booking => booking.AppointmentId == ids.ActiveSessionId).ToArray();
        Assert.Equal(2, thursdayBookings.Length);
        var cancelled = Assert.Single(thursdayBookings, booking => booking.EnrollmentId == ids.En6);
        Assert.Equal((byte)BookingStatus.Cancelled, cancelled.Status);
        Assert.Contains(thursdayBookings, booking =>
            booking.EnrollmentId == ids.En7 && booking.Status == (byte)BookingStatus.Pending);

        var bookingsA = afterFirst.Bookings.Where(booking => booking.BusinessId == ids.BusinessA).ToArray();
        Assert.Equal(7, bookingsA.Length);
        var mondayAnnotation = Assert.Single(bookingsA, booking => booking.AppointmentId == mondaySession.Id);
        Assert.Equal(ids.En1, mondayAnnotation.EnrollmentId);
        Assert.Equal(ids.Cl1, mondayAnnotation.ClientId);
        Assert.Equal((byte)BookingModality.Fixed, mondayAnnotation.Modality);
        Assert.Equal((byte)BookingStatus.Pending, mondayAnnotation.Status);
        Assert.Equal(
            new int?[] { ids.En2, ids.En3 }.OrderBy(id => id).ToArray(),
            bookingsA.Where(booking => booking.AppointmentId == tuesdaySession.Id).Select(booking => booking.EnrollmentId).OrderBy(id => id).ToArray());
        // Neither the expired validity assignment nor its client was annotated.
        Assert.DoesNotContain(bookingsA, booking => booking.EnrollmentId == ids.En8);
        Assert.DoesNotContain(bookingsA, booking => booking.ClientId == ids.Cl8);

        // Tenant isolation: confirming business A leaves business B's appointment and booking untouched.
        var appointmentsB = afterFirst.Appointments.Where(appointment => appointment.BusinessId == ids.BusinessB).ToArray();
        var appointmentB = Assert.Single(appointmentsB);
        Assert.Equal(ids.BusinessBAppointmentId, appointmentB.Id);
        Assert.Equal(WeekStart.ToDateTime(new TimeOnly(10, 0)), appointmentB.StartsAt);
        Assert.Equal((byte)AppointmentStatus.Scheduled, appointmentB.Status);
        var bookingB = Assert.Single(afterFirst.Bookings, booking => booking.BusinessId == ids.BusinessB);
        Assert.Equal((byte)BookingStatus.Pending, bookingB.Status);
        Assert.Null(bookingB.EnrollmentId);

        // Second confirm of the same week: nothing is created and no row changes.
        var second = await service.ConfirmAsync(ids.BusinessA, WeekStart);
        Assert.True(second.Succeeded, second.Error?.Code ?? "The repeated week generation failed.");
        Assert.Equal(0, second.Value.CreatedSessions);
        Assert.Equal(0, second.Value.CreatedFixedBookings);
        Assert.Equal(4, second.Value.ExistingSessions);

        var afterSecond = await ReadStateAsync(target.ConnectionString);
        Assert.Equal(
            afterFirst.Appointments.OrderBy(appointment => appointment.Id).ToArray(),
            afterSecond.Appointments.OrderBy(appointment => appointment.Id).ToArray());
        Assert.Equal(
            afterFirst.Bookings.OrderBy(booking => booking.Id).ToArray(),
            afterSecond.Bookings.OrderBy(booking => booking.Id).ToArray());
        Assert.Equal(4, afterSecond.Appointments.Count(appointment => appointment.BusinessId == ids.BusinessA));
        Assert.Equal(7, afterSecond.Bookings.Count(booking => booking.BusinessId == ids.BusinessA));
        Assert.Equal((byte)AppointmentStatus.Suspended,
            Assert.Single(afterSecond.Appointments, appointment => appointment.Id == ids.SuspendedSessionId).Status);
        Assert.Equal((byte)BookingStatus.Cancelled,
            Assert.Single(afterSecond.Bookings, booking => booking.EnrollmentId == ids.En6).Status);

        output.WriteLine("SQL assertions passed: one session per active grid class, five fixed annotations, suspended session and cancelled booking preserved, tenant B untouched, and a repeated confirm created zero rows.");
    }

    /// <summary>
    /// Two confirms of the same week racing on separate connections. The UPDLOCK/HOLDLOCK probe over
    /// UX_Appointments_GridSession must serialize them: exactly one session and exactly one fixed annotation per
    /// student survive, whatever the interleaving is.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_confirms_of_the_same_week_leave_one_session_per_grid_slot()
    {
        var supplied = Environment.GetEnvironmentVariable("WORKUP_T13_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine("N/A: WORKUP_T13_SQL_CONNECTION absent; no SQL connection opened.");
            return;
        }

        var target = ValidateTarget(supplied, "_WeekGenerationRace");
        var applied = await CreateTargetAsync(target);
        output.WriteLine($"Target: {target.DatabaseName}; DB_ID absent before creation; applied {applied} schema script(s); retained without teardown.");

        var clock = new FakeClock { Now = FixedNow };
        var ids = await SeedRaceAsync(target.ConnectionString, clock);

        // A bare Task.WhenAll over two async calls does not force real overlap: each runs synchronously up to its
        // first await, so the first could finish before the second starts and nothing would race. The barrier pins
        // both confirms on their own thread-pool thread and releases them the instant before each opens its
        // transaction, so both read an empty week and then fight for the same session slot.
        using var startGate = new Barrier(participantCount: 2);

        Task<ConfirmOutcome> ConfirmOnceAsync() => Task.Run(async () =>
        {
            await using var session = new DbSession(target.ConnectionString);
            var service = new WeekGenerationService(new SqlWeekGenerationRepository(session), session, clock);
            startGate.SignalAndWait();
            var result = await service.ConfirmAsync(ids.BusinessA, WeekStart);
            Assert.True(result.Succeeded, result.Error?.Code ?? "The concurrent week generation failed.");
            return new ConfirmOutcome(result.Value.CreatedSessions, result.Value.CreatedFixedBookings);
        });

        var outcomes = await Task.WhenAll(ConfirmOnceAsync(), ConfirmOnceAsync());

        // Count-based by design: whichever confirm lost the race reports zero created rows instead of failing.
        Assert.Equal(1, outcomes.Sum(outcome => outcome.CreatedSessions));
        Assert.Equal(2, outcomes.Sum(outcome => outcome.CreatedFixedBookings));

        var state = await ReadStateAsync(target.ConnectionString);
        var sessions = state.Appointments.Where(appointment => appointment.BusinessId == ids.BusinessA).ToArray();
        var generated = Assert.Single(sessions);
        Assert.Equal(ids.GridClassId, generated.GridClassId);
        Assert.Equal(WeekStart.ToDateTime(new TimeOnly(10, 0)), generated.StartsAt);
        Assert.Equal(1, sessions.Count(appointment => appointment.GridClassId == ids.GridClassId
            && appointment.StartsAt.Date == WeekStart.ToDateTime(TimeOnly.MinValue).Date));

        var annotations = state.Bookings.Where(booking => booking.AppointmentId == generated.Id).ToArray();
        Assert.Equal(2, annotations.Length);
        Assert.Equal(2, annotations.Select(booking => booking.EnrollmentId).Distinct().Count());
        Assert.All(annotations, booking => Assert.Equal((byte)BookingModality.Fixed, booking.Modality));
        Assert.All(annotations, booking => Assert.Equal((byte)BookingStatus.Pending, booking.Status));

        output.WriteLine("Race assertions passed: exactly one session for the grid slot and one fixed annotation per student after two concurrent confirms.");
    }

    private sealed record ConfirmOutcome(int CreatedSessions, int CreatedFixedBookings);

    private static SqlTarget ValidateTarget(string suppliedConnectionString, string testSuffix)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("WORKUP_T13_SQL_CONNECTION could not be parsed; no SQL connection was opened.");
        }

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T13_SQL_CONNECTION and
        // the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT13Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
        if (!string.Equals(supplied.DataSource, Server, StringComparison.OrdinalIgnoreCase)
            || !supplied.IntegratedSecurity
            || !safeName
            || !string.IsNullOrEmpty(supplied.UserID)
            || !string.IsNullOrEmpty(supplied.Password)
            || !string.IsNullOrEmpty(supplied.AttachDBFilename)
            || supplied.UserInstance
            || !string.IsNullOrEmpty(supplied.FailoverPartner))
        {
            throw new InvalidOperationException(
                "WORKUP_T13_SQL_CONNECTION must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT13Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T13 SQL integration test",
            TrustServerCertificate = true
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<int> CreateTargetAsync(SqlTarget target)
    {
        var startInfo = new ProcessStartInfo("sqlcmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-I", "-S", Server, "-d", "master", "-E", "-b", "-h", "-1", "-W", "-Q",
                     $"SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'{target.DatabaseName}') IS NULL THEN 0 ELSE 1 END;"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("sqlcmd preflight unavailable.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"master preflight failed: {stderr.Trim()}");
        Assert.Equal("0", stdout.Trim());

        // SQL identifiers cannot be parameters. The strict allowlist above excludes brackets and quotes, so quoting
        // the identifier keeps CREATE DATABASE bound to exactly this target, and a competing creator fails loudly
        // instead of letting this run migrate someone else's database.
        var masterConnectionString = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{target.DatabaseName}]", master);
            await create.ExecuteNonQueryAsync();
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        return scripts.Count;
    }

    private static async Task<WeekIds> SeedWeekAsync(string connectionString, FakeClock clock)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var now = clock.Now;

        var businessA = await InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @createdAt)",
            ("@name", "Synthetic T13 Business"), ("@slug", "synthetic-t13-week-generation"),
            ("@phone", "0000000000"), ("@email", "t13.business@example.invalid"), ("@createdAt", now));
        var businessB = await InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @createdAt)",
            ("@name", "Synthetic T13 Other"), ("@slug", "synthetic-t13-other"),
            ("@phone", "0000000000"), ("@email", "t13.other@example.invalid"), ("@createdAt", now));

        var serviceA = await InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, @capacity)",
            ("@businessId", businessA), ("@name", "Synthetic T13 Class"), ("@price", 100.00m), ("@duration", 60), ("@capacity", 8));
        var workerA = await InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", businessA), ("@firstName", "Synthetic"), ("@lastName", "Teacher"),
            ("@phone", "0000000000"), ("@cuil", "20131313131"));

        var clientsA = new int[8];
        for (var index = 0; index < clientsA.Length; index++)
        {
            clientsA[index] = await InsertIdAsync(connection, transaction,
                "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
                ("@businessId", businessA), ("@firstName", "Client"), ("@lastName", $"T13 {index + 1}"),
                ("@email", $"t13.client{index + 1}@example.invalid"), ("@phone", "0000000000"), ("@createdAt", now));
        }

        async Task<int> GridClass(int gridBusinessId, int serviceId, int workerId, DayOfWeek day, TimeOnly start, bool isActive) =>
            await InsertIdAsync(connection, transaction,
                "INSERT INTO GridClasses (BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime, IsActive) OUTPUT INSERTED.Id VALUES (@businessId, @serviceId, @workerId, @dayOfWeek, @startTime, @isActive)",
                ("@businessId", gridBusinessId), ("@serviceId", serviceId), ("@workerId", workerId),
                ("@dayOfWeek", (byte)day), ("@startTime", start), ("@isActive", isActive));

        var gridMonday = await GridClass(businessA, serviceA, workerA, DayOfWeek.Monday, new TimeOnly(10, 0), isActive: true);
        var gridTuesday = await GridClass(businessA, serviceA, workerA, DayOfWeek.Tuesday, new TimeOnly(11, 0), isActive: true);
        var gridWednesday = await GridClass(businessA, serviceA, workerA, DayOfWeek.Wednesday, new TimeOnly(12, 0), isActive: true);
        var gridThursday = await GridClass(businessA, serviceA, workerA, DayOfWeek.Thursday, new TimeOnly(13, 0), isActive: true);
        var gridInactive = await GridClass(businessA, serviceA, workerA, DayOfWeek.Friday, new TimeOnly(14, 0), isActive: false);

        async Task<int> Enrollment(int clientId, EnrollmentStatus status, int gridClassId, DateOnly validFrom, DateOnly? validTo)
        {
            var enrollmentId = await InsertIdAsync(connection, transaction,
                "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @createdAt)",
                ("@businessId", businessA), ("@clientId", clientId), ("@frequency", 1), ("@status", status),
                ("@price", 100.00m), ("@createdAt", now));
            await ExecuteAsync(connection, transaction,
                "INSERT INTO EnrollmentFixedClasses (EnrollmentId, GridClassId, ValidFrom, ValidTo) VALUES (@enrollmentId, @gridClassId, @validFrom, @validTo)",
                ("@enrollmentId", enrollmentId), ("@gridClassId", gridClassId),
                ("@validFrom", validFrom), ("@validTo", validTo));
            return enrollmentId;
        }

        async Task Membership(int enrollmentId, DateOnly coverageStart, DateOnly expiresOn)
        {
            var paymentId = await InsertIdAsync(connection, transaction,
                "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, NULL, @concept, NULL, @enrollmentId, @amount, @paidAt, @recordedAt)",
                ("@businessId", businessA), ("@concept", PaymentConcept.Membership), ("@enrollmentId", enrollmentId),
                ("@amount", 100.00m), ("@paidAt", now), ("@recordedAt", now));
            await ExecuteAsync(connection, transaction,
                "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
                ("@enrollmentId", enrollmentId), ("@coverageStart", coverageStart), ("@expiresOn", expiresOn),
                ("@paymentId", paymentId), ("@createdAt", now));
        }

        var en1 = await Enrollment(clientsA[0], EnrollmentStatus.Active, gridMonday, new DateOnly(2026, 1, 1), null);
        var en2 = await Enrollment(clientsA[1], EnrollmentStatus.Active, gridTuesday, new DateOnly(2026, 1, 1), null);
        var en3 = await Enrollment(clientsA[2], EnrollmentStatus.PendingPayment, gridTuesday, WeekStart.AddDays(-3), null);
        var en4 = await Enrollment(clientsA[3], EnrollmentStatus.Active, gridWednesday, new DateOnly(2026, 1, 1), null);
        var en5 = await Enrollment(clientsA[4], EnrollmentStatus.Active, gridWednesday, new DateOnly(2026, 1, 1), null);
        var en6 = await Enrollment(clientsA[5], EnrollmentStatus.Active, gridThursday, new DateOnly(2026, 1, 1), null);
        var en7 = await Enrollment(clientsA[6], EnrollmentStatus.Active, gridThursday, new DateOnly(2026, 1, 1), null);
        var en8 = await Enrollment(clientsA[7], EnrollmentStatus.Active, gridMonday, new DateOnly(2026, 1, 1), WeekStart.AddDays(-1));

        await Membership(en1, new DateOnly(2026, 2, 9), new DateOnly(2026, 3, 20)); // covers the Monday session
        await Membership(en2, new DateOnly(2026, 1, 9), new DateOnly(2026, 2, 9)); // expired: pending renewal, still annotated
        await Membership(en4, new DateOnly(2026, 2, 9), new DateOnly(2026, 3, 20));
        await Membership(en5, new DateOnly(2026, 2, 9), new DateOnly(2026, 3, 20));
        await Membership(en6, new DateOnly(2026, 2, 9), new DateOnly(2026, 3, 20));
        await Membership(en7, new DateOnly(2026, 2, 9), new DateOnly(2026, 3, 20));

        async Task<int> ClassSession(int sessionBusinessId, int serviceId, int workerId, int gridClassId, DateTime startsAt, AppointmentStatus status) =>
            await InsertIdAsync(connection, transaction,
                "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, @gridClassId, @startsAt, @endsAt, @capacity, NULL, @status, @createdAt)",
                ("@businessId", sessionBusinessId), ("@kind", AppointmentKind.ClassSession), ("@serviceId", serviceId),
                ("@workerId", workerId), ("@gridClassId", gridClassId), ("@startsAt", startsAt),
                ("@endsAt", startsAt.AddMinutes(60)), ("@capacity", 8), ("@status", status), ("@createdAt", now));

        async Task<int> FixedBooking(int bookingBusinessId, int appointmentId, int clientId, int enrollmentId, BookingStatus status, DateTime? cancelledAt) =>
            await InsertIdAsync(connection, transaction,
                "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, @createdAt, @cancelledAt)",
                ("@businessId", bookingBusinessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
                ("@enrollmentId", enrollmentId), ("@modality", BookingModality.Fixed), ("@status", status),
                ("@createdAt", now), ("@cancelledAt", cancelledAt));

        // Wednesday: an existing suspended session that already has one confirmed annotation.
        var suspendedSession = await ClassSession(businessA, serviceA, workerA, gridWednesday,
            WeekStart.AddDays(2).ToDateTime(new TimeOnly(12, 0)), AppointmentStatus.Suspended);
        await FixedBooking(businessA, suspendedSession, clientsA[3], en4, BookingStatus.Confirmed, null);

        // Thursday: an existing scheduled session with a cancelled fixed booking, which must never be resurrected.
        var activeSession = await ClassSession(businessA, serviceA, workerA, gridThursday,
            WeekStart.AddDays(3).ToDateTime(new TimeOnly(13, 0)), AppointmentStatus.Scheduled);
        await FixedBooking(businessA, activeSession, clientsA[5], en6, BookingStatus.Cancelled, now);

        // The other tenant: its own grid class, session and booking, none of which business A may touch.
        var serviceB = await InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, @capacity)",
            ("@businessId", businessB), ("@name", "Synthetic T13 Other Class"), ("@price", 100.00m), ("@duration", 60), ("@capacity", 8));
        var workerB = await InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", businessB), ("@firstName", "Other"), ("@lastName", "Teacher"),
            ("@phone", "0000000000"), ("@cuil", "20999999999"));
        var clientB = await InsertIdAsync(connection, transaction,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
            ("@businessId", businessB), ("@firstName", "Other"), ("@lastName", "Client"),
            ("@email", "t13.other.client@example.invalid"), ("@phone", "0000000000"), ("@createdAt", now));
        var gridB = await GridClass(businessB, serviceB, workerB, DayOfWeek.Monday, new TimeOnly(10, 0), isActive: true);
        var appointmentB = await ClassSession(businessB, serviceB, workerB, gridB,
            WeekStart.ToDateTime(new TimeOnly(10, 0)), AppointmentStatus.Scheduled);
        await InsertIdAsync(connection, transaction,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, NULL, @modality, @status, @createdAt)",
            ("@businessId", businessB), ("@appointmentId", appointmentB), ("@clientId", clientB),
            ("@modality", BookingModality.DropIn), ("@status", BookingStatus.Pending), ("@createdAt", now));

        await transaction.CommitAsync();

        return new WeekIds(
            BusinessA: businessA,
            BusinessB: businessB,
            GridClassMonday: gridMonday,
            GridClassTuesday: gridTuesday,
            GridClassWednesday: gridWednesday,
            GridClassThursday: gridThursday,
            GridClassInactive: gridInactive,
            SuspendedSessionId: suspendedSession,
            ActiveSessionId: activeSession,
            BusinessBAppointmentId: appointmentB,
            En1: en1, En2: en2, En3: en3, En4: en4, En5: en5, En6: en6, En7: en7, En8: en8,
            Cl1: clientsA[0], Cl8: clientsA[7]);
    }

    private static async Task<RaceIds> SeedRaceAsync(string connectionString, FakeClock clock)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var now = clock.Now;

        var business = await InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @createdAt)",
            ("@name", "Synthetic T13 Race Business"), ("@slug", "synthetic-t13-week-generation-race"),
            ("@phone", "0000000000"), ("@email", "t13.race.business@example.invalid"), ("@createdAt", now));
        var service = await InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, @capacity)",
            ("@businessId", business), ("@name", "Synthetic T13 Race Class"), ("@price", 100.00m), ("@duration", 60), ("@capacity", 8));
        var worker = await InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", business), ("@firstName", "Race"), ("@lastName", "Teacher"),
            ("@phone", "0000000000"), ("@cuil", "20141414141"));
        var gridClass = await InsertIdAsync(connection, transaction,
            "INSERT INTO GridClasses (BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime, IsActive) OUTPUT INSERTED.Id VALUES (@businessId, @serviceId, @workerId, @dayOfWeek, @startTime, 1)",
            ("@businessId", business), ("@serviceId", service), ("@workerId", worker),
            ("@dayOfWeek", (byte)DayOfWeek.Monday), ("@startTime", new TimeOnly(10, 0)));

        var clients = new int[2];
        var enrollments = new int[2];
        for (var index = 0; index < clients.Length; index++)
        {
            clients[index] = await InsertIdAsync(connection, transaction,
                "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
                ("@businessId", business), ("@firstName", "Race"), ("@lastName", $"Client {index + 1}"),
                ("@email", $"t13.race.client{index + 1}@example.invalid"), ("@phone", "0000000000"), ("@createdAt", now));
            enrollments[index] = await InsertIdAsync(connection, transaction,
                "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @createdAt)",
                ("@businessId", business), ("@clientId", clients[index]), ("@frequency", 1),
                ("@status", EnrollmentStatus.Active), ("@price", 100.00m), ("@createdAt", now));
            await ExecuteAsync(connection, transaction,
                "INSERT INTO EnrollmentFixedClasses (EnrollmentId, GridClassId, ValidFrom, ValidTo) VALUES (@enrollmentId, @gridClassId, @validFrom, NULL)",
                ("@enrollmentId", enrollments[index]), ("@gridClassId", gridClass), ("@validFrom", new DateOnly(2026, 1, 1)));
            var paymentId = await InsertIdAsync(connection, transaction,
                "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, NULL, @concept, NULL, @enrollmentId, @amount, @paidAt, @recordedAt)",
                ("@businessId", business), ("@concept", PaymentConcept.Membership), ("@enrollmentId", enrollments[index]),
                ("@amount", 100.00m), ("@paidAt", now), ("@recordedAt", now));
            await ExecuteAsync(connection, transaction,
                "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
                ("@enrollmentId", enrollments[index]), ("@coverageStart", new DateOnly(2026, 2, 9)),
                ("@expiresOn", new DateOnly(2026, 3, 20)), ("@paymentId", paymentId), ("@createdAt", now));
        }

        await transaction.CommitAsync();

        return new RaceIds(
            BusinessA: business,
            GridClassId: gridClass,
            En1: enrollments[0],
            En2: enrollments[1],
            Cl1: clients[0],
            Cl2: clients[1]);
    }

    private static async Task<DatabaseState> ReadStateAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var appointments = new List<AppointmentRow>();
        await using (var command = new SqlCommand(
                         "SELECT Id, BusinessId, GridClassId, StartsAt, EndsAt, Status, MaxCapacity FROM Appointments ORDER BY Id",
                         connection))
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                appointments.Add(new AppointmentRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.GetDateTime(3),
                    reader.GetDateTime(4),
                    reader.GetByte(5),
                    reader.GetInt32(6)));
            }
        }

        var bookings = new List<BookingRow>();
        await using (var command = new SqlCommand(
                         "SELECT Id, BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status FROM Bookings ORDER BY Id",
                         connection))
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                bookings.Add(new BookingRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetByte(5),
                    reader.GetByte(6)));
            }
        }

        return new DatabaseState(appointments.ToArray(), bookings.ToArray());
    }

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static SqlCommand Create(
        SqlConnection connection, SqlTransaction transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            var actual = value is Enum enumeration ? Convert.ToByte(enumeration, CultureInfo.InvariantCulture) : value;
            if (actual is DateTime dateTime) command.Parameters.Add(name, SqlDbType.DateTime2).Value = dateTime;
            else if (actual is DateOnly date) command.Parameters.Add(name, SqlDbType.Date).Value = date.ToDateTime(TimeOnly.MinValue);
            else command.Parameters.AddWithValue(name, actual ?? DBNull.Value);
        }

        return command;
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record WeekIds(
        int BusinessA,
        int BusinessB,
        int GridClassMonday,
        int GridClassTuesday,
        int GridClassWednesday,
        int GridClassThursday,
        int GridClassInactive,
        int SuspendedSessionId,
        int ActiveSessionId,
        int BusinessBAppointmentId,
        int En1, int En2, int En3, int En4, int En5, int En6, int En7, int En8,
        int Cl1, int Cl8);

    private sealed record RaceIds(int BusinessA, int GridClassId, int En1, int En2, int Cl1, int Cl2);

    private sealed record AppointmentRow(
        int Id, int BusinessId, int? GridClassId, DateTime StartsAt, DateTime EndsAt, byte Status, int MaxCapacity);

    private sealed record BookingRow(
        int Id, int BusinessId, int AppointmentId, int ClientId, int? EnrollmentId, byte? Modality, byte Status);

    private sealed record DatabaseState(AppointmentRow[] Appointments, BookingRow[] Bookings);
}
