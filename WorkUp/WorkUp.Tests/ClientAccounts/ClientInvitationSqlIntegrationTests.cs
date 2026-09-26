using System.Data;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.ClientAccounts;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.ClientAccounts;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlClientInvitationRepository"/> driven by
/// <see cref="ClientInvitationService"/> (T22, RF-26): the owner-assisted invitation, reissue and revoke. It requires
/// a new, dedicated database supplied through <c>WORKUP_T22_INVITATION_SQL_CONNECTION</c>; without it every test
/// returns N/A and no SQL connection is opened. Each test derives its own database name from the supplied one, so the
/// whole class runs in a single command, the preflight asserts <c>DB_ID</c> is absent before migrating, and nothing is
/// torn down: the target is retained on purpose and never reused.
/// <para>
/// The service tests prove the issue and revoke rules against an in-memory port; they cannot prove what storage does
/// with the Clients row padlock, a transaction or a real race. This fixture is the runtime proof of the port: the
/// invitation stored with the business, the ficha and the normalised destination, its 48 h expiry and 32-byte token
/// hash while the raw token travels only inside the queued email, the single <c>invite:{id}</c> notice, a reissue that
/// revokes the previous invitation and leaves exactly one live, an idempotent revoke, the generic refusal of a missing
/// or foreign ficha, the linked-account precheck, two simultaneous issues that serialize into one live invitation, and
/// a link-builder failure that rolls back the replacement write and keeps the previous invitation live.
/// </para>
/// </summary>
public sealed class ClientInvitationSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T22_INVITATION_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT22InvitationSql";
    private const string TrustedBaseUrl = "https://reservas.example.com";
    private const string Slug = "peluqueria-t22";
    private const string OtherSlug = "yoga-t22";
    private const string BusinessName = "Peluquería Marina";
    private const string FirstEmail = "ana@example.com";
    private const string DestinationEmail = "nueva@example.com";
    private const string SecondDestinationEmail = "segunda@example.com";
    private const string InvitationMarker = "/cuenta/invitacion/";
    private static readonly Regex SafeDatabaseName = new(@"\AWorkUpDemoT22InvitationSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>The single clock instant every scenario starts from.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task Issue_persists_the_owner_invitation_and_a_reissue_leaves_exactly_one_live()
    {
        var target = await PrepareAsync("_Issue");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstEmail);
        var clock = new FakeClock { Now = FixedNow };

        await using var session = new DbSession(connectionString);
        var service = NewService(session, clock: clock);

        // The owner confirms a destination that differs from the ficha email; the service normalises it.
        var issued = await service.IssueAsync(businessId, clientId, "  Nueva@Example.COM ");

        Assert.True(issued.Succeeded, issued.Error?.Code);
        Assert.Equal(DestinationEmail, issued.Value.Email);
        Assert.Equal(FixedNow.AddHours(48), issued.Value.ExpiresAt);

        var invitation = Assert.Single(await ReadInvitationsAsync(connectionString, businessId));
        Assert.Equal(businessId, invitation.BusinessId);
        Assert.Equal(clientId, invitation.ClientId);
        Assert.Equal(DestinationEmail, invitation.Email);
        Assert.Equal(32, invitation.TokenHash.Length);
        Assert.Equal(FixedNow.AddHours(48), invitation.ExpiresAt);
        Assert.Equal(FixedNow, invitation.CreatedAt);
        Assert.Null(invitation.UsedAt);
        Assert.Null(invitation.RevokedAt);

        var notification = Assert.Single(await ReadNotificationsAsync(connectionString, businessId));
        Assert.Equal((byte)NotificationType.AccountInvitation, notification.Type);
        Assert.Equal(DestinationEmail, notification.Recipient);
        Assert.Equal(ClientInvitationService.InvitationDedupKey(invitation.Id), notification.DedupKey);
        Assert.StartsWith("invite:", notification.DedupKey, StringComparison.Ordinal);

        // Only the 32-byte SHA-256 hash is stored; the raw token exists exclusively inside the queued email.
        var rawToken = TokenFrom(notification.BodyHtml);
        Assert.Equal(SecureToken.Hash(rawToken), invitation.TokenHash);
        Assert.Contains(rawToken, notification.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, Convert.ToHexString(invitation.TokenHash), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawToken, notification.DedupKey, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, notification.Subject, StringComparison.Ordinal);
        Assert.StartsWith($"{TrustedBaseUrl}/r/{Slug}{InvitationMarker}", LinkFrom(notification.BodyHtml));

        // Reissue: the previous live invitation is revoked at the reissue instant and exactly one stays live.
        clock.Now = FixedNow.AddHours(3);
        var reissued = await service.IssueAsync(businessId, clientId, SecondDestinationEmail);

        Assert.True(reissued.Succeeded, reissued.Error?.Code);
        var invitations = await ReadInvitationsAsync(connectionString, businessId);
        Assert.Equal(2, invitations.Count);
        var live = Assert.Single(invitations, candidate => candidate.UsedAt is null && candidate.RevokedAt is null);
        Assert.Equal(SecondDestinationEmail, live.Email);
        Assert.Equal(FixedNow.AddHours(3), invitations[0].RevokedAt);
        Assert.True(live.ExpiresAt > clock.Now);

        var notifications = await ReadNotificationsAsync(connectionString, businessId);
        Assert.Equal(2, notifications.Count);
        Assert.NotEqual(notifications[0].DedupKey, notifications[1].DedupKey);
        Assert.Equal(ClientInvitationService.InvitationDedupKey(live.Id), notifications[1].DedupKey);

        // Revoke is idempotent: the first call revokes the live invitation and the second changes nothing new.
        var firstRevoke = await service.RevokeAsync(businessId, clientId);
        var secondRevoke = await service.RevokeAsync(businessId, clientId);

        Assert.True(firstRevoke.Succeeded, firstRevoke.Error?.Code);
        Assert.True(secondRevoke.Succeeded, secondRevoke.Error?.Code);
        var revoked = await ReadInvitationsAsync(connectionString, businessId);
        Assert.DoesNotContain(revoked, candidate => candidate.UsedAt is null && candidate.RevokedAt is null);
        Assert.Equal(FixedNow.AddHours(3), revoked.Single(candidate => candidate.Id == live.Id).RevokedAt);
        Assert.Equal(2, (await ReadNotificationsAsync(connectionString, businessId)).Count);

        output.WriteLine(
            "SQL assertions passed: the owner invitation stored the business, the ficha and the normalised " +
            "destination with a 48 h expiry and a 32-byte token hash, the raw token lived only in the single " +
            "invite:{id} notice, the reissue revoked the previous invitation leaving exactly one live, and the revoke " +
            "was idempotent.");
    }

    [Fact]
    public async Task Guards_refuse_missing_foreign_and_linked_fichas_without_writing()
    {
        var target = await PrepareAsync("_Guards");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var linkedClientId = await SeedClientAsync(connectionString, businessId, "link@example.com");
        await SeedLinkedAccountAsync(connectionString, businessId, linkedClientId, "link@example.com");
        var otherBusinessId = await SeedBusinessAsync(connectionString, OtherSlug, "Estudio de Yoga");
        var foreignClientId = await SeedClientAsync(connectionString, otherBusinessId, "ajena@example.com", "Bruno", "Pérez");

        await using var session = new DbSession(connectionString);
        var service = NewService(session);

        var missingIssue = await service.IssueAsync(businessId, 999_999, DestinationEmail);
        var foreignIssue = await service.IssueAsync(businessId, foreignClientId, DestinationEmail);
        var linkedIssue = await service.IssueAsync(businessId, linkedClientId, DestinationEmail);
        var missingRevoke = await service.RevokeAsync(businessId, 999_999);
        var foreignRevoke = await service.RevokeAsync(businessId, foreignClientId);

        // A missing ficha and a ficha of another business get the same generic error and no row.
        Assert.False(missingIssue.Succeeded);
        Assert.Equal("client_invitation_unavailable", missingIssue.Error!.Code);
        Assert.False(foreignIssue.Succeeded);
        Assert.Equal("client_invitation_unavailable", foreignIssue.Error!.Code);
        Assert.Equal(missingIssue.Error, foreignIssue.Error);

        // An already linked ficha is refused through the precheck, before any Clients row lock and any write.
        Assert.False(linkedIssue.Succeeded);
        Assert.Equal("client_invitation_already_linked", linkedIssue.Error!.Code);

        Assert.False(missingRevoke.Succeeded);
        Assert.Equal("client_invitation_unavailable", missingRevoke.Error!.Code);
        Assert.False(foreignRevoke.Succeeded);
        Assert.Equal("client_invitation_unavailable", foreignRevoke.Error!.Code);

        Assert.Empty(await ReadInvitationsAsync(connectionString, businessId));
        Assert.Empty(await ReadInvitationsAsync(connectionString, otherBusinessId));
        Assert.Empty(await ReadNotificationsAsync(connectionString, businessId));

        output.WriteLine(
            "SQL guard assertions passed: a missing and a foreign ficha were the generic refusal with no invitation " +
            "written, the linked-account precheck denied the issue before the lock, and the missing and foreign revoke " +
            "were refused without writing.");
    }

    [Fact]
    public async Task Concurrent_issues_of_the_same_ficha_serialize_into_one_live_invitation()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstEmail);

        // Both issuers start together; the Clients row is taken UPDLOCK, ROWLOCK, HOLDLOCK, so the second waits for
        // the first to commit before it revokes and inserts. This observes one forced overlap, not every schedule.
        using var startGate = new Barrier(participantCount: 2);

        Task<Result<ClientInvitationReceipt>> IssueAsync(string destination) => Task.Run(async () =>
        {
            await using var session = new DbSession(connectionString);
            startGate.SignalAndWait();
            return await NewService(session).IssueAsync(businessId, clientId, destination);
        });

        var results = await Task.WhenAll(
            IssueAsync(DestinationEmail),
            IssueAsync(SecondDestinationEmail));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Error?.Code));

        var invitations = await ReadInvitationsAsync(connectionString, businessId);
        Assert.Equal(2, invitations.Count);
        Assert.Single(invitations, candidate => candidate.UsedAt is null && candidate.RevokedAt is null);

        var notifications = await ReadNotificationsAsync(connectionString, businessId);
        Assert.Equal(2, notifications.Count);
        Assert.NotEqual(notifications[0].DedupKey, notifications[1].DedupKey);

        output.WriteLine(
            "SQL race assertions passed: two simultaneous issues of the same ficha serialized on the Clients UPDLOCK " +
            "padlock and left exactly one live invitation; this is one forced overlap, not a universal no-deadlock claim.");
    }

    [Fact]
    public async Task A_link_builder_failure_after_the_new_invitation_write_rolls_back_and_keeps_the_old_invitation_live()
    {
        var target = await PrepareAsync("_Rollback");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstEmail);

        int firstInvitationId;
        await using (var seedSession = new DbSession(connectionString))
        {
            var seeded = await NewService(seedSession, clock: new FakeClock { Now = FixedNow })
                .IssueAsync(businessId, clientId, FirstEmail);
            Assert.True(seeded.Succeeded, seeded.Error?.Code);
            firstInvitationId = Assert.Single(await ReadInvitationsAsync(connectionString, businessId)).Id;
        }

        // The service issues the replacement (revoke previous + insert) before composing the notice; the failing link
        // builder throws right after that write, so the whole issue must roll back.
        var clock = new FakeClock { Now = FixedNow.AddHours(5) };
        await using var session = new DbSession(connectionString);
        var service = NewService(session, new ThrowingInvitationUrlBuilder(), clock);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IssueAsync(businessId, clientId, DestinationEmail));

        var invitation = Assert.Single(await ReadInvitationsAsync(connectionString, businessId));
        Assert.Equal(firstInvitationId, invitation.Id);
        Assert.Equal(FirstEmail, invitation.Email);
        Assert.Null(invitation.RevokedAt);
        Assert.Null(invitation.UsedAt);

        var notification = Assert.Single(await ReadNotificationsAsync(connectionString, businessId));
        Assert.Equal(FirstEmail, notification.Recipient);

        output.WriteLine(
            "SQL rollback assertions passed: the link builder threw after the replacement invitation write, and the " +
            "rollback left the previous invitation live with no new notice and no replacement row.");
    }

    // --- Service wiring -----------------------------------------------------------------------------------------

    private static ClientInvitationService NewService(
        DbSession session,
        IClientInvitationUrlBuilder? urlBuilder = null,
        IClock? clock = null) =>
        new(new SqlBusinessRepository(session), new SqlClientInvitationRepository(session), session,
            urlBuilder ?? new TrustedInvitationUrlBuilder(), clock ?? new FakeClock { Now = FixedNow });

    // --- Seeding ------------------------------------------------------------------------------------------------

    private static async Task<int> SeedBusinessAsync(string connectionString, string slug, string name) =>
        await InsertIdAsync(connectionString,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @createdAt)",
            ("@name", name), ("@slug", slug), ("@createdAt", FixedNow));

    private static async Task<int> SeedClientAsync(
        string connectionString, int businessId, string email,
        string firstName = "Ana", string lastName = "Gómez") =>
        await InsertIdAsync(connectionString,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @createdAt)",
            ("@businessId", businessId), ("@firstName", firstName), ("@lastName", lastName),
            ("@email", email), ("@createdAt", FixedNow));

    private static async Task SeedLinkedAccountAsync(
        string connectionString, int businessId, int clientId, string email) =>
        await InsertIdAsync(connectionString,
            "INSERT INTO ClientAccounts (BusinessId, ClientId, Email, PasswordHash, EmailVerifiedAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @email, @passwordHash, @verifiedAt, @createdAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@email", email),
            ("@passwordHash", "synthetic-hash"), ("@verifiedAt", FixedNow), ("@createdAt", FixedNow));

    // --- Plain reads of the persisted state ---------------------------------------------------------------------

    private sealed record StoredInvitation(
        int Id, int BusinessId, int ClientId, string Email, byte[] TokenHash,
        DateTime ExpiresAt, DateTime? UsedAt, DateTime? RevokedAt, DateTime CreatedAt);

    private sealed record StoredNotification(int Id, byte Type, string Recipient, string Subject, string BodyHtml, string DedupKey);

    private static Task<List<StoredInvitation>> ReadInvitationsAsync(string connectionString, int businessId) =>
        QueryListAsync(connectionString,
            "SELECT Id, BusinessId, ClientId, Email, TokenHash, ExpiresAt, UsedAt, RevokedAt, CreatedAt FROM AccountInvitations WHERE BusinessId = @businessId ORDER BY Id",
            reader => new StoredInvitation(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetFieldValue<byte[]>(4), reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7), reader.GetDateTime(8)),
            ("@businessId", businessId));

    private static Task<List<StoredNotification>> ReadNotificationsAsync(string connectionString, int businessId) =>
        QueryListAsync(connectionString,
            "SELECT Id, Type, Recipient, Subject, BodyHtml, DedupKey FROM Notifications WHERE BusinessId = @businessId ORDER BY Id",
            reader => new StoredNotification(
                reader.GetInt32(0), reader.GetByte(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5)),
            ("@businessId", businessId));

    private static string LinkFrom(string bodyHtml)
    {
        var match = Regex.Match(bodyHtml, "href=\"([^\"]+)\"");
        Assert.True(match.Success, "The invitation email must contain a link.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Extracts the raw token from the queued email, so assertions can prove only its hash was stored.</summary>
    private static string TokenFrom(string bodyHtml)
    {
        var link = LinkFrom(bodyHtml);
        var index = link.IndexOf(InvitationMarker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The invitation email must link to the account invitation route.");
        return link[(index + InvitationMarker.Length)..];
    }

    // --- Opt-in target ------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves this test's opt-in target. <c>null</c> means the variable is absent: the test returns N/A without
    /// opening any connection or touching any server. The created database is retained and never reused or dropped.
    /// </summary>
    private async Task<SqlTarget?> PrepareAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"N/A: {EnvVar} absent; no SQL connection opened.");
            return null;
        }

        var target = ValidateTarget(supplied, testSuffix);

        var master = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            TrustServerCertificate = true
        };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@database)", connection);
            check.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = target.DatabaseName;
            var existing = await check.ExecuteScalarAsync();
            Assert.True(existing is null or DBNull,
                $"Refusing existing database {target.DatabaseName}; the target must be new and is never reused or dropped.");
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        Assert.Contains("001_schema.sql", scripts);
        output.WriteLine(
            $"Target {target.DatabaseName}: DB_ID absent before migration; applied {scripts.Count} schema script(s); retained permanently without teardown.");
        return target;
    }

    /// <summary>
    /// Derives this test's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog,
    /// so one <c>WORKUP_T22_INVITATION_SQL_CONNECTION</c> value serves every fact. Only the validated LocalDB instance
    /// and the derived database survive into the connection string.
    /// </summary>
    private static SqlTarget ValidateTarget(string suppliedConnectionString, string testSuffix)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{EnvVar} could not be parsed; no SQL connection was opened.");
        }

        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && databaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal)
            && SafeDatabaseName.IsMatch(databaseName);
        if (!string.Equals(supplied.DataSource, LocalDb, StringComparison.OrdinalIgnoreCase)
            || !supplied.IntegratedSecurity
            || !safeName
            || !string.IsNullOrEmpty(supplied.UserID)
            || !string.IsNullOrEmpty(supplied.Password)
            || !string.IsNullOrEmpty(supplied.AttachDBFilename)
            || supplied.UserInstance
            || !string.IsNullOrEmpty(supplied.FailoverPartner))
        {
            throw new InvalidOperationException(
                $"{EnvVar} must target {LocalDb} with Integrated Security and a safe {DatabasePrefix}* database; credentials, attached files and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = LocalDb,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T22 invitation SQL integration test",
            TrustServerCertificate = true,
            MultipleActiveResultSets = false
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    // --- Plain reads and inserts --------------------------------------------------------------------------------

    private static async Task<List<T>> QueryListAsync<T>(
        string connectionString, string sql, Func<SqlDataReader, T> map,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<T>();
        while (await reader.ReadAsync())
            items.Add(map(reader));
        return items;
    }

    private static async Task<int> InsertIdAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlCommand Create(
        SqlConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection);
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
            case byte[] bytes:
                command.Parameters.Add(name, SqlDbType.Binary, bytes.Length).Value = bytes;
                return;
            case null:
                command.Parameters.AddWithValue(name, DBNull.Value);
                return;
            default:
                command.Parameters.AddWithValue(name, value);
                return;
        }
    }

    // --- Test-only builders -------------------------------------------------------------------------------------

    private sealed class TrustedInvitationUrlBuilder : IClientInvitationUrlBuilder
    {
        public string BuildInvitationUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildInvitationUrl(TrustedBaseUrl, slug, token);
    }

    /// <summary>Fails exactly where the trusted builder runs, after the issue already wrote its SQL rows.</summary>
    private sealed class ThrowingInvitationUrlBuilder : IClientInvitationUrlBuilder
    {
        public string BuildInvitationUrl(string slug, string token) =>
            throw new InvalidOperationException("synthetic invitation link builder failure");
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
