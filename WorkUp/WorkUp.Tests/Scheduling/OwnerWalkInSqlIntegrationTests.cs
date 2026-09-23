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

/// <summary>Opt-in SQL coverage. The dedicated LocalDB database is created once and permanently retained.</summary>
public sealed class OwnerWalkInSqlIntegrationTests(ITestOutputHelper output)
{
    private const string DatabaseName = "WorkUpDemoT12SqlWalkIn20260923180927";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private static readonly DateTime Now = new(2026, 3, 10, 10, 5, 7);

    [Fact]
    public async Task Walk_in_persists_overlap_payment_note_and_rejects_invalid_tenant_or_disabled_context()
    {
        var supplied = Environment.GetEnvironmentVariable("WORKUP_T12_WALKIN_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine("SKIPPED: WORKUP_T12_WALKIN_SQL_CONNECTION is unset; no SQL connection opened.");
            return;
        }

        SqlConnectionStringBuilder input;
        try { input = new SqlConnectionStringBuilder(supplied); }
        catch (ArgumentException ex) { throw new InvalidOperationException("Invalid SQL connection string; no SQL connection opened.", ex); }
        if (!string.Equals(input.DataSource, LocalDb, StringComparison.OrdinalIgnoreCase)
            || !input.IntegratedSecurity || !string.Equals(input.InitialCatalog, DatabaseName, StringComparison.Ordinal)
            || !Regex.IsMatch(input.InitialCatalog, @"\AWorkUpDemoT12Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)
            || input.InitialCatalog.Length > 128 || !string.IsNullOrEmpty(input.UserID)
            || !string.IsNullOrEmpty(input.Password) || !string.IsNullOrEmpty(input.AttachDBFilename)
            || input.UserInstance || !string.IsNullOrEmpty(input.FailoverPartner))
            throw new InvalidOperationException("Requires the dedicated new WorkUpDemoT12Sql* LocalDB database with integrated security; no SQL connection opened.");

        var master = new SqlConnectionStringBuilder { DataSource = LocalDb, InitialCatalog = "master", IntegratedSecurity = true, ConnectTimeout = 15, TrustServerCertificate = true };
        var target = new SqlConnectionStringBuilder { DataSource = LocalDb, InitialCatalog = DatabaseName, IntegratedSecurity = true, ConnectTimeout = 15, TrustServerCertificate = true };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var check = new SqlCommand("SELECT DB_ID(@database)", connection))
            {
                check.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = DatabaseName;
                var existing = await check.ExecuteScalarAsync();
                if (existing is not DBNull)
                    throw new InvalidOperationException($"Refusing existing database {DatabaseName}; no reuse or teardown.");
            }
            output.WriteLine($"master DB_ID('{DatabaseName}') = NULL (absent)");
            // CREATE DATABASE is not permitted inside a transaction. SQL Server executes this statement atomically;
            // a concurrent creator causes CREATE to fail rather than opening or reusing its database.
            await using var create = new SqlCommand($"CREATE DATABASE [{DatabaseName}]", connection);
            await create.ExecuteNonQueryAsync();
            output.WriteLine($"Created database {DatabaseName}; it will be retained permanently.");
        }
        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        output.WriteLine($"Applied {scripts.Count} migration script(s).");

