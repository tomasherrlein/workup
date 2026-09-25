using System.ComponentModel;
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
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlClassDetailRepository"/> and the read model it feeds.
/// Requires a new, dedicated database supplied through WORKUP_T16_SQL_CONNECTION; without it every test returns N/A
/// and no SQL connection is opened. Each test derives its own database name from the supplied one, so the whole class
/// runs in a single command, and nothing is torn down: the target is retained on purpose.
/// </summary>
public sealed class ClassDetailSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T16_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    private static readonly DateTime FixedNow = new(2026, 3, 10, 8, 0, 0);

    [Fact]
    public async Task Detail_read_returns_the_session_with_every_annotation_and_the_membership_inputs()
    {
        var target = await ResolveTargetAsync("_Read");
        if (target is null)
            return;

        var ids = await SeedDetailAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassDetailRepository(session);

        var row = await repository.GetSessionAsync(ids.BusinessA, ids.Session);
        Assert.NotNull(row);
        Assert.Equal(ids.Session, row!.AppointmentId);
        Assert.Equal(ids.ServiceA, row.ServiceId);
        Assert.Equal(ids.WorkerA, row.WorkerId);
        Assert.Equal(new DateTime(2026, 3, 12, 18, 0, 0), row.StartsAt);
        Assert.Equal(6, row.MaxCapacity);
        Assert.Equal(AppointmentKind.ClassSession, row.Kind);
        Assert.Equal(AppointmentStatus.Scheduled, row.Status);
        Assert.Null(row.SuspensionReason);

        // Every annotation travels, cancelled included, with its client name, modality and fixed membership inputs.
        Assert.Equal(4, row.Bookings.Count);

        var active = Assert.Single(row.Bookings, booking => booking.ClientId == ids.ActiveClient);
        Assert.Equal("Alumna Vigente", active.ClientName);
        Assert.Equal(BookingModality.Fixed, active.Modality);
        Assert.Equal(ids.ActiveEnrollment, active.EnrollmentId);
        Assert.Equal(EnrollmentStatus.Active, active.EnrollmentStatus);
        var activePeriod = Assert.Single(active.Periods);
        Assert.Equal(new DateOnly(2026, 3, 1), activePeriod.CoverageStart);
        Assert.Equal(new DateOnly(2026, 4, 1), activePeriod.ExpiresOn);
        Assert.True(activePeriod.Covers(new DateOnly(2026, 3, 12)));

        // The second fixed annotation carries its enrollment and its expired period: the pending-renewal input.
        var renewal = Assert.Single(row.Bookings, booking => booking.ClientId == ids.RenewalClient);
        Assert.Equal("Alumna Renovar", renewal.ClientName);
        Assert.Equal(EnrollmentStatus.Active, renewal.EnrollmentStatus);
        Assert.DoesNotContain(renewal.Periods, period => period.Covers(new DateOnly(2026, 3, 12)));

        // The drop-in has no enrollment, no status and no period; the cancelled annotation keeps its status.
        var dropIn = Assert.Single(row.Bookings, booking => booking.ClientId == ids.DropInClient);
        Assert.Equal(BookingModality.DropIn, dropIn.Modality);
        Assert.Equal(BookingStatus.Pending, dropIn.Status);
        Assert.Null(dropIn.EnrollmentId);
        Assert.Null(dropIn.EnrollmentStatus);
        Assert.Empty(dropIn.Periods);

        var cancelled = Assert.Single(row.Bookings, booking => booking.ClientId == ids.CancelledClient);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);

        // Tenant isolation and not-found are answered at the read boundary: the foreign session is invisible to
        // business A and the missing one is simply null.
        Assert.Null(await repository.GetSessionAsync(ids.BusinessA, ids.ForeignSession));
        Assert.Null(await repository.GetSessionAsync(ids.BusinessA, 999999));
        var foreignRow = await repository.GetSessionAsync(ids.BusinessB, ids.ForeignSession);
        Assert.NotNull(foreignRow);
        var foreignBooking = Assert.Single(foreignRow!.Bookings);
        Assert.Equal(ids.ForeignClient, foreignBooking.ClientId);
        Assert.Equal(BookingModality.DropIn, foreignBooking.Modality);

        output.WriteLine("Read assertions passed: the session came back with its four annotations, cancelled included, the fixed ones carrying their enrollment status and paid periods, the drop-in without membership inputs, and the foreign and missing sessions invisible to business A.");
    }

    [Fact]
    public async Task Detail_projects_the_derived_states_the_occupancy_and_the_suspension()
    {
        var target = await ResolveTargetAsync("_Project");
        if (target is null)
            return;

        var ids = await SeedDetailAsync(target.ConnectionString, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var service = new ClassDetailService(new SqlClassDetailRepository(session), new FakeClock { Now = FixedNow });

        var result = await service.GetAsync(ids.BusinessA, ids.Session);
        Assert.True(result.Succeeded, result.Error?.Code);
        var detail = result.Value;

        Assert.Equal(4, detail.Students.Count);
        Assert.Equal(
            ClassFixedStudentState.Active,
            detail.Students.Single(student => student.ClientId == ids.ActiveClient).FixedState);
        Assert.Equal(
            ClassFixedStudentState.PendingRenewal,
            detail.Students.Single(student => student.ClientId == ids.RenewalClient).FixedState);
        Assert.Null(detail.Students.Single(student => student.ClientId == ids.DropInClient).FixedState);
        var cancelled = detail.Students.Single(student => student.ClientId == ids.CancelledClient);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.FixedState);

        // Occupancy: two fixed plus one drop-in occupy three of the six places, and the cancelled annotation
        // releases its own.
        Assert.Equal(3, detail.PlacesTaken);
        Assert.Equal(3, detail.RemainingPlaces);

        // A suspended session reports its reason without altering anything.
        var suspended = await service.GetAsync(ids.BusinessA, ids.SuspendedSession);
        Assert.True(suspended.Succeeded, suspended.Error?.Code);
        Assert.True(suspended.Value.Suspended);
        Assert.Equal("Feriado nacional", suspended.Value.Session.SuspensionReason);

        // A missing session and a session of another business are the same answer.
        var missing = await service.GetAsync(ids.BusinessA, 999999);
        var foreign = await service.GetAsync(ids.BusinessA, ids.ForeignSession);
        Assert.False(missing.Succeeded);
        Assert.False(foreign.Succeeded);
        Assert.Equal("class_detail_not_found", missing.Error!.Code);
        Assert.Equal(missing.Error!.Code, foreign.Error!.Code);
        Assert.Equal(missing.Error!.Message, foreign.Error!.Message);

        output.WriteLine("Projection assertions passed: active coverage and pending renewal derived from the membership periods, the drop-in without a fixed state, the cancelled annotation kept, occupancy 3 of 6, the suspension reason reported, and the foreign and missing sessions both class_detail_not_found.");
    }

    [Fact]
    public async Task Detail_read_projects_the_persisted_end_after_the_tariff_duration_changes()
    {
        var target = await ResolveTargetAsync("_EndsAt");
        if (target is null)
            return;

        var ids = await SeedDetailAsync(target.ConnectionString, FixedNow);
        // The session was generated with a 60-minute duration and the tariff is changed to 30 afterwards: only the
        // stored Appointments.EndsAt may answer, exactly as the walk-in window on the server judges it.
        await UpdateServiceDurationAsync(target.ConnectionString, ids.ServiceA, 30);

        await using var session = new DbSession(target.ConnectionString);
        var repository = new SqlClassDetailRepository(session);

        var row = await repository.GetSessionAsync(ids.BusinessA, ids.Session);
        Assert.NotNull(row);
        Assert.Equal(new DateTime(2026, 3, 12, 19, 0, 0), row!.EndsAt);

        var service = new ClassDetailService(repository, new FakeClock { Now = FixedNow });
        var result = await service.GetAsync(ids.BusinessA, ids.Session);
        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(new DateTime(2026, 3, 12, 19, 0, 0), result.Value.Session.EndsAt);
        Assert.Equal(new TimeOnly(19, 0), result.Value.Session.EndTime);

        output.WriteLine("EndsAt assertion passed: after the tariff duration changed from 60 to 30 minutes, the read and the projected detail still report the stored end 19:00 of the generated session.");
    }

    /// <summary>
    /// Changes the tariff duration of a seeded service, emulating a tariff edit after the session was generated. The
    /// opt-in fixture is the only place a real column can be written, because the projection under test never rereads
    /// the tariff to answer the end.
    /// </summary>
    private static async Task UpdateServiceDurationAsync(string connectionString, int serviceId, int durationMinutes)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE Services SET DurationMinutes = @duration WHERE Id = @serviceId", connection);
        command.Parameters.AddWithValue("@duration", durationMinutes);
        command.Parameters.AddWithValue("@serviceId", serviceId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// Resolves this test's opt-in target. <c>null</c> means the variable is absent: the test returns N/A without
    /// opening any connection or touching any server.
    /// </summary>
    private async Task<SqlTarget?> ResolveTargetAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"N/A: {VariableName} absent; no SQL connection opened.");
            return null;
        }

        var target = ValidateTarget(supplied, testSuffix);
        var applied = await CreateTargetAsync(target);
        output.WriteLine($"Target: {target.DatabaseName}; DB_ID absent before creation; applied {applied} schema script(s); retained without teardown.");
        return target;
    }

    private static SqlTarget ValidateTarget(string suppliedConnectionString, string testSuffix)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{VariableName} could not be parsed; no SQL connection was opened.");
        }

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T16_SQL_CONNECTION and
        // the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT16Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT16Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T16 SQL integration test",
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

        using var process = StartSqlCmd(startInfo);
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

    /// <summary>
    /// Starts <c>sqlcmd</c>, turning a missing binary into an explicit message instead of an uncaught
    /// <see cref="Win32Exception"/> that hides which tool is required.
    /// </summary>
    private static Process StartSqlCmd(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("sqlcmd preflight unavailable.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "sqlcmd could not be started; install the SQL Server command line tools and make sure sqlcmd is on PATH.",
                exception);
        }
    }

    private static async Task<DetailIds> SeedDetailAsync(string connectionString, DateTime now)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

        var businessA = await InsertBusinessAsync(connection, transaction, "Synthetic T16 Detail Business", "synthetic-t16-detail", now);
        var businessB = await InsertBusinessAsync(connection, transaction, "Synthetic T16 Other Business", "synthetic-t16-other", now);
        var serviceA = await InsertServiceAsync(connection, transaction, businessA, "Synthetic T16 Class", 60, 6);
        var serviceB = await InsertServiceAsync(connection, transaction, businessB, "Synthetic T16 Other Class", 60, 6);
        var workerA = await InsertWorkerAsync(connection, transaction, businessA, "Synthetic", "Teacher", "20201616161");
        var workerB = await InsertWorkerAsync(connection, transaction, businessB, "Other", "Teacher", "20201616162");
        var activeClient = await InsertClientAsync(connection, transaction, businessA, "Alumna", "Vigente", "t16.detail.active@example.invalid", now);
        var renewalClient = await InsertClientAsync(connection, transaction, businessA, "Alumna", "Renovar", "t16.detail.renewal@example.invalid", now);
        var dropInClient = await InsertClientAsync(connection, transaction, businessA, "Alumna", "Suelta", "t16.detail.dropin@example.invalid", now);
        var cancelledClient = await InsertClientAsync(connection, transaction, businessA, "Alumna", "Cancelada", "t16.detail.cancelled@example.invalid", now);
        var foreignClient = await InsertClientAsync(connection, transaction, businessB, "Otra", "Alumna", "t16.detail.foreign@example.invalid", now);

        // The dated session: capacity 6, two fixed annotations, one drop-in and one cancelled annotation.
        var sessionDate = new DateTime(2026, 3, 12, 18, 0, 0);
        var session = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Thursday, new TimeOnly(18, 0)),
            serviceA, workerA, sessionDate, 60, 6, AppointmentStatus.Scheduled, null, now);

        var activeEnrollment = await InsertEnrollmentAsync(connection, transaction, businessA, activeClient, EnrollmentStatus.Active, now);
        var renewalEnrollment = await InsertEnrollmentAsync(connection, transaction, businessA, renewalClient, EnrollmentStatus.Active, now);
        var activePayment = await InsertPaymentAsync(connection, transaction, businessA, activeEnrollment, now);
        var renewalPayment = await InsertPaymentAsync(connection, transaction, businessA, renewalEnrollment, now);
        // The first period covers the class date; the second expired the day before it: the pending-renewal input.
        await InsertMembershipPeriodAsync(connection, transaction, activeEnrollment,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), activePayment, now);
        await InsertMembershipPeriodAsync(connection, transaction, renewalEnrollment,
            new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), renewalPayment, now);

        await InsertBookingAsync(connection, transaction, businessA, session, activeClient, activeEnrollment,
            BookingModality.Fixed, BookingStatus.Confirmed, now);
        await InsertBookingAsync(connection, transaction, businessA, session, renewalClient, renewalEnrollment,
            BookingModality.Fixed, BookingStatus.Pending, now);
        await InsertBookingAsync(connection, transaction, businessA, session, dropInClient, null,
            BookingModality.DropIn, BookingStatus.Pending, now);
        await InsertBookingAsync(connection, transaction, businessA, session, cancelledClient, null,
            BookingModality.DropIn, BookingStatus.Cancelled, now);

        // A suspended session with its reason (RF-10): suspending cancels nobody, so it has no annotations.
        var suspendedSession = await InsertClassSessionAsync(connection, transaction, businessA,
            await InsertGridClassAsync(connection, transaction, businessA, serviceA, workerA, DayOfWeek.Friday, new TimeOnly(18, 0)),
            serviceA, workerA, new DateTime(2026, 3, 13, 18, 0, 0), 60, 6, AppointmentStatus.Suspended,
            "Feriado nacional", now);

        // The other tenant: a class at the same instant as business A's, which business A must never see.
        var foreignSession = await InsertClassSessionAsync(connection, transaction, businessB,
            await InsertGridClassAsync(connection, transaction, businessB, serviceB, workerB, DayOfWeek.Thursday, new TimeOnly(18, 0)),
            serviceB, workerB, sessionDate, 60, 6, AppointmentStatus.Scheduled, null, now);
        await InsertBookingAsync(connection, transaction, businessB, foreignSession, foreignClient, null,
            BookingModality.DropIn, BookingStatus.Pending, now);

        await transaction.CommitAsync();

        return new DetailIds(businessA, businessB, serviceA, workerA, session, suspendedSession, foreignSession,
            activeClient, renewalClient, dropInClient, cancelledClient, foreignClient, activeEnrollment, renewalEnrollment);
    }

    private static Task<int> InsertBusinessAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string slug, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @createdAt)",
            ("@name", name), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@createdAt", now));

    private static Task<int> InsertServiceAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string name, int durationMinutes,
        int maxCapacity) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity) OUTPUT INSERTED.Id VALUES (@businessId, @name, @price, @duration, @capacity)",
            ("@businessId", businessId), ("@name", name), ("@price", 100.00m),
            ("@duration", durationMinutes), ("@capacity", maxCapacity));

    private static Task<int> InsertWorkerAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string firstName, string lastName,
        string cuil) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @phone, @cuil)",
            ("@businessId", businessId), ("@firstName", firstName), ("@lastName", lastName),
            ("@phone", "0000000000"), ("@cuil", cuil));

    private static Task<int> InsertClientAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, string firstName, string lastName,
        string email, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @phone, @createdAt)",
            ("@businessId", businessId), ("@firstName", firstName), ("@lastName", lastName),
            ("@email", email), ("@phone", "0000000000"), ("@createdAt", now));

    private static Task<int> InsertGridClassAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int serviceId, int workerId,
        DayOfWeek day, TimeOnly startTime) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO GridClasses (BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime, IsActive) OUTPUT INSERTED.Id VALUES (@businessId, @serviceId, @workerId, @dayOfWeek, @startTime, 1)",
            ("@businessId", businessId), ("@serviceId", serviceId), ("@workerId", workerId),
            ("@dayOfWeek", day), ("@startTime", startTime));

    private static Task<int> InsertClassSessionAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int gridClassId, int serviceId,
        int workerId, DateTime startsAt, int durationMinutes, int maxCapacity,
        AppointmentStatus status, string? suspensionReason, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Appointments (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, SuspensionReason, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @kind, @serviceId, @workerId, @gridClassId, @startsAt, @endsAt, @capacity, NULL, @status, @suspensionReason, @createdAt)",
            ("@businessId", businessId), ("@kind", AppointmentKind.ClassSession), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@gridClassId", gridClassId), ("@startsAt", startsAt),
            ("@endsAt", startsAt.AddMinutes(durationMinutes)), ("@capacity", maxCapacity), ("@status", status),
            ("@suspensionReason", suspensionReason), ("@createdAt", now));

    private static Task<int> InsertEnrollmentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int clientId,
        EnrollmentStatus status, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Enrollments (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @frequency, @status, @price, @createdAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@frequency", 1),
            ("@status", status), ("@price", 100.00m), ("@createdAt", now));

    private static Task<int> InsertPaymentAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int enrollmentId, DateTime paidAt) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, PaidAt, RecordedAt) OUTPUT INSERTED.Id VALUES (@businessId, NULL, @concept, NULL, @enrollmentId, @amount, @paidAt, @recordedAt)",
            ("@businessId", businessId), ("@concept", PaymentConcept.Membership), ("@enrollmentId", enrollmentId),
            ("@amount", 100.00m), ("@paidAt", paidAt), ("@recordedAt", paidAt));

    private static Task<int> InsertMembershipPeriodAsync(
        SqlConnection connection, SqlTransaction transaction, int enrollmentId, DateOnly coverageStart,
        DateOnly expiresOn, int paymentId, DateTime now) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt) OUTPUT INSERTED.Id VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)",
            ("@enrollmentId", enrollmentId), ("@coverageStart", coverageStart), ("@expiresOn", expiresOn),
            ("@paymentId", paymentId), ("@createdAt", now));

    /// <summary>
    /// One booking row with its schema constraints honoured: a fixed annotation carries its enrollment, and a
    /// cancelled one carries its cancellation instant.
    /// </summary>
    private static Task<int> InsertBookingAsync(
        SqlConnection connection, SqlTransaction transaction, int businessId, int appointmentId, int clientId,
        int? enrollmentId, BookingModality? modality, BookingStatus status, DateTime createdAt) =>
        InsertIdAsync(connection, transaction,
            "INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, CancelledAt) OUTPUT INSERTED.Id VALUES (@businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, @createdAt, @cancelledAt)",
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@enrollmentId", enrollmentId), ("@modality", modality), ("@status", status),
            ("@createdAt", createdAt),
            ("@cancelledAt", status == BookingStatus.Cancelled ? createdAt : null));

    private static async Task<int> InsertIdAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlCommand Create(
        SqlConnection connection, SqlTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            AddParameter(command, name, value);
        return command;
    }

    private static void AddParameter(SqlCommand command, string name, object? value)
    {
        switch (value)
        {
            case DateTime dateTime:
                command.Parameters.Add(name, SqlDbType.DateTime2).Value = dateTime;
                return;
            case DateOnly date:
                command.Parameters.Add(name, SqlDbType.Date).Value = date.ToDateTime(TimeOnly.MinValue);
                return;
            case null:
                command.Parameters.AddWithValue(name, DBNull.Value);
                return;
            case Enum enumeration:
                command.Parameters.AddWithValue(name, Convert.ToByte(enumeration, CultureInfo.InvariantCulture));
                return;
            default:
                command.Parameters.AddWithValue(name, value);
                return;
        }
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);

    private sealed record DetailIds(
        int BusinessA,
        int BusinessB,
        int ServiceA,
        int WorkerA,
        int Session,
        int SuspendedSession,
        int ForeignSession,
        int ActiveClient,
        int RenewalClient,
        int DropInClient,
        int CancelledClient,
        int ForeignClient,
        int ActiveEnrollment,
        int RenewalEnrollment);
}
