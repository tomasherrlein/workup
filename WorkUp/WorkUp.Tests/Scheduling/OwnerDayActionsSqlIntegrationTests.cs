using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.Data;
using WorkUp.Data.Scheduling;
using WorkUp.Domain.Enums;
using Xunit.Abstractions;

namespace WorkUp.Tests.Scheduling;

/// <summary>Opt-in synthetic LocalDB only; the created database is retained.</summary>
public sealed class OwnerDayActionsSqlIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Missing_tenant_scoped_note_rolls_back_attendance_and_payment()
    {
        var supplied = Environment.GetEnvironmentVariable("WORKUP_T12_ACTIONS_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine("N/A: WORKUP_T12_ACTIONS_SQL_CONNECTION absent; no SQL connection opened.");
            return;
        }
        SqlConnectionStringBuilder input;
        try { input = new SqlConnectionStringBuilder(supplied); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid T12 actions connection string; no SQL connection opened."); }
        const string server = @"(localdb)\MSSQLLocalDB";
        var name = input.InitialCatalog;
        if (!string.Equals(input.DataSource, server, StringComparison.OrdinalIgnoreCase) || !input.IntegratedSecurity
            || !string.IsNullOrEmpty(input.UserID) || !string.IsNullOrEmpty(input.Password)
            || !string.IsNullOrEmpty(input.AttachDBFilename) || input.UserInstance
            || !string.IsNullOrEmpty(input.FailoverPartner) || name.Length > 128
            || !Regex.IsMatch(name, @"\AWorkUpDemoT12Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("T12 actions requires integrated localdb and a safe WorkUpDemoT12Sql* target; no SQL connection opened.");
        var masterString = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", IntegratedSecurity = true,
            TrustServerCertificate = true, ConnectTimeout = 15 }.ConnectionString;
        await using (var master = new SqlConnection(masterString))
        {
            await master.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@name)", master);
            check.Parameters.AddWithValue("@name", name);
            Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
            await using var create = new SqlCommand($"CREATE DATABASE [{name}]", master);
            await create.ExecuteNonQueryAsync(); // Atomic creation: a competing creator fails; never migrate its target.
        }
        var connectionString = new SqlConnectionStringBuilder(masterString) { InitialCatalog = name }.ConnectionString;
        var scripts = await new DatabaseMigrator(connectionString).MigrateAsync();
        output.WriteLine($"Created {name}; applied {scripts.Count} scripts; retained without teardown.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var now = new DateTime(2026, 3, 10, 10, 0, 0);
        var tenant = await Insert(connection, "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES ('Actions', 'actions-test', '000', 'actions@example.invalid', @now)", ("@now", now));
        var service = await Insert(connection, "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@id, 'Test', 100, 45, 1)", ("@id", tenant));
        var worker = await Insert(connection, "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@id, 'Test', 'Worker', '000', @cuil)", ("@id", tenant), ("@cuil", $"20{tenant:000000000}"));
        var client = await Insert(connection, "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@id, 'Test', 'Client', 'actions-client@example.invalid', '000', @now)", ("@id", tenant), ("@now", now));
        var appointment = await Insert(connection, "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@id, @kind, @service, @worker, @start, @end, 1, @origin, @status, @now)",
            ("@id", tenant), ("@kind", AppointmentKind.Individual), ("@service", service), ("@worker", worker), ("@start", now.AddHours(-1)), ("@end", now.AddMinutes(-15)), ("@origin", AppointmentOrigin.Reserved), ("@status", AppointmentStatus.Scheduled), ("@now", now));
        var booking = await Insert(connection, "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@id, @appointment, @client, @status, @now)",
            ("@id", tenant), ("@appointment", appointment), ("@client", client), ("@status", BookingStatus.Pending), ("@now", now));

        await using (var session = new DbSession(connectionString))
        {
            await using var transaction = await session.BeginAsync();
            var repo = new SqlOwnerDayActionsRepository(session);
            Assert.True(await repo.SetStatusAsync(tenant, booking, BookingStatus.Attended, now));
            await repo.InsertPaymentAsync(new(tenant, client, booking, PaymentConcept.IndividualAppointment, 100m, 100m, null, now));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repo.InsertNoteAsync(tenant + 1, client, appointment, "lost note", now));
            // Disposing an uncommitted unit of work must roll back the preceding writes.
        }
        await using var verify = new SqlCommand("SELECT Status FROM Bookings WHERE Id = @booking; SELECT COUNT(*) FROM Payments WHERE BookingId = @booking; SELECT COUNT(*) FROM ClientNotes WHERE AppointmentId = @appointment", connection);
        verify.Parameters.AddWithValue("@booking", booking);
        verify.Parameters.AddWithValue("@appointment", appointment);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((byte)BookingStatus.Pending, reader.GetByte(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
    }

    private static async Task<int> Insert(SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            if (value is DateTime) command.Parameters.Add(name, SqlDbType.DateTime2).Value = value;
            else command.Parameters.AddWithValue(name, value is Enum enumeration ? Convert.ToByte(enumeration, CultureInfo.InvariantCulture) : value);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
