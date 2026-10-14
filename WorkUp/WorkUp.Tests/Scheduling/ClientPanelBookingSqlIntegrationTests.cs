using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Data;
using WorkUp.Data.Classes;
using WorkUp.Data.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// Opt-in real-SQL coverage for both client-panel booking paths of T21.5 (RF-07, RF-08, RF-22):
/// <see cref="BookingService.BookIndividualForClientAsync"/> (the authenticated individual reservation) and
/// <see cref="ClassDropInService.BookForClientAsync"/> (the drop-in booking into an already generated class session).
/// It requires <c>WORKUP_T21_PANEL_BOOKING_SQL_CONNECTION</c> pointing at a brand-new, dedicated
/// <c>(localdb)\MSSQLLocalDB</c> database whose name starts with <c>WorkUpDemoT21PanelBookingSql</c>. Each fact appends
/// its own suffix so the whole class runs with a single command; the preflight asserts <c>DB_ID</c> is absent before
/// migrating, and the created databases are permanently retained, never reused and never dropped. Without the variable
/// every fact reports N/A and opens no connection.
/// </summary>
public sealed class ClientPanelBookingSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T21_PANEL_BOOKING_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT21PanelBookingSql";
    private static readonly Regex SafeDatabaseName =
        new(@"\AWorkUpDemoT21PanelBookingSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    /// <summary>Wednesday 2026-03-11: the day the seeded worker schedule covers for the individual path.</summary>
    private static readonly DateOnly PanelDate = new(2026, 3, 11);
    private static readonly TimeOnly OwnStart = new(10, 0);
    private static readonly TimeOnly PublicStart = new(11, 0);

    /// <summary>A free slot that the guards reach after availability, so their client resolution is the failing step.</summary>
    private static readonly TimeOnly GuardStart = new(14, 0);

    private const decimal IndividualPrice = 5000m;
    private const string OwnEmail = "t21.panel.own@example.invalid";

    [Fact]
    public async Task Individual_own_booking_persists_a_pending_turn_with_link_and_notice_and_guards_write_nothing()
    {
        var target = await PrepareAsync("_Individual");
        if (target is null)
            return;

        var ids = await SeedIndividualAsync(target.ConnectionString);
        await using var session = new DbSession(target.ConnectionString);
        var urlBuilder = new FixedAppointmentUrlBuilder();
        var service = new BookingService(
            new SqlPublicBookingRepository(session), session, new PassthroughLinkTokenProtector(), urlBuilder,
            new FakeClock { Now = Now });

        var clientsBefore = await CountClientsAsync(target.ConnectionString, ids.BusinessA);

        var own = await service.BookIndividualForClientAsync(
            ids.BusinessA, ids.OwnClientA, ids.IndividualServiceA, ids.WorkerA, PanelDate, OwnStart);
        Assert.True(own.Succeeded, own.Error?.Code ?? "the SQL individual booking did not succeed.");
        var confirmation = own.Confirmation;
        Assert.Equal(IndividualPrice, confirmation.AgreedPrice);
        Assert.Equal(PanelDate.ToDateTime(OwnStart), confirmation.StartsAt);
        Assert.Equal(PanelDate.ToDateTime(OwnStart).AddMinutes(30), confirmation.EndsAt);

        var appointment = await ReadAppointmentAsync(target.ConnectionString, confirmation.AppointmentId);
        Assert.Equal(ids.BusinessA, appointment.BusinessId);
        Assert.Equal(AppointmentKind.Individual, appointment.Kind);
        Assert.Equal(AppointmentOrigin.Reserved, appointment.Origin);
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
        Assert.Equal(1, appointment.MaxCapacity);
        Assert.Equal(ids.IndividualServiceA, appointment.ServiceId);
        Assert.Equal(ids.WorkerA, appointment.WorkerId);
        Assert.Equal(PanelDate.ToDateTime(OwnStart), appointment.StartsAt);
        Assert.Equal(PanelDate.ToDateTime(OwnStart).AddMinutes(30), appointment.EndsAt);

        // The exact cookie clientId is the booking's ficha, the turn is Pending and carries the negotiated price.
        var booking = await ReadBookingRowAsync(target.ConnectionString, confirmation.BookingId);
        Assert.Equal(ids.BusinessA, booking.BusinessId);
        Assert.Equal(confirmation.AppointmentId, booking.AppointmentId);
        Assert.Equal(ids.OwnClientA, booking.ClientId);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Null(booking.Modality);
        Assert.Null(booking.EnrollmentId);
        Assert.Equal(IndividualPrice, booking.AgreedPrice);
        Assert.Equal(Now, booking.CreatedAt);
        Assert.Null(booking.CancelledAt);

        // One private link: a 32-byte SHA-256 token hash that matches the protected token, expiring 24h after start.
        var link = await ReadLinkAsync(target.ConnectionString, confirmation.BookingId);
        Assert.Equal(confirmation.BookingId, link.BookingId);
        Assert.Equal(32, link.TokenHash.Length);
        Assert.Equal(PanelDate.ToDateTime(OwnStart).AddHours(24), link.ExpiresAt);
        Assert.Equal(Now, link.CreatedAt);
        Assert.Equal(SecureToken.Hash(PassthroughLinkTokenProtector.TokenOf(link.ProtectedToken)), link.TokenHash);

        // The notice goes to the stored ficha email, not the cookie, and uses the booking dedup key.
        var notice = await ReadNotificationAsync(target.ConnectionString, $"booking:{confirmation.BookingId}");
        Assert.Equal(NotificationType.BookingCreated, notice.Type);
        Assert.Equal(OwnEmail, notice.Recipient);
        Assert.Equal($"booking:{confirmation.BookingId}", notice.DedupKey);

        // The panel path never creates a client: the stored ficha was used in place.
        Assert.Equal(clientsBefore, await CountClientsAsync(target.ConnectionString, ids.BusinessA));

        var afterOwn = await ReadWriteCountsAsync(target.ConnectionString, ids.BusinessA);

        // A ficha of another business and a missing clientId fail with the generic code and write no appointment,
        // booking, link or notice.
        var crossTenant = await service.BookIndividualForClientAsync(
            ids.BusinessA, ids.CrossTenantClientB, ids.IndividualServiceA, ids.WorkerA, PanelDate, GuardStart);
        Assert.False(crossTenant.Succeeded);
        Assert.Equal("client_booking_not_found", crossTenant.Error!.Code);

        var missing = await service.BookIndividualForClientAsync(
            ids.BusinessA, int.MaxValue, ids.IndividualServiceA, ids.WorkerA, PanelDate, GuardStart);
        Assert.False(missing.Succeeded);
        Assert.Equal("client_booking_not_found", missing.Error!.Code);

        Assert.Equal(afterOwn, await ReadWriteCountsAsync(target.ConnectionString, ids.BusinessA));

        // Regression of the public email form, which shares the core but resolves or creates the ficha by email.
        var publicAttempt = await service.BookIndividualAsync(ids.BusinessA, new BookIndividualAppointmentRequest(
            ids.IndividualServiceA, ids.WorkerA, PanelDate, PublicStart, " T21.Public@Example.invalid ", "Nueva", "Publica"));
        Assert.True(publicAttempt.Succeeded, publicAttempt.Error?.Code ?? "the public regression booking did not succeed.");
        var publicBooking = await ReadBookingRowAsync(target.ConnectionString, publicAttempt.Confirmation.BookingId);
        Assert.Equal(BookingStatus.Pending, publicBooking.Status);
        Assert.NotEqual(ids.OwnClientA, publicBooking.ClientId);
        var created = await ReadClientAsync(target.ConnectionString, ids.BusinessA, "t21.public@example.invalid");
        Assert.NotNull(created);
        Assert.Equal(created!.Id, publicBooking.ClientId);
        Assert.Equal(BookingStatus.Pending, publicBooking.Status);
        var publicNotice = await ReadNotificationAsync(
            target.ConnectionString, $"booking:{publicAttempt.Confirmation.BookingId}");
        Assert.Equal("t21.public@example.invalid", publicNotice.Recipient);
        Assert.Equal(clientsBefore + 1, await CountClientsAsync(target.ConnectionString, ids.BusinessA));

        output.WriteLine(
            "Individual assertions passed: the cookie ficha produced a Pending turn with the negotiated price, one " +
            "32-byte link token hash matching its protected token and a 24-hour expiry, and one notice addressed to the " +
            "stored email; the foreign and missing fichas wrote nothing, and the public form still creates its client.");
    }

    [Fact]
    public async Task Class_own_booking_persists_a_pending_drop_in_without_a_client_and_rejects_foreign_and_duplicate()
    {
        var target = await PrepareAsync("_Class");
        if (target is null)
            return;

        var ids = await SeedClassAsync(target.ConnectionString);
        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassDropInService(new SqlClassDropInRepository(session), session, new FakeClock { Now = Now });

        var clientsBefore = await CountClientsAsync(target.ConnectionString, ids.BusinessA);
        var paymentsBefore = await ReadPaymentsAsync(target.ConnectionString);

        var own = await service.BookForClientAsync(ids.BusinessA, ids.SessionA, ids.OwnClientA);
        Assert.True(own.Succeeded, own.Error?.Code ?? "the SQL drop-in booking did not succeed.");
        Assert.Equal(ids.SessionA, own.Value.AppointmentId);
        Assert.Equal(ids.OwnClientA, own.Value.ClientId);

        var booking = await ReadBookingRowAsync(target.ConnectionString, own.Value.BookingId);
        Assert.Equal(ids.BusinessA, booking.BusinessId);
        Assert.Equal(ids.SessionA, booking.AppointmentId);
        Assert.Equal(ids.OwnClientA, booking.ClientId);
        Assert.Equal(BookingModality.DropIn, booking.Modality);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Null(booking.EnrollmentId);
        Assert.Null(booking.AgreedPrice);
        Assert.Equal(Now, booking.CreatedAt);
        Assert.Null(booking.CancelledAt);

        // The class starts within 24h, so the immediate drop-in reminder is queued once with its booking dedup key.
        Assert.True(own.Value.ImmediateEmailQueued);
        var reminder = await ReadNotificationAsync(target.ConnectionString, $"reminder:booking:{own.Value.BookingId}");
        Assert.Equal(NotificationType.DropInReminder, reminder.Type);
        Assert.Equal(OwnEmail, reminder.Recipient);

        // No new client: the existing ficha of the business was used.
        Assert.Equal(clientsBefore, await CountClientsAsync(target.ConnectionString, ids.BusinessA));

        // The same ficha cannot hold a second active booking of the session.
        var duplicate = await service.BookForClientAsync(ids.BusinessA, ids.SessionA, ids.OwnClientA);
        Assert.False(duplicate.Succeeded);
        Assert.Equal("drop_in_already_booked", duplicate.Error!.Code);

        // A client of another tenant and a session of another tenant are invisible and write nothing.
        var foreignClient = await service.BookForClientAsync(ids.BusinessA, ids.SessionA, ids.CrossTenantClientB);
        Assert.False(foreignClient.Succeeded);
        Assert.Equal("drop_in_client_not_found", foreignClient.Error!.Code);
        var foreignSession = await service.BookForClientAsync(ids.BusinessA, ids.SessionB, ids.OwnClientA);
        Assert.False(foreignSession.Succeeded);
        Assert.Equal("drop_in_not_found", foreignSession.Error!.Code);
        var missingClient = await service.BookForClientAsync(ids.BusinessA, ids.SessionA, int.MaxValue);
        Assert.False(missingClient.Succeeded);
        Assert.Equal("drop_in_client_not_found", missingClient.Error!.Code);

        // Exactly one active drop-in of this ficha on the session, and the recorded payment is untouched.
        Assert.Equal(1, await CountAsync(
            target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND ClientId = @clientId AND Status <> @cancelled",
            ("@appointmentId", ids.SessionA), ("@clientId", ids.OwnClientA), ("@cancelled", BookingStatus.Cancelled)));
        Assert.Equal(paymentsBefore, await ReadPaymentsAsync(target.ConnectionString));

        output.WriteLine(
            "Class assertions passed: the existing session took a Pending Suelta booking for the stored ficha with a " +
            "single reminder, no client was created, the second booking was refused, the foreign client and session " +
            "were invisible, and payments were unchanged.");
    }

    [Fact]
    public async Task Class_last_place_race_leaves_exactly_one_winner_and_never_overbooks()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        var ids = await SeedRaceAsync(target.ConnectionString);

        // A bare Task.WhenAll would not force real overlap: the barrier pins both bookings on their own thread and
        // releases them at the same instant, right before each opens its transaction and asks for the session's
        // UPDLOCK. That is what proves the lock serializes the count and the guarded insert for the last place.
        using var startGate = new Barrier(participantCount: 2);

        Task<Result<DropInBookingOutcome>> BookAsync(int clientId) => Task.Run(async () =>
        {
            await using var raceSession = new DbSession(target.ConnectionString);
            var service = new ClassDropInService(
                new SqlClassDropInRepository(raceSession), raceSession, new FakeClock { Now = Now });
            startGate.SignalAndWait();
            return await service.BookForClientAsync(ids.BusinessA, ids.SessionA, clientId);
        });

        var results = await Task.WhenAll(BookAsync(ids.ClientIds[0]), BookAsync(ids.ClientIds[1]));

        var winner = Assert.Single(results, result => result.Succeeded);
        var loser = Assert.Single(results, result => !result.Succeeded);
        Assert.Equal("drop_in_full", loser.Error!.Code);

        var winningRow = await ReadBookingRowAsync(target.ConnectionString, winner.Value.BookingId);
        Assert.Equal(ids.BusinessA, winningRow.BusinessId);
        Assert.Equal(ids.SessionA, winningRow.AppointmentId);
        Assert.Contains(winningRow.ClientId, ids.ClientIds);
        Assert.Equal(BookingModality.DropIn, winningRow.Modality);
        Assert.Equal(BookingStatus.Pending, winningRow.Status);

        var total = await CountAsync(
            target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId",
            ("@appointmentId", ids.SessionA));
        var active = await CountAsync(
            target.ConnectionString,
            "SELECT COUNT(*) FROM Bookings WHERE AppointmentId = @appointmentId AND Status <> @cancelled",
            ("@appointmentId", ids.SessionA), ("@cancelled", BookingStatus.Cancelled));
        Assert.Equal(1, total);
        Assert.Equal(1, active);
        Assert.True(active <= ids.SessionCapacity, $"The class holds {active} active bookings over capacity {ids.SessionCapacity}.");

        output.WriteLine(
            "Race assertions passed: two concurrent bookings for the last place ended with exactly one winner and one " +
            "drop_in_full rejection, and the session keeps a single active booking within its capacity.");
    }

    /// <summary>Validates the opt-in connection, derives a fresh per-test database, proves it is absent, and migrates it.</summary>
    private async Task<SqlTarget?> PrepareAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"N/A: {EnvVar} is unset; no SQL connection was opened.");
            return null;
        }

        var target = ValidateTarget(supplied, testSuffix);
        output.WriteLine($"SQL Server LocalDB target: {target.DatabaseName}");

        var master = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb, InitialCatalog = "master", IntegratedSecurity = true,
            ConnectTimeout = 15, TrustServerCertificate = true
        };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@database)", connection);
            check.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = target.DatabaseName;
            var existing = await check.ExecuteScalarAsync();
            output.WriteLine(
                $"master DB_ID('{target.DatabaseName}') = {(existing is null or DBNull ? "NULL (absent)" : "present")}");
            Assert.True(existing is null or DBNull,
                $"Refusing existing database {target.DatabaseName}; the target must be new and is never reused or dropped.");
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        output.WriteLine(
            $"Applied {scripts.Count} migration script(s): {string.Join(", ", scripts)}; the database is retained permanently.");
        return target;
    }

    /// <summary>
    /// Derives this test's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog, so
    /// one <c>WORKUP_T21_PANEL_BOOKING_SQL_CONNECTION</c> value serves every fact. Only the validated local instance
    /// and the derived database survive into the connection string.
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
            throw new InvalidOperationException(
                $"{EnvVar} could not be parsed; no SQL connection was opened.");
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
                $"Requires a {DatabasePrefix}* LocalDB database with integrated security; credentials, attached files and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T21.5 panel booking SQL integration test",
            TrustServerCertificate = true
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<IndividualIds> SeedIndividualAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var businessA = await InsertAsync(connection,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @now)",
            ("@name", "Synthetic T21 Panel Individual"), ("@slug", "synthetic-t21-panel-individual"), ("@now", Now));
        var businessB = await InsertAsync(connection,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @now)",
            ("@name", "Synthetic T21 Panel Individual Other"), ("@slug", "synthetic-t21-panel-individual-other"), ("@now", Now));

        // An individual service (capacity 1) and a class service with capacity, so both shaped services exist.
        var individualServiceA = await InsertAsync(connection,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Individual', @price, 30, 1)",
            ("@business", businessA), ("@price", IndividualPrice));
        await InsertAsync(connection,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Class', 1200.00, 60, 8)",
            ("@business", businessA));

        var workerA = await InsertAsync(connection,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Worker', '0000000000', @cuil)",
            ("@business", businessA), ("@cuil", "20333333001"));

        // A future worker schedule covering the requested day, 09:00 to 17:00.
        await InsertAsync(connection,
            "INSERT INTO WorkerSchedules (WorkerId, DayOfWeek, StartTime, EndTime) VALUES (@worker, @day, '09:00', '17:00')",
            ("@worker", workerA), ("@day", (byte)PanelDate.DayOfWeek));

        var ownClientA = await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Sol', 'Panel', @email, @now)",
            ("@business", businessA), ("@email", OwnEmail), ("@now", Now));
        await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Otra', 'Ficha', @email, @now)",
            ("@business", businessA), ("@email", "t21.panel.other@example.invalid"), ("@now", Now));
        var crossTenantClientB = await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Ajena', 'Ficha', @email, @now)",
            ("@business", businessB), ("@email", "t21.panel.foreign@example.invalid"), ("@now", Now));

        return new IndividualIds(businessA, businessB, individualServiceA, workerA, ownClientA, crossTenantClientB);
    }

    private static async Task<ClassIds> SeedClassAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var businessA = await InsertAsync(connection,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @now)",
            ("@name", "Synthetic T21 Panel Class"), ("@slug", "synthetic-t21-panel-class"), ("@now", Now));
        var businessB = await InsertAsync(connection,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @now)",
            ("@name", "Synthetic T21 Panel Class Other"), ("@slug", "synthetic-t21-panel-class-other"), ("@now", Now));

        var classServiceA = await InsertAsync(connection,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Class', 1200.00, 60, 8)",
            ("@business", businessA));
        var classServiceB = await InsertAsync(connection,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Class Other', 1200.00, 60, 8)",
            ("@business", businessB));
        var workerA = await InsertAsync(connection,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Teacher', '0000000000', @cuil)",
            ("@business", businessA), ("@cuil", "20333333002"));
        var workerB = await InsertAsync(connection,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Foreign', 'Teacher', '0000000000', @cuil)",
            ("@business", businessB), ("@cuil", "20333333003"));

        // Two future class sessions, capacity 3: the own tenant's and a foreign one that must stay invisible.
        var sessionA = await InsertAsync(connection,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @start, @end, 3, NULL, @status, @now)",
            ("@business", businessA), ("@kind", (byte)AppointmentKind.ClassSession), ("@service", classServiceA),
            ("@worker", workerA), ("@start", Now.AddHours(2)), ("@end", Now.AddHours(3)),
            ("@status", (byte)AppointmentStatus.Scheduled), ("@now", Now));
        var sessionB = await InsertAsync(connection,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @start, @end, 3, NULL, @status, @now)",
            ("@business", businessB), ("@kind", (byte)AppointmentKind.ClassSession), ("@service", classServiceB),
            ("@worker", workerB), ("@start", Now.AddHours(2)), ("@end", Now.AddHours(3)),
            ("@status", (byte)AppointmentStatus.Scheduled), ("@now", Now));

        var ownClientA = await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Sol', 'Panel', @email, @now)",
            ("@business", businessA), ("@email", OwnEmail), ("@now", Now));
        var otherClientA = await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Otra', 'Ficha', @email, @now)",
            ("@business", businessA), ("@email", "t21.panel.class.other@example.invalid"), ("@now", Now));
        var crossTenantClientB = await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Ajena', 'Ficha', @email, @now)",
            ("@business", businessB), ("@email", "t21.panel.class.foreign@example.invalid"), ("@now", Now));

        // A pre-existing drop-in and its recorded payment: the new booking must not touch either.
        var existingBooking = await InsertAsync(connection,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, NULL, @modality, @status, @now)",
            ("@business", businessA), ("@appointment", sessionA), ("@client", otherClientA),
            ("@modality", (byte)BookingModality.DropIn), ("@status", (byte)BookingStatus.Pending), ("@now", Now));
        await InsertAsync(connection,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@business, @client, @concept, @booking, NULL, 1200.00, 1200.00, @now, @now)",
            ("@business", businessA), ("@client", otherClientA), ("@concept", (byte)PaymentConcept.DropInClass),
            ("@booking", existingBooking), ("@now", Now));

        return new ClassIds(businessA, businessB, ownClientA, otherClientA, crossTenantClientB, sessionA, sessionB, existingBooking);
    }

    private static async Task<RaceIds> SeedRaceAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var businessA = await InsertAsync(connection,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @now)",
            ("@name", "Synthetic T21 Panel Race"), ("@slug", "synthetic-t21-panel-race"), ("@now", Now));
        var classServiceA = await InsertAsync(connection,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Race Class', 1200.00, 60, 8)",
            ("@business", businessA));
        var workerA = await InsertAsync(connection,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Race', 'Teacher', '0000000000', @cuil)",
            ("@business", businessA), ("@cuil", "20333333004"));

        // One single place for two fichas to contend for.
        var session = await InsertAsync(connection,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @start, @end, 1, NULL, @status, @now)",
            ("@business", businessA), ("@kind", (byte)AppointmentKind.ClassSession), ("@service", classServiceA),
            ("@worker", workerA), ("@start", Now.AddHours(2)), ("@end", Now.AddHours(3)),
            ("@status", (byte)AppointmentStatus.Scheduled), ("@now", Now));

        var firstClient = await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Race', 'One', @email, @now)",
            ("@business", businessA), ("@email", "t21.panel.race.one@example.invalid"), ("@now", Now));
        var secondClient = await InsertAsync(connection,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Race', 'Two', @email, @now)",
            ("@business", businessA), ("@email", "t21.panel.race.two@example.invalid"), ("@now", Now));

        return new RaceIds(businessA, session, 1, new[] { firstClient, secondClient });
    }

    private static async Task<int> InsertAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
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

    private static async Task<AppointmentRow> ReadAppointmentAsync(string connectionString, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BusinessId, Kind, ServiceId, WorkerId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt FROM Appointments WHERE Id = @id",
            connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = appointmentId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Appointment {appointmentId} was not found.");
        return new AppointmentRow(
            reader.GetInt32(0),
            (AppointmentKind)reader.GetByte(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetDateTime(4),
            reader.GetDateTime(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : (AppointmentOrigin)reader.GetByte(7),
            (AppointmentStatus)reader.GetByte(8),
            reader.GetDateTime(9));
    }

    private static async Task<BookingRow> ReadBookingRowAsync(string connectionString, int bookingId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, AgreedPrice, CreatedAt, CancelledAt FROM Bookings WHERE Id = @id",
            connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = bookingId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Booking {bookingId} was not found.");
        return new BookingRow(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : (BookingModality)reader.GetByte(4),
            (BookingStatus)reader.GetByte(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8));
    }

    private static async Task<LinkRow> ReadLinkAsync(string connectionString, int bookingId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BookingId, TokenHash, ProtectedToken, ExpiresAt, CreatedAt FROM AppointmentLinks WHERE BookingId = @bookingId ORDER BY Id",
            connection);
        command.Parameters.Add("@bookingId", SqlDbType.Int).Value = bookingId;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<LinkRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new LinkRow(
                reader.GetInt32(0),
                (byte[])reader.GetValue(1),
                reader.GetString(2),
                reader.GetDateTime(3),
                reader.GetDateTime(4)));
        }

        return Assert.Single(rows);
    }

    private static async Task<NotificationRow> ReadNotificationAsync(string connectionString, string dedupKey)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT BusinessId, Type, Recipient, Subject, DedupKey FROM Notifications WHERE DedupKey = @dedupKey",
            connection);
        command.Parameters.Add("@dedupKey", SqlDbType.NVarChar, 150).Value = dedupKey;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Notification '{dedupKey}' was not found.");
        return new NotificationRow(
            reader.GetInt32(0),
            (NotificationType)reader.GetByte(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    private static async Task<ClientRow?> ReadClientAsync(string connectionString, int businessId, string email)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, BusinessId, FirstName, LastName, Email FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            connection);
        command.Parameters.Add("@businessId", SqlDbType.Int).Value = businessId;
        command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = email;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return new ClientRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    private static async Task<IReadOnlyList<PaymentSnapshot>> ReadPaymentsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Id, BookingId, Amount, PaidAt FROM Payments ORDER BY Id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<PaymentSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new PaymentSnapshot(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDecimal(2),
                reader.GetDateTime(3)));
        }

        return rows;
    }

    private static Task<int> CountClientsAsync(string connectionString, int businessId) => CountAsync(
        connectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId", ("@businessId", businessId));

    private static async Task<int> CountAsync(string connectionString, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.Add(Parameter(name, value));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<WriteCounts> ReadWriteCountsAsync(string connectionString, int businessId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId),
                (SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId),
                (SELECT COUNT(*) FROM AppointmentLinks AS l JOIN Bookings AS b ON b.Id = l.BookingId WHERE b.BusinessId = @businessId),
                (SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId)
            """,
            connection);
        command.Parameters.Add("@businessId", SqlDbType.Int).Value = businessId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new WriteCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record IndividualIds(
        int BusinessA, int BusinessB, int IndividualServiceA, int WorkerA, int OwnClientA, int CrossTenantClientB);

    private sealed record ClassIds(
        int BusinessA, int BusinessB, int OwnClientA, int OtherClientA, int CrossTenantClientB,
        int SessionA, int SessionB, int ExistingBooking);

    private sealed record RaceIds(int BusinessA, int SessionA, int SessionCapacity, int[] ClientIds);

    private sealed record AppointmentRow(
        int BusinessId, AppointmentKind Kind, int ServiceId, int WorkerId, DateTime StartsAt, DateTime EndsAt,
        int MaxCapacity, AppointmentOrigin? Origin, AppointmentStatus Status, DateTime CreatedAt);

    private sealed record BookingRow(
        int BusinessId, int AppointmentId, int ClientId, int? EnrollmentId, BookingModality? Modality,
        BookingStatus Status, decimal? AgreedPrice, DateTime CreatedAt, DateTime? CancelledAt);

    private sealed record LinkRow(int BookingId, byte[] TokenHash, string ProtectedToken, DateTime ExpiresAt, DateTime CreatedAt);

    private sealed record NotificationRow(int BusinessId, NotificationType Type, string Recipient, string Subject, string DedupKey);

    private sealed record ClientRow(int Id, int BusinessId, string FirstName, string LastName, string Email);

    private sealed record PaymentSnapshot(int Id, int? BookingId, decimal Amount, DateTime PaidAt);

    private sealed record WriteCounts(int Appointments, int Bookings, int Links, int Notifications);

    /// <summary>Reversible stand-in for the Data Protection protector: the token travels as a prefixed literal.</summary>
    private sealed class PassthroughLinkTokenProtector : ILinkTokenProtector
    {
        private const string Prefix = "protected:";

        public string Protect(string token) => Prefix + token;

        public string Unprotect(string protectedToken) => TokenOf(protectedToken);

        public static string TokenOf(string protectedToken) => protectedToken[Prefix.Length..];
    }

    /// <summary>Fixed base URL so the notice body stays deterministic without the application options.</summary>
    private sealed class FixedAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://workup.test/t/" + token;
    }
}
