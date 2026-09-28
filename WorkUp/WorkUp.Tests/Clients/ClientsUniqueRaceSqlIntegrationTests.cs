using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Common;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Clients;
using WorkUp.Domain.Entities;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Clients;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for the per-business unique email race of RF-01 / Regla 7 (T27):
/// <c>UQ_Clients_Email</c> is unique on <c>(BusinessId, Email)</c>, so a create or an edit that passed its sequential
/// precheck can still lose the write to a competing writer that committed in between. The unit tests prove the service
/// maps the typed outcome to the existing <c>client_email_taken</c> field error; only storage can prove that the real
/// <c>UQ_Clients_Email</c> violation is the one reported, that no second ficha is written, that a rejected edit leaves
/// the stored row unchanged, that the constraint stays scoped to the business, and that a foreign key or any other
/// storage failure still propagates instead of being disguised as an email conflict. Requires a new, dedicated database
/// supplied through WORKUP_T27_CLIENTS_SQL_CONNECTION; without it every test returns N/A and no SQL connection is
/// opened. Each test derives its own database name from the supplied one, so the whole class runs in a single command,
/// and nothing is torn down: the target is retained on purpose.
/// </summary>
public sealed class ClientsUniqueRaceSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T27_CLIENTS_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>The single clock instant every create, edit and seeded row shares.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task Concurrent_creates_with_the_same_email_keep_one_fiche_and_report_email_taken_without_a_500()
    {
        var target = await ResolveTargetAsync("_CreateRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");

        // Winner: a real create that commits the email.
        await using (var winnerSession = new DbSession(target.ConnectionString))
        {
            var winner = await CreateAsync(winnerSession, businessId, "marina@peluqueria.com");
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // Loser: an independent session whose sequential email precheck is forced to report the email free, exactly
        // what a competing request reads just before the winner commits. The real INSERT then meets UQ_Clients_Email,
        // and the repository must turn that one violation into a typed outcome instead of letting it escape as a 500.
        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var loser = await CreateAsync(
                loserSession,
                businessId,
                "marina@peluqueria.com",
                new FreeEmailWindowClientRepository(new SqlClientRepository(loserSession)));

            Assert.False(loser.Succeeded);
            Assert.Equal("client_email_taken", loser.Error!.Code);
            Assert.Equal(nameof(CreateClientRequest.Email), loser.Error.Field);
        }

        // The losing create wrote nothing: one ficha holds the contested email, not two.
        Assert.Equal(1, await CountClientsAsync(target.ConnectionString, businessId, "marina@peluqueria.com"));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Clients"));
        output.WriteLine($"Target: {target.DatabaseName}; one ficha for the contested email; retained without teardown.");
    }

    [Fact]
    public async Task A_public_find_or_create_that_loses_the_email_race_reports_email_taken_instead_of_a_competing_fiche()
    {
        var target = await ResolveTargetAsync("_FindOrCreateRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        await SeedClientAsync(target.ConnectionString, businessId, "marina@peluqueria.com");

        // The public path looks the email up and inserts without a precheck, so it has the same window. The decorator
        // forces that lookup to report nothing — what the losing request reads just before the competitor commits —
        // while the INSERT still meets the real unique key.
        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var result = await new ClientsService(
                    new FreeEmailWindowClientRepository(new SqlClientRepository(loserSession)),
                    new SqlClientNoteRepository(loserSession),
                    new FakeClock { Now = FixedNow })
                .FindOrCreateByEmailAsync(businessId, "marina@peluqueria.com", "Marina", "Gómez", "1122334455");

            // No identity is handed back: reading Value from a failed result is a programming error, so the failure plus
            // the row count below is what proves the competing ficha was never adopted as this request's identity.
            Assert.False(result.Succeeded);
            Assert.Equal("client_email_taken", result.Error!.Code);
            Assert.Equal("email", result.Error.Field);
        }

        Assert.Equal(1, await CountClientsAsync(target.ConnectionString, businessId, "marina@peluqueria.com"));
        Assert.Equal(1, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Clients"));
        output.WriteLine($"Target: {target.DatabaseName}; the losing booking bound no competing ficha; retained without teardown.");
    }

    [Fact]
    public async Task A_contact_edit_that_loses_the_email_race_reports_email_taken_and_leaves_the_stored_row_unchanged()
    {
        var target = await ResolveTargetAsync("_EditRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var ownId = await SeedClientAsync(target.ConnectionString, businessId, "marina@peluqueria.com");
        var otherId = await SeedClientAsync(target.ConnectionString, businessId, "lucia@yoga.com");

        await using (var loserSession = new DbSession(target.ConnectionString))
        {
            var result = await UpdateAsync(
                loserSession,
                businessId,
                ownId,
                new UpdateClientRequest("Marina", "Pérez", "lucia@yoga.com", "1199887766", "Actualizada"),
                new FreeEmailWindowClientRepository(new SqlClientRepository(loserSession)));

            Assert.False(result.Succeeded);
            Assert.Equal("client_email_taken", result.Error!.Code);
            Assert.Equal(nameof(UpdateClientRequest.Email), result.Error.Field);
        }

        // Atomic: the rejected statement changed no column, so both fichas survive exactly as they were. A partial
        // write here would have repointed one ficha's contact data without its email.
        var stored = await GetByIdAsync(target.ConnectionString, businessId, ownId);
        Assert.NotNull(stored);
        Assert.Equal("marina@peluqueria.com", stored!.Email);
        Assert.Equal("Gómez", stored.LastName);
        Assert.Null(stored.HealthNotes);

        var other = await GetByIdAsync(target.ConnectionString, businessId, otherId);
        Assert.Equal("lucia@yoga.com", other!.Email);
        output.WriteLine($"Target: {target.DatabaseName}; the rejected edit left both rows unchanged; retained without teardown.");
    }

    [Fact]
    public async Task The_email_race_is_scoped_to_the_business_so_another_tenant_still_creates_its_own_fiche()
    {
        var target = await ResolveTargetAsync("_TenantScope");
        if (target is null)
            return;

        var firstBusiness = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var secondBusiness = await SeedBusinessAsync(target.ConnectionString, "estudio-lucia");
        Assert.True(firstBusiness != secondBusiness, "the fixture seeded two distinct tenants");

        await using (var firstSession = new DbSession(target.ConnectionString))
        {
            var winner = await CreateAsync(firstSession, firstBusiness, "marina@peluqueria.com");
            Assert.True(winner.Succeeded, winner.Error?.Code);
        }

        // The 2601/2627 mapping must stay inside the tenant that owns the constraint: the same email in another
        // business is a different ficha (Regla 7), so a lost window there must insert, not report a cross-tenant conflict.
        await using (var secondSession = new DbSession(target.ConnectionString))
        {
            var otherTenant = await CreateAsync(
                secondSession,
                secondBusiness,
                "marina@peluqueria.com",
                new FreeEmailWindowClientRepository(new SqlClientRepository(secondSession)));

            Assert.True(otherTenant.Succeeded, otherTenant.Error?.Code);
            Assert.True(otherTenant.Value.Id > 0);
        }

        Assert.Equal(1, await CountClientsAsync(target.ConnectionString, firstBusiness, "marina@peluqueria.com"));
        Assert.Equal(1, await CountClientsAsync(target.ConnectionString, secondBusiness, "marina@peluqueria.com"));
        Assert.Equal(2, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Clients"));
        output.WriteLine($"Target: {target.DatabaseName}; the constraint stayed per business; retained without teardown.");
    }

    [Fact]
    public async Task A_foreign_key_violation_still_propagates_instead_of_being_reported_as_email_taken()
    {
        var target = await ResolveTargetAsync("_ForeignKey");
        if (target is null)
            return;

        await using var session = new DbSession(target.ConnectionString);

        // A missing business violates FK_Clients_Businesses, not UQ_Clients_Email. The narrow catch must not disguise
        // an unknown tenant as a benign email conflict: it has to surface as the real SqlException.
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => CreateAsync(session, 987654321, "marina@peluqueria.com"));

        Assert.Equal(547, exception.Number);
        Assert.Contains("FK_Clients_Businesses", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(target.ConnectionString, "SELECT COUNT(*) FROM Clients"));
        output.WriteLine($"Target: {target.DatabaseName}; the foreign key violation propagated unchanged; retained without teardown.");
    }

    [Fact]
    public async Task An_unexpected_storage_failure_on_the_edit_still_propagates_instead_of_being_reported_as_email_taken()
    {
        var target = await ResolveTargetAsync("_EditOtherFailure");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var clientId = await SeedClientAsync(target.ConnectionString, businessId, "marina@peluqueria.com");

        await using (var session = new DbSession(target.ConnectionString))
        {
            // Email is valid, so this never reaches the unique key: the value is too long for NVARCHAR(60), which is a
            // truncation error and not an email conflict. SQL Server reports it as 8152 (legacy generic message) or as
            // 2628 (table/column detail, raised from 2019 CU27 on), so either number still proves truncation.
            var tooLongLastName = new string('a', 100);
            var exception = await Assert.ThrowsAsync<SqlException>(
                () => UpdateAsync(session, businessId, clientId,
                    new UpdateClientRequest("Marina", tooLongLastName, "marina@peluqueria.com", "1122334455", null)));

            Assert.True(
                exception.Number is 8152 or 2628,
                $"expected a string-truncation SqlException (8152 or 2628) but got {exception.Number}: {exception.Message}");
        }

        var stored = await GetByIdAsync(target.ConnectionString, businessId, clientId);
        Assert.Equal("Gómez", stored!.LastName);
        output.WriteLine($"Target: {target.DatabaseName}; the truncation error propagated unchanged; retained without teardown.");
    }

    /// <summary>
    /// Drives one create through the real service. When <paramref name="clients"/> is the "free window" decorator the
    /// sequential email precheck is forced to pass while the real insert still meets storage.
    /// </summary>
    private static async Task<Result<Client>> CreateAsync(
        DbSession session, int businessId, string email, IClientRepository? clients = null) =>
        await new ClientsService(
                clients ?? new SqlClientRepository(session),
                new SqlClientNoteRepository(session),
                new FakeClock { Now = FixedNow })
            .CreateAsync(businessId, new CreateClientRequest("Marina", "Gómez", email, "1122334455", null));

    private static async Task<Result> UpdateAsync(
        DbSession session, int businessId, int clientId, UpdateClientRequest request, IClientRepository? clients = null) =>
        await new ClientsService(
                clients ?? new SqlClientRepository(session),
                new SqlClientNoteRepository(session),
                new FakeClock { Now = FixedNow })
            .UpdateAsync(businessId, clientId, request);

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

    private static async Task<int> SeedClientAsync(string connectionString, int businessId, string email)
    {
        await using var session = new DbSession(connectionString);
        var inserted = await new SqlClientRepository(session).InsertAsync(new Client
        {
            BusinessId = businessId,
            FirstName = "Marina",
            LastName = "Gómez",
            Email = email,
            Phone = "1122334455",
            CreatedAt = FixedNow
        });
        Assert.True(inserted.IsInserted, "the fixture could not seed the ficha");
        return inserted.ClientId;
    }

    private static async Task<Client?> GetByIdAsync(string connectionString, int businessId, int clientId)
    {
        await using var session = new DbSession(connectionString);
        return await new SqlClientRepository(session).GetByIdAsync(businessId, clientId);
    }

    private static async Task<int> CountClientsAsync(string connectionString, int businessId, string email) =>
        await CountAsync(connectionString,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            p => p.AddValue("@businessId", businessId).AddValue("@email", email));

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

        // Suffixed so each opt-in test derives its own fresh target from one configured WORKUP_T27_CLIENTS_SQL_CONNECTION
        // and the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT27ClientsSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT27ClientsSql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T27 clients SQL integration test",
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
    /// Deterministic "lost the check-then-write window" model for the email: it reports the email free and the ficha
    /// absent even when another session already stored it, so the real INSERT/UPDATE meets the unique constraint
    /// exactly as the losing request would. Every other call goes to the real repository; no write is faked.
    /// </summary>
    private sealed class FreeEmailWindowClientRepository(IClientRepository inner) : IClientRepository
    {
        public Task<List<Client>> SearchAsync(int businessId, string? query, CancellationToken ct = default) =>
            inner.SearchAsync(businessId, query, ct);

        public Task<Client?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
            inner.GetByIdAsync(businessId, id, ct);

        public Task<Client?> GetByEmailAsync(int businessId, string email, CancellationToken ct = default) =>
            Task.FromResult<Client?>(null);

        public Task<bool> EmailExistsAsync(int businessId, string email, int? excludingId = null, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<ClientInsertResult> InsertAsync(Client client, CancellationToken ct = default) =>
            inner.InsertAsync(client, ct);

        public Task<ClientUpdateResult> UpdateAsync(Client client, CancellationToken ct = default) =>
            inner.UpdateAsync(client, ct);

        public Task<bool> HasAccountAsync(int businessId, int clientId, CancellationToken ct = default) =>
            inner.HasAccountAsync(businessId, clientId, ct);

        public Task<List<ClientBookingHistoryItem>> ListBookingHistoryAsync(int businessId, int clientId, CancellationToken ct = default) =>
            inner.ListBookingHistoryAsync(businessId, clientId, ct);

        public Task<List<ClientPaymentHistoryItem>> ListPaymentHistoryAsync(int businessId, int clientId, CancellationToken ct = default) =>
            inner.ListPaymentHistoryAsync(businessId, clientId, ct);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
