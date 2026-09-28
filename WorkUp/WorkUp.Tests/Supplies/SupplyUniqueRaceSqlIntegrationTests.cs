using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Supplies;
using WorkUp.BusinessLogic.Common;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Supplies;
using WorkUp.Domain.Entities;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Supplies;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for the per-business unique name race of RF-14 (T27):
/// <c>UQ_Supplies_Name</c> is unique on <c>(BusinessId, Name)</c>, so a create or an edit that passed its sequential
/// precheck can still lose the write to a competing writer that committed in between. The unit tests prove the service
/// maps the typed outcome to the existing <c>supply_name_taken</c> field error; only storage can prove that the real
/// <c>UQ_Supplies_Name</c> violation is the one reported, that no second supply is written, that a rejected update
/// leaves the stored row unchanged, that the constraint stays scoped to the business, and that a foreign key or any
/// other storage failure still propagates instead of being disguised as a name conflict. Requires a new, dedicated
/// database supplied through WORKUP_T27_SUPPLIES_SQL_CONNECTION; without it every test returns N/A and no SQL
/// connection is opened. Each test derives its own database name from the supplied one, so the whole class runs in a
/// single command, and nothing is torn down: the target is retained on purpose.
/// </summary>
public sealed class SupplyUniqueRaceSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T27_SUPPLIES_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    [Fact]
    public async Task Concurrent_creates_with_the_same_name_keep_one_supply_and_report_name_taken_without_a_500()
    {
        var target = await ResolveTargetAsync("_CreateRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");

        // Winner: a real create that commits the name.
        await using (var winnerSession = new DbSession(target.ConnectionString))
        {
            var winner = await CreateAsync(winnerSession, businessId, "Tintura 7.1");
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // Loser: an independent session whose sequential name precheck is forced to report the name free, exactly what a
        // competing request reads just before the winner commits. The real INSERT then meets UQ_Supplies_Name, and the
        // repository must turn that one violation into a typed outcome instead of letting it escape as a 500.
        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var loser = await CreateAsync(
                loserSession,
                businessId,
                "Tintura 7.1",
                new FreeNameWindowSupplyRepository(new SqlSupplyRepository(loserSession)));

            Assert.False(loser.Succeeded);
            Assert.Equal("supply_name_taken", loser.Error!.Code);
            Assert.Equal("Name", loser.Error.Field);
        }

        // The losing create wrote nothing: one supply holds the contested name, not two.
        Assert.Equal(1, await CountSuppliesAsync(target.ConnectionString, businessId, "Tintura 7.1"));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Supplies"));
        output.WriteLine($"Target: {target.DatabaseName}; one supply for the contested name; retained without teardown.");
    }

    [Fact]
    public async Task An_edit_that_loses_the_name_race_reports_name_taken_and_leaves_the_stored_row_unchanged()
    {
        var target = await ResolveTargetAsync("_RenameRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var ownId = await SeedSupplyAsync(target.ConnectionString, businessId, "Guantes", 2m, 1m);
        await SeedSupplyAsync(target.ConnectionString, businessId, "Tintura 7.1", 1m, 1m);

        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var result = await UpdateAsync(
                loserSession,
                businessId,
                ownId,
                "Tintura 7.1",
                8m,
                2m,
                new FreeNameWindowSupplyRepository(new SqlSupplyRepository(loserSession)));

            Assert.False(result.Succeeded);
            Assert.Equal("supply_name_taken", result.Error!.Code);
            Assert.Equal("Name", result.Error.Field);
        }

        // Atomic: the rejected statement changed no column, so the stored supply keeps its own name and stock, and only
        // one row holds the contested name.
        var stored = await GetByIdAsync(target.ConnectionString, businessId, ownId);
        Assert.NotNull(stored);
        Assert.Equal("Guantes", stored!.Name);
        Assert.Equal(2m, stored.CurrentStock);
        Assert.Equal(1, await CountSuppliesAsync(target.ConnectionString, businessId, "Tintura 7.1"));
        output.WriteLine($"Target: {target.DatabaseName}; the rejected edit left the stored row unchanged; retained without teardown.");
    }

    [Fact]
    public async Task The_name_race_is_scoped_to_the_business_so_another_tenant_still_creates_its_own_supply()
    {
        var target = await ResolveTargetAsync("_TenantScope");
        if (target is null)
            return;

        var firstBusiness = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var secondBusiness = await SeedBusinessAsync(target.ConnectionString, "estudio-lucia");
        Assert.True(firstBusiness != secondBusiness, "the fixture seeded two distinct tenants");

        await using (var firstSession = new DbSession(target.ConnectionString))
        {
            var winner = await CreateAsync(firstSession, firstBusiness, "Tintura 7.1");
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // The 2601/2627 mapping must stay inside the tenant that owns the constraint: the same name in another business
        // is a different supply (RF-14), so a lost window there must insert, not report a cross-tenant conflict.
        await using (var secondSession = new DbSession(target.ConnectionString))
        {
            var otherTenant = await CreateAsync(
                secondSession,
                secondBusiness,
                "Tintura 7.1",
                new FreeNameWindowSupplyRepository(new SqlSupplyRepository(secondSession)));

            Assert.True(otherTenant.Succeeded, otherTenant.Error?.Code);
            Assert.True(otherTenant.Value.Id > 0);
        }

        Assert.Equal(1, await CountSuppliesAsync(target.ConnectionString, firstBusiness, "Tintura 7.1"));
        Assert.Equal(1, await CountSuppliesAsync(target.ConnectionString, secondBusiness, "Tintura 7.1"));
        Assert.Equal(2, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Supplies"));
        output.WriteLine($"Target: {target.DatabaseName}; the constraint stayed per business; retained without teardown.");
    }

    [Fact]
    public async Task A_foreign_key_violation_still_propagates_instead_of_being_reported_as_name_taken()
    {
        var target = await ResolveTargetAsync("_ForeignKey");
        if (target is null)
            return;

        await using var session = new DbSession(target.ConnectionString);

        // A missing business violates FK_Supplies_Businesses, not UQ_Supplies_Name. The narrow catch must not disguise
        // an unknown tenant as a benign name conflict: it has to surface as the real SqlException.
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => CreateAsync(session, 987654321, "Tintura 7.1"));

        Assert.Equal(547, exception.Number);
        Assert.Contains("FK_Supplies_Businesses", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Supplies"));
        output.WriteLine($"Target: {target.DatabaseName}; the foreign key violation propagated unchanged; retained without teardown.");
    }

    [Fact]
    public async Task An_unexpected_storage_failure_on_the_edit_still_propagates_instead_of_being_reported_as_name_taken()
    {
        var target = await ResolveTargetAsync("_RenameOtherFailure");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var supplyId = await SeedSupplyAsync(target.ConnectionString, businessId, "Guantes", 2m, 1m);

        await using (var session = new DbSession(target.ConnectionString))
        {
            // The service caps the name at 100 characters before storage, so this drives the repository directly: that
            // is the surface whose narrow catch must not swallow a non-unique failure. The value is too long for
            // NVARCHAR(100), which is a truncation error and not a name conflict. SQL Server reports it as 8152 (legacy
            // generic message) or as 2628 (table/column detail, raised from 2019 CU27 on), so either number still
            // proves truncation.
            var repository = new SqlSupplyRepository(session);
            var tooLongName = new string('a', 150);
            var exception = await Assert.ThrowsAsync<SqlException>(
                () => repository.UpdateAsync(new Supply
                {
                    Id = supplyId,
                    BusinessId = businessId,
                    Name = tooLongName,
                    CurrentStock = 2m,
                    MinimumStock = 1m,
                    LastDeclaredAt = DateTime.UtcNow,
                    IsActive = true
                }));

            Assert.True(
                exception.Number is 8152 or 2628,
                $"expected a string-truncation SqlException (8152 or 2628) but got {exception.Number}: {exception.Message}");
        }

        var stored = await GetByIdAsync(target.ConnectionString, businessId, supplyId);
        Assert.Equal("Guantes", stored!.Name);
        output.WriteLine($"Target: {target.DatabaseName}; the truncation error propagated unchanged; retained without teardown.");
    }

    /// <summary>
    /// Drives one create through the real service. When <paramref name="supplies"/> is the "free window" decorator the
    /// sequential name precheck is forced to pass while the real insert still meets storage.
    /// </summary>
    private static async Task<Result<Supply>> CreateAsync(
        DbSession session, int businessId, string name, ISupplyRepository? supplies = null) =>
        await new SupplyService(supplies ?? new SqlSupplyRepository(session), new FakeClock())
            .CreateAsync(businessId, new DeclareSupplyRequest(name, 0.5m, 1m));

    private static async Task<Result> UpdateAsync(
        DbSession session, int businessId, int id, string name, decimal currentStock, decimal minimumStock,
        ISupplyRepository? supplies = null) =>
        await new SupplyService(supplies ?? new SqlSupplyRepository(session), new FakeClock())
            .UpdateAsync(businessId, id, new DeclareSupplyRequest(name, currentStock, minimumStock));

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

    private static async Task<int> SeedSupplyAsync(
        string connectionString, int businessId, string name, decimal currentStock, decimal minimumStock)
    {
        await using var session = new DbSession(connectionString);
        var inserted = await new SqlSupplyRepository(session).InsertAsync(new Supply
        {
            BusinessId = businessId,
            Name = name,
            CurrentStock = currentStock,
            MinimumStock = minimumStock,
            LastDeclaredAt = DateTime.UtcNow,
            IsActive = true
        });
        Assert.True(inserted.IsInserted, "the fixture could not seed the supply");
        return inserted.SupplyId;
    }

    private static async Task<Supply?> GetByIdAsync(string connectionString, int businessId, int id)
    {
        await using var session = new DbSession(connectionString);
        return await new SqlSupplyRepository(session).GetByIdAsync(businessId, id);
    }

    private static async Task<int> CountSuppliesAsync(string connectionString, int businessId, string name) =>
        await CountAsync(connectionString,
            "SELECT COUNT(*) FROM Supplies WHERE BusinessId = @businessId AND Name = @name",
            p => p.AddValue("@businessId", businessId).AddValue("@name", name));

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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T27_SUPPLIES_SQL_CONNECTION
        // and the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT27SuppliesSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT27SuppliesSql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T27 supplies SQL integration test",
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
    /// Deterministic "lost the check-then-write window" model for the name: it reports the name free even when another
    /// session already stored it, so the real INSERT/UPDATE meets the unique constraint exactly as the losing request
    /// would. Every other call goes to the real repository; no write is faked.
    /// </summary>
    private sealed class FreeNameWindowSupplyRepository(ISupplyRepository inner) : ISupplyRepository
    {
        public Task<List<Supply>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
            inner.ListByBusinessAsync(businessId, ct);

        public Task<Supply?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
            inner.GetByIdAsync(businessId, id, ct);

        public Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<SupplyInsertResult> InsertAsync(Supply supply, CancellationToken ct = default) =>
            inner.InsertAsync(supply, ct);

        public Task<SupplyUpdateResult> UpdateAsync(Supply supply, CancellationToken ct = default) =>
            inner.UpdateAsync(supply, ct);

        public Task<int> CountBelowMinimumAsync(int businessId, CancellationToken ct = default) =>
            inner.CountBelowMinimumAsync(businessId, ct);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
