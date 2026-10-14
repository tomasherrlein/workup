using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.Data;
using WorkUp.Data.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.BusinessLogic.Scheduling;
using Xunit.Abstractions;

namespace WorkUp.Tests.Scheduling;

/// <summary>Opt-in, retained synthetic LocalDB fixture. Never connects to the default database or tears down a target.</summary>
public sealed class OwnerDaySqlIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private static readonly DateOnly Day = new(2026, 3, 10);

    [Fact]
    public async Task Day_projection_and_paid_total_obey_tenant_membership_payment_and_date_boundaries()
    {
        var supplied = Environment.GetEnvironmentVariable("WORKUP_T12_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine("N/A: WORKUP_T12_SQL_CONNECTION absent; no SQL connection opened.");
            return;
        }

        SqlConnectionStringBuilder input;
        try { input = new SqlConnectionStringBuilder(supplied); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid T12 connection string; no SQL connection opened."); }
        var name = input.InitialCatalog + "_OwnerDay";
        if (!string.Equals(input.DataSource, Server, StringComparison.OrdinalIgnoreCase) || !input.IntegratedSecurity
            || !string.IsNullOrEmpty(input.UserID) || !string.IsNullOrEmpty(input.Password)
            || !string.IsNullOrEmpty(input.AttachDBFilename) || input.UserInstance
            || !string.IsNullOrEmpty(input.FailoverPartner)
            || name.Length > 128 || !Regex.IsMatch(name, @"\AWorkUpDemoT12Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("T12 requires integrated (localdb)\\MSSQLLocalDB and a safe WorkUpDemoT12Sql* database; no SQL connection opened.");

        var connectionString = new SqlConnectionStringBuilder { DataSource = Server, InitialCatalog = name,
            IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 15 }.ConnectionString;
        output.WriteLine($"Target: {name}");
        var psi = new ProcessStartInfo("sqlcmd") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-I", "-S", Server, "-d", "master", "-E", "-b", "-h", "-1", "-W", "-Q",
            $"SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'{name}') IS NULL THEN 0 ELSE 1 END;" }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("sqlcmd preflight unavailable.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"master preflight failed: {stderr.Trim()}");
        Assert.Equal("0", stdout.Trim());
        output.WriteLine($"DB_ID({name}) absent; attempting atomic creation before migration.");
        // SQL identifiers cannot be parameters. The strict allowlist above excludes brackets and
        // quotes; quoting the identifier also keeps CREATE DATABASE bound to this exact target.
        var masterConnectionString = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{name}]", master);
            await create.ExecuteNonQueryAsync(); // A competing creator makes this fail; never migrate its database.
        }
        var scripts = await new DatabaseMigrator(connectionString).MigrateAsync();
        output.WriteLine($"Created {name}; applied {scripts.Count} scripts; retained without teardown.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var now = Day.ToDateTime(new TimeOnly(8, 0));
        var tenant = await Insert(connection, "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES ('T12', 't12-test', '000', 't12@example.invalid', @now)", ("@now", now));
        var other = await Insert(connection, "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES ('Other', 't12-other', '000', 'other@example.invalid', @now)", ("@now", now));
        async Task<(int Service, int Worker, int Client)> SeedTenant(int business)
        {
            var service = await Insert(connection, "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@id, 'Test', 100, 45, 3)", ("@id", business));
            var worker = await Insert(connection, "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@id, 'Test', 'Worker', '000', @cuil)", ("@id", business), ("@cuil", $"20{business:000000000}"));
            var client = await Insert(connection, "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@id, 'Test', 'Client', @email, '000', @now)", ("@id", business), ("@email", $"client{business}@example.invalid"), ("@now", now));
            return (service, worker, client);
        }
        var a = await SeedTenant(tenant);
        var b = await SeedTenant(other);
        async Task<int> Booking(int business, (int Service, int Worker, int Client) ids, DateTime start,
            AppointmentKind kind = AppointmentKind.Individual, AppointmentStatus status = AppointmentStatus.Scheduled,
            BookingModality? modality = null, AppointmentOrigin? origin = AppointmentOrigin.Reserved)
        {
            var appointment = await Insert(connection, "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, @start, @end, @capacity, @origin, @status, @now)",
                ("@business", business), ("@kind", kind), ("@service", ids.Service), ("@worker", ids.Worker), ("@start", start),
                ("@end", start.AddMinutes(45)), ("@capacity", kind == AppointmentKind.Individual ? 1 : 3),
                ("@origin", kind == AppointmentKind.Individual ? origin : null), ("@status", status), ("@now", now));
            int? enrollment = modality == BookingModality.Fixed
                ? await Insert(connection, "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @client, 1, 100, @now)", ("@business", business), ("@client", ids.Client), ("@now", now))
                : null;
            return await Insert(connection, "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, @enrollment, @modality, @status, @now)",
                ("@business", business), ("@appointment", appointment), ("@client", ids.Client), ("@enrollment", enrollment), ("@modality", modality), ("@status", BookingStatus.Pending), ("@now", now));
        }
        var start = Day.ToDateTime(new TimeOnly(10, 0));
        var paid = await Booking(tenant, a, start);
        var fixedBooking = await Booking(tenant, a, start.AddHours(1), AppointmentKind.ClassSession, modality: BookingModality.Fixed);
        var suspended = await Booking(tenant, a, start.AddHours(2), AppointmentKind.ClassSession, AppointmentStatus.Suspended, BookingModality.DropIn);
        var dropIn = await Booking(tenant, a, start.AddHours(3), AppointmentKind.ClassSession, modality: BookingModality.DropIn);
        var annulled = await Booking(tenant, a, start.AddHours(4));
        var refunded = await Booking(tenant, a, start.AddHours(5));
        await Booking(other, b, start);
        await Booking(tenant, a, start.AddDays(1));
        async Task<int> Payment(int business, int booking, decimal amount, DateTime when, PaymentConcept concept = PaymentConcept.IndividualAppointment) =>
            await Insert(connection, "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, Amount, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@business, @client, @concept, @booking, @amount, @when, @now)",
                ("@business", business), ("@client", business == tenant ? a.Client : b.Client), ("@concept", concept), ("@booking", booking), ("@amount", amount), ("@when", when), ("@now", now));
        await Payment(tenant, paid, 100m, start);
        await Payment(tenant, paid, 25m, start); // two payments cannot multiply the booking row
        await Payment(tenant, fixedBooking, 50m, start);
        await Payment(tenant, suspended, 40m, start, PaymentConcept.DropInClass);
        var annulledPayment = await Payment(tenant, annulled, 30m, start);
        await Execute(connection, "INSERT INTO PaymentAnnulments (PaymentId, Reason, AnnulledAt, AnnulledBy) VALUES (@id, 'error', @now, 'owner')", ("@id", annulledPayment), ("@now", now));
        var refundPayment = await Payment(tenant, refunded, 60m, start);
        await Execute(connection, "INSERT INTO Refunds (PaymentId, Amount, RefundedOn, Reason, RecordedAt) VALUES (@id, 20, @day, 'partial', @now)", ("@id", refundPayment), ("@day", Day), ("@now", now));
        await Payment(tenant, dropIn, 10m, start.AddDays(-1), PaymentConcept.DropInClass);
        await Payment(other, await Booking(other, b, start.AddHours(1)), 900m, start);
        await Payment(tenant, await Booking(tenant, a, start.AddDays(-1)), 70m, Day.AddDays(-1).ToDateTime(new TimeOnly(23, 59, 59)));
        await Payment(tenant, await Booking(tenant, a, start.AddDays(1).AddHours(1)), 80m, Day.AddDays(1).ToDateTime(TimeOnly.MinValue));

        await using var session = new DbSession(connectionString);
        var repository = new SqlOwnerDayRepository(session);
        var rows = await repository.ListDayAsync(tenant, Day);
        Assert.Equal(6, rows.Count);
        Assert.Equal(6, rows.Select(row => row.BookingId).Distinct().Count());
        Assert.Equal(OwnerDayPaymentState.Paid, Assert.Single(rows, row => row.BookingId == paid).PaymentState);
        Assert.Equal(OwnerDayPaymentState.NotApplicable, Assert.Single(rows, row => row.BookingId == fixedBooking).PaymentState);
        Assert.Equal(OwnerDayPaymentState.NotApplicable, Assert.Single(rows, row => row.BookingId == suspended).PaymentState);
        Assert.Equal(OwnerDayPaymentState.Paid, Assert.Single(rows, row => row.BookingId == dropIn).PaymentState);
        Assert.Equal(OwnerDayPaymentState.Pending, Assert.Single(rows, row => row.BookingId == annulled).PaymentState);
        Assert.Equal(OwnerDayPaymentState.Paid, Assert.Single(rows, row => row.BookingId == refunded).PaymentState);
        Assert.Equal(255m, await repository.GetPaidTotalAsync(tenant, Day)); // 100+25+50+40+60-20
        Assert.Equal(900m, await repository.GetPaidTotalAsync(other, Day));
        Assert.Empty(await repository.ListDayAsync(tenant, Day.AddDays(-2)));
        output.WriteLine("SQL projection, tenant/date isolation, non-multiplication, and effective daily total passed.");
    }

    private static async Task<int> Insert(SqlConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = Create(connection, sql, args);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    private static async Task Execute(SqlConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = Create(connection, sql, args);
        await command.ExecuteNonQueryAsync();
    }
    private static SqlCommand Create(SqlConnection connection, string sql, (string Name, object? Value)[] args)
    {
        var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            var actual = value is Enum enumeration ? Convert.ToByte(enumeration, CultureInfo.InvariantCulture) : value;
            if (actual is DateTime) command.Parameters.Add(name, SqlDbType.DateTime2).Value = actual;
            else if (actual is DateOnly date) command.Parameters.Add(name, SqlDbType.Date).Value = date.ToDateTime(TimeOnly.MinValue);
            else command.Parameters.AddWithValue(name, actual ?? DBNull.Value);
        }
        return command;
    }
}
