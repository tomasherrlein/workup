using System.Data;
using System.Diagnostics;
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
/// Opt-in integration coverage for the owner reschedule SQL transaction. Requires a new, dedicated LocalDB database
/// supplied through WORKUP_T11_SQL_CONNECTION; this fixture never tears the database down.
/// </summary>
public sealed class OwnerRescheduleSqlIntegrationTests(ITestOutputHelper output)
{
    private const string LocalDbDataSource = @"(localdb)\MSSQLLocalDB";
    private const string RequiredDatabasePrefix = "WorkUpDemoT11Sql";
    private static readonly Regex SafeDatabaseName = new(@"\AWorkUpDemoT11Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime OriginalStart = new(2026, 3, 11, 10, 0, 0);
    private static readonly DateTime RescheduledStart = new(2026, 3, 11, 11, 0, 0);
    private static readonly DateTime OccupiedBookingStart = new(2026, 3, 11, 12, 0, 0);

    [Fact]
    public async Task Reschedules_existing_sql_booking_and_rejects_a_stale_occupied_target_atomically()
    {
        var suppliedConnectionString = Environment.GetEnvironmentVariable("WORKUP_T11_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(suppliedConnectionString))
        {
            output.WriteLine("SKIPPED: WORKUP_T11_SQL_CONNECTION is not set; no SQL connection was opened.");
            return;
        }

        var target = ValidateTarget(suppliedConnectionString);
        output.WriteLine($"SQL Server LocalDB target: {target.DatabaseName}");

        var exists = await DatabaseExistsViaSqlcmdAsync(target.DatabaseName);
        output.WriteLine($"master sqlcmd result: DB_ID('{target.DatabaseName}') = {(exists ? "1 (present)" : "0 (absent)")}");
        Assert.False(exists, $"Refusing to use existing database '{target.DatabaseName}'; the target must be new.");

        var appliedScripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        output.WriteLine($"DatabaseMigrator applied {appliedScripts.Count} schema script(s) to the new target; no teardown will run.");

        var clock = new FakeClock { Now = FixedNow };
        var ids = await SeedSyntheticDataAsync(target.ConnectionString, clock);
        var before = await ReadSnapshotAsync(target.ConnectionString, ids);
        Assert.Empty(before.Notifications);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlOwnerRescheduleRepository(session);
        var service = new OwnerRescheduleService(repository, session, clock);

        var success = await service.RescheduleIndividualAsync(
            ids.BusinessId,
            ids.FirstBookingId,
            new OwnerRescheduleRequest(ids.WorkerId, RescheduledStart));

        Assert.True(success.Succeeded, success.Error?.Code ?? "The SQL reschedule did not succeed.");
        Assert.Equal(RescheduledStart, success.Confirmation.StartsAt);
        Assert.Equal(RescheduledStart.AddMinutes(45), success.Confirmation.EndsAt);

        var afterSuccess = await ReadSnapshotAsync(target.ConnectionString, ids);
        Assert.Equal(RescheduledStart, afterSuccess.FirstAppointment.StartsAt);
        Assert.Equal(RescheduledStart.AddMinutes(45), afterSuccess.FirstAppointment.EndsAt);
        Assert.Equal(BookingStatus.Confirmed, afterSuccess.FirstBooking.Status);
        Assert.Equal(FixedNow, afterSuccess.FirstBooking.ConfirmedAt);
        Assert.Equal(before.FirstLink.Id, afterSuccess.FirstLink.Id);
        Assert.Equal(RescheduledStart.AddHours(24), afterSuccess.FirstLink.ExpiresAt);
        Assert.Equal(before.FirstLink.RevokedAt, afterSuccess.FirstLink.RevokedAt);
        Assert.True(before.FirstLink.TokenHash.AsSpan().SequenceEqual(afterSuccess.FirstLink.TokenHash), "The appointment-link token hash changed.");
        Assert.True(string.Equals(before.FirstLink.ProtectedToken, afterSuccess.FirstLink.ProtectedToken, StringComparison.Ordinal), "The protected appointment-link token changed.");
        Assert.Null(afterSuccess.FirstLink.RevokedAt);
        Assert.Single(afterSuccess.Notifications);
        Assert.Equal(NotificationType.Rescheduled, afterSuccess.Notifications[0].Type);
        Assert.Equal(before.Payments, afterSuccess.Payments);

        var occupiedAttempt = await service.RescheduleIndividualAsync(
            ids.BusinessId,
            ids.SecondBookingId,
            new OwnerRescheduleRequest(ids.WorkerId, RescheduledStart));

        Assert.False(occupiedAttempt.Succeeded);
        Assert.Equal("owner_reschedule_slot_unavailable", occupiedAttempt.Error?.Code);

        var afterOccupiedAttempt = await ReadSnapshotAsync(target.ConnectionString, ids);
        Assert.Equal(before.SecondAppointment, afterOccupiedAttempt.SecondAppointment);
        Assert.Equal(before.SecondBooking, afterOccupiedAttempt.SecondBooking);
        Assert.Equal(before.SecondLink.Id, afterOccupiedAttempt.SecondLink.Id);
        Assert.Equal(before.SecondLink.ExpiresAt, afterOccupiedAttempt.SecondLink.ExpiresAt);
        Assert.Equal(before.SecondLink.RevokedAt, afterOccupiedAttempt.SecondLink.RevokedAt);
        Assert.True(before.SecondLink.TokenHash.AsSpan().SequenceEqual(afterOccupiedAttempt.SecondLink.TokenHash), "The stale booking's appointment-link token hash changed.");
        Assert.True(string.Equals(before.SecondLink.ProtectedToken, afterOccupiedAttempt.SecondLink.ProtectedToken, StringComparison.Ordinal), "The stale booking's protected appointment-link token changed.");
        Assert.Equal(afterSuccess.Notifications, afterOccupiedAttempt.Notifications);
        Assert.Equal(before.Payments, afterOccupiedAttempt.Payments);

        output.WriteLine("SQL assertions passed: appointment moved, booking confirmed, link token material preserved with a 24-hour expiry, one Rescheduled outbox row, and payments unchanged.");
        output.WriteLine("SQL assertions passed: the stale occupied target left its appointment, booking, link, and outbox unchanged.");
    }

    private static SqlTarget ValidateTarget(string suppliedConnectionString)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("WORKUP_T11_SQL_CONNECTION could not be parsed; no SQL connection was opened.");
        }

        var databaseName = supplied.InitialCatalog;
        var safeName = databaseName.Length <= 128
            && databaseName.StartsWith(RequiredDatabasePrefix, StringComparison.Ordinal)
            && SafeDatabaseName.IsMatch(databaseName);
        if (!string.Equals(supplied.DataSource, LocalDbDataSource, StringComparison.OrdinalIgnoreCase)
            || !supplied.IntegratedSecurity
            || !safeName
            || !string.IsNullOrEmpty(supplied.UserID)
            || !string.IsNullOrEmpty(supplied.Password)
            || !string.IsNullOrEmpty(supplied.AttachDBFilename)
            || supplied.UserInstance
            || !string.IsNullOrEmpty(supplied.FailoverPartner))
        {
            throw new InvalidOperationException(
                "WORKUP_T11_SQL_CONNECTION must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT11Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated local instance and database so unneeded connection-string options cannot
        // redirect this test or add credentials, attach files, or other connection behavior.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = LocalDbDataSource,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T11 SQL integration test",
            TrustServerCertificate = true
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<bool> DatabaseExistsViaSqlcmdAsync(string databaseName)
    {
        var query = $"SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'{databaseName}') IS NULL THEN 0 ELSE 1 END;";
        var startInfo = new ProcessStartInfo("sqlcmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add(LocalDbDataSource);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add("master");
        startInfo.ArgumentList.Add("-E");
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add("-1");
        startInfo.ArgumentList.Add("-W");
        startInfo.ArgumentList.Add("-Q");
        startInfo.ArgumentList.Add(query);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start sqlcmd for the master-database preflight.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"sqlcmd master preflight failed with exit code {process.ExitCode}: {stderr.Trim()}");

        var resultLines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = resultLines
            .Select(line => int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (int?)null)
            .SingleOrDefault(value => value.HasValue);
        if (result is not (0 or 1))
            throw new InvalidOperationException($"sqlcmd master preflight returned an unexpected result: {stdout.Trim()}");
        return result == 1;
    }

    private static async Task<SeededIds> SeedSyntheticDataAsync(string connectionString, FakeClock clock)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessId = await InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @createdAt)",
            ("@name", "Synthetic T11 Business"), ("@slug", "synthetic-t11-owner-reschedule"),
            ("@phone", "0000000000"), ("@email", "t11.business@example.invalid"), ("@createdAt", clock.Now));
        var serviceId = await InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, 1)",
            ("@businessId", businessId), ("@name", "Synthetic T11 Service"), ("@price", 125.50m), ("@duration", 45));
        var workerId = await InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", businessId), ("@firstName", "Synthetic"), ("@lastName", "Worker"),
            ("@phone", "0000000000"), ("@cuil", "20123456789"));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO WorkerSchedules (WorkerId, DayOfWeek, StartTime, EndTime) VALUES (@workerId, @day, @start, @end)",
            ("@workerId", workerId), ("@day", (byte)DayOfWeek.Wednesday), ("@start", new TimeSpan(9, 0, 0)), ("@end", new TimeSpan(17, 0, 0)));
        var clientId = await InsertIdAsync(connection, transaction,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
            ("@businessId", businessId), ("@firstName", "Synthetic"), ("@lastName", "Client"),
            ("@email", "t11.client@example.invalid"), ("@phone", "0000000000"), ("@createdAt", clock.Now));

        var firstAppointmentId = await InsertAppointmentAsync(connection, transaction, businessId, serviceId, workerId, OriginalStart, clock.Now);
        var secondAppointmentId = await InsertAppointmentAsync(connection, transaction, businessId, serviceId, workerId, OccupiedBookingStart, clock.Now);
        var firstBookingId = await InsertBookingAsync(connection, transaction, businessId, firstAppointmentId, clientId, clock.Now);
        var secondBookingId = await InsertBookingAsync(connection, transaction, businessId, secondAppointmentId, clientId, clock.Now);
        var firstLinkId = await InsertLinkAsync(connection, transaction, firstBookingId, 1, OriginalStart.AddHours(24), clock.Now);
        var secondLinkId = await InsertLinkAsync(connection, transaction, secondBookingId, 2, OccupiedBookingStart.AddHours(24), clock.Now);

        await ExecuteAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt) VALUES (@businessId, @clientId, @concept, @bookingId, NULL, @amount, @listPrice, NULL, @paidAt, @recordedAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@concept", PaymentConcept.IndividualAppointment),
            ("@bookingId", firstBookingId), ("@amount", 125.50m), ("@listPrice", 125.50m),
            ("@paidAt", clock.Now), ("@recordedAt", clock.Now));

        await transaction.CommitAsync();
        return new SeededIds(businessId, workerId, firstAppointmentId, firstBookingId,
            secondAppointmentId, secondBookingId, firstLinkId, secondLinkId);
    }

    private static Task<int> InsertAppointmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DateTime startsAt, DateTime createdAt) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.Individual), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(45)),
            ("@origin", AppointmentOrigin.Reserved), ("@status", AppointmentStatus.Scheduled), ("@createdAt", createdAt));

    private static Task<int> InsertBookingAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int appointmentId, int clientId, DateTime createdAt) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @status, @createdAt)",
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@status", BookingStatus.Pending), ("@createdAt", createdAt));

    private static Task<int> InsertLinkAsync(
        SqlConnection connection, SqlTransaction transaction, int bookingId, byte discriminator, DateTime expiresAt, DateTime createdAt) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO AppointmentLinks (BookingId, TokenHash, ProtectedToken, ExpiresAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@bookingId, @tokenHash, @protectedToken, @expiresAt, @createdAt)",
            ("@bookingId", bookingId), ("@tokenHash", CreateSyntheticHash(discriminator)),
            ("@protectedToken", $"synthetic-protected-token-{discriminator}"), ("@expiresAt", expiresAt), ("@createdAt", createdAt));

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(string connectionString, SeededIds ids)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return new DatabaseSnapshot(
            await ReadAppointmentAsync(connection, ids.FirstAppointmentId),
            await ReadBookingAsync(connection, ids.FirstBookingId),
            await ReadLinkAsync(connection, ids.FirstLinkId),
            await ReadAppointmentAsync(connection, ids.SecondAppointmentId),
            await ReadBookingAsync(connection, ids.SecondBookingId),
            await ReadLinkAsync(connection, ids.SecondLinkId),
            await ReadPaymentsAsync(connection),
            await ReadNotificationsAsync(connection));
    }

    private static async Task<AppointmentSnapshot> ReadAppointmentAsync(SqlConnection connection, int id)
    {
        await using var command = new SqlCommand("SELECT Id, StartsAt, EndsAt FROM Appointments WHERE Id = @id", connection);
        AddParameter(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The seeded appointment was not found.");
        return new AppointmentSnapshot(reader.GetInt32(0), reader.GetDateTime(1), reader.GetDateTime(2));
    }

    private static async Task<BookingSnapshot> ReadBookingAsync(SqlConnection connection, int id)
    {
        await using var command = new SqlCommand("SELECT Id, AppointmentId, Status, ConfirmedAt FROM Bookings WHERE Id = @id", connection);
        AddParameter(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The seeded booking was not found.");
        return new BookingSnapshot(reader.GetInt32(0), reader.GetInt32(1), (BookingStatus)reader.GetByte(2), reader.IsDBNull(3) ? null : reader.GetDateTime(3));
    }

    private static async Task<LinkSnapshot> ReadLinkAsync(SqlConnection connection, int id)
    {
        await using var command = new SqlCommand("SELECT Id, TokenHash, ProtectedToken, ExpiresAt, RevokedAt FROM AppointmentLinks WHERE Id = @id", connection);
        AddParameter(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The seeded appointment link was not found.");
        return new LinkSnapshot(reader.GetInt32(0), (byte[])reader.GetValue(1), reader.GetString(2), reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4));
    }

    private static async Task<PaymentSnapshot[]> ReadPaymentsAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand(
            "SELECT Id, BusinessId, ClientId, Concept, BookingId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt FROM Payments ORDER BY Id",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var payments = new List<PaymentSnapshot>();
        while (await reader.ReadAsync())
        {
            payments.Add(new PaymentSnapshot(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetByte(3),
                reader.GetInt32(4), reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetDateTime(8), reader.GetDateTime(9)));
        }

        return payments.ToArray();
    }

    private static async Task<NotificationSnapshot[]> ReadNotificationsAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT Id, Type FROM Notifications ORDER BY Id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var notifications = new List<NotificationSnapshot>();
        while (await reader.ReadAsync())
            notifications.Add(new NotificationSnapshot(reader.GetInt32(0), (NotificationType)reader.GetByte(1)));
        return notifications.ToArray();
    }

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            AddParameter(command, name, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            AddParameter(command, name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(SqlCommand command, string name, object? value)
    {
        if (value is DateTime dateTime)
        {
            command.Parameters.Add(name, SqlDbType.DateTime2).Value = dateTime;
            return;
        }

        if (value is Enum enumValue)
            value = Convert.ToByte(enumValue, CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static byte[] CreateSyntheticHash(byte discriminator) =>
        Enumerable.Range(0, 32).Select(index => (byte)(index + discriminator)).ToArray();

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
    private sealed record SeededIds(
        int BusinessId, int WorkerId, int FirstAppointmentId, int FirstBookingId,
        int SecondAppointmentId, int SecondBookingId, int FirstLinkId, int SecondLinkId);
    private sealed record AppointmentSnapshot(int Id, DateTime StartsAt, DateTime EndsAt);
    private sealed record BookingSnapshot(int Id, int AppointmentId, BookingStatus Status, DateTime? ConfirmedAt);
    private sealed record LinkSnapshot(int Id, byte[] TokenHash, string ProtectedToken, DateTime ExpiresAt, DateTime? RevokedAt);
    private sealed record PaymentSnapshot(
        int Id, int BusinessId, int ClientId, byte Concept, int BookingId, decimal Amount,
        decimal? ListPrice, string? PriceReason, DateTime PaidAt, DateTime RecordedAt);
    private sealed record NotificationSnapshot(int Id, NotificationType Type);
    private sealed record DatabaseSnapshot(
        AppointmentSnapshot FirstAppointment, BookingSnapshot FirstBooking, LinkSnapshot FirstLink,
        AppointmentSnapshot SecondAppointment, BookingSnapshot SecondBooking, LinkSnapshot SecondLink,
        PaymentSnapshot[] Payments, NotificationSnapshot[] Notifications);
}
