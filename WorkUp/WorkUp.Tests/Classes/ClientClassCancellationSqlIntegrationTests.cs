using System.Data;
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
/// Opt-in SQL coverage for the client panel drop-in cancellation (T21.3, RF-22). It requires
/// <c>WORKUP_T21_CLASS_SQL_CONNECTION</c> pointing at a brand-new, dedicated <c>(localdb)\MSSQLLocalDB</c> database
/// whose name starts with <c>WorkUpDemoT21ClassSql</c>; each test appends its own suffix so all of them can run with
/// one command, the preflight asserts <c>DB_ID</c> is absent, and the created databases are permanently retained,
/// never reused and never dropped. Without the variable every fact reports N/A and opens no connection.
/// </summary>
public sealed class ClientClassCancellationSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T21_CLASS_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT21ClassSql";
    private static readonly Regex SafeDatabaseName = new(@"\AWorkUpDemoT21ClassSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task Cancel_own_drop_in_writes_client_attribution_and_leaves_payments_untouched()
    {
        var target = await PrepareAsync("_Cancel");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);
        var paymentsBefore = await ReadPaymentsAsync(target.ConnectionString);
        var pendingRangeBefore = await ReadAppointmentRangeAsync(target.ConnectionString, ids.PendingAppointmentId);
        var confirmedRangeBefore = await ReadAppointmentRangeAsync(target.ConnectionString, ids.ConfirmedAppointmentId);
        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        var cancelled = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, ids.PendingBookingId);
        Assert.True(cancelled.Succeeded, cancelled.Error?.Code ?? "the SQL client cancellation did not succeed.");

        var afterCancel = await ReadBookingAsync(target.ConnectionString, ids.PendingBookingId);
        Assert.Equal(BookingStatus.Cancelled, afterCancel.Status);
        Assert.Equal(CancelledBy.Client, afterCancel.CancelledBy);
        Assert.Equal(Now, afterCancel.CancelledAt);
        Assert.Null(afterCancel.ConfirmedAt);
        Assert.Equal(pendingRangeBefore, await ReadAppointmentRangeAsync(target.ConnectionString, ids.PendingAppointmentId));

        // The cancelled place no longer trips the filtered unique index, so the same client books the class again.
        var rebooked = await new ClassDropInService(
                new SqlClassDropInRepository(session), session, new FakeClock { Now = Now })
            .BookForClientAsync(ids.BusinessId, ids.PendingAppointmentId, ids.ClientId);
        Assert.True(rebooked.Succeeded, rebooked.Error?.Code ?? "the same client could not book the class again.");

        // A second cancellation of the same annotation is refused and never rewrites the original instant or author.
        var repeated = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, ids.PendingBookingId);
        Assert.False(repeated.Succeeded);
        Assert.Equal("client_class_booking_unavailable", repeated.Error!.Code);
        var afterRepeat = await ReadBookingAsync(target.ConnectionString, ids.PendingBookingId);
        Assert.Equal(BookingStatus.Cancelled, afterRepeat.Status);
        Assert.Equal(CancelledBy.Client, afterRepeat.CancelledBy);
        Assert.Equal(Now, afterRepeat.CancelledAt);

        // A confirmed drop-in is cancellable too and keeps its ConfirmedAt.
        var confirmed = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, ids.ConfirmedBookingId);
        Assert.True(confirmed.Succeeded, confirmed.Error?.Code ?? "the confirmed SQL cancellation did not succeed.");
        var afterConfirmed = await ReadBookingAsync(target.ConnectionString, ids.ConfirmedBookingId);
        Assert.Equal(BookingStatus.Cancelled, afterConfirmed.Status);
        Assert.Equal(CancelledBy.Client, afterConfirmed.CancelledBy);
        Assert.Equal(Now, afterConfirmed.CancelledAt);
        Assert.Equal(Now.AddMinutes(-10), afterConfirmed.ConfirmedAt);
        Assert.Equal(confirmedRangeBefore, await ReadAppointmentRangeAsync(target.ConnectionString, ids.ConfirmedAppointmentId));

        // RF-22 error handling and D-24: the cancellation touches the annotation only, never a payment.
        Assert.Equal(paymentsBefore, await ReadPaymentsAsync(target.ConnectionString));

        output.WriteLine(
            "SQL assertions passed: the own drop-in was cancelled as Client with CancelledAt from the clock, the " +
            "place was freed for the same client, a repeat cancel kept the original instant, a confirmed drop-in was " +
            "cancelled keeping its ConfirmedAt, and every payment and appointment range was unchanged.");
    }

    [Fact]
    public async Task Hidden_foreign_and_invalid_states_are_rejected_without_writes()
    {
        var target = await PrepareAsync("_Guards");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);
        var before = await ReadAllBookingsAsync(target.ConnectionString);
        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        // Another ficha of the same tenant, another tenant reached with this tenant's own identity, and an unknown id.
        var crossClient = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, ids.OtherClientBookingId);
        var crossTenant = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, ids.OtherTenantBookingId);
        var crossTenantWrongClient = await service.CancelOwnDropInAsync(
            ids.OtherBusinessId, ids.ClientId, ids.OtherTenantBookingId);
        var unknown = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, 999_999);

        // A fixed annotation is not a drop-in and an individual turn is not a class: both are the generic not found.
        var fixedResult = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, ids.FixedBookingId);
        var individualResult = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, ids.IndividualBookingId);

        foreach (var result in new[] { crossClient, crossTenant, crossTenantWrongClient, unknown, fixedResult, individualResult })
        {
            Assert.False(result.Succeeded);
            Assert.Equal("client_class_booking_not_found", result.Error!.Code);
        }

        // Own drop-ins whose class or annotation no longer admits the cancellation.
        foreach (var bookingId in new[]
                 {
                     ids.SuspendedBookingId, ids.StartedBookingId, ids.ExactStartBookingId,
                     ids.AttendedBookingId, ids.AbsentBookingId
                 })
        {
            var result = await service.CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, bookingId);
            Assert.False(result.Succeeded, $"cancel unexpectedly succeeded for booking {bookingId}.");
            Assert.Equal("client_class_booking_unavailable", result.Error!.Code);
        }

        Assert.Equal(before, await ReadAllBookingsAsync(target.ConnectionString));

        output.WriteLine(
            "SQL assertions passed: another ficha, another tenant, an unknown id, a fixed annotation and an " +
            "individual turn were the generic not found; a suspended class, an already started class, the exact " +
            "start, an attended and an absent annotation were all refused with no row written.");
    }

    /// <summary>
    /// Two client cancellations of the same annotation racing on the session row <c>UPDLOCK</c> and then the
    /// annotation row lock. Both serialize, so exactly one writes: the winner cancels once and the loser reads the
    /// already cancelled annotation and is refused, leaving one consistent row. No deadlock was observed in this run;
    /// that is not a universal claim.
    /// </summary>
    [Fact]
    public async Task Concurrent_client_cancellations_serialize_into_one_cancelled_row()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);
        var paymentsBefore = await ReadPaymentsAsync(target.ConnectionString);
        using var startGate = new Barrier(participantCount: 2);

        Task<WorkUp.BusinessLogic.Common.Result> CancelAsync() => Task.Run(async () =>
        {
            await using var session = new DbSession(target.ConnectionString);
            startGate.SignalAndWait();
            return await NewService(session).CancelOwnDropInAsync(ids.BusinessId, ids.ClientId, ids.PendingBookingId);
        });

        var results = await Task.WhenAll(CancelAsync(), CancelAsync());

        Assert.Equal(1, results.Count(result => result.Succeeded));
        var loser = Assert.Single(results, result => !result.Succeeded);
        Assert.Equal("client_class_booking_unavailable", loser.Error!.Code);

        var after = await ReadBookingAsync(target.ConnectionString, ids.PendingBookingId);
        Assert.Equal(BookingStatus.Cancelled, after.Status);
        Assert.Equal(CancelledBy.Client, after.CancelledBy);
        Assert.Equal(Now, after.CancelledAt);
        Assert.Equal(paymentsBefore, await ReadPaymentsAsync(target.ConnectionString));

        output.WriteLine(
            "Race assertions passed: exactly one client cancellation won, the other was refused with the original " +
            "instant intact and payments unchanged; no deadlock observed in this run.");
    }

    private ClientClassCancellationService NewService(DbSession session) =>
        new(new SqlClassDropInRepository(session), session, new FakeClock { Now = Now });

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
    /// Derives this test's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog,
    /// so one <c>WORKUP_T21_CLASS_SQL_CONNECTION</c> value serves every fact. Only the validated local instance and
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
            throw new InvalidOperationException("WORKUP_T21_CLASS_SQL_CONNECTION could not be parsed; no SQL connection was opened.");
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
                "Requires a WorkUpDemoT21ClassSql* LocalDB database with integrated security; credentials, attached files and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T21 class cancellation SQL integration test",
            TrustServerCertificate = true
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<Seeded> SeedAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        async Task<int> Insert(string sql, params (string Name, object? Value)[] args)
        {
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in args)
                command.Parameters.Add(Parameter(name, value));
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        async Task<int> Business(string suffix) => await Insert(
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @now)",
            ("@name", "Synthetic T21 Class " + suffix), ("@slug", "synthetic-t21-class-" + suffix), ("@now", Now));
        async Task<int> Client(int business, string suffix) => await Insert(
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Client', @email, @now)",
            ("@business", business), ("@email", suffix + "@example.invalid"), ("@now", Now));
        async Task<int> Service(int business) => await Insert(
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Service', 100.00, 60, 8)",
            ("@business", business));
        async Task<int> Worker(int business, string cuil) => await Insert(
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Worker', '0000000000', @cuil)",
            ("@business", business), ("@cuil", cuil));
        async Task<int> Enrollment(int business, int client) => await Insert(
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @client, 1, @status, 100.00, @now)",
            ("@business", business), ("@client", client), ("@status", EnrollmentStatus.Active), ("@now", Now));
        async Task<int> Appointment(
            int business, int service, int worker, DateTime start, AppointmentKind kind,
            AppointmentOrigin? origin, int maxCapacity) => await Insert(
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @start, @end, @capacity, @origin, @status, @now)",
            ("@business", business), ("@kind", kind), ("@service", service), ("@worker", worker),
            ("@start", start), ("@end", start.AddMinutes(60)), ("@capacity", maxCapacity),
            ("@origin", origin), ("@status", AppointmentStatus.Scheduled), ("@now", Now));
        async Task<int> SuspendedAppointment(
            int business, int service, int worker, DateTime start, int maxCapacity) => await Insert(
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @start, @end, @capacity, NULL, @status, @now)",
            ("@business", business), ("@kind", AppointmentKind.ClassSession), ("@service", service),
            ("@worker", worker), ("@start", start), ("@end", start.AddMinutes(60)), ("@capacity", maxCapacity),
            ("@status", AppointmentStatus.Suspended), ("@now", Now));
        async Task<int> Booking(
            int business, int appointment, int client, int? enrollmentId, BookingModality? modality,
            BookingStatus status, DateTime? confirmedAt, DateTime? cancelledAt, CancelledBy? cancelledBy,
            DateTime? attendanceRecordedAt, AbsenceSource? absenceSource) => await Insert(
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, ConfirmedAt, CancelledAt, CancelledBy, AttendanceRecordedAt, AbsenceSource) OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, @enrollment, @modality, @status, @now, @confirmedAt, @cancelledAt, @cancelledBy, @attendance, @absence)",
            ("@business", business), ("@appointment", appointment), ("@client", client),
            ("@enrollment", enrollmentId), ("@modality", modality), ("@status", status), ("@now", Now),
            ("@confirmedAt", confirmedAt), ("@cancelledAt", cancelledAt), ("@cancelledBy", cancelledBy),
            ("@attendance", attendanceRecordedAt), ("@absence", absenceSource));

        var businessId = await Business("primary");
        var clientId = await Client(businessId, "primary-own");
        var otherClientId = await Client(businessId, "primary-other");
        var serviceId = await Service(businessId);
        var workerId = await Worker(businessId, "20333333333");

        var pendingAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(2), AppointmentKind.ClassSession, null, 8);
        var pendingBookingId = await Booking(businessId, pendingAppointmentId, clientId, null, BookingModality.DropIn, BookingStatus.Pending, null, null, null, null, null);

        var confirmedAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(3), AppointmentKind.ClassSession, null, 8);
        var confirmedBookingId = await Booking(businessId, confirmedAppointmentId, clientId, null, BookingModality.DropIn, BookingStatus.Confirmed, Now.AddMinutes(-10), null, null, null, null);

        var suspendedAppointmentId = await SuspendedAppointment(businessId, serviceId, workerId, Now.AddHours(4), 8);
        var suspendedBookingId = await Booking(businessId, suspendedAppointmentId, clientId, null, BookingModality.DropIn, BookingStatus.Pending, null, null, null, null, null);

        var startedAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(-1), AppointmentKind.ClassSession, null, 8);
        var startedBookingId = await Booking(businessId, startedAppointmentId, clientId, null, BookingModality.DropIn, BookingStatus.Pending, null, null, null, null, null);

        var exactStartAppointmentId = await Appointment(businessId, serviceId, workerId, Now, AppointmentKind.ClassSession, null, 8);
        var exactStartBookingId = await Booking(businessId, exactStartAppointmentId, clientId, null, BookingModality.DropIn, BookingStatus.Pending, null, null, null, null, null);

        var attendedAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(-2), AppointmentKind.ClassSession, null, 8);
        var attendedBookingId = await Booking(businessId, attendedAppointmentId, clientId, null, BookingModality.DropIn, BookingStatus.Attended, null, null, null, Now.AddHours(-2), null);

        var absentAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(-2), AppointmentKind.ClassSession, null, 8);
        var absentBookingId = await Booking(businessId, absentAppointmentId, clientId, null, BookingModality.DropIn, BookingStatus.Absent, null, null, null, Now.AddHours(-2), AbsenceSource.Owner);

        var enrollmentId = await Enrollment(businessId, clientId);
        var fixedAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(5), AppointmentKind.ClassSession, null, 8);
        var fixedBookingId = await Booking(businessId, fixedAppointmentId, clientId, enrollmentId, BookingModality.Fixed, BookingStatus.Confirmed, null, null, null, null, null);

        var individualAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(2), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var individualBookingId = await Booking(businessId, individualAppointmentId, clientId, null, null, BookingStatus.Pending, null, null, null, null, null);

        var otherClientAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(6), AppointmentKind.ClassSession, null, 8);
        var otherClientBookingId = await Booking(businessId, otherClientAppointmentId, otherClientId, null, BookingModality.DropIn, BookingStatus.Pending, null, null, null, null, null);

        var otherBusinessId = await Business("other");
        var otherTenantClientId = await Client(otherBusinessId, "other-own");
        var otherServiceId = await Service(otherBusinessId);
        var otherWorkerId = await Worker(otherBusinessId, "20444444444");
        var otherTenantAppointmentId = await Appointment(otherBusinessId, otherServiceId, otherWorkerId, Now.AddHours(2), AppointmentKind.ClassSession, null, 8);
        var otherTenantBookingId = await Booking(otherBusinessId, otherTenantAppointmentId, otherTenantClientId, null, BookingModality.DropIn, BookingStatus.Pending, null, null, null, null, null);

        await Insert(
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@business, @client, @concept, @booking, NULL, 100.00, 100.00, NULL, @now, @now)",
            ("@business", businessId), ("@client", clientId), ("@concept", PaymentConcept.DropInClass),
            ("@booking", confirmedBookingId), ("@now", Now));

        return new Seeded(
            businessId, clientId, otherBusinessId,
            pendingAppointmentId, pendingBookingId, confirmedAppointmentId, confirmedBookingId,
            suspendedBookingId, startedBookingId, exactStartBookingId, attendedBookingId, absentBookingId,
            fixedBookingId, individualBookingId, otherClientBookingId, otherTenantBookingId);
    }

    private static SqlParameter Parameter(string name, object? value) => value switch
    {
        null => new SqlParameter(name, DBNull.Value),
        DateTime dateTime => new SqlParameter(name, SqlDbType.DateTime2) { Value = dateTime },
        Enum enumValue => new SqlParameter(name, Convert.ToByte(enumValue, CultureInfo.InvariantCulture)),
        _ => new SqlParameter(name, value)
    };

    private static async Task<BookingSnapshot> ReadBookingAsync(string connectionString, int bookingId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Status, ConfirmedAt, CancelledAt, CancelledBy, AttendanceRecordedAt, AbsenceSource FROM Bookings WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = bookingId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Booking {bookingId} was not found.");
        return ReadBookingSnapshot(reader);
    }

    private static async Task<IReadOnlyList<BookingSnapshotWithId>> ReadAllBookingsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id, Status, ConfirmedAt, CancelledAt, CancelledBy, AttendanceRecordedAt, AbsenceSource FROM Bookings ORDER BY Id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<BookingSnapshotWithId>();
        while (await reader.ReadAsync())
        {
            var snapshot = ReadBookingSnapshot(reader, offset: 1);
            rows.Add(new BookingSnapshotWithId(
                reader.GetInt32(0), snapshot.Status, snapshot.ConfirmedAt, snapshot.CancelledAt,
                snapshot.CancelledBy, snapshot.AttendanceRecordedAt, snapshot.AbsenceSource));
        }

        return rows;
    }

    private static async Task<(DateTime StartsAt, DateTime EndsAt)> ReadAppointmentRangeAsync(
        string connectionString, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT StartsAt, EndsAt FROM Appointments WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = appointmentId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Appointment {appointmentId} was not found.");
        return (reader.GetDateTime(0), reader.GetDateTime(1));
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

    /// <summary>Reads cancellation facts starting at column zero, or after the Id in an all-row projection.</summary>
    private static BookingSnapshot ReadBookingSnapshot(SqlDataReader reader, int offset = 0) => new(
        (BookingStatus)reader.GetByte(offset),
        reader.IsDBNull(offset + 1) ? null : reader.GetDateTime(offset + 1),
        reader.IsDBNull(offset + 2) ? null : reader.GetDateTime(offset + 2),
        reader.IsDBNull(offset + 3) ? null : (CancelledBy)reader.GetByte(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetDateTime(offset + 4),
        reader.IsDBNull(offset + 5) ? null : (AbsenceSource)reader.GetByte(offset + 5));

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record Seeded(
        int BusinessId,
        int ClientId,
        int OtherBusinessId,
        int PendingAppointmentId,
        int PendingBookingId,
        int ConfirmedAppointmentId,
        int ConfirmedBookingId,
        int SuspendedBookingId,
        int StartedBookingId,
        int ExactStartBookingId,
        int AttendedBookingId,
        int AbsentBookingId,
        int FixedBookingId,
        int IndividualBookingId,
        int OtherClientBookingId,
        int OtherTenantBookingId);

    private sealed record BookingSnapshot(
        BookingStatus Status,
        DateTime? ConfirmedAt,
        DateTime? CancelledAt,
        CancelledBy? CancelledBy,
        DateTime? AttendanceRecordedAt,
        AbsenceSource? AbsenceSource);

    private sealed record BookingSnapshotWithId(
        int Id,
        BookingStatus Status,
        DateTime? ConfirmedAt,
        DateTime? CancelledAt,
        CancelledBy? CancelledBy,
        DateTime? AttendanceRecordedAt,
        AbsenceSource? AbsenceSource);

    private sealed record PaymentSnapshot(int Id, int? BookingId, decimal Amount, DateTime PaidAt);
}
