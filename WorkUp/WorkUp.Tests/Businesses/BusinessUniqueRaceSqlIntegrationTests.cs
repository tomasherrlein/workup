using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Domain.Entities;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Businesses;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for the globally unique write races of RF-01 (T27, T01 finding): the
/// slug of <c>Businesses</c> and the username of <c>OwnerAccounts</c> are unique in storage, so a registration or an
/// edit that passed its sequential precheck can still lose the write to a competing writer that committed in between.
/// The unit tests prove the service maps the typed outcome to a <c>Result.Failure</c>; only storage can prove that the
/// real <c>UQ_Businesses_Slug</c> / <c>UQ_OwnerAccounts_Username</c> violation is the one reported, that the losing
/// registration leaves no orphan business row after rollback, that the rejected rename leaves the stored row unchanged,
/// and that a different unique key is not swallowed. Requires a new, dedicated database supplied through
/// WORKUP_T27_BUSINESS_SQL_CONNECTION; without it every test returns N/A and no SQL connection is opened. Each test
/// derives its own database name from the supplied one, so the whole class runs in a single command, and nothing is
/// torn down: the target is retained on purpose.
/// </summary>
public sealed class BusinessUniqueRaceSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T27_BUSINESS_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>The single clock instant every registration and every seeded row shares.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task Concurrent_registrations_with_the_same_slug_keep_one_business_and_report_slug_taken_without_a_500()
    {
        var target = await ResolveTargetAsync("_SlugRace");
        if (target is null)
            return;

        // Winner: a real registration that commits the slug.
        await using (var winnerSession = new DbSession(target.ConnectionString))
        {
            var winner = await RegisterAsync(winnerSession, "peluqueria-de-marina", "marina");
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // Loser: an independent session whose sequential slug precheck is forced to report the slug free, exactly what a
        // competing request reads just before the winner commits. The real INSERT then meets UQ_Businesses_Slug, and the
        // repository must turn that one violation into a typed outcome instead of letting it escape as a 500.
        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var loser = await RegisterAsync(
                loserSession,
                "peluqueria-de-marina",
                "otra",
                businesses: new FreeSlugWindowRepository(new SqlBusinessRepository(loserSession)));

            Assert.False(loser.Succeeded);
            Assert.Equal("slug_taken", loser.Error!.Code);
            Assert.Equal(nameof(RegisterBusinessRequest.Slug), loser.Error.Field);
        }

        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Businesses WHERE Slug = @slug", p => p.AddValue("@slug", "peluqueria-de-marina")));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM OwnerAccounts"));
        output.WriteLine($"Target: {target.DatabaseName}; one business for the contested slug; retained without teardown.");
    }

    [Fact]
    public async Task Concurrent_registrations_with_the_same_username_keep_one_owner_and_roll_the_loser_back_without_an_orphan_business()
    {
        var target = await ResolveTargetAsync("_UsernameRace");
        if (target is null)
            return;

        await using (var winnerSession = new DbSession(target.ConnectionString))
        {
            var winner = await RegisterAsync(winnerSession, "peluqueria-de-marina", "marina");
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // The loser uses a different, still free slug, so its business insert succeeds; the owner insert is the one that
        // loses. Its transaction must roll back, leaving no business without an owner behind.
        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var loser = await RegisterAsync(
                loserSession,
                "estudio-lucia",
                "marina",
                ownerAccounts: new FreeUsernameWindowRepository(new SqlOwnerAccountRepository(loserSession)));

            Assert.False(loser.Succeeded);
            Assert.Equal("username_taken", loser.Error!.Code);
            Assert.Equal(nameof(RegisterBusinessRequest.Username), loser.Error.Field);
        }

        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Businesses"));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM OwnerAccounts"));
        Assert.Equal(0, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Businesses WHERE Slug = @slug", p => p.AddValue("@slug", "estudio-lucia")));
        Assert.True(await BusinessExistsAsync(target.ConnectionString, "peluqueria-de-marina"));
        output.WriteLine($"Target: {target.DatabaseName}; the losing registration left no orphan business; retained without teardown.");
    }

    [Fact]
    public async Task A_settings_edit_that_loses_the_slug_race_reports_slug_taken_and_leaves_both_stored_slugs_unchanged()
    {
        var target = await ResolveTargetAsync("_SettingsSlugRace");
        if (target is null)
            return;

        var firstId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        await SeedBusinessAsync(target.ConnectionString, "estudio-lucia");

        await using (var session = new DbSession(target.ConnectionString))
        {
            var service = new BusinessSettingsService(new FreeSlugWindowRepository(new SqlBusinessRepository(session)));
            var result = await service.UpdateAsync(firstId, new UpdateBusinessSettingsRequest(
                Name: "Peluquería de Marina",
                Slug: "estudio-lucia",
                ContactPhone: "1122334455",
                NotificationEmail: "avisos@marina.com",
                AllowsWalkIns: false,
                ReminderLeadHours: 48,
                DailySummaryTime: new TimeOnly(20, 30),
                StockReviewEveryDays: 14));

            Assert.False(result.Succeeded);
            Assert.Equal("slug_taken", result.Error!.Code);
            Assert.Equal(nameof(UpdateBusinessSettingsRequest.Slug), result.Error.Field);
        }

        // Atomic: the rejected statement changes no column, so both stored slugs survive untouched and only one row
        // holds the contested slug.
        Assert.True(await BusinessExistsAsync(target.ConnectionString, "peluqueria-de-marina"));
        Assert.True(await BusinessExistsAsync(target.ConnectionString, "estudio-lucia"));
        Assert.Equal(1, await CountAsync(target.ConnectionString,
            "SELECT COUNT(*) FROM Businesses WHERE Slug = @slug", p => p.AddValue("@slug", "estudio-lucia")));
        output.WriteLine($"Target: {target.DatabaseName}; the rejected rename left both slugs unchanged; retained without teardown.");
    }

    [Fact]
    public async Task A_different_unique_key_on_owner_accounts_still_propagates_instead_of_being_reported_as_username_taken()
    {
        var target = await ResolveTargetAsync("_ForeignUnique");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");

        await using (var session = new DbSession(target.ConnectionString))
        {
            var ownerAccounts = new SqlOwnerAccountRepository(session);
            var first = await ownerAccounts.InsertAsync(new OwnerAccount
            {
                BusinessId = businessId,
                Username = "marina",
                PasswordHash = "hashed:correcta123",
                CreatedAt = FixedNow
            });
            Assert.True(first.IsInserted);

            // A second owner for the same business violates UQ_OwnerAccounts_Business, not UQ_OwnerAccounts_Username.
            // The narrow catch must not disguise it as username_taken: it has to surface as the real SqlException.
            var exception = await Assert.ThrowsAsync<SqlException>(() => ownerAccounts.InsertAsync(new OwnerAccount
            {
                BusinessId = businessId,
                Username = "otra",
                PasswordHash = "hashed:correcta123",
                CreatedAt = FixedNow
            }));

            Assert.Contains(exception.Number, new[] { 2601, 2627 });
            Assert.Contains(exception.Errors.Cast<SqlError>(),
                error => error.Number is 2601 or 2627
                    && error.Message.Contains("UQ_OwnerAccounts_Business", StringComparison.Ordinal));
        }

        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM OwnerAccounts"));
        output.WriteLine($"Target: {target.DatabaseName}; the foreign unique key propagated unchanged; retained without teardown.");
    }

    /// <summary>
    /// Drives one registration. When <paramref name="businesses"/> is a "free window" decorator the sequential slug
    /// precheck is forced to pass while the real insert still meets storage, and the same trick forces the username
    /// precheck through <paramref name="ownerAccounts"/>.
    /// </summary>
    private static async Task<WorkUp.BusinessLogic.Common.Result<OwnerSession>> RegisterAsync(
        DbSession session,
        string slug,
        string username,
        IBusinessRepository? businesses = null,
        IOwnerAccountRepository? ownerAccounts = null) =>
        await new RegisterBusinessService(
            businesses ?? new SqlBusinessRepository(session),
            ownerAccounts ?? new SqlOwnerAccountRepository(session),
            session,
            new FakePasswordHasher(),
            new FakeClock { Now = FixedNow })
        .RegisterAsync(new RegisterBusinessRequest(
            Name: "Peluquería de Marina",
            Slug: slug,
            ContactPhone: "1122334455",
            NotificationEmail: "avisos@marina.com",
            Username: username,
            Password: "correcta123",
            ConfirmPassword: "correcta123"));

    private static async Task<int> SeedBusinessAsync(string connectionString, string slug)
    {
        await using var session = new DbSession(connectionString);
        var inserted = await new SqlBusinessRepository(session).InsertAsync(new Business
        {
            Name = slug,
            Slug = slug,
            ContactPhone = "1122334455",
            CreatedAt = FixedNow
        });
        Assert.True(inserted.IsInserted, "the fixture could not seed the business");
        return inserted.BusinessId;
    }

    private static async Task<bool> BusinessExistsAsync(string connectionString, string slug) =>
        await CountAsync(connectionString, "SELECT COUNT(*) FROM Businesses WHERE Slug = @slug",
            p => p.AddValue("@slug", slug)) == 1;

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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T27_BUSINESS_SQL_CONNECTION
        // and the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT27BusinessSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT27BusinessSql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T27 business SQL integration test",
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
    /// Deterministic "lost the check-then-write window" model for the slug: it reports the slug free even when another
    /// session already stored it, so the real INSERT/UPDATE meets the unique constraint exactly as the losing request
    /// would. Every other call goes to the real repository; no write is faked.
    /// </summary>
    private sealed class FreeSlugWindowRepository(IBusinessRepository inner) : IBusinessRepository
    {
        public Task<Business?> GetByIdAsync(int id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<Business?> GetBySlugAsync(string slug, CancellationToken ct = default) => inner.GetBySlugAsync(slug, ct);

        public Task<bool> SlugExistsAsync(string slug, int? excludingId = null, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<BusinessInsertResult> InsertAsync(Business business, CancellationToken ct = default) =>
            inner.InsertAsync(business, ct);

        public Task<BusinessUpdateResult> UpdateAsync(Business business, CancellationToken ct = default) =>
            inner.UpdateAsync(business, ct);
    }

    /// <summary>The same deterministic lost window for the username.</summary>
    private sealed class FreeUsernameWindowRepository(IOwnerAccountRepository inner) : IOwnerAccountRepository
    {
        public Task<OwnerAccount?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
            inner.GetByUsernameAsync(username, ct);

        public Task<OwnerAccount?> GetByBusinessIdAsync(int businessId, CancellationToken ct = default) =>
            inner.GetByBusinessIdAsync(businessId, ct);

        public Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<OwnerAccountInsertResult> InsertAsync(OwnerAccount account, CancellationToken ct = default) =>
            inner.InsertAsync(account, ct);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
