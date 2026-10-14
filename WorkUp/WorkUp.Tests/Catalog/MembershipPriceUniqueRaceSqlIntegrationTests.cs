using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Common;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Catalog;
using WorkUp.Domain.Entities;
using Xunit.Abstractions;

namespace WorkUp.Tests.Catalog;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for the per-tenant set-price race of T27 (set-price idempotente):
/// <c>UQ_MembershipPrices_Frequency</c> is unique on <c>(BusinessId, WeeklyFrequency)</c>, so two simultaneous sets of the
/// same frequency can both pass their sequential read and only one insert can win. The unit tests prove the service
/// maps the typed outcome to the requested price; only storage can prove that the real
/// <c>UQ_MembershipPrices_Frequency</c> violation is the one reported, that the losing set updates the winner's row
/// instead of duplicating it, that the constraint stays scoped to the business, that a foreign key or any other
/// storage failure still propagates instead of being disguised as a benign lost race, and that no unrelated row is
/// modified. Requires a new, dedicated database supplied through WORKUP_T27_PRICE_SQL_CONNECTION; without it every test
/// returns N/A and no SQL connection is opened. Each test derives its own database name from the supplied one, so the
/// whole class runs in a single command, and nothing is torn down: the target is retained on purpose.
/// </summary>
public sealed class MembershipPriceUniqueRaceSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T27_PRICE_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    [Fact]
    public async Task Concurrent_sets_with_the_same_frequency_keep_one_row_and_apply_the_requested_price_without_a_500()
    {
        var target = await ResolveTargetAsync("_SetRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");

        // An unrelated frequency of the same tenant, to prove the lost race touches only the contested row.
        await using (var seedSession = new DbSession(target.ConnectionString))
        {
            var seeded = await new SqlMembershipPriceRepository(seedSession).InsertAsync(new MembershipPrice
            {
                BusinessId = businessId,
                WeeklyFrequency = 3,
                Price = 9000m
            });
            Assert.True(seeded.IsInserted, "the fixture could not seed the unrelated membership price");
        }

        // Winner: a real set that commits frequency 2 at 15000.
        await using (var winnerSession = new DbSession(target.ConnectionString))
        {
            var winner = await SetPriceAsync(winnerSession, businessId, 2, 15000m);
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // Loser: an independent session whose sequential frequency read is forced blind, exactly what a competing request
        // reads just before the winner commits. Its real INSERT meets UQ_MembershipPrices_Frequency; the repository must
        // turn that one violation into AlreadyPresent, and the service must re-read the winner's row and apply 18000.
        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var loser = await SetPriceAsync(
                loserSession,
                businessId,
                2,
                18000m,
                new BlindFirstFrequencyReadMembershipPriceRepository(new SqlMembershipPriceRepository(loserSession)));

            Assert.True(loser.Succeeded, loser.Error?.Code);
            Assert.Equal(18000m, loser.Value.Price);
        }

        // One row holds the contested frequency, it carries the losing request's price, and the unrelated row is intact.
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM MembershipPrices WHERE BusinessId = @businessId AND WeeklyFrequency = 2",
            p => p.AddValue("@businessId", businessId)));
        Assert.Equal(18000m, await PriceAsync(target.ConnectionString, businessId, 2));
        Assert.Equal(9000m, await PriceAsync(target.ConnectionString, businessId, 3));
        Assert.Equal(2, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM MembershipPrices"));
        output.WriteLine($"Target: {target.DatabaseName}; one row for the contested frequency updated to the losing request's price; retained without teardown.");
    }

    [Fact]
    public async Task The_frequency_race_is_scoped_to_the_business_so_another_tenant_still_sets_its_own_price()
    {
        var target = await ResolveTargetAsync("_TenantScope");
        if (target is null)
            return;

        var firstBusiness = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var secondBusiness = await SeedBusinessAsync(target.ConnectionString, "estudio-lucia");
        Assert.True(firstBusiness != secondBusiness, "the fixture seeded two distinct tenants");

        await using (var firstSession = new DbSession(target.ConnectionString))
        {
            var winner = await SetPriceAsync(firstSession, firstBusiness, 2, 15000m);
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // The 2601/2627 mapping must stay inside the tenant that owns the constraint: the same frequency in another
        // business is a different price, so a blind read there must insert its own row, not report a cross-tenant race.
        await using (var secondSession = new DbSession(target.ConnectionString))
        {
            var otherTenant = await SetPriceAsync(
                secondSession,
                secondBusiness,
                2,
                20000m,
                new BlindFirstFrequencyReadMembershipPriceRepository(new SqlMembershipPriceRepository(secondSession)));

            Assert.True(otherTenant.Succeeded, otherTenant.Error?.Code);
            Assert.True(otherTenant.Value.Id > 0);
        }

        Assert.Equal(15000m, await PriceAsync(target.ConnectionString, firstBusiness, 2));
        Assert.Equal(20000m, await PriceAsync(target.ConnectionString, secondBusiness, 2));
        Assert.Equal(2, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM MembershipPrices"));
        output.WriteLine($"Target: {target.DatabaseName}; the constraint stayed per business; retained without teardown.");
    }

    [Fact]
    public async Task A_foreign_key_violation_still_propagates_instead_of_being_reported_as_already_present()
    {
        var target = await ResolveTargetAsync("_ForeignKey");
        if (target is null)
            return;

        await using var session = new DbSession(target.ConnectionString);

        // A missing business violates FK_MembershipPrices_Businesses, not UQ_MembershipPrices_Frequency. The narrow
        // catch must not disguise an unknown tenant as a benign lost race: it has to surface as the real SqlException.
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => SetPriceAsync(session, 987654321, 2, 15000m));

        Assert.Equal(547, exception.Number);
        Assert.Contains("FK_MembershipPrices_Businesses", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM MembershipPrices"));
        output.WriteLine($"Target: {target.DatabaseName}; the foreign key violation propagated unchanged; retained without teardown.");
    }

    [Fact]
    public async Task An_unexpected_check_constraint_failure_still_propagates_and_writes_no_row()
    {
        var target = await ResolveTargetAsync("_CheckConstraint");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");

        await using (var session = new DbSession(target.ConnectionString))
        {
            // Price 0 violates CK_MembershipPrices_Price: the narrow catch must report AlreadyPresent only for the exact
            // frequency unique key, never for any other storage failure.
            var exception = await Assert.ThrowsAsync<SqlException>(
                () => new SqlMembershipPriceRepository(session).InsertAsync(new MembershipPrice
                {
                    BusinessId = businessId,
                    WeeklyFrequency = 2,
                    Price = 0m
                }));

            Assert.Equal(547, exception.Number);
            Assert.Contains("CK_MembershipPrices_Price", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM MembershipPrices"));
        output.WriteLine($"Target: {target.DatabaseName}; the check constraint violation propagated unchanged; retained without teardown.");
    }

    /// <summary>
    /// Drives one set through the real service. When <paramref name="prices"/> is the "blind first read" decorator the
    /// sequential frequency precheck is forced to pass while the real insert still meets storage.
    /// </summary>
    private static async Task<Result<MembershipPrice>> SetPriceAsync(
        DbSession session, int businessId, int weeklyFrequency, decimal price, IMembershipPriceRepository? prices = null) =>
        await new MembershipPriceService(prices ?? new SqlMembershipPriceRepository(session))
            .SetPriceAsync(businessId, weeklyFrequency, price);

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

    private static async Task<decimal> PriceAsync(string connectionString, int businessId, int weeklyFrequency)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Price FROM MembershipPrices WHERE BusinessId = @businessId AND WeeklyFrequency = @weeklyFrequency",
            connection);
        command.Parameters.AddValue("@businessId", businessId).AddValue("@weeklyFrequency", weeklyFrequency);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T27_PRICE_SQL_CONNECTION
        // and the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT27PriceSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT27PriceSql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T27 price SQL integration test",
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
    /// Deterministic "lost the check-then-write window" model for the frequency: the first frequency read of the losing
    /// request is blind — it reports the frequency free even when another session already committed it — so the real
    /// INSERT meets <c>UQ_MembershipPrices_Frequency</c> exactly as the losing request would. The later re-read delegates
    /// to the real repository, so it observes what storage actually holds. Every other call goes to the real repository.
    /// </summary>
    private sealed class BlindFirstFrequencyReadMembershipPriceRepository(IMembershipPriceRepository inner) : IMembershipPriceRepository
    {
        private bool _firstReadServed;

        public Task<List<MembershipPrice>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
            inner.ListByBusinessAsync(businessId, ct);

        public Task<MembershipPrice?> GetByFrequencyAsync(int businessId, int weeklyFrequency, CancellationToken ct = default)
        {
            if (!_firstReadServed)
            {
                _firstReadServed = true;
                return Task.FromResult<MembershipPrice?>(null);
            }

            return inner.GetByFrequencyAsync(businessId, weeklyFrequency, ct);
        }

        public Task<MembershipPriceInsertResult> InsertAsync(MembershipPrice price, CancellationToken ct = default) =>
            inner.InsertAsync(price, ct);

        public Task UpdateAsync(MembershipPrice price, CancellationToken ct = default) => inner.UpdateAsync(price, ct);

        public Task DeleteAsync(int businessId, int weeklyFrequency, CancellationToken ct = default) =>
            inner.DeleteAsync(businessId, weeklyFrequency, ct);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
