using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Data;
using WorkUp.Data.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// Opt-in SQL coverage for the client panel confirm/cancel actions (T21.2, RF-08, RF-22). It requires
/// <c>WORKUP_T21_ACTION_SQL_CONNECTION</c> pointing at a brand-new, dedicated <c>(localdb)\MSSQLLocalDB</c> database
/// whose name starts with <c>WorkUpDemoT21ActionSql</c>; each test appends its own suffix so all of them can run with
/// one command, the preflight asserts <c>DB_ID</c> is absent, and the created databases are permanently retained,
/// never reused and never dropped. Without the variable every fact reports N/A and opens no connection.
/// </summary>
public sealed class ClientBookingActionsSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T21_ACTION_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT21ActionSql";
    private static readonly Regex SafeDatabaseName = new(@"\AWorkUpDemoT21ActionSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task Confirm_and_cancel_write_own_individual_turns_and_leave_payments_untouched()
    {
        var target = await PrepareAsync("_Confirm");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);
        var pendingStartsBefore = await ReadAppointmentRangeAsync(target.ConnectionString, ids.PendingAppointmentId);
        var confirmedStartsBefore = await ReadAppointmentRangeAsync(target.ConnectionString, ids.ConfirmedAppointmentId);
        var paymentsBefore = await ReadPaymentsAsync(target.ConnectionString);
        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        var confirmed = await service.ConfirmIntentionAsync(ids.BusinessId, ids.ClientId, ids.PendingBookingId);
        Assert.True(confirmed.Succeeded, confirmed.Error?.Code ?? "the SQL confirmation did not succeed.");
        var afterConfirm = await ReadBookingAsync(target.ConnectionString, ids.PendingBookingId);
        Assert.Equal(BookingStatus.Confirmed, afterConfirm.Status);
        Assert.Equal(Now, afterConfirm.ConfirmedAt);
        Assert.Null(afterConfirm.CancelledAt);
        Assert.Null(afterConfirm.CancelledBy);
        Assert.Equal(pendingStartsBefore, await ReadAppointmentRangeAsync(target.ConnectionString, ids.PendingAppointmentId));

        var repeated = await service.ConfirmIntentionAsync(ids.BusinessId, ids.ClientId, ids.PendingBookingId);
        Assert.True(repeated.Succeeded, repeated.Error?.Code ?? "the idempotent SQL confirmation did not succeed.");
        var afterRepeat = await ReadBookingAsync(target.ConnectionString, ids.PendingBookingId);
        Assert.Equal(BookingStatus.Confirmed, afterRepeat.Status);
        Assert.Equal(Now, afterRepeat.ConfirmedAt);

        var cancelled = await service.CancelAsync(ids.BusinessId, ids.ClientId, ids.ConfirmedBookingId);
        Assert.True(cancelled.Succeeded, cancelled.Error?.Code ?? "the SQL cancellation did not succeed.");
        var afterCancel = await ReadBookingAsync(target.ConnectionString, ids.ConfirmedBookingId);
        Assert.Equal(BookingStatus.Cancelled, afterCancel.Status);
        Assert.Equal(CancelledBy.Client, afterCancel.CancelledBy);
        Assert.Equal(Now, afterCancel.CancelledAt);
        Assert.Equal(Now.AddMinutes(-10), afterCancel.ConfirmedAt);
        Assert.Equal(confirmedStartsBefore, await ReadAppointmentRangeAsync(target.ConnectionString, ids.ConfirmedAppointmentId));

        // RF-22 error handling and D-24: cancelling mutates only the booking; the recorded payment and the
        // appointment range stay exactly as they were.
        Assert.Equal(paymentsBefore, await ReadPaymentsAsync(target.ConnectionString));

        output.WriteLine(
            "SQL assertions passed: pending turn confirmed with ConfirmedAt, repeat confirmation kept the original " +
            "ConfirmedAt, confirmed turn cancelled as Client, appointment ranges and payments unchanged.");
    }

    [Fact]
    public async Task Foreign_rows_invalid_states_and_the_exact_start_are_rejected_without_writes()
    {
        var target = await PrepareAsync("_Guards");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);
        var before = await ReadAllBookingsAsync(target.ConnectionString);
        await using var session = new DbSession(target.ConnectionString);
        var service = NewService(session);

        // Own individual turn, but past, at the exact start, or in a state RF-08/RF-22 do not accept.
        foreach (var bookingId in new[]
                 {
                     ids.PastBookingId, ids.ExactStartBookingId, ids.AttendedBookingId, ids.AbsentBookingId,
                     ids.CancelledBookingId, ids.ClassBookingId, ids.WalkInBookingId
                 })
        {
            var confirmed = await service.ConfirmIntentionAsync(ids.BusinessId, ids.ClientId, bookingId);
            var cancelled = await service.CancelAsync(ids.BusinessId, ids.ClientId, bookingId);
            Assert.False(confirmed.Succeeded, $"confirm unexpectedly succeeded for booking {bookingId}.");
            Assert.False(cancelled.Succeeded, $"cancel unexpectedly succeeded for booking {bookingId}.");
            Assert.Equal("client_booking_unavailable", confirmed.Error!.Code);
            Assert.Equal("client_booking_unavailable", cancelled.Error!.Code);
        }

        // Another ficha of the same tenant, a booking that belongs to another tenant, and a booking id that does
        // not exist. The last two are reached with this tenant's own session identity, so the scoped filter has to
        // hide them exactly like a missing row.
        var crossClient = await service.CancelAsync(ids.BusinessId, ids.ClientId, ids.OtherClientBookingId);
        var crossTenant = await service.ConfirmIntentionAsync(ids.BusinessId, ids.ClientId, ids.OtherTenantBookingId);
        var crossTenantWrongClient = await service.CancelAsync(ids.OtherBusinessId, ids.ClientId, ids.OtherTenantBookingId);
        var unknown = await service.ConfirmIntentionAsync(ids.BusinessId, ids.ClientId, 999_999);
        foreach (var result in new[] { crossClient, crossTenant, crossTenantWrongClient, unknown })
        {
            Assert.False(result.Succeeded);
            Assert.Equal("client_booking_not_found", result.Error!.Code);
        }

        Assert.Equal(before, await ReadAllBookingsAsync(target.ConnectionString));

        output.WriteLine(
            "SQL assertions passed: past, exact start, attended, absent, cancelled, class, walk-in, foreign ficha, " +
            "foreign tenant and unknown ids were all rejected with no row written.");
    }

    /// <summary>
    /// A confirmation and a cancellation of the same individual turn racing under the per-worker UPDLOCK the README
    /// fixes for an individual turn plus the booking row lock. Both serialize, so exactly one consistent sequence is
    /// observed: either the cancellation sees the fresh confirmation and cancels a confirmed turn, or it wins first
    /// and the confirmation is refused. No deadlock was observed in this run; that is not a universal claim.
    /// </summary>
    [Fact]
    public async Task Concurrent_confirm_and_cancel_serialize_and_leave_one_consistent_state()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        var ids = await SeedAsync(target.ConnectionString);
        var paymentsBefore = await ReadPaymentsAsync(target.ConnectionString);
        using var startGate = new Barrier(participantCount: 2);

        Task<Result> ConfirmAsync() => Task.Run(async () =>
        {
            await using var session = new DbSession(target.ConnectionString);
            startGate.SignalAndWait();
            return await NewService(session).ConfirmIntentionAsync(ids.BusinessId, ids.ClientId, ids.PendingBookingId);
        });

        Task<Result> CancelAsync() => Task.Run(async () =>
        {
            await using var session = new DbSession(target.ConnectionString);
            startGate.SignalAndWait();
            return await NewService(session).CancelAsync(ids.BusinessId, ids.ClientId, ids.PendingBookingId);
        });

        var results = await Task.WhenAll(ConfirmAsync(), CancelAsync());
        var confirmed = results[0];
        var cancelled = results[1];

        // The cancellation is always applicable (pending or confirmed), so it must have won; the confirmation
        // either wrote first or was refused after seeing the cancellation.
        Assert.True(cancelled.Succeeded, cancelled.Error?.Code ?? "the racing cancellation did not succeed.");
        if (!confirmed.Succeeded)
            Assert.Equal("client_booking_unavailable", confirmed.Error!.Code);

        var after = await ReadBookingAsync(target.ConnectionString, ids.PendingBookingId);
        Assert.Equal(BookingStatus.Cancelled, after.Status);
        Assert.Equal(CancelledBy.Client, after.CancelledBy);
        Assert.Equal(Now, after.CancelledAt);
        Assert.Equal(paymentsBefore, await ReadPaymentsAsync(target.ConnectionString));

        output.WriteLine(
            $"Race assertions passed: cancellation won, confirmation {(confirmed.Succeeded ? "also applied before it" : "was refused")}, " +
            "one consistent cancelled row and payments unchanged; no deadlock observed in this run.");
    }

    private ClientBookingActionsService NewService(DbSession session) =>
        new(new SqlClientBookingActionsRepository(session), session, new FakeClock { Now = Now });

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
    /// so one <c>WORKUP_T21_ACTION_SQL_CONNECTION</c> value serves every fact. Only the validated local instance and
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
            throw new InvalidOperationException("WORKUP_T21_ACTION_SQL_CONNECTION could not be parsed; no SQL connection was opened.");
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
                $"Requires a WorkUpDemoT21ActionSql* LocalDB database with integrated security; credentials, attached files and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T21 action SQL integration test",
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
            ("@name", "Synthetic T21 Action " + suffix), ("@slug", "synthetic-t21-action-" + suffix), ("@now", Now));
        async Task<int> Client(int business, string suffix) => await Insert(
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Client', @email, @now)",
            ("@business", business), ("@email", suffix + "@example.invalid"), ("@now", Now));
        async Task<int> Service(int business) => await Insert(
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Service', 100.00, 30, 1)",
            ("@business", business));
        async Task<int> Worker(int business, string cuil) => await Insert(
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Worker', '0000000000', @cuil)",
            ("@business", business), ("@cuil", cuil));
        async Task<int> Appointment(
            int business, int service, int worker, DateTime start, AppointmentKind kind,
            AppointmentOrigin? origin, int maxCapacity) => await Insert(
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, NULL, @start, @end, @capacity, @origin, @status, @now)",
            ("@business", business), ("@kind", kind), ("@service", service), ("@worker", worker),
            ("@start", start), ("@end", start.AddMinutes(30)), ("@capacity", maxCapacity),
            ("@origin", origin), ("@status", AppointmentStatus.Scheduled), ("@now", Now));
        async Task<int> Booking(
            int business, int appointment, int client, BookingModality? modality, BookingStatus status,
            DateTime? confirmedAt, DateTime? cancelledAt, CancelledBy? cancelledBy,
            DateTime? attendanceRecordedAt, AbsenceSource? absenceSource) => await Insert(
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, ConfirmedAt, CancelledAt, CancelledBy, AttendanceRecordedAt, AbsenceSource) OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, NULL, @modality, @status, @now, @confirmedAt, @cancelledAt, @cancelledBy, @attendance, @absence)",
            ("@business", business), ("@appointment", appointment), ("@client", client),
            ("@modality", modality), ("@status", status), ("@now", Now), ("@confirmedAt", confirmedAt),
            ("@cancelledAt", cancelledAt), ("@cancelledBy", cancelledBy), ("@attendance", attendanceRecordedAt),
            ("@absence", absenceSource));

        var businessId = await Business("primary");
        var clientId = await Client(businessId, "primary-own");
        var otherClientId = await Client(businessId, "primary-other");
        var serviceId = await Service(businessId);
        var workerId = await Worker(businessId, "20333333333");

        var pendingAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(2), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var pendingBookingId = await Booking(businessId, pendingAppointmentId, clientId, null, BookingStatus.Pending, null, null, null, null, null);

        var confirmedAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(3), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var confirmedBookingId = await Booking(businessId, confirmedAppointmentId, clientId, null, BookingStatus.Confirmed, Now.AddMinutes(-10), null, null, null, null);

        var pastAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(-1), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var pastBookingId = await Booking(businessId, pastAppointmentId, clientId, null, BookingStatus.Pending, null, null, null, null, null);

        var exactStartAppointmentId = await Appointment(businessId, serviceId, workerId, Now, AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var exactStartBookingId = await Booking(businessId, exactStartAppointmentId, clientId, null, BookingStatus.Pending, null, null, null, null, null);

        var attendedAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(-2), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var attendedBookingId = await Booking(businessId, attendedAppointmentId, clientId, null, BookingStatus.Attended, null, null, null, Now.AddHours(-2), null);

        var absentAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(-2), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var absentBookingId = await Booking(businessId, absentAppointmentId, clientId, null, BookingStatus.Absent, null, null, null, Now.AddHours(-2), AbsenceSource.ClientLink);

        var cancelledAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(4), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var cancelledBookingId = await Booking(businessId, cancelledAppointmentId, clientId, null, BookingStatus.Cancelled, null, Now.AddHours(-1), CancelledBy.Client, null, null);

        var classAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(5), AppointmentKind.ClassSession, null, 8);
        var classBookingId = await Booking(businessId, classAppointmentId, clientId, BookingModality.DropIn, BookingStatus.Pending, null, null, null, null, null);

        var walkInAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(6), AppointmentKind.Individual, AppointmentOrigin.WalkIn, 1);
        var walkInBookingId = await Booking(businessId, walkInAppointmentId, clientId, null, BookingStatus.Pending, null, null, null, null, null);

        var otherClientAppointmentId = await Appointment(businessId, serviceId, workerId, Now.AddHours(7), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var otherClientBookingId = await Booking(businessId, otherClientAppointmentId, otherClientId, null, BookingStatus.Pending, null, null, null, null, null);

        var otherBusinessId = await Business("other");
        var otherTenantClientId = await Client(otherBusinessId, "other-own");
        var otherServiceId = await Service(otherBusinessId);
        var otherWorkerId = await Worker(otherBusinessId, "20444444444");
        var otherTenantAppointmentId = await Appointment(otherBusinessId, otherServiceId, otherWorkerId, Now.AddHours(2), AppointmentKind.Individual, AppointmentOrigin.Reserved, 1);
        var otherTenantBookingId = await Booking(otherBusinessId, otherTenantAppointmentId, otherTenantClientId, null, BookingStatus.Pending, null, null, null, null, null);

        await Insert(
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@business, @client, @concept, @booking, NULL, 100.00, 100.00, NULL, @now, @now)",
            ("@business", businessId), ("@client", clientId), ("@concept", PaymentConcept.IndividualAppointment),
            ("@booking", confirmedBookingId), ("@now", Now));

        return new Seeded(
            businessId, clientId, otherClientId, otherBusinessId, otherTenantClientId,
            pendingAppointmentId, pendingBookingId, confirmedAppointmentId, confirmedBookingId,
            pastBookingId, exactStartBookingId, attendedBookingId, absentBookingId, cancelledBookingId,
            classBookingId, walkInBookingId, otherClientBookingId, otherTenantBookingId);
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
        return new BookingSnapshot(
            (BookingStatus)reader.GetByte(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : (CancelledBy)reader.GetByte(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : (AbsenceSource)reader.GetByte(5));
    }

    private static async Task<(DateTime StartsAt, DateTime EndsAt)> ReadAppointmentRangeAsync(string connectionString, int appointmentId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT StartsAt, EndsAt FROM Appointments WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = appointmentId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Appointment {appointmentId} was not found.");
        return (reader.GetDateTime(0), reader.GetDateTime(1));
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
            rows.Add(new BookingSnapshotWithId(
                reader.GetInt32(0),
                (BookingStatus)reader.GetByte(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : (CancelledBy)reader.GetByte(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : (AbsenceSource)reader.GetByte(6)));
        }

        return rows;
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

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record Seeded(
        int BusinessId,
        int ClientId,
        int OtherClientId,
        int OtherBusinessId,
        int OtherTenantClientId,
        int PendingAppointmentId,
        int PendingBookingId,
        int ConfirmedAppointmentId,
        int ConfirmedBookingId,
        int PastBookingId,
        int ExactStartBookingId,
        int AttendedBookingId,
        int AbsentBookingId,
        int CancelledBookingId,
        int ClassBookingId,
        int WalkInBookingId,
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
