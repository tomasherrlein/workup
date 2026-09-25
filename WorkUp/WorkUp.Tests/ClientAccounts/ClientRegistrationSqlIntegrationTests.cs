using System.ComponentModel;
using System.Data;
using System.Diagnostics;
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
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.ClientAccounts;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlClientRegistrationRepository"/> and the registration
/// flow that drives it (RF-25, D-18). It requires a new, dedicated database supplied through
/// WORKUP_T20_SQL_CONNECTION; without it every test returns N/A and no SQL connection is opened. Each test derives
/// its own database name from the supplied one, so the whole class runs in a single command, and nothing is torn
/// down: the target is retained on purpose.
/// <para>
/// The service tests prove the registration rules against an in-memory port; they cannot prove what storage does with
/// a token, a transaction or a race. This fixture is the runtime proof of the port: the pending account stored with
/// its temporary names and no ficha, the ficha and the activation created by one verified link, the single-use
/// token consumed by purpose, expiry and tenant, a same-second resend that still delivers the rotated link, a
/// refused verification whose rollback returns the consumed token to its usable state, the guarded insert under a
/// real concurrent registration, and the outbox link built from the trusted base URL with no plaintext secret left
/// in the account or in the token.
/// </para>
/// </summary>
public sealed class ClientRegistrationSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T20_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string TrustedBaseUrl = "https://reservas.example.com";
    private const string Slug = "peluqueria-t20";
    private const string OtherSlug = "yoga-t20";
    private const string BusinessName = "Peluquería Marina";
    private const string Email = "ana@example.com";
    private const string OtherEmail = "bruno@example.com";
    private const string PlainPassword = "secreto-123";

    /// <summary>The single clock instant every scenario starts from.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task Schema_migration_applies_001_and_002_and_creates_the_nullable_pending_names()
    {
        var target = await ResolveTargetAsync("_Schema");
        if (target is null)
            return;

        var applied = await QueryListAsync(
            target.ConnectionString,
            "SELECT ScriptName FROM SchemaVersions ORDER BY ScriptName",
            reader => reader.GetString(0));

        Assert.Contains("001_schema.sql", applied);
        Assert.Contains("002_client_account_pending_names.sql", applied);

        var columns = await QueryListAsync(
            target.ConnectionString,
            """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH
              FROM INFORMATION_SCHEMA.COLUMNS
             WHERE TABLE_NAME = 'ClientAccounts' AND COLUMN_NAME IN ('PendingFirstName', 'PendingLastName')
             ORDER BY COLUMN_NAME
            """,
            reader => (
                Name: reader.GetString(0),
                Type: reader.GetString(1),
                Nullable: reader.GetString(2),
                Length: reader.GetInt32(3)));

        Assert.Equal(2, columns.Count);
        Assert.All(columns, column =>
        {
            Assert.Equal("nvarchar", column.Type);
            Assert.Equal("YES", column.Nullable);
            Assert.Equal(60, column.Length);
        });
        Assert.Contains(columns, column => column.Name == "PendingFirstName");
        Assert.Contains(columns, column => column.Name == "PendingLastName");

        output.WriteLine("Schema assertions passed: the fresh database applied 001 and 002 and ClientAccounts carries two nullable nvarchar(60) pending-name columns.");
    }

    [Fact]
    public async Task Registration_stores_a_pending_account_with_the_pending_names_and_no_ficha()
    {
        var target = await ResolveTargetAsync("_Register");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, Slug, BusinessName, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session, new SqlClientRegistrationRepository(session));

        var result = await service.RegisterAsync(Request());

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(Email, result.Value.Email);
        Assert.Equal(FixedNow.AddHours(48), result.Value.VerificationExpiresAt);

        var account = await ReadAccountAsync(target.ConnectionString, businessId, Email);
        Assert.NotNull(account);
        Assert.Null(account!.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        Assert.Equal("Ana", account.PendingFirstName);
        Assert.Equal("Gómez", account.PendingLastName);
        Assert.NotEqual(PlainPassword, account.PasswordHash);
        Assert.DoesNotContain(PlainPassword, account.PasswordHash, StringComparison.Ordinal);

        // The pending account creates no visible ficha before the email is confirmed.
        Assert.Equal(0, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId)));

        var notification = await ReadNotificationAsync(target.ConnectionString, businessId);
        Assert.NotNull(notification);
        Assert.Equal((byte)NotificationType.EmailVerification, notification!.Type);
        Assert.Equal(Email, notification.Recipient);
        Assert.Equal($"Confirmá tu cuenta en {BusinessName}", notification.Subject);

        var rawToken = TokenFrom(notification.BodyHtml);
        var token = await ReadTokenAsync(target.ConnectionString, account.Id);
        Assert.NotNull(token);
        Assert.Equal((byte)AccountTokenPurpose.EmailVerification, token!.Purpose);
        Assert.Null(token.UsedAt);
        Assert.Equal(FixedNow.AddHours(48), token.ExpiresAt);
        Assert.Equal(SecureToken.Hash(rawToken), token.Hash);
        Assert.Equal(32, token.Hash.Length);
        Assert.Equal(
            ClientRegistrationService.VerificationDedupKey(account.Id, token.Hash),
            notification.DedupKey);

        output.WriteLine("Registration assertions passed: the pending account carried the temporary names and no ficha was created, and the token was stored only as its 32-byte hash with a hash-bound dedup key in the outbox.");
    }

    [Fact]
    public async Task Verification_creates_one_ficha_and_activates_the_account_in_the_same_transaction()
    {
        var target = await ResolveTargetAsync("_Verify");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, Slug, BusinessName, FixedNow);
        var clock = new FakeClock { Now = FixedNow };

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session, new SqlClientRegistrationRepository(session), clock);

        Assert.True((await service.RegisterAsync(Request())).Succeeded);
        var notification = await ReadNotificationAsync(target.ConnectionString, businessId);
        var rawToken = TokenFrom(notification!.BodyHtml);
        clock.Now = FixedNow.AddHours(2);

        var result = await service.VerifyEmailAsync(Slug, rawToken);

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal("Ana", result.Value.FirstName);
        Assert.Equal("Gómez", result.Value.LastName);
        Assert.Equal(Email, result.Value.Email);

        var client = await ReadClientAsync(target.ConnectionString, businessId, Email);
        Assert.NotNull(client);
        Assert.Equal("Ana", client!.FirstName);
        Assert.Equal("Gómez", client.LastName);
        Assert.Equal(result.Value.ClientId, client.Id);
        // The ficha the public booking flow creates carries no phone and no health notes.
        Assert.Null(client.Phone);
        Assert.Null(client.HealthNotes);

        var account = await ReadAccountAsync(target.ConnectionString, businessId, Email);
        Assert.NotNull(account);
        Assert.Equal(client.Id, account!.ClientId);
        Assert.Equal(clock.Now, account.EmailVerifiedAt);
        Assert.Null(account.PendingFirstName);
        Assert.Null(account.PendingLastName);

        // The consumed link is marked used in the same transaction that created the ficha.
        var token = await ReadTokenAsync(target.ConnectionString, account.Id);
        Assert.NotNull(token);
        Assert.Equal(clock.Now, token!.UsedAt);

        Assert.Equal(1, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId)));

        output.WriteLine("Verification assertions passed: one verified link created exactly one ficha with the pending names, activated and cleared the pending account, and marked the token used.");
    }

    [Fact]
    public async Task Expired_replayed_foreign_and_wrong_purpose_tokens_are_refused_without_writing()
    {
        var target = await ResolveTargetAsync("_Tokens");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, Slug, BusinessName, FixedNow);
        var otherBusinessId = await SeedBusinessAsync(target.ConnectionString, OtherSlug, "Estudio de Yoga", FixedNow);
        var clock = new FakeClock { Now = FixedNow };

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session, new SqlClientRegistrationRepository(session), clock);

        // A pending account in the other business, so the foreign tenant check has a real target.
        Assert.True((await service.RegisterAsync(Request(slug: OtherSlug, email: OtherEmail))).Succeeded);
        var otherNotification = await ReadNotificationAsync(target.ConnectionString, otherBusinessId);
        var otherToken = TokenFrom(otherNotification!.BodyHtml);
        var otherAccount = await ReadAccountAsync(target.ConnectionString, otherBusinessId, OtherEmail);

        Assert.True((await service.RegisterAsync(Request())).Succeeded);
        var anaNotification = await ReadNotificationAsync(target.ConnectionString, businessId);
        var anaToken = TokenFrom(anaNotification!.BodyHtml);
        var anaAccount = await ReadAccountAsync(target.ConnectionString, businessId, Email);

        var errors = new List<BusinessError?>
        {
            // A token of another business is not a token of this one.
            (await service.VerifyEmailAsync(Slug, otherToken)).Error,
            // Blank and unknown links get the same answer.
            (await service.VerifyEmailAsync(Slug, " ")).Error,
            (await service.VerifyEmailAsync(Slug, "token-inexistente")).Error,
            (await service.VerifyEmailAsync("negocio-inexistente", anaToken)).Error
        };

        // The expired link: the clock passes the 48 h window, and the refused attempt does not consume the token.
        clock.Now = FixedNow.AddHours(49);
        errors.Add((await service.VerifyEmailAsync(Slug, anaToken)).Error);

        Assert.All(errors, error => Assert.Equal("verification_link_unavailable", error!.Code));
        Assert.All(errors, error => Assert.Equal(errors[0], error));

        var otherAfter = await ReadAccountAsync(target.ConnectionString, otherBusinessId, OtherEmail);
        Assert.Null(otherAfter!.ClientId);
        Assert.Null(otherAfter.EmailVerifiedAt);
        Assert.Null((await ReadTokenAsync(target.ConnectionString, otherAccount!.Id))!.UsedAt);

        var anaAfterForeignAndExpiry = await ReadAccountAsync(target.ConnectionString, businessId, Email);
        Assert.Null(anaAfterForeignAndExpiry!.ClientId);
        Assert.Null(anaAfterForeignAndExpiry.EmailVerifiedAt);
        Assert.Null((await ReadTokenAsync(target.ConnectionString, anaAccount!.Id))!.UsedAt);
        Assert.Equal(0, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId)));

        // The resend rescues the expired account, and the replacement link works once.
        clock.Now = FixedNow.AddHours(49);
        Assert.True((await service.ResendVerificationAsync(
            new ResendClientVerificationRequest(Slug, Email))).Succeeded);
        var rescuedNotification = (await ReadNotificationsAsync(target.ConnectionString, businessId))[1];
        var rescuedToken = TokenFrom(rescuedNotification.BodyHtml);
        Assert.True((await service.VerifyEmailAsync(Slug, rescuedToken)).Succeeded);

        var replayed = await service.VerifyEmailAsync(Slug, rescuedToken);
        Assert.False(replayed.Succeeded);
        Assert.Equal("verification_link_unavailable", replayed.Error!.Code);
        Assert.Equal(1, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId)));

        // A password-reset token of the same pending account never verifies an email, even with a valid expiry.
        await InsertTokenRowAsync(
            target.ConnectionString, otherAccount.Id, AccountTokenPurpose.PasswordReset,
            SecureToken.Hash("reset-token"), clock.Now.AddMinutes(30), clock.Now);
        var wrongPurpose = await service.VerifyEmailAsync(OtherSlug, "reset-token");
        Assert.False(wrongPurpose.Succeeded);
        Assert.Equal("verification_link_unavailable", wrongPurpose.Error!.Code);
        var otherFinally = await ReadAccountAsync(target.ConnectionString, otherBusinessId, OtherEmail);
        Assert.Null(otherFinally!.ClientId);
        Assert.Null(otherFinally.EmailVerifiedAt);

        output.WriteLine("Token assertions passed: expired, replayed, foreign-business and wrong-purpose links were all refused with one generic error and no write, the resend rescued the expired account, and the replacement link worked exactly once.");
    }

    [Fact]
    public async Task Resend_rotates_the_token_and_delivers_the_replacement_in_the_same_second()
    {
        var target = await ResolveTargetAsync("_Resend");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, Slug, BusinessName, FixedNow);
        var clock = new FakeClock { Now = FixedNow };

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session, new SqlClientRegistrationRepository(session), clock);

        Assert.True((await service.RegisterAsync(Request())).Succeeded);
        var account = await ReadAccountAsync(target.ConnectionString, businessId, Email);
        var firstNotification = await ReadNotificationAsync(target.ConnectionString, businessId);
        var firstToken = TokenFrom(firstNotification!.BodyHtml);

        // No clock advance: the resend shares the issuing second with the registration.
        var resend = await service.ResendVerificationAsync(new ResendClientVerificationRequest(Slug, Email));

        Assert.True(resend.Succeeded, resend.Error?.Code);
        var notifications = await ReadNotificationsAsync(target.ConnectionString, businessId);
        Assert.Equal(2, notifications.Count);
        Assert.NotEqual(notifications[0].DedupKey, notifications[1].DedupKey);
        var replacementToken = TokenFrom(notifications[1].BodyHtml);
        Assert.NotEqual(firstToken, replacementToken);

        var tokens = await ReadTokensAsync(target.ConnectionString, account!.Id);
        Assert.Equal(2, tokens.Count);
        // The old link stopped working at the rotation instant, which is the same second as its issuing.
        Assert.Equal(FixedNow, tokens[0].UsedAt);
        Assert.Null(tokens[1].UsedAt);
        Assert.Equal(SecureToken.Hash(replacementToken), tokens[1].Hash);

        Assert.False((await service.VerifyEmailAsync(Slug, firstToken)).Succeeded);
        Assert.True((await service.VerifyEmailAsync(Slug, replacementToken)).Succeeded);
        Assert.Equal(1, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId)));

        // A verified account gets the same generic answer and no new link.
        Assert.True((await service.ResendVerificationAsync(new ResendClientVerificationRequest(Slug, Email))).Succeeded);
        Assert.Equal(2, (await ReadNotificationsAsync(target.ConnectionString, businessId)).Count);
        Assert.Equal(2, (await ReadTokensAsync(target.ConnectionString, account.Id)).Count);

        output.WriteLine("Resend assertions passed: the same-second resend rotated the token, stored a distinct hash-bound dedup key, delivered the replacement, invalidated the previous link and left a verified account untouched.");
    }

    [Fact]
    public async Task A_ficha_created_after_registration_refuses_the_verification_and_the_rollback_keeps_the_token_unused()
    {
        var target = await ResolveTargetAsync("_Rollback");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, Slug, BusinessName, FixedNow);
        var clock = new FakeClock { Now = FixedNow };

        int accountId;
        string rawToken;
        await using (var session = new DbSession(target.ConnectionString))
        {
            var service = CreateService(session, new SqlClientRegistrationRepository(session), clock);
            Assert.True((await service.RegisterAsync(Request())).Succeeded);
            var account = await ReadAccountAsync(target.ConnectionString, businessId, Email);
            accountId = account!.Id;
            rawToken = TokenFrom((await ReadNotificationAsync(target.ConnectionString, businessId))!.BodyHtml);
        }

        // A public booking created the ficha with that email after the registration started.
        var bookedClientId = await InsertClientRowAsync(
            target.ConnectionString, businessId, "Ana", "Gómez", Email, FixedNow.AddMinutes(5));

        await using var verificationSession = new DbSession(target.ConnectionString);
        var verification = CreateService(verificationSession, new SqlClientRegistrationRepository(verificationSession), clock);

        var result = await verification.VerifyEmailAsync(Slug, rawToken);

        Assert.False(result.Succeeded);
        Assert.Equal("client_record_needs_owner_assistance", result.Error!.Code);

        // The refusal never attaches the account to the ficha that owns the email.
        var account2 = await ReadAccountByIdAsync(target.ConnectionString, accountId);
        Assert.NotNull(account2);
        Assert.Null(account2!.ClientId);
        Assert.Null(account2.EmailVerifiedAt);
        Assert.Equal("Ana", account2.PendingFirstName);
        Assert.Equal(1, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId)));
        Assert.Equal(bookedClientId, await ReadScalarAsync<int>(
            target.ConnectionString, "SELECT Id FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", Email)));

        // The SQL rollback returned the consumed token to its usable state and wrote no notice.
        var token = await ReadTokenAsync(target.ConnectionString, accountId);
        Assert.NotNull(token);
        Assert.Null(token!.UsedAt);
        Assert.Equal(1, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId",
            ("@businessId", businessId)));

        output.WriteLine("Rollback assertions passed: a ficha created after registration refused the verification, never linked the pending account, and the SQL rollback returned the consumed token to its unused state.");
    }

    [Fact]
    public async Task A_ficha_committed_before_the_guarded_insert_refuses_the_verification_without_linking()
    {
        var target = await ResolveTargetAsync("_InsertRace");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, Slug, BusinessName, FixedNow);
        var clock = new FakeClock { Now = FixedNow };

        int accountId;
        string rawToken;
        await using (var session = new DbSession(target.ConnectionString))
        {
            var service = CreateService(session, new SqlClientRegistrationRepository(session), clock);
            Assert.True((await service.RegisterAsync(Request())).Succeeded);
            accountId = (await ReadAccountAsync(target.ConnectionString, businessId, Email))!.Id;
            rawToken = TokenFrom((await ReadNotificationAsync(target.ConnectionString, businessId))!.BodyHtml);
        }

        // The competing ficha commits from another connection right after the verification's lookup returned none,
        // so the guarded insert loses the email race exactly as a simultaneous public booking would.
        var competingClientId = 0;
        await using var verificationSession = new DbSession(target.ConnectionString);
        var repository = new ObservedRegistrationRepository(new SqlClientRegistrationRepository(verificationSession))
        {
            AfterFichaLookup = async () =>
                competingClientId = await InsertClientRowAsync(
                    target.ConnectionString, businessId, "Ana", "Gómez", Email, FixedNow.AddMinutes(1))
        };
        var verification = CreateService(verificationSession, repository, clock);

        var result = await verification.VerifyEmailAsync(Slug, rawToken);

        Assert.False(result.Succeeded);
        Assert.Equal("client_record_needs_owner_assistance", result.Error!.Code);
        Assert.True(competingClientId > 0);
        Assert.Equal(1, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId)));
        Assert.Equal(competingClientId, await ReadScalarAsync<int>(
            target.ConnectionString, "SELECT Id FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", Email)));

        var account = await ReadAccountByIdAsync(target.ConnectionString, accountId);
        Assert.Null(account!.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        // The lost insert race also fails closed and the rollback returns the token to its usable state.
        Assert.All(await ReadTokensAsync(target.ConnectionString, accountId), token => Assert.Null(token.UsedAt));

        output.WriteLine("Insert-race assertions passed: the guarded insert returned zero when a competing ficha committed first, the verification failed closed instead of re-reading and linking, and the rollback returned the token to its unused state.");
    }

    [Fact]
    public async Task Two_concurrent_registrations_create_one_account_and_no_ficha()
    {
        var target = await ResolveTargetAsync("_Race");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, Slug, BusinessName, FixedNow);
        var gate = new TwoPartyGate();

        await using var firstSession = new DbSession(target.ConnectionString);
        await using var secondSession = new DbSession(target.ConnectionString);
        var firstService = CreateService(
            firstSession,
            new ObservedRegistrationRepository(new SqlClientRegistrationRepository(firstSession)) { EmailStateGate = gate });
        var secondService = CreateService(
            secondSession,
            new ObservedRegistrationRepository(new SqlClientRegistrationRepository(secondSession)) { EmailStateGate = gate });

        var results = await Task.WhenAll(
            firstService.RegisterAsync(Request()),
            secondService.RegisterAsync(Request()));

        Assert.Single(results, result => result.Succeeded);
        var refused = Assert.Single(results, result => !result.Succeeded);
        Assert.Equal("email_already_registered", refused.Error!.Code);

        Assert.Equal(1, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM ClientAccounts WHERE BusinessId = @businessId",
            ("@businessId", businessId)));
        Assert.Equal(0, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId)));
        Assert.Equal(1, await CountAsync(
            target.ConnectionString,
            "SELECT COUNT(*) FROM AccountTokens t JOIN ClientAccounts a ON a.Id = t.ClientAccountId WHERE a.BusinessId = @businessId",
            ("@businessId", businessId)));
        Assert.Equal(1, await CountAsync(
            target.ConnectionString, "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId",
            ("@businessId", businessId)));

        output.WriteLine("Race assertions passed: two simultaneous registrations with the same email left exactly one pending account, one token and one notice, and no ficha.");
    }

    [Fact]
    public async Task The_verification_link_uses_the_trusted_base_url_and_no_plaintext_secret_is_stored()
    {
        var target = await ResolveTargetAsync("_Outbox");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, Slug, BusinessName, FixedNow);

        await using var session = new DbSession(target.ConnectionString);
        var service = CreateService(session, new SqlClientRegistrationRepository(session));

        Assert.True((await service.RegisterAsync(Request())).Succeeded);

        var account = await ReadAccountAsync(target.ConnectionString, businessId, Email);
        var token = await ReadTokenAsync(target.ConnectionString, account!.Id);
        var notification = await ReadNotificationAsync(target.ConnectionString, businessId);

        var link = LinkFrom(notification!.BodyHtml);
        Assert.StartsWith($"{TrustedBaseUrl}/r/{Slug}/cuenta/verificar/", link);
        var rawToken = TokenFrom(notification.BodyHtml);

        // The raw token and the raw password exist only outside the stored account and token rows.
        Assert.DoesNotContain(rawToken, account.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, account.PendingFirstName ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(PlainPassword, account.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, Convert.ToHexString(token!.Hash), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawToken, notification.DedupKey, StringComparison.Ordinal);
        Assert.Contains(rawToken, notification.BodyHtml, StringComparison.Ordinal);

        // The real Identity hasher is what stores the password, and it matches the plaintext by verification only.
        var hasher = new IdentityPasswordHasher();
        Assert.True(hasher.Verify(account.PasswordHash, PlainPassword));
        Assert.False(hasher.Verify(account.PasswordHash, "otra-clave"));

        output.WriteLine("Outbox assertions passed: the link was built from the trusted base URL, the raw token travelled only in the queued email, and neither the account nor the token kept a plaintext secret.");
    }

    // --- Seeding ------------------------------------------------------------------------------------------------

    private static RegisterClientAccountRequest Request(
        string slug = Slug, string email = Email, string password = PlainPassword) =>
        new(slug, "Ana", "Gómez", email, password);

    private static ClientRegistrationService CreateService(
        DbSession session,
        IClientRegistrationRepository repository,
        IClock? clock = null) =>
        new(new SqlBusinessRepository(session), repository, session, new IdentityPasswordHasher(),
            new TrustedUrlBuilder(), clock ?? new FakeClock { Now = FixedNow });

    private static async Task<int> SeedBusinessAsync(
        string connectionString, string slug, string name, DateTime now) =>
        await InsertIdAsync(connectionString,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, @phone, @email, @allowsWalkIns, @createdAt)",
            ("@name", name), ("@slug", slug), ("@phone", "0000000000"),
            ("@email", $"{slug}@example.invalid"), ("@allowsWalkIns", true), ("@createdAt", now));

    private static async Task<int> InsertClientRowAsync(
        string connectionString, int businessId, string firstName, string lastName, string email, DateTime now) =>
        await InsertIdAsync(connectionString,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, NULL, NULL, @createdAt)",
            ("@businessId", businessId), ("@firstName", firstName), ("@lastName", lastName),
            ("@email", email), ("@createdAt", now));

    private static async Task InsertTokenRowAsync(
        string connectionString, int clientAccountId, AccountTokenPurpose purpose, byte[] tokenHash,
        DateTime expiresAt, DateTime now) =>
        await InsertIdAsync(connectionString,
            "INSERT INTO AccountTokens (ClientAccountId, Purpose, TokenHash, ExpiresAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@clientAccountId, @purpose, @tokenHash, @expiresAt, @createdAt)",
            ("@clientAccountId", clientAccountId), ("@purpose", purpose), ("@tokenHash", tokenHash),
            ("@expiresAt", expiresAt), ("@createdAt", now));

    // --- Plain reads of the persisted state ---------------------------------------------------------------------

    private sealed record StoredAccount(
        int Id, int? ClientId, string Email, string PasswordHash, DateTime? EmailVerifiedAt,
        string? PendingFirstName, string? PendingLastName);

    private sealed record StoredToken(byte[] Hash, byte Purpose, DateTime ExpiresAt, DateTime? UsedAt, DateTime CreatedAt);

    private sealed record StoredNotification(
        int Id, int? BusinessId, byte Type, string Recipient, string Subject, string BodyHtml, string DedupKey);

    private sealed record StoredClient(int Id, string FirstName, string LastName, string Email, string? Phone, string? HealthNotes);

    private const string AccountColumns =
        "Id, ClientId, Email, PasswordHash, EmailVerifiedAt, PendingFirstName, PendingLastName";

    private static StoredAccount MapAccount(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6));

    private static Task<StoredAccount?> ReadAccountAsync(string connectionString, int businessId, string email) =>
        QueryOneAsync(connectionString,
            $"SELECT {AccountColumns} FROM ClientAccounts WHERE BusinessId = @businessId AND Email = @email",
            MapAccount, ("@businessId", businessId), ("@email", email));

    private static Task<StoredAccount?> ReadAccountByIdAsync(string connectionString, int id) =>
        QueryOneAsync(connectionString,
            $"SELECT {AccountColumns} FROM ClientAccounts WHERE Id = @id",
            MapAccount, ("@id", id));

    private static Task<StoredToken?> ReadTokenAsync(string connectionString, int clientAccountId) =>
        QueryOneAsync(connectionString,
            "SELECT TokenHash, Purpose, ExpiresAt, UsedAt, CreatedAt FROM AccountTokens WHERE ClientAccountId = @clientAccountId ORDER BY Id DESC",
            MapToken, ("@clientAccountId", clientAccountId));

    private static Task<List<StoredToken>> ReadTokensAsync(string connectionString, int clientAccountId) =>
        QueryListAsync(connectionString,
            "SELECT TokenHash, Purpose, ExpiresAt, UsedAt, CreatedAt FROM AccountTokens WHERE ClientAccountId = @clientAccountId ORDER BY Id",
            MapToken, ("@clientAccountId", clientAccountId));

    private static StoredToken MapToken(SqlDataReader reader) => new(
        reader.GetFieldValue<byte[]>(0), reader.GetByte(1), reader.GetDateTime(2),
        reader.IsDBNull(3) ? null : reader.GetDateTime(3), reader.GetDateTime(4));

    private const string NotificationColumns =
        "Id, BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey";

    private static Task<StoredNotification?> ReadNotificationAsync(string connectionString, int businessId) =>
        QueryOneAsync(connectionString,
            $"SELECT {NotificationColumns} FROM Notifications WHERE BusinessId = @businessId ORDER BY Id",
            MapNotification, ("@businessId", businessId));

    private static Task<List<StoredNotification>> ReadNotificationsAsync(string connectionString, int businessId) =>
        QueryListAsync(connectionString,
            $"SELECT {NotificationColumns} FROM Notifications WHERE BusinessId = @businessId ORDER BY Id",
            MapNotification, ("@businessId", businessId));

    private static StoredNotification MapNotification(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1),
        reader.GetByte(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6));

    private static Task<StoredClient?> ReadClientAsync(string connectionString, int businessId, string email) =>
        QueryOneAsync(connectionString,
            "SELECT Id, FirstName, LastName, Email, Phone, HealthNotes FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            reader => new StoredClient(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)),
            ("@businessId", businessId), ("@email", email));

    // --- Opt-in target ------------------------------------------------------------------------------------------

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
        Assert.Contains("001_schema.sql", applied);
        Assert.Contains("002_client_account_pending_names.sql", applied);
        output.WriteLine($"Target: {target.DatabaseName}; DB_ID absent before creation; applied {applied.Count} schema script(s), 001 and 002 included; retained without teardown.");
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

        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT20Sql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT20Sql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T20 SQL integration test",
            TrustServerCertificate = true,
            MultipleActiveResultSets = false
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<IReadOnlyList<string>> CreateTargetAsync(SqlTarget target)
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

        return await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
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

    // --- Plain reads and inserts --------------------------------------------------------------------------------

    private static async Task<int> CountAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters) =>
        await ReadScalarAsync<int>(connectionString, sql, parameters);

    private static async Task<T?> ReadScalarAsync<T>(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, null, sql, parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
            return default;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<T?> QueryOneAsync<T>(
        string connectionString, string sql, Func<SqlDataReader, T> map,
        params (string Name, object? Value)[] parameters) where T : class
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, null, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? map(reader) : null;
    }

    private static async Task<List<T>> QueryListAsync<T>(
        string connectionString, string sql, Func<SqlDataReader, T> map,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, null, sql, parameters);
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
        await using var command = Create(connection, null, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlCommand Create(
        SqlConnection connection, SqlTransaction? transaction, string sql,
        (string Name, object? Value)[] parameters)
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
            case byte[] bytes:
                command.Parameters.Add(name, SqlDbType.Binary, bytes.Length).Value = bytes;
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

    private static string LinkFrom(string bodyHtml)
    {
        var match = Regex.Match(bodyHtml, "href=\"([^\"]+)\"");
        Assert.True(match.Success, "The verification email must contain a link.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string TokenFrom(string bodyHtml)
    {
        var link = LinkFrom(bodyHtml);
        const string marker = "/cuenta/verificar/";
        var index = link.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The verification email must link to the account verification route.");
        return link[(index + marker.Length)..];
    }

    // --- Test-only observers ------------------------------------------------------------------------------------

    /// <summary>
    /// Decorator over the real repository. It can hold the read both concurrent registrations must finish before
    /// their guarded insert, and it can commit a competing ficha right after the verification's lookup returned
    /// none, so both branches the port has to protect are exercised against real SQL instead of an in-memory model.
    /// </summary>
    private sealed class ObservedRegistrationRepository(IClientRegistrationRepository inner) : IClientRegistrationRepository
    {
        public TwoPartyGate? EmailStateGate { get; init; }

        public Func<Task>? AfterFichaLookup { get; init; }

        public async Task<ClientRegistrationEmailState> GetEmailStateAsync(
            int businessId, string normalizedEmail, CancellationToken ct = default)
        {
            var state = await inner.GetEmailStateAsync(businessId, normalizedEmail, ct);
            if (EmailStateGate is not null)
                await EmailStateGate.ArriveAndWaitAsync();
            return state;
        }

        public async Task<Client?> GetClientByEmailAsync(
            int businessId, string normalizedEmail, CancellationToken ct = default)
        {
            var client = await inner.GetClientByEmailAsync(businessId, normalizedEmail, ct);
            if (AfterFichaLookup is not null)
                await AfterFichaLookup();
            return client;
        }

        public Task<int> InsertPendingAccountAsync(
            ClientAccount account, string pendingFirstName, string pendingLastName,
            CancellationToken ct = default) =>
            inner.InsertPendingAccountAsync(account, pendingFirstName, pendingLastName, ct);

        public Task<int> InsertAccountTokenAsync(
            AccountToken token, CancellationToken ct = default) =>
            inner.InsertAccountTokenAsync(token, ct);

        public Task<int> InsertNotificationAsync(
            Notification notification, CancellationToken ct = default) =>
            inner.InsertNotificationAsync(notification, ct);

        public Task<PendingClientAccount?> ConsumeEmailVerificationTokenAsync(
            byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default) =>
            inner.ConsumeEmailVerificationTokenAsync(tokenHash, businessId, now, ct);

        public Task<int> InsertClientAsync(
            Client client, CancellationToken ct = default) =>
            inner.InsertClientAsync(client, ct);

        public Task<bool> ActivateVerifiedAccountAsync(
            int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default) =>
            inner.ActivateVerifiedAccountAsync(businessId, clientAccountId, clientId, verifiedAt, ct);

        public Task<PendingClientAccount?> GetPendingAccountAsync(
            int businessId, string normalizedEmail, CancellationToken ct = default) =>
            inner.GetPendingAccountAsync(businessId, normalizedEmail, ct);

        public Task<bool> ReplaceVerificationTokenAsync(
            int businessId, int clientAccountId, AccountToken replacement,
            CancellationToken ct = default) =>
            inner.ReplaceVerificationTokenAsync(businessId, clientAccountId, replacement, ct);
    }

    /// <summary>Holds the next two arrivals until both reached the same point, so a test forces a real race.</summary>
    private sealed class TwoPartyGate
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public async Task ArriveAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _gate.TrySetResult();
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class TrustedUrlBuilder : IClientAccountUrlBuilder
    {
        public string BuildVerificationUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildVerificationUrl(TrustedBaseUrl, slug, token);

        public string BuildPasswordResetUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildPasswordResetUrl(TrustedBaseUrl, slug, token);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
