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
/// Opt-in SQL coverage for the client panel agenda (T21.1). It requires <c>WORKUP_T21_SQL_CONNECTION</c> pointing at
/// a brand-new, dedicated <c>(localdb)\MSSQLLocalDB</c> database named <c>WorkUpDemoT21Sql*</c>; the database is
/// created once and permanently retained, never reused and never dropped. Without the variable the fact reports N/A
/// and opens no connection.
/// </summary>
public sealed class ClientPanelSqlIntegrationTests(ITestOutputHelper output)
{
    private const string DatabaseName = "WorkUpDemoT21SqlClientPanel20260926";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private static readonly DateTime Now = new(2026, 3, 10, 10, 0, 0);

    [Fact]
    public async Task Agenda_reads_only_the_authenticated_clients_own_rows_with_one_query()
    {
        var supplied = Environment.GetEnvironmentVariable("WORKUP_T21_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine("SKIPPED: WORKUP_T21_SQL_CONNECTION is unset; no SQL connection opened.");
            return;
        }

        SqlConnectionStringBuilder input;
        try { input = new SqlConnectionStringBuilder(supplied); }
        catch (ArgumentException ex) { throw new InvalidOperationException("Invalid SQL connection string; no SQL connection opened.", ex); }
        if (!string.Equals(input.DataSource, LocalDb, StringComparison.OrdinalIgnoreCase)
            || !input.IntegratedSecurity || !string.Equals(input.InitialCatalog, DatabaseName, StringComparison.Ordinal)
            || !Regex.IsMatch(input.InitialCatalog, @"\AWorkUpDemoT21Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)
            || input.InitialCatalog.Length > 128 || !string.IsNullOrEmpty(input.UserID)
            || !string.IsNullOrEmpty(input.Password) || !string.IsNullOrEmpty(input.AttachDBFilename)
            || input.UserInstance || !string.IsNullOrEmpty(input.FailoverPartner))
            throw new InvalidOperationException("Requires the dedicated new WorkUpDemoT21Sql* LocalDB database with integrated security; no SQL connection opened.");

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
            await using var create = new SqlCommand($"CREATE DATABASE [{DatabaseName}]", connection);
            await create.ExecuteNonQueryAsync();
            output.WriteLine($"Created database {DatabaseName}; it will be retained permanently.");
        }
        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        output.WriteLine($"Applied {scripts.Count} migration script(s): {string.Join(", ", scripts)}");

        await using var seed = new SqlConnection(target.ConnectionString);
        await seed.OpenAsync();
        async Task<int> Insert(string sql, params (string Name, object Value)[] args)
        {
            await using var command = new SqlCommand(sql, seed);
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        async Task<int> Business(string suffix) => await Insert(
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '000', @now)",
            ("@name", "Synthetic " + suffix), ("@slug", "synthetic-t21-" + suffix), ("@now", Now));
        async Task<int> Client(int business, string suffix) => await Insert(
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Client', @email, @now)",
            ("@business", business), ("@email", suffix + "@example.invalid"), ("@now", Now));
        async Task<int> Service(int business) => await Insert(
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic Service', 150.00, 60, 8)",
            ("@business", business));
        async Task<int> Worker(int business, string cuil) => await Insert(
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@business, 'Synthetic', 'Worker', '000', @cuil)",
            ("@business", business), ("@cuil", cuil));
        async Task<int> GridClass(int business, int service, int worker) => await Insert(
            "INSERT INTO GridClasses (BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime) OUTPUT INSERTED.Id VALUES (@business, @service, @worker, @day, '19:00')",
            ("@business", business), ("@service", service), ("@worker", worker), ("@day", (byte)DayOfWeek.Tuesday));
        async Task<int> IndividualAppointment(int business, int service, int worker, DateTime start) => await Insert(
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, @start, @end, 1, @origin, @status, @now)",
            ("@business", business), ("@kind", (byte)AppointmentKind.Individual), ("@service", service), ("@worker", worker),
            ("@start", start), ("@end", start.AddMinutes(30)), ("@origin", (byte)AppointmentOrigin.Reserved),
            ("@status", (byte)AppointmentStatus.Scheduled), ("@now", Now));
        async Task<int> ClassSession(int business, int service, int worker, int gridClass, DateTime start, int minutes) => await Insert(
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Status, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @kind, @service, @worker, @grid, @start, @end, 8, @status, @now)",
            ("@business", business), ("@kind", (byte)AppointmentKind.ClassSession), ("@service", service), ("@worker", worker),
            ("@grid", gridClass), ("@start", start), ("@end", start.AddMinutes(minutes)),
            ("@status", (byte)AppointmentStatus.Scheduled), ("@now", Now));
        async Task<int> Enrollment(int business, int client) => await Insert(
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@business, @client, 1, @status, 1000.00, @now)",
            ("@business", business), ("@client", client), ("@status", (byte)EnrollmentStatus.Active), ("@now", Now));
        async Task<int> Booking(int business, int appointment, int client, int? enrollment, BookingModality? modality, BookingStatus status) => await Insert(
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt) OUTPUT INSERTED.Id VALUES (@business, @appointment, @client, @enrollment, @modality, @status, @now, @cancelled)",
            ("@business", business), ("@appointment", appointment), ("@client", client),
            ("@enrollment", (object?)enrollment ?? DBNull.Value),
            ("@modality", modality is null ? DBNull.Value : (byte)modality.Value),
            ("@status", (byte)status), ("@now", Now),
            ("@cancelled", status == BookingStatus.Cancelled ? Now : (object)DBNull.Value));

        var tenant = await Business("primary");
        var otherTenant = await Business("other");
        var ownClient = await Client(tenant, "primary-own");
        var sameTenantOtherClient = await Client(tenant, "primary-other");
        var otherTenantClient = await Client(otherTenant, "other-own");

        var tenantService = await Service(tenant);
        var tenantWorker = await Worker(tenant, "20111111111");
        var tenantGridClass = await GridClass(tenant, tenantService, tenantWorker);
        var enrollment = await Enrollment(tenant, ownClient);

        var individualUpcoming = await Booking(tenant, await IndividualAppointment(tenant, tenantService, tenantWorker, Now.AddHours(2)), ownClient, null, null, BookingStatus.Pending);
        var individualPast = await Booking(tenant, await IndividualAppointment(tenant, tenantService, tenantWorker, Now.AddHours(-2)), ownClient, null, null, BookingStatus.Attended);
        var fixedUpcoming = await Booking(tenant, await ClassSession(tenant, tenantService, tenantWorker, tenantGridClass, Now.AddDays(1).Date.AddHours(19), 60), ownClient, enrollment, BookingModality.Fixed, BookingStatus.Pending);
        var dropInUpcoming = await Booking(tenant, await ClassSession(tenant, tenantService, tenantWorker, tenantGridClass, Now.AddDays(1).Date.AddHours(21), 60), ownClient, null, BookingModality.DropIn, BookingStatus.Confirmed);
        var dropInCancelled = await Booking(tenant, await ClassSession(tenant, tenantService, tenantWorker, tenantGridClass, Now.AddDays(-1).Date.AddHours(19), 60), ownClient, null, BookingModality.DropIn, BookingStatus.Cancelled);
        var sameTenantOtherBooking = await Booking(tenant, await IndividualAppointment(tenant, tenantService, tenantWorker, Now.AddHours(3)), sameTenantOtherClient, null, null, BookingStatus.Pending);

        var otherService = await Service(otherTenant);
        var otherWorker = await Worker(otherTenant, "20222222222");
        var otherTenantBooking = await Booking(otherTenant, await IndividualAppointment(otherTenant, otherService, otherWorker, Now.AddHours(4)), otherTenantClient, null, null, BookingStatus.Pending);

        var clock = new FakeClock { Now = Now };
        await using var session = new DbSession(target.ConnectionString);
        var serviceUnderTest = new ClientPanelService(new SqlClientPanelRepository(session), clock);

        var agenda = await serviceUnderTest.GetAgendaAsync(tenant, ownClient);

        Assert.Equal(new[] { individualUpcoming, fixedUpcoming, dropInUpcoming }, agenda.Upcoming.Select(booking => booking.BookingId));
        Assert.Equal(new[] { individualPast, dropInCancelled }, agenda.History.Select(booking => booking.BookingId));

        Assert.Equal(AppointmentKind.Individual, agenda.Upcoming[0].AppointmentKind);
        Assert.Null(agenda.Upcoming[0].Modality);
        Assert.True(agenda.Upcoming[0].CanConfirmIntention);
        Assert.True(agenda.Upcoming[0].CanCancel);
        Assert.Equal("Synthetic Service", agenda.Upcoming[0].ServiceName);
        Assert.Equal("Synthetic Worker", agenda.Upcoming[0].WorkerName);
        Assert.Equal(Now.AddHours(2), agenda.Upcoming[0].StartsAt);
        Assert.Equal(Now.AddHours(2).AddMinutes(30), agenda.Upcoming[0].EndsAt);

        Assert.Equal(AppointmentKind.ClassSession, agenda.Upcoming[1].AppointmentKind);
        Assert.Equal(BookingModality.Fixed, agenda.Upcoming[1].Modality);
        Assert.False(agenda.Upcoming[1].CanConfirmIntention);
        Assert.False(agenda.Upcoming[1].CanCancel);

        Assert.Equal(BookingModality.DropIn, agenda.Upcoming[2].Modality);
        Assert.True(agenda.Upcoming[2].CanCancel);
        Assert.False(agenda.Upcoming[2].CanConfirmIntention);

        Assert.Equal(BookingStatus.Attended, agenda.History[0].BookingStatus);
        Assert.Equal(BookingStatus.Cancelled, agenda.History[1].BookingStatus);

        var returned = agenda.Upcoming.Concat(agenda.History).Select(booking => booking.BookingId).ToArray();
        Assert.DoesNotContain(sameTenantOtherBooking, returned);
        Assert.DoesNotContain(otherTenantBooking, returned);

        output.WriteLine("SQL assertions passed: one query returned only the authenticated client's own individual, fixed and drop-in bookings, ordered upcoming first and history after, cross-client and cross-tenant rows excluded.");
    }
}
