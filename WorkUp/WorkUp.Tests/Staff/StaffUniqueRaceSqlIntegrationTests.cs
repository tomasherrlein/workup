using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;
using Xunit.Abstractions;

namespace WorkUp.Tests.Staff;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for the per-business unique CUIL race of RF-03 / Regla 1 (T27):
/// <c>UQ_Workers_Cuil</c> is unique on <c>(BusinessId, Cuil)</c>, so a create or an edit that passed its sequential
/// precheck can still lose the write to a competing writer that committed in between. The unit tests prove the service
/// maps the typed outcome to the existing <c>cuil_taken</c> field error; only storage can prove that the real
/// <c>UQ_Workers_Cuil</c> violation is the one reported, that no second worker is written, that a rejected edit leaves
/// the stored row and its schedule unchanged, that the constraint stays scoped to the business, and that a foreign key
/// or any other storage failure still propagates instead of being disguised as a CUIL conflict. Requires a new,
/// dedicated database supplied through WORKUP_T27_WORKERS_SQL_CONNECTION; without it every test returns N/A and no SQL
/// connection is opened. Each test derives its own database name from the supplied one, so the whole class runs in a
/// single command, and nothing is torn down: the target is retained on purpose.
/// </summary>
public sealed class StaffUniqueRaceSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T27_WORKERS_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>The CUIL every create and edit uses; the service stores it normalized as <c>20-30405060-7</c>.</summary>
    private const string ContestedCuil = "20304050607";

    [Fact]
    public async Task Concurrent_creates_with_the_same_cuil_keep_one_worker_and_report_cuil_taken_without_a_500()
    {
        var target = await ResolveTargetAsync("_CreateRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");

        // Winner: a real create that commits the CUIL, with its own saved schedule.
        await using (var winnerSession = new DbSession(target.ConnectionString))
        {
            var winner = await CreateAsync(winnerSession, businessId, ContestedCuil);
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // Loser: an independent session whose sequential CUIL precheck is forced to report the CUIL free, exactly what
        // a competing request reads just before the winner commits. The real INSERT then meets UQ_Workers_Cuil, and the
        // repository must turn that one violation into a typed outcome instead of letting it escape as a 500.
        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var loser = await CreateAsync(
                loserSession,
                businessId,
                ContestedCuil,
                new FreeCuilWindowWorkerRepository(new SqlWorkerRepository(loserSession)));

            Assert.False(loser.Succeeded);
            Assert.Equal("cuil_taken", loser.Error!.Code);
            Assert.Equal(nameof(CreateWorkerRequest.Cuil), loser.Error.Field);
        }

        // The losing create wrote nothing: one worker holds the contested CUIL, and the winner's schedule is the only
        // child row — the loser never reached ReplaceSchedulesAsync and its transaction rolled back.
        Assert.Equal(1, await CountWorkersAsync(target.ConnectionString, businessId, "20-30405060-7"));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Workers"));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM WorkerSchedules"));
        output.WriteLine($"Target: {target.DatabaseName}; one worker for the contested CUIL and no orphan schedule; retained without teardown.");
    }

    [Fact]
    public async Task A_worker_edit_that_loses_the_cuil_race_reports_cuil_taken_and_leaves_the_stored_row_and_schedule_unchanged()
    {
        var target = await ResolveTargetAsync("_EditRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var ownId = await SeedWorkerAsync(target.ConnectionString, businessId, ContestedCuil, "Gómez", DayOfWeek.Monday);
        // Seeded through the same normalization the service uses, so the stored CUIL is 27-12345678-4 and the edit's
        // 27123456784 really collides with it; raw digits here would dodge UQ_Workers_Cuil and let the edit succeed.
        await SeedWorkerAsync(target.ConnectionString, businessId, "27123456784", "Pérez", DayOfWeek.Tuesday);

        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var result = await UpdateAsync(
                loserSession,
                businessId,
                ownId,
                new UpdateWorkerRequest("Marina", "Pérez", "1199887766", "27123456784",
                    [WorkDay(DayOfWeek.Tuesday)]),
                new FreeCuilWindowWorkerRepository(new SqlWorkerRepository(loserSession)));

            Assert.False(result.Succeeded);
            Assert.Equal("cuil_taken", result.Error!.Code);
            Assert.Equal(nameof(UpdateWorkerRequest.Cuil), result.Error.Field);
        }

        // Atomic: the rejected statement changed no column, so the stored worker keeps its CUIL and last name. A
        // partial write here would have repointed one worker's profile without its CUIL.
        var stored = await GetByIdAsync(target.ConnectionString, businessId, ownId);
        Assert.NotNull(stored);
        Assert.Equal("20-30405060-7", stored!.Cuil);
        Assert.Equal("Gómez", stored.LastName);

        // No orphan schedule: the losing edit returned before replacing the schedule, so the worker still works Monday
        // and no extra schedule row was written.
        var schedule = Assert.Single(stored.Schedules);
        Assert.Equal(DayOfWeek.Monday, schedule.DayOfWeek);
        Assert.Equal(2, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM WorkerSchedules"));
        output.WriteLine($"Target: {target.DatabaseName}; the rejected edit left the row and its schedule unchanged; retained without teardown.");
    }

    [Fact]
    public async Task The_cuil_race_is_scoped_to_the_business_so_another_tenant_still_creates_its_own_worker()
    {
        var target = await ResolveTargetAsync("_TenantScope");
        if (target is null)
            return;

        var firstBusiness = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var secondBusiness = await SeedBusinessAsync(target.ConnectionString, "estudio-lucia");
        Assert.True(firstBusiness != secondBusiness, "the fixture seeded two distinct tenants");

        await using (var firstSession = new DbSession(target.ConnectionString))
        {
            var winner = await CreateAsync(firstSession, firstBusiness, ContestedCuil);
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // The 2601/2627 mapping must stay inside the tenant that owns the constraint: the same CUIL in another
        // business is a different worker (Regla 1), so a lost window there must insert, not report a cross-tenant
        // conflict.
        await using (var secondSession = new DbSession(target.ConnectionString))
        {
            var otherTenant = await CreateAsync(
                secondSession,
                secondBusiness,
                ContestedCuil,
                new FreeCuilWindowWorkerRepository(new SqlWorkerRepository(secondSession)));

            Assert.True(otherTenant.Succeeded, otherTenant.Error?.Code);
            Assert.True(otherTenant.Value.Id > 0);
        }

        Assert.Equal(1, await CountWorkersAsync(target.ConnectionString, firstBusiness, "20-30405060-7"));
        Assert.Equal(1, await CountWorkersAsync(target.ConnectionString, secondBusiness, "20-30405060-7"));
        Assert.Equal(2, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Workers"));
        output.WriteLine($"Target: {target.DatabaseName}; the constraint stayed per business; retained without teardown.");
    }

    [Fact]
    public async Task A_foreign_key_violation_still_propagates_instead_of_being_reported_as_cuil_taken()
    {
        var target = await ResolveTargetAsync("_ForeignKey");
        if (target is null)
            return;

        await using var session = new DbSession(target.ConnectionString);

        // A missing business violates FK_Workers_Businesses, not UQ_Workers_Cuil. The narrow catch must not disguise
        // an unknown tenant as a benign CUIL conflict: it has to surface as the real SqlException.
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => CreateAsync(session, 987654321, ContestedCuil));

        Assert.Equal(547, exception.Number);
        Assert.Contains("FK_Workers_Businesses", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Workers"));
        output.WriteLine($"Target: {target.DatabaseName}; the foreign key violation propagated unchanged; retained without teardown.");
    }

    [Fact]
    public async Task An_unexpected_storage_failure_on_the_edit_still_propagates_instead_of_being_reported_as_cuil_taken()
    {
        var target = await ResolveTargetAsync("_EditOtherFailure");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var workerId = await SeedWorkerAsync(target.ConnectionString, businessId, ContestedCuil, "Gómez", DayOfWeek.Monday);

        await using (var session = new DbSession(target.ConnectionString))
        {
            // The CUIL is unchanged, so this never reaches the unique key: the last name is too long for NVARCHAR(60),
            // which is a truncation error and not a CUIL conflict. SQL Server reports it as 8152 (legacy generic
            // message) or as 2628 (table/column detail, raised from 2019 CU27 on), so either number proves truncation.
            var tooLongLastName = new string('a', 100);
            var exception = await Assert.ThrowsAsync<SqlException>(
                () => UpdateAsync(session, businessId, workerId,
                    new UpdateWorkerRequest("Marina", tooLongLastName, "1122334455", ContestedCuil,
                        [WorkDay(DayOfWeek.Monday)])));

            Assert.True(
                exception.Number is 8152 or 2628,
                $"expected a string-truncation SqlException (8152 or 2628) but got {exception.Number}: {exception.Message}");
        }

        var stored = await GetByIdAsync(target.ConnectionString, businessId, workerId);
        Assert.Equal("Gómez", stored!.LastName);
        output.WriteLine($"Target: {target.DatabaseName}; the truncation error propagated unchanged; retained without teardown.");
    }

    private static DayScheduleInput WorkDay(DayOfWeek day) =>
        new(day, true, new TimeOnly(9, 0), new TimeOnly(18, 0), []);

    /// <summary>
    /// Drives one create through the real service. When <paramref name="workers"/> is the "free window" decorator the
    /// sequential CUIL precheck is forced to pass while the real insert still meets storage.
    /// </summary>
    private static async Task<Result<Worker>> CreateAsync(
        DbSession session, int businessId, string cuil, IWorkerRepository? workers = null) =>
        await new StaffService(workers ?? new SqlWorkerRepository(session), session)
            .CreateAsync(businessId, new CreateWorkerRequest("Marina", "Gómez", "1122334455", cuil, [WorkDay(DayOfWeek.Monday)]));

    private static async Task<Result> UpdateAsync(
        DbSession session, int businessId, int workerId, UpdateWorkerRequest request, IWorkerRepository? workers = null) =>
        await new StaffService(workers ?? new SqlWorkerRepository(session), session)
            .UpdateAsync(businessId, workerId, request);

    private static async Task<int> SeedBusinessAsync(string connectionString, string slug)
    {
        await using var session = new DbSession(connectionString);
        var inserted = await new SqlBusinessRepository(session).InsertAsync(new Business
        {
            Name = slug,
            Slug = slug,
            ContactPhone = "1122334455",
            CreatedAt = DateTime.UtcNow
        });
        Assert.True(inserted.IsInserted, "the fixture could not seed the business");
        return inserted.BusinessId;
    }

    /// <summary>
    /// Seeds one worker exactly as the real service would: the CUIL is normalized to the canonical
    /// <c>XX-XXXXXXXX-X</c> form the service stores, instead of raw digits. A direct insert of the raw value would
    /// occupy the column with a different string than any normalized request, so a later edit that should collide
    /// with this row would silently pass instead of meeting the real <c>UQ_Workers_Cuil</c>.
    /// </summary>
    private static async Task<int> SeedWorkerAsync(
        string connectionString, int businessId, string cuil, string lastName, DayOfWeek day)
    {
        Assert.True(CuilFormat.TryNormalize(cuil, out var canonicalCuil), "the fixture seeds only valid CUILs");
        await using var session = new DbSession(connectionString);
        var repository = new SqlWorkerRepository(session);
        var inserted = await repository.InsertAsync(new Worker
        {
            BusinessId = businessId,
            FirstName = "Marina",
            LastName = lastName,
            Phone = "1122334455",
            Cuil = canonicalCuil,
            IsActive = true
        });
        Assert.True(inserted.IsInserted, "the fixture could not seed the worker");
        await repository.ReplaceSchedulesAsync(inserted.WorkerId, [new WorkerSchedule
        {
            DayOfWeek = day,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(18, 0)
        }]);
        return inserted.WorkerId;
    }

    private static async Task<Worker?> GetByIdAsync(string connectionString, int businessId, int workerId)
    {
        await using var session = new DbSession(connectionString);
        return await new SqlWorkerRepository(session).GetByIdAsync(businessId, workerId);
    }

    private static async Task<int> CountWorkersAsync(string connectionString, int businessId, string cuil) =>
        await CountAsync(connectionString,
            "SELECT COUNT(*) FROM Workers WHERE BusinessId = @businessId AND Cuil = @cuil",
            p => p.AddValue("@businessId", businessId).AddValue("@cuil", cuil));

    private static async Task<int> CountAsync(
        string connectionString, string sql, Action<SqlParameterCollection>? parameters = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        parameters?.Invoke(command.Parameters);
        return (int)(await command.ExecuteScalarAsync())!;
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
        Assert.Equal(3, applied);
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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T27_WORKERS_SQL_CONNECTION
        // and the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT27WorkersSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT27WorkersSql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T27 workers SQL integration test",
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

    /// <summary>
    /// Deterministic "lost the check-then-write window" model for the CUIL: it reports the CUIL free even when another
    /// session already stored it, so the real INSERT/UPDATE meets the unique constraint exactly as the losing request
    /// would. Every other call goes to the real repository; no write is faked.
    /// </summary>
    private sealed class FreeCuilWindowWorkerRepository(IWorkerRepository inner) : IWorkerRepository
    {
        public Task<List<Worker>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
            inner.ListByBusinessAsync(businessId, ct);

        public Task<Worker?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
            inner.GetByIdAsync(businessId, id, ct);

        public Task<bool> CuilExistsAsync(int businessId, string cuil, int? excludingId = null, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<WorkerInsertResult> InsertAsync(Worker worker, CancellationToken ct = default) =>
            inner.InsertAsync(worker, ct);

        public Task<WorkerUpdateResult> UpdateAsync(Worker worker, CancellationToken ct = default) =>
            inner.UpdateAsync(worker, ct);

        public Task ReplaceSchedulesAsync(int workerId, List<WorkerSchedule> schedules, CancellationToken ct = default) =>
            inner.ReplaceSchedulesAsync(workerId, schedules, ct);

        public Task ReplaceBreaksAsync(int workerId, List<WorkerBreak> breaks, CancellationToken ct = default) =>
            inner.ReplaceBreaksAsync(workerId, breaks, ct);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
