using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Data;
using WorkUp.Data.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// Opt-in SQL coverage for the authenticated client reschedule (T21.4, RF-09): the sibling of
/// <see cref="OwnerRescheduleSqlIntegrationTests"/>, but driven through the ficha-scoped
/// <see cref="OwnerRescheduleService.RescheduleOwnIndividualAsync"/> /
/// <see cref="OwnerRescheduleService.GetOwnOptionsAsync"/> entry points. It requires
/// <c>WORKUP_T21_RESCHEDULE_SQL_CONNECTION</c> pointing at a brand-new, dedicated <c>(localdb)\MSSQLLocalDB</c>
/// database whose name starts with <c>WorkUpDemoT21RescheduleSql</c>; every fact appends its own suffix so the whole
/// class runs with one command, the helper preflight asserts <c>DB_ID</c> is absent, and the created databases are
/// permanently retained, never reused and never dropped. Without the variable every fact reports N/A and opens no
/// connection.
/// </summary>
public sealed class ClientRescheduleSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T21_RESCHEDULE_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT21RescheduleSql";
    private static readonly Regex SafeDatabaseName = new(@"\AWorkUpDemoT21RescheduleSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime OwnPendingStart = new(2026, 3, 11, 10, 0, 0);
    private static readonly DateTime OwnPendingNewStart = new(2026, 3, 11, 11, 0, 0);
    private static readonly DateTime OwnConfirmedStart = new(2026, 3, 11, 14, 0, 0);
    private static readonly DateTime OwnConfirmedNewStart = new(2026, 3, 11, 14, 15, 0);
    private static readonly DateTime OccupiedStart = new(2026, 3, 11, 12, 0, 0);
    private static readonly DateTime OwnWorkerTwoStart = new(2026, 3, 11, 10, 0, 0);
    private static readonly DateTime ClassStart = new(2026, 3, 11, 15, 0, 0);
    private static readonly DateTime FixedStart = new(2026, 3, 11, 16, 0, 0);
    private static readonly DateTime OtherTenantStart = new(2026, 3, 11, 10, 0, 0);
    private static readonly DateTime PastStart = new(2026, 3, 10, 8, 0, 0);

    // 13:00 lies inside the primary worker's Wednesday schedule and outside the second worker's, so it is reachable
    // only by leaking availability from a worker the client API never names.
    private static readonly DateTime OtherWorkerOnlySlot = new(2026, 3, 11, 13, 0, 0);
    private static readonly DateOnly RescheduleDate = DateOnly.FromDateTime(OwnPendingStart);

    [Fact]
    public async Task Reschedules_own_pending_turn_confirming_it_and_refreshing_only_the_private_link_expiry()
    {
        var target = await PrepareAsync("_Pending");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);

        var appointmentBefore = await ReadAppointmentRangeAsync(target.ConnectionString, ids.OwnPendingAppointmentId);
        var linkBefore = await ReadLinkAsync(target.ConnectionString, ids.OwnPendingLinkId);
        var paymentsBefore = await ReadPaymentsAsync(target.ConnectionString);
        Assert.Empty(await ReadNotificationTypesAsync(target.ConnectionString));

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        var result = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.OwnPendingBookingId, OwnPendingNewStart);

        Assert.True(result.Succeeded, result.Error?.Code ?? "the SQL client reschedule did not succeed.");
        Assert.Equal(OwnPendingNewStart, result.Confirmation.StartsAt);
        Assert.Equal(OwnPendingNewStart.AddMinutes(45), result.Confirmation.EndsAt);

        var moved = await ReadAppointmentRangeAsync(target.ConnectionString, ids.OwnPendingAppointmentId);
        Assert.Equal(OwnPendingNewStart, moved.StartsAt);
        Assert.Equal(OwnPendingNewStart.AddMinutes(45), moved.EndsAt);
        Assert.NotEqual(appointmentBefore, moved);

        var bookingAfter = await ReadBookingAsync(target.ConnectionString, ids.OwnPendingBookingId);
        Assert.Equal(ids.OwnPendingAppointmentId, bookingAfter.AppointmentId);
        Assert.Equal(BookingStatus.Confirmed, bookingAfter.Status);
        Assert.Equal(Now, bookingAfter.ConfirmedAt);
        Assert.Null(bookingAfter.CancelledAt);
        Assert.Null(bookingAfter.CancelledBy);

        // The private link keeps every byte of its token material; only the expiry moves to the new start plus 24 h
        // (RF-09), never to a fresh token.
        var linkAfter = await ReadLinkAsync(target.ConnectionString, ids.OwnPendingLinkId);
        Assert.Equal(linkBefore.Id, linkAfter.Id);
        Assert.Equal(linkBefore.ProtectedToken, linkAfter.ProtectedToken);
        Assert.True(linkBefore.TokenHash.AsSpan().SequenceEqual(linkAfter.TokenHash), "the appointment-link token hash changed.");
        Assert.Null(linkAfter.RevokedAt);
        Assert.Equal(OwnPendingNewStart.AddHours(24), linkAfter.ExpiresAt);

        // Exactly one Rescheduled outbox row was written, on the successful path only.
        Assert.Equal(NotificationType.Rescheduled, Assert.Single(await ReadNotificationTypesAsync(target.ConnectionString)));

        // D-24/RF-22: rescheduling never rewrites a recorded payment.
        Assert.Equal(paymentsBefore, await ReadPaymentsAsync(target.ConnectionString));

        // A second attempt at the slot the booking now occupies is the same-slot refusal: no move, no confirmation
        // rewrite, no extra outbox row.
        var repeated = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.OwnPendingBookingId, OwnPendingNewStart);
        Assert.False(repeated.Succeeded);
        Assert.Equal("client_reschedule_same_slot", repeated.Error!.Code);
        Assert.Equal(moved, await ReadAppointmentRangeAsync(target.ConnectionString, ids.OwnPendingAppointmentId));
        Assert.Equal(Now, (await ReadBookingAsync(target.ConnectionString, ids.OwnPendingBookingId)).ConfirmedAt);
        Assert.Single(await ReadNotificationTypesAsync(target.ConnectionString));
        Assert.Equal(paymentsBefore, await ReadPaymentsAsync(target.ConnectionString));

        output.WriteLine(
            "SQL assertions passed: the own pending turn moved to the new 15-minute slot, was confirmed with " +
            "ConfirmedAt from the clock, kept its private-link token material with a newStart+24h expiry, wrote " +
            "exactly one Rescheduled notification on success only, and left every payment untouched.");
    }

    [Fact]
    public async Task Keeps_the_original_ConfirmedAt_and_previews_only_own_free_options()
    {
        var target = await PrepareAsync("_ConfirmedOptions");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);
        var originalConfirmation = Now.AddMinutes(-10);
        Assert.Equal(originalConfirmation, (await ReadBookingAsync(target.ConnectionString, ids.OwnConfirmedBookingId)).ConfirmedAt);

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        // The unlocked preview ficha-scopes the booking, drops the current slot and still sees the other reservation
        // of the same worker as occupied.
        var options = await service.GetOwnOptionsAsync(ids.BusinessId, ids.ClientId, ids.OwnConfirmedBookingId, RescheduleDate);
        Assert.NotNull(options);
        Assert.Equal(OwnConfirmedStart, options!.CurrentStartsAt);
        Assert.Contains(new TimeOnly(14, 15), options.AvailableStartTimes);
        Assert.DoesNotContain(new TimeOnly(14, 0), options.AvailableStartTimes);
        Assert.DoesNotContain(new TimeOnly(12, 0), options.AvailableStartTimes);

        // Another ficha, another tenant, and shapes that are not movable individual reservations all preview null.
        Assert.Null(await service.GetOwnOptionsAsync(ids.BusinessId, ids.OtherClientId, ids.OwnConfirmedBookingId, RescheduleDate));
        Assert.Null(await service.GetOwnOptionsAsync(ids.OtherBusinessId, ids.ClientId, ids.OwnConfirmedBookingId, RescheduleDate));
        Assert.Null(await service.GetOwnOptionsAsync(ids.BusinessId, ids.ClientId, ids.FixedBookingId, RescheduleDate));
        Assert.Null(await service.GetOwnOptionsAsync(ids.BusinessId, ids.ClientId, ids.ClassBookingId, RescheduleDate));

        var result = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.OwnConfirmedBookingId, OwnConfirmedNewStart);

        Assert.True(result.Succeeded, result.Error?.Code ?? "the SQL client reschedule did not succeed.");
        Assert.Equal(OwnConfirmedNewStart, (await ReadAppointmentRangeAsync(target.ConnectionString, ids.OwnConfirmedAppointmentId)).StartsAt);

        // T27 subfinding, enforced by the SQL COALESCE(ConfirmedAt, @now): an already confirmed booking keeps the
        // instant the client first confirmed instead of being restamped with the move time.
        var bookingAfter = await ReadBookingAsync(target.ConnectionString, ids.OwnConfirmedBookingId);
        Assert.Equal(BookingStatus.Confirmed, bookingAfter.Status);
        Assert.Equal(originalConfirmation, bookingAfter.ConfirmedAt);
        Assert.Equal(OwnConfirmedNewStart.AddHours(24), (await ReadLinkAsync(target.ConnectionString, ids.OwnConfirmedLinkId)).ExpiresAt);
        Assert.Equal(NotificationType.Rescheduled, Assert.Single(await ReadNotificationTypesAsync(target.ConnectionString)));

        output.WriteLine(
            "SQL assertions passed: the own options excluded the current and the occupied slot, foreign ficha, " +
            "foreign tenant, class and fixed previews were null, and the already confirmed booking moved keeping " +
            "its original ConfirmedAt (the T27 COALESCE guard).");
    }

    [Fact]
    public async Task Hides_foreign_rows_and_refuses_unavailable_shapes_without_writing()
    {
        var target = await PrepareAsync("_Guards");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);
        var appointmentsBefore = await ReadAllAppointmentsAsync(target.ConnectionString);
        var bookingsBefore = await ReadAllBookingsAsync(target.ConnectionString);
        var paymentsBefore = await ReadPaymentsAsync(target.ConnectionString);
        Assert.Empty(await ReadNotificationTypesAsync(target.ConnectionString));

        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        // Another ficha of the same tenant, a booking from another tenant (reached with both the caller's own
        // identity and the foreign tenant's), and an unknown id are all the generic not found.
        var foreignFicha = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.OccupiedBookingId, OwnPendingNewStart);
        var foreignTenant = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.OtherTenantBookingId, OwnPendingNewStart);
        var foreignTenantWrongIdentity = await service.RescheduleOwnIndividualAsync(
            ids.OtherBusinessId, ids.ClientId, ids.OtherTenantBookingId, OwnPendingNewStart);
        var unknown = await service.RescheduleOwnIndividualAsync(ids.BusinessId, ids.ClientId, 999_999, OwnPendingNewStart);
        foreach (var result in new[] { foreignFicha, foreignTenant, foreignTenantWrongIdentity, unknown })
        {
            Assert.False(result.Succeeded);
            Assert.Equal("client_booking_not_found", result.Error!.Code);
            Assert.Equal("No encontramos ese turno.", result.Error.Message);
        }

        // The ficha-scoped preview hides the same foreign rows.
        Assert.Null(await service.GetOwnOptionsAsync(ids.BusinessId, ids.ClientId, ids.OccupiedBookingId, RescheduleDate));
        Assert.Null(await service.GetOwnOptionsAsync(ids.BusinessId, ids.ClientId, ids.OtherTenantBookingId, RescheduleDate));

        // The client API accepts no worker: it can only read the locked booking's own worker, so a slot free only on
        // another worker is unreachable and is refused as unavailable.
        var otherWorker = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.OwnWorkerTwoBookingId, OtherWorkerOnlySlot);
        Assert.False(otherWorker.Succeeded);
        Assert.Equal("client_reschedule_slot_unavailable", otherWorker.Error!.Code);

        var occupied = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.OwnPendingBookingId, OccupiedStart);
        Assert.False(occupied.Succeeded);
        Assert.Equal("client_reschedule_slot_unavailable", occupied.Error!.Code);

        var past = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.OwnPendingBookingId, PastStart);
        Assert.False(past.Succeeded);
        Assert.Equal("client_booking_unavailable", past.Error!.Code);

        var group = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.ClassBookingId, OwnPendingNewStart);
        Assert.False(group.Succeeded);
        Assert.Equal("client_booking_unavailable", group.Error!.Code);

        var fixedBooking = await service.RescheduleOwnIndividualAsync(
            ids.BusinessId, ids.ClientId, ids.FixedBookingId, OwnPendingNewStart);
        Assert.False(fixedBooking.Succeeded);
        Assert.Equal("client_booking_unavailable", fixedBooking.Error!.Code);

        // Nothing crossed a transaction boundary: every appointment, booking and payment is unchanged and the
        // outbox stayed empty, so each refused attempt rolled back without writing.
        Assert.Equal(appointmentsBefore, await ReadAllAppointmentsAsync(target.ConnectionString));
        Assert.Equal(bookingsBefore, await ReadAllBookingsAsync(target.ConnectionString));
        Assert.Equal(paymentsBefore, await ReadPaymentsAsync(target.ConnectionString));
        Assert.Empty(await ReadNotificationTypesAsync(target.ConnectionString));

        output.WriteLine(
            "SQL assertions passed: a foreign ficha, a foreign tenant and an unknown id were the generic not found " +
            "with null previews; another worker's slot, an occupied slot, a past start, a class and a fixed booking " +
            "were refused, and no appointment, booking, payment or outbox row changed.");
    }

    private static OwnerRescheduleService NewService(DbSession session) =>
        new(new SqlOwnerRescheduleRepository(session), session, new FakeClock { Now = Now });

    /// <summary>Validates the opt-in connection, derives a fresh per-test database, proves it is absent, and migrates it.</summary>
    private async Task<SqlTarget?> PrepareAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"SKIPPED: {EnvVar} is unset; no SQL connection was opened.");
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
            output.WriteLine($"master DB_ID('{target.DatabaseName}') = {(existing is null or DBNull ? "NULL (absent)" : "present")}");
            Assert.True(existing is null or DBNull,
                $"Refusing existing database {target.DatabaseName}; the target must be new and is never reused or dropped.");
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        output.WriteLine($"Applied {scripts.Count} migration script(s): {string.Join(", ", scripts)}; the database is retained permanently.");
        return target;
    }

    /// <summary>
    /// Derives this fact's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog,
    /// so one <c>WORKUP_T21_RESCHEDULE_SQL_CONNECTION</c> value serves every fact. Only the validated local instance
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
            throw new InvalidOperationException("WORKUP_T21_RESCHEDULE_SQL_CONNECTION could not be parsed; no SQL connection was opened.");
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
                "Requires a WorkUpDemoT21RescheduleSql* LocalDB database with integrated security; credentials, attached files and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T21 client reschedule SQL integration test",
            TrustServerCertificate = true
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<Seeded> SeedAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        async Task<int> Insert(string sql, params (string Name, object? Value)[] args)
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            foreach (var (name, value) in args)
                command.Parameters.Add(Parameter(name, value));
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        async Task<int> Business(string suffix) => await Insert(
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @now)",
            ("@name", "Synthetic T21 Reschedule " + suffix), ("@slug", "synthetic-t21-reschedule-" + suffix), ("@now", Now));
        async Task<int> Client(int business, string suffix) => await Insert(
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Client', @email, @now)",
            ("@business", business), ("@email", suffix + "@example.invalid"), ("@now", Now));
        async Task<int> Service(int business, int duration, int maxCapacity) => await Insert(
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Service', 100.00, @duration, @capacity)",
            ("@business", business), ("@duration", duration), ("@capacity", maxCapacity));
        async Task<int> Worker(int business, string cuil, TimeSpan scheduleStart, TimeSpan scheduleEnd)
        {
            var workerId = await Insert(
                "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Worker', '0000000000', @cuil)",
                ("@business", business), ("@cuil", cuil));
            await Insert(
                "INSERT INTO WorkerSchedules (WorkerId, DayOfWeek, StartTime, EndTime) OUTPUT INSERTED.Id VALUES (@worker, @day, @start, @end)",
                ("@worker", workerId), ("@day", (byte)DayOfWeek.Wednesday), ("@start", scheduleStart), ("@end", scheduleEnd));
            return workerId;
        }
        async Task<int> IndividualAppointment(int business, int service, int worker, DateTime start) => await Insert(
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @start, @end, 1, @origin, @status, @now)",
            ("@business", business), ("@kind", AppointmentKind.Individual), ("@service", service), ("@worker", worker),
            ("@start", start), ("@end", start.AddMinutes(45)), ("@origin", AppointmentOrigin.Reserved),
            ("@status", AppointmentStatus.Scheduled), ("@now", Now));
        async Task<int> ClassAppointment(int business, int service, int worker, DateTime start) => await Insert(
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @start, @end, 8, NULL, @status, @now)",
            ("@business", business), ("@kind", AppointmentKind.ClassSession), ("@service", service), ("@worker", worker),
            ("@start", start), ("@end", start.AddMinutes(60)), ("@status", AppointmentStatus.Scheduled), ("@now", Now));
        async Task<int> Booking(
            int business, int appointment, int client, int? enrollmentId, BookingModality? modality,
            BookingStatus status, DateTime? confirmedAt) => await Insert(
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, ConfirmedAt, CancelledAt, CancelledBy, AttendanceRecordedAt, AbsenceSource) OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, @enrollment, @modality, @status, @now, @confirmedAt, NULL, NULL, NULL, NULL)",
            ("@business", business), ("@appointment", appointment), ("@client", client),
            ("@enrollment", enrollmentId), ("@modality", modality), ("@status", status), ("@now", Now),
            ("@confirmedAt", confirmedAt));
        async Task<int> Link(int booking, byte discriminator, DateTime expiresAt) => await Insert(
            "INSERT INTO AppointmentLinks (BookingId, TokenHash, ProtectedToken, ExpiresAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@booking, @hash, @protected, @expires, @now)",
            ("@booking", booking), ("@hash", CreateSyntheticHash(discriminator)),
            ("@protected", $"synthetic-protected-token-{discriminator}"), ("@expires", expiresAt), ("@now", Now));

        var businessId = await Business("primary");
        var otherBusinessId = await Business("other");
        var clientId = await Client(businessId, "primary-own");
        var otherClientId = await Client(businessId, "primary-other");
        var otherTenantClientId = await Client(otherBusinessId, "other-own");
        var individualServiceId = await Service(businessId, 45, 1);
        var classServiceId = await Service(businessId, 60, 8);
        var otherServiceId = await Service(otherBusinessId, 45, 1);
        var workerId = await Worker(businessId, "20333333333", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
        var otherWorkerId = await Worker(businessId, "20444444444", new TimeSpan(9, 0, 0), new TimeSpan(11, 0, 0));
        var otherTenantWorkerId = await Worker(otherBusinessId, "20555555555", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));

        var ownPendingAppointmentId = await IndividualAppointment(businessId, individualServiceId, workerId, OwnPendingStart);
        var ownPendingBookingId = await Booking(businessId, ownPendingAppointmentId, clientId, null, null, BookingStatus.Pending, null);
        var ownPendingLinkId = await Link(ownPendingBookingId, 1, OwnPendingStart.AddHours(24));

        var ownConfirmedAppointmentId = await IndividualAppointment(businessId, individualServiceId, workerId, OwnConfirmedStart);
        var ownConfirmedBookingId = await Booking(businessId, ownConfirmedAppointmentId, clientId, null, null, BookingStatus.Confirmed, Now.AddMinutes(-10));
        var ownConfirmedLinkId = await Link(ownConfirmedBookingId, 2, OwnConfirmedStart.AddHours(24));

        var occupiedAppointmentId = await IndividualAppointment(businessId, individualServiceId, workerId, OccupiedStart);
        var occupiedBookingId = await Booking(businessId, occupiedAppointmentId, otherClientId, null, null, BookingStatus.Pending, null);

        var otherWorkerAppointmentId = await IndividualAppointment(businessId, individualServiceId, otherWorkerId, OwnWorkerTwoStart);
        var ownWorkerTwoBookingId = await Booking(businessId, otherWorkerAppointmentId, clientId, null, null, BookingStatus.Pending, null);

        var classAppointmentId = await ClassAppointment(businessId, classServiceId, workerId, ClassStart);
        var classBookingId = await Booking(businessId, classAppointmentId, clientId, null, BookingModality.DropIn, BookingStatus.Pending, null);

        var enrollmentId = await Insert(
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @client, 1, @status, 100.00, @now)",
            ("@business", businessId), ("@client", clientId), ("@status", EnrollmentStatus.Active), ("@now", Now));
        var fixedAppointmentId = await IndividualAppointment(businessId, individualServiceId, workerId, FixedStart);
        var fixedBookingId = await Booking(businessId, fixedAppointmentId, clientId, enrollmentId, BookingModality.Fixed, BookingStatus.Pending, null);

        var otherTenantAppointmentId = await IndividualAppointment(otherBusinessId, otherServiceId, otherTenantWorkerId, OtherTenantStart);
        var otherTenantBookingId = await Booking(otherBusinessId, otherTenantAppointmentId, otherTenantClientId, null, null, BookingStatus.Pending, null);

        await Insert(
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@business, @client, @concept, @booking, NULL, 100.00, 100.00, NULL, @now, @now)",
            ("@business", businessId), ("@client", clientId), ("@concept", PaymentConcept.IndividualAppointment),
            ("@booking", ownPendingBookingId), ("@now", Now));
        await Insert(
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@business, @client, @concept, @booking, NULL, 100.00, 100.00, NULL, @now, @now)",
            ("@business", businessId), ("@client", clientId), ("@concept", PaymentConcept.IndividualAppointment),
            ("@booking", ownConfirmedBookingId), ("@now", Now));

        await transaction.CommitAsync();
        return new Seeded(
            businessId, otherBusinessId, clientId, otherClientId,
            ownPendingAppointmentId, ownPendingBookingId, ownPendingLinkId,
            ownConfirmedAppointmentId, ownConfirmedBookingId, ownConfirmedLinkId,
            occupiedBookingId, ownWorkerTwoBookingId, classBookingId, fixedBookingId, otherTenantBookingId);
    }

    private static SqlParameter Parameter(string name, object? value) => value switch
    {
        null => new SqlParameter(name, DBNull.Value),
        DateTime dateTime => new SqlParameter(name, SqlDbType.DateTime2) { Value = dateTime },
        Enum enumValue => new SqlParameter(name, Convert.ToByte(enumValue, CultureInfo.InvariantCulture)),
        _ => new SqlParameter(name, value)
    };

    private static async Task<AppointmentRange> ReadAppointmentRangeAsync(string connectionString, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT StartsAt, EndsAt FROM Appointments WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = appointmentId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Appointment {appointmentId} was not found.");
        return new AppointmentRange(reader.GetDateTime(0), reader.GetDateTime(1));
    }

    private static async Task<IReadOnlyList<AppointmentRange>> ReadAllAppointmentsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT StartsAt, EndsAt FROM Appointments ORDER BY Id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AppointmentRange>();
        while (await reader.ReadAsync())
            rows.Add(new AppointmentRange(reader.GetDateTime(0), reader.GetDateTime(1)));
        return rows;
    }

    private static async Task<BookingSnapshot> ReadBookingAsync(string connectionString, int bookingId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, AppointmentId, Status, ConfirmedAt, CancelledAt, CancelledBy FROM Bookings WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = bookingId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Booking {bookingId} was not found.");
        return ReadBookingSnapshot(reader);
    }

    private static async Task<IReadOnlyList<BookingSnapshot>> ReadAllBookingsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, AppointmentId, Status, ConfirmedAt, CancelledAt, CancelledBy FROM Bookings ORDER BY Id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<BookingSnapshot>();
        while (await reader.ReadAsync())
            rows.Add(ReadBookingSnapshot(reader));
        return rows;
    }

    private static BookingSnapshot ReadBookingSnapshot(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetInt32(1),
        (BookingStatus)reader.GetByte(2),
        reader.IsDBNull(3) ? null : reader.GetDateTime(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
        reader.IsDBNull(5) ? null : (CancelledBy)reader.GetByte(5));

    private static async Task<LinkSnapshot> ReadLinkAsync(string connectionString, int linkId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, TokenHash, ProtectedToken, ExpiresAt, RevokedAt FROM AppointmentLinks WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = linkId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"AppointmentLink {linkId} was not found.");
        return new LinkSnapshot(
            reader.GetInt32(0), (byte[])reader.GetValue(1), reader.GetString(2), reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4));
    }

    private static async Task<IReadOnlyList<PaymentSnapshot>> ReadPaymentsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, BookingId, Amount, PaidAt, RecordedAt FROM Payments ORDER BY Id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<PaymentSnapshot>();
        while (await reader.ReadAsync())
        {
            rows.Add(new PaymentSnapshot(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDecimal(2),
                reader.GetDateTime(3),
                reader.GetDateTime(4)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<NotificationType>> ReadNotificationTypesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Type FROM Notifications ORDER BY Id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<NotificationType>();
        while (await reader.ReadAsync())
            rows.Add((NotificationType)reader.GetByte(0));
        return rows;
    }

    private static byte[] CreateSyntheticHash(byte discriminator) =>
        Enumerable.Range(0, 32).Select(index => (byte)(index + discriminator)).ToArray();

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record Seeded(
        int BusinessId,
        int OtherBusinessId,
        int ClientId,
        int OtherClientId,
        int OwnPendingAppointmentId,
        int OwnPendingBookingId,
        int OwnPendingLinkId,
        int OwnConfirmedAppointmentId,
        int OwnConfirmedBookingId,
        int OwnConfirmedLinkId,
        int OccupiedBookingId,
        int OwnWorkerTwoBookingId,
        int ClassBookingId,
        int FixedBookingId,
        int OtherTenantBookingId);

    private sealed record AppointmentRange(DateTime StartsAt, DateTime EndsAt);

    private sealed record BookingSnapshot(
        int Id, int AppointmentId, BookingStatus Status, DateTime? ConfirmedAt, DateTime? CancelledAt, CancelledBy? CancelledBy);

    private sealed record LinkSnapshot(int Id, byte[] TokenHash, string ProtectedToken, DateTime ExpiresAt, DateTime? RevokedAt);

    private sealed record PaymentSnapshot(int Id, int? BookingId, decimal Amount, DateTime PaidAt, DateTime RecordedAt);
}