        await using var seed = new SqlConnection(target.ConnectionString);
        await seed.OpenAsync();
        async Task<int> Insert(string sql, params (string Name, object Value)[] args)
        {
            await using var command = new SqlCommand(sql, seed);
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        async Task<int> Business(string suffix, bool allowed) => await Insert(
            "INSERT INTO Businesses (Name, Slug, ContactPhone, AllowsWalkIns, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '000', @allowed, @now)",
            ("@name", "Synthetic " + suffix), ("@slug", "synthetic-t12-walkin-" + suffix), ("@allowed", allowed), ("@now", Now));
        var tenant = await Business("primary", true);
        var other = await Business("other", false);
        async Task<(int Client, int Service, int Worker)> Entities(int business, string suffix)
        {
            var client = await Insert("INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Client', @email, @now)",
                ("@business", business), ("@email", suffix + "@example.invalid"), ("@now", Now));
            var service = await Insert("INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Service', 125.50, 30, 1)", ("@business", business));
            var worker = await Insert("INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Worker', '000', @cuil)",
                ("@business", business), ("@cuil", suffix == "primary" ? "20111111111" : "20222222222"));
            return (client, service, worker);
        }
        var first = await Entities(tenant, "primary");
        var second = await Entities(other, "other");
        async Task<int> Appointment(DateTime start) => await Insert(
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, @start, @end, 1, @origin, @status, @now)",
            ("@business", tenant), ("@kind", (byte)AppointmentKind.Individual), ("@service", first.Service), ("@worker", first.Worker),
            ("@start", start), ("@end", start.AddMinutes(30)), ("@origin", (byte)AppointmentOrigin.Reserved), ("@status", (byte)AppointmentStatus.Scheduled), ("@now", Now));
        var occupied = await Appointment(Now.AddMinutes(-10));
        var cancelled = await Appointment(Now.AddHours(2));
        async Task Booking(int appointment, BookingStatus status) => await Insert(
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, Status, CreatedAt, CancelledAt) OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, @status, @now, @cancelled)",
            ("@business", tenant), ("@appointment", appointment), ("@client", first.Client), ("@status", (byte)status), ("@now", Now),
            ("@cancelled", status == BookingStatus.Cancelled ? Now : DBNull.Value));
        await Booking(occupied, BookingStatus.Confirmed);
        await Booking(cancelled, BookingStatus.Cancelled);

        var clock = new FakeClock { Now = Now };
        await using var session = new DbSession(target.ConnectionString);
        var serviceUnderTest = new OwnerWalkInService(new SqlOwnerWalkInRepository(session), session, clock);
        var result = await serviceUnderTest.RegisterAsync(tenant, new(first.Client, first.Service, first.Worker, 90m, "Discount", true, "  Next time  "));
        Assert.True(result.Succeeded, result.Error?.Code ?? "walk-in failed");
        Assert.True(result.Value.HasOverlap);
        await using (var verify = new SqlCommand("""
            SELECT a.BusinessId, a.Kind, a.Origin, a.Status, a.StartsAt, a.EndsAt, b.Status, b.AgreedPrice,
                   b.PriceReason, b.AttendanceRecordedAt, p.Concept, p.Amount, p.ListPrice, p.PriceReason,
                   n.Text, (SELECT COUNT(*) FROM Payments WHERE BookingId = b.Id)
            FROM Appointments a JOIN Bookings b ON b.AppointmentId = a.Id
            LEFT JOIN Payments p ON p.BookingId = b.Id LEFT JOIN ClientNotes n ON n.AppointmentId = a.Id
            WHERE a.Id = @appointment AND b.Id = @booking
            """, seed))
        {
            verify.Parameters.AddWithValue("@appointment", result.Value.AppointmentId);
            verify.Parameters.AddWithValue("@booking", result.Value.BookingId);
            await using var row = await verify.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(tenant, row.GetInt32(0));
            Assert.Equal((byte)AppointmentKind.Individual, row.GetByte(1));
            Assert.Equal((byte)AppointmentOrigin.WalkIn, row.GetByte(2));
            Assert.Equal((byte)AppointmentStatus.Scheduled, row.GetByte(3));
            Assert.Equal(Now, row.GetDateTime(4));
            Assert.Equal(Now.AddMinutes(30), row.GetDateTime(5));
            Assert.Equal((byte)BookingStatus.Attended, row.GetByte(6));
            Assert.Equal(90m, row.GetDecimal(7));
            Assert.Equal("Discount", row.GetString(8));
            Assert.Equal(Now, row.GetDateTime(9));
            Assert.Equal((byte)PaymentConcept.WalkIn, row.GetByte(10));
            Assert.Equal(90m, row.GetDecimal(11));
            Assert.Equal(125.50m, row.GetDecimal(12));
            Assert.Equal("Discount", row.GetString(13));
            Assert.Equal("Next time", row.GetString(14));
            Assert.Equal(1, row.GetInt32(15));
            Assert.False(await row.ReadAsync());
        }
        foreach (var request in new[] { new OwnerWalkInRequest(second.Client, first.Service, first.Worker),
                     new OwnerWalkInRequest(first.Client, second.Service, first.Worker),
                     new OwnerWalkInRequest(first.Client, first.Service, second.Worker) })
            Assert.False((await serviceUnderTest.RegisterAsync(tenant, request)).Succeeded);
        Assert.False((await serviceUnderTest.RegisterAsync(other, new(second.Client, second.Service, second.Worker))).Succeeded);
        clock.Now = Now.AddHours(2);
        var noPayment = await serviceUnderTest.RegisterAsync(tenant, new(first.Client, first.Service, first.Worker));
        Assert.True(noPayment.Succeeded, noPayment.Error?.Code ?? "walk-in failed");
        Assert.False(noPayment.Value.HasOverlap); // The only booking at this time is cancelled.
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Payments WHERE BookingId = @booking", seed);
        count.Parameters.AddWithValue("@booking", noPayment.Value.BookingId);
        Assert.Equal(0, (int)(await count.ExecuteScalarAsync())!);
        output.WriteLine("SQL assertions passed: overlapping arrival persisted with payment and note; cancelled-only overlap ignored; cross-tenant/disabled rejected; optional payment absent.");
    }
}
