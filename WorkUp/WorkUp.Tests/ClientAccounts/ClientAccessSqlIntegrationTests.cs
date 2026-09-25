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
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlClientAccessRepository"/> and the sign-in / password
/// recovery flow that drives it (RF-15, RF-27). It requires a new, dedicated database supplied through
/// WORKUP_T20_ACCESS_SQL_CONNECTION; without it every test returns N/A and no SQL connection is opened. Each test
/// derives its own database name from the supplied one, so the whole class runs in a single command, and nothing is
/// torn down: the target is retained on purpose.
/// <para>
/// The business tests prove the sign-in and recovery rules against an in-memory port; they cannot prove what storage
/// does with a tenant join, a padlock, a transaction or a real race. This fixture is the runtime proof of the port:
/// sign-in only through an active, verified and linked account of the requested business, one generic answer for
/// absent, wrong-password, pending, unlinked and foreign states, a recovery link that lives 30 minutes with an exact
/// boundary and works once, a same-second resend whose rotation invalidates the earlier link while still delivering
/// the replacement, two concurrent requests that leave exactly one live link, two concurrent completions that consume
/// once and write one password, a rotation racing a completion that hits no SQL error 1205 and leaves the newest link
/// as the only live one, a forced password-write failure whose rollback returns the consumed token to its usable
/// state, and a trusted link plus no plaintext secret anywhere in the database.
/// </para>
/// <para>
/// Every recovery assertion reads the account's tokens scoped to <see cref="AccountTokenPurpose.PasswordReset"/>:
/// preparing a verified account consumes its <c>EmailVerification</c> link and leaves that row stored, so a
/// purpose-agnostic count would mix two flows. Tests that need that retained row read it explicitly instead.
/// </para>
/// </summary>
public sealed class ClientAccessSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T20_ACCESS_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string TrustedBaseUrl = "https://reservas.example.com";
    private const string Slug = "peluqueria-t20";
    private const string OtherSlug = "yoga-t20";
    private const string BusinessName = "Peluquería Marina";
    private const string OtherBusinessName = "Estudio de Yoga";
    private const string Email = "ana@example.com";
    private const string OtherEmail = "bruno@example.com";
    private const string PendingEmail = "pendiente@example.com";
    private const string UnlinkedEmail = "sin-ficha@example.com";
    private const string CrossTenantEmail = "ajena@example.com";
    private const string PlainPassword = "secreto-123";
    private const string NewPassword = "nueva-clave-1";
    private const string SecondPassword = "nueva-clave-2";
    private const string ResetMarker = "/cuenta/restablecer/";
    private const string VerificationMarker = "/cuenta/verificar/";

    /// <summary>The single clock instant every scenario starts from.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    // --- Sign-in (RF-15) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_accepts_only_the_active_account_of_that_tenant_and_hides_every_other_state()
    {
        var target = await ResolveTargetAsync("_Login");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var otherBusinessId = await SeedBusinessAsync(connectionString, OtherSlug, OtherBusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);
        var bruno = await RegisterVerifiedAccountAsync(target, otherBusinessId, OtherSlug, OtherEmail);
        var pendingAccountId = await RegisterPendingAccountAsync(target, businessId, Slug, PendingEmail);
        var unlinkedAccountId = await InsertAccountRowAsync(
            connectionString, businessId, null, UnlinkedEmail, Hash(PlainPassword), FixedNow.AddDays(-1));

        // A verified account of this business pointing at a ficha of another tenant: impossible through the flow, but
        // the corrupt row proves the tenant-safe join is what keeps the lookup inside the requested business.
        var foreignClientId = await InsertClientRowAsync(
            connectionString, otherBusinessId, "A", "Jena", CrossTenantEmail, FixedNow);
        var crossTenantAccountId = await InsertAccountRowAsync(
            connectionString, businessId, foreignClientId, CrossTenantEmail, Hash(PlainPassword), FixedNow.AddDays(-1));

        await using var session = new DbSession(connectionString);
        var service = CreateAccessService(session, new SqlClientAccessRepository(session));

        var success = await service.LoginAsync(new ClientLoginRequest(Slug, Email, PlainPassword));
        Assert.True(success.Succeeded, success.Error?.Code);
        Assert.Equal(businessId, success.Value.BusinessId);
        Assert.Equal(Slug, success.Value.BusinessSlug);
        Assert.Equal(ana.AccountId, success.Value.ClientAccountId);
        Assert.Equal(ana.ClientId, success.Value.ClientId);
        Assert.Equal(Email, success.Value.Email);

        // The same email can be active in another business, and only through its own slug.
        var otherSuccess = await service.LoginAsync(new ClientLoginRequest(OtherSlug, OtherEmail, PlainPassword));
        Assert.True(otherSuccess.Succeeded, otherSuccess.Error?.Code);
        Assert.Equal(bruno.ClientId, otherSuccess.Value.ClientId);

        var failures = new List<Result<ClientSession>>
        {
            await service.LoginAsync(new ClientLoginRequest(Slug, "nadie@example.com", PlainPassword)),
            await service.LoginAsync(new ClientLoginRequest(Slug, Email, "contrasena-mala")),
            await service.LoginAsync(new ClientLoginRequest(Slug, PendingEmail, PlainPassword)),
            await service.LoginAsync(new ClientLoginRequest(Slug, UnlinkedEmail, PlainPassword)),
            await service.LoginAsync(new ClientLoginRequest(Slug, OtherEmail, PlainPassword)),
            await service.LoginAsync(new ClientLoginRequest(Slug, CrossTenantEmail, PlainPassword))
        };

        Assert.All(failures, failure => Assert.False(failure.Succeeded));
        Assert.All(failures, failure => Assert.Equal("invalid_credentials", failure.Error!.Code));
        Assert.All(failures, failure => Assert.Equal(failures[0].Error, failure.Error));

        // Sign-in never activates or links anything: the pending and unlinked states survive it untouched.
        var pendingAfter = await ReadAccountByIdAsync(connectionString, pendingAccountId);
        Assert.Null(pendingAfter!.ClientId);
        Assert.Null(pendingAfter.EmailVerifiedAt);
        var unlinkedAfter = await ReadAccountByIdAsync(connectionString, unlinkedAccountId);
        Assert.Null(unlinkedAfter!.ClientId);
        Assert.NotNull(unlinkedAfter.EmailVerifiedAt);
        var crossTenantAfter = await ReadAccountByIdAsync(connectionString, crossTenantAccountId);
        Assert.Equal(foreignClientId, crossTenantAfter!.ClientId);
        Assert.Equal(0, await CountAsync(
            connectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", UnlinkedEmail)));

        output.WriteLine("Sign-in assertions passed: only the active account of the requested tenant signed in, and absent, wrong-password, pending, unlinked and foreign states all shared one generic error without linking anything.");
    }

    // --- Recovery request (RF-27) -------------------------------------------------------------------------------

    [Fact]
    public async Task Reset_request_answers_generically_and_rotates_a_trusted_single_use_link_in_the_same_second()
    {
        var target = await ResolveTargetAsync("_Reset");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var otherBusinessId = await SeedBusinessAsync(connectionString, OtherSlug, OtherBusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);
        await RegisterVerifiedAccountAsync(target, otherBusinessId, OtherSlug, OtherEmail);
        await RegisterPendingAccountAsync(target, businessId, Slug, PendingEmail);
        await InsertAccountRowAsync(
            connectionString, businessId, null, UnlinkedEmail, Hash(PlainPassword), FixedNow.AddDays(-1));

        await using var session = new DbSession(connectionString);
        var service = CreateAccessService(session, new SqlClientAccessRepository(session));

        // Every state that is not an active account of this business gets the same empty success and no link.
        var generic = new List<Result>
        {
            await service.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, "nadie@example.com")),
            await service.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, PendingEmail)),
            await service.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, UnlinkedEmail)),
            await service.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, OtherEmail))
        };
        Assert.All(generic, result => Assert.True(result.Succeeded));
        Assert.All(generic, result => Assert.Null(result.Error));
        Assert.Empty(await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset));
        Assert.Empty(await ReadResetTokensAsync(connectionString, ana.AccountId));

        // Scoping the recovery assertions to PasswordReset does not hide the verification link: registering this
        // account consumed it, and the used row is still stored for its own purpose.
        var verification = Assert.Single(
            await ReadTokensAsync(connectionString, ana.AccountId, AccountTokenPurpose.EmailVerification));
        Assert.Equal((byte)AccountTokenPurpose.EmailVerification, verification.Purpose);
        Assert.Equal(FixedNow, verification.UsedAt);

        var active = await service.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, Email));
        Assert.True(active.Succeeded, active.Error?.Code);

        var notice = Assert.Single(await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset));
        Assert.Equal(Email, notice.Recipient);
        var firstToken = TokenFrom(notice.BodyHtml, ResetMarker);
        Assert.StartsWith($"{TrustedBaseUrl}/r/{Slug}/cuenta/restablecer/", LinkFrom(notice.BodyHtml));

        var stored = Assert.Single(await ReadResetTokensAsync(connectionString, ana.AccountId));
        Assert.Equal((byte)AccountTokenPurpose.PasswordReset, stored.Purpose);
        Assert.Equal(FixedNow.AddMinutes(30), stored.ExpiresAt);
        Assert.Null(stored.UsedAt);
        Assert.Equal(SecureToken.Hash(firstToken), stored.Hash);
        Assert.Equal(ClientAccessService.PasswordResetDedupKey(ana.AccountId, stored.Hash), notice.DedupKey);
        Assert.DoesNotContain(firstToken, notice.DedupKey, StringComparison.Ordinal);

        // Same-second resend: the rotation invalidates the earlier link and still delivers the replacement.
        var resend = await service.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, Email));
        Assert.True(resend.Succeeded, resend.Error?.Code);

        var notices = await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset);
        Assert.Equal(2, notices.Count);
        Assert.NotEqual(notices[0].DedupKey, notices[1].DedupKey);
        var secondToken = TokenFrom(notices[1].BodyHtml, ResetMarker);
        Assert.NotEqual(firstToken, secondToken);

        var tokens = await ReadResetTokensAsync(connectionString, ana.AccountId);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(FixedNow, tokens[0].UsedAt);
        Assert.Null(tokens[1].UsedAt);
        Assert.Equal(SecureToken.Hash(secondToken), tokens[1].Hash);

        var obsolete = await service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, firstToken, NewPassword));
        Assert.False(obsolete.Succeeded);
        Assert.Equal("password_reset_link_unavailable", obsolete.Error!.Code);

        var completed = await service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, secondToken, NewPassword));
        Assert.True(completed.Succeeded, completed.Error?.Code);

        var account = await ReadAccountAsync(connectionString, businessId, Email);
        Assert.True(new IdentityPasswordHasher().Verify(account!.PasswordHash, NewPassword));
        Assert.False(new IdentityPasswordHasher().Verify(account.PasswordHash, PlainPassword));

        output.WriteLine("Reset-request assertions passed: the answer did not depend on the account state, and the same-second resend rotated the hash-bound link, invalidated the earlier one and delivered the replacement that completed the recovery.");
    }

    // --- Expiry boundary and single use -------------------------------------------------------------------------

    [Fact]
    public async Task Reset_link_expires_at_the_exact_thirty_minute_boundary_and_works_only_once()
    {
        var target = await ResolveTargetAsync("_Expiry");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);
        var clock = new FakeClock { Now = FixedNow };

        await using var session = new DbSession(connectionString);
        var service = CreateAccessService(session, new SqlClientAccessRepository(session), clock);

        Assert.True((await service.RequestPasswordResetAsync(
            new ClientPasswordResetRequest(Slug, Email))).Succeeded);
        var firstNotice = Assert.Single(await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset));
        var firstToken = TokenFrom(firstNotice.BodyHtml, ResetMarker);
        var firstExpiry = Assert.Single(await ReadResetTokensAsync(connectionString, ana.AccountId)).ExpiresAt;
        Assert.Equal(FixedNow.AddMinutes(30), firstExpiry);

        // Exactly at the expiry instant the link is already unavailable, and the refused consume rolls back.
        clock.Now = firstExpiry;
        var atBoundary = await service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, firstToken, NewPassword));
        Assert.False(atBoundary.Succeeded);
        Assert.Equal("password_reset_link_unavailable", atBoundary.Error!.Code);
        Assert.All(await ReadResetTokensAsync(connectionString, ana.AccountId), token => Assert.Null(token.UsedAt));
        Assert.True(new IdentityPasswordHasher().Verify(
            (await ReadAccountAsync(connectionString, businessId, Email))!.PasswordHash, PlainPassword));

        // One tick before the boundary a fresh link still works, and it works exactly once.
        clock.Now = FixedNow.AddMinutes(1);
        Assert.True((await service.RequestPasswordResetAsync(
            new ClientPasswordResetRequest(Slug, Email))).Succeeded);
        var tokens = await ReadResetTokensAsync(connectionString, ana.AccountId);
        var live = tokens.Single(token => token.UsedAt is null);
        var liveNotice = (await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset))[1];
        var liveToken = TokenFrom(liveNotice.BodyHtml, ResetMarker);
        clock.Now = live.ExpiresAt.AddTicks(-1);

        var accepted = await service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, liveToken, NewPassword));
        Assert.True(accepted.Succeeded, accepted.Error?.Code);
        Assert.NotNull((await ReadResetTokensAsync(connectionString, ana.AccountId)).Single(token => token.Id == live.Id).UsedAt);

        var replayed = await service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, liveToken, SecondPassword));
        Assert.False(replayed.Succeeded);
        Assert.Equal("password_reset_link_unavailable", replayed.Error!.Code);

        var account = await ReadAccountAsync(connectionString, businessId, Email);
        var hasher = new IdentityPasswordHasher();
        Assert.True(hasher.Verify(account!.PasswordHash, NewPassword));
        Assert.False(hasher.Verify(account.PasswordHash, SecondPassword));

        output.WriteLine("Expiry assertions passed: the link was refused at the exact 30-minute instant without consuming it, accepted one tick before its own boundary, and refused forever after its single use.");
    }

    // --- Tenant and purpose isolation ---------------------------------------------------------------------------

    [Fact]
    public async Task Reset_completion_refuses_foreign_wrong_purpose_pending_and_unlinked_tokens_without_writing()
    {
        var target = await ResolveTargetAsync("_Isolation");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var otherBusinessId = await SeedBusinessAsync(connectionString, OtherSlug, OtherBusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);
        await RegisterVerifiedAccountAsync(target, otherBusinessId, OtherSlug, OtherEmail);
        var pendingAccountId = await RegisterPendingAccountAsync(target, businessId, Slug, PendingEmail);
        var unlinkedAccountId = await InsertAccountRowAsync(
            connectionString, businessId, null, UnlinkedEmail, Hash(PlainPassword), FixedNow.AddDays(-1));

        await using var session = new DbSession(connectionString);
        var service = CreateAccessService(session, new SqlClientAccessRepository(session), new FakeClock { Now = FixedNow });

        Assert.True((await service.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, Email))).Succeeded);
        var anaToken = TokenFrom(
            Assert.Single(await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset)).BodyHtml,
            ResetMarker);
        Assert.True((await service.RequestPasswordResetAsync(new ClientPasswordResetRequest(OtherSlug, OtherEmail))).Succeeded);

        // Tokens that exist in the database but never belong to the requested tenant, purpose or account state.
        await InsertTokenRowAsync(
            connectionString, ana.AccountId, AccountTokenPurpose.EmailVerification,
            SecureToken.Hash("verificacion-ana"), FixedNow.AddMinutes(30), FixedNow);
        await InsertTokenRowAsync(
            connectionString, pendingAccountId, AccountTokenPurpose.PasswordReset,
            SecureToken.Hash("reset-pendiente"), FixedNow.AddMinutes(30), FixedNow);
        await InsertTokenRowAsync(
            connectionString, unlinkedAccountId, AccountTokenPurpose.PasswordReset,
            SecureToken.Hash("reset-sin-ficha"), FixedNow.AddMinutes(30), FixedNow);

        var errors = new List<BusinessError?>
        {
            // A recovery token of another business is not a token of this one.
            (await service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(OtherSlug, anaToken, NewPassword))).Error,
            (await service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(Slug, "verificacion-ana", NewPassword))).Error,
            (await service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(Slug, "reset-pendiente", NewPassword))).Error,
            (await service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(Slug, "reset-sin-ficha", NewPassword))).Error,
            (await service.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(Slug, "token-inexistente", NewPassword))).Error
        };

        Assert.All(errors, error => Assert.NotNull(error));
        Assert.All(errors, error => Assert.Equal("password_reset_link_unavailable", error!.Code));
        Assert.All(errors, error => Assert.Equal(errors[0], error));

        // No refused link wrote a password and no stored token was consumed.
        var hasher = new IdentityPasswordHasher();
        Assert.True(hasher.Verify((await ReadAccountAsync(connectionString, businessId, Email))!.PasswordHash, PlainPassword));
        Assert.True(hasher.Verify((await ReadAccountAsync(connectionString, otherBusinessId, OtherEmail))!.PasswordHash, PlainPassword));
        Assert.All(await ReadResetTokensAsync(connectionString, ana.AccountId), token => Assert.Null(token.UsedAt));
        Assert.All(await ReadResetTokensAsync(connectionString, pendingAccountId), token => Assert.Null(token.UsedAt));
        Assert.All(await ReadResetTokensAsync(connectionString, unlinkedAccountId), token => Assert.Null(token.UsedAt));

        // The real link of its own tenant still works afterwards.
        Assert.True((await service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, anaToken, NewPassword))).Succeeded);
        Assert.True(hasher.Verify((await ReadAccountAsync(connectionString, businessId, Email))!.PasswordHash, NewPassword));

        output.WriteLine("Isolation assertions passed: a foreign-tenant, wrong-purpose, pending and unlinked token were all refused with one generic error and no write, and the tenant's own link still completed afterwards.");
    }

    // --- Concurrency --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_simultaneous_reset_requests_leave_exactly_one_live_link()
    {
        var target = await ResolveTargetAsync("_Race");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);
        var gate = new TwoPartyGate();

        await using var firstSession = new DbSession(connectionString);
        await using var secondSession = new DbSession(connectionString);
        var first = CreateAccessService(
            firstSession,
            new GatedAccessRepository(new SqlClientAccessRepository(firstSession)) { RotationGate = gate });
        var second = CreateAccessService(
            secondSession,
            new GatedAccessRepository(new SqlClientAccessRepository(secondSession)) { RotationGate = gate });

        var results = await Task.WhenAll(
            first.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, Email)),
            second.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, Email)));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Error?.Code));

        var tokens = await ReadResetTokensAsync(connectionString, ana.AccountId);
        Assert.Equal(2, tokens.Count);
        var live = tokens.Single(token => token.UsedAt is null);

        var notices = await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset);
        Assert.Equal(2, notices.Count);
        Assert.NotEqual(notices[0].DedupKey, notices[1].DedupKey);
        var liveIndex = tokens[0].Id == live.Id ? 0 : 1;
        var liveToken = TokenFrom(notices[liveIndex].BodyHtml, ResetMarker);
        var obsoleteToken = TokenFrom(notices[1 - liveIndex].BodyHtml, ResetMarker);

        await using var verifySession = new DbSession(connectionString);
        var verifier = CreateAccessService(verifySession, new SqlClientAccessRepository(verifySession));

        var obsolete = await verifier.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, obsoleteToken, NewPassword));
        Assert.False(obsolete.Succeeded);
        Assert.Equal("password_reset_link_unavailable", obsolete.Error!.Code);

        var completed = await verifier.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, liveToken, NewPassword));
        Assert.True(completed.Succeeded, completed.Error?.Code);

        output.WriteLine("Reset-race assertions passed: two simultaneous rotations serialized on the account padlock and left exactly one live, delivered link after invalidating the other.");
    }

    [Fact]
    public async Task Two_simultaneous_completions_consume_the_link_once_and_write_one_password()
    {
        var target = await ResolveTargetAsync("_ConsumeRace");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);

        string rawToken;
        await using (var seedSession = new DbSession(connectionString))
        {
            var seedService = CreateAccessService(seedSession, new SqlClientAccessRepository(seedSession));
            Assert.True((await seedService.RequestPasswordResetAsync(
                new ClientPasswordResetRequest(Slug, Email))).Succeeded);
            rawToken = TokenFrom(
                Assert.Single(await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset)).BodyHtml,
                ResetMarker);
        }

        var gate = new TwoPartyGate();
        await using var firstSession = new DbSession(connectionString);
        await using var secondSession = new DbSession(connectionString);
        var first = CreateAccessService(
            firstSession,
            new GatedAccessRepository(new SqlClientAccessRepository(firstSession)) { ConsumeGate = gate });
        var second = CreateAccessService(
            secondSession,
            new GatedAccessRepository(new SqlClientAccessRepository(secondSession)) { ConsumeGate = gate });

        var results = await Task.WhenAll(
            first.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(Slug, rawToken, NewPassword)),
            second.CompletePasswordResetAsync(new CompleteClientPasswordResetRequest(Slug, rawToken, SecondPassword)));

        Assert.Single(results, result => result.Succeeded);
        var refused = results.Single(result => !result.Succeeded);
        Assert.Equal("password_reset_link_unavailable", refused.Error!.Code);

        // One link means one consume: the surviving row is used exactly once.
        var stored = Assert.Single(await ReadResetTokensAsync(connectionString, ana.AccountId));
        Assert.NotNull(stored.UsedAt);

        // Exactly one of the two candidate passwords reached the account.
        var hasher = new IdentityPasswordHasher();
        var account = await ReadAccountAsync(connectionString, businessId, Email);
        Assert.True(
            hasher.Verify(account!.PasswordHash, NewPassword) ^ hasher.Verify(account.PasswordHash, SecondPassword));
        Assert.False(hasher.Verify(account.PasswordHash, PlainPassword));

        output.WriteLine("Consume-race assertions passed: two simultaneous completions consumed the single-use link once, one succeeded, one was refused generically, and exactly one password write survived.");
    }

    // --- Rotation racing a completion ---------------------------------------------------------------------------

    [Fact]
    public async Task A_rotation_racing_a_completion_hits_no_sql_1205_and_leaves_the_newest_link_as_the_only_live_one()
    {
        var target = await ResolveTargetAsync("_MixedRace");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);

        string firstToken;
        await using (var seedSession = new DbSession(connectionString))
        {
            var seedService = CreateAccessService(seedSession, new SqlClientAccessRepository(seedSession));
            Assert.True((await seedService.RequestPasswordResetAsync(
                new ClientPasswordResetRequest(Slug, Email))).Succeeded);
            firstToken = TokenFrom(
                Assert.Single(await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset)).BodyHtml,
                ResetMarker);
        }

        // Both parties pause right before their SQL batch, so the rotation and the completion reach the database
        // together and the two write paths meet on the same account lock for real.
        var gate = new TwoPartyGate();
        await using var rotationSession = new DbSession(connectionString);
        await using var completionSession = new DbSession(connectionString);
        var rotator = CreateAccessService(
            rotationSession,
            new GatedAccessRepository(new SqlClientAccessRepository(rotationSession)) { RotationGate = gate });
        var completer = CreateAccessService(
            completionSession,
            new GatedAccessRepository(new SqlClientAccessRepository(completionSession)) { ConsumeGate = gate });

        var outcomes = await Task.WhenAll(
            CaptureAsync(() => rotator.RequestPasswordResetAsync(new ClientPasswordResetRequest(Slug, Email))),
            CaptureAsync(() => completer.CompletePasswordResetAsync(
                new CompleteClientPasswordResetRequest(Slug, firstToken, NewPassword))));

        // A deadlock would make one of the two calls the victim and surface here as SQL error 1205; any other SQL
        // failure would surface too. This is one forced overlap, not every possible interleaving, so it refutes the
        // inverted account/token order rather than proving that no schedule can ever deadlock.
        var sqlFailures = outcomes.Select(outcome => outcome.Exception).OfType<SqlException>().ToList();
        Assert.DoesNotContain(sqlFailures, failure => failure.Number == 1205);
        Assert.Empty(sqlFailures);
        Assert.All(outcomes, outcome => Assert.Null(outcome.Exception));

        var rotation = outcomes[0].Value!;
        var completion = outcomes[1].Value!;
        Assert.True(rotation.Succeeded, rotation.Error?.Code);

        // Last-link semantics: the rotation invalidated whatever was live before, so exactly one PasswordReset row
        // survives usable and it is the link of the newest queued notice; the earlier link is dead either way.
        var tokens = await ReadResetTokensAsync(connectionString, ana.AccountId);
        Assert.Equal(2, tokens.Count);
        var live = Assert.Single(tokens, token => token.UsedAt is null);
        Assert.Equal(FixedNow.AddMinutes(30), live.ExpiresAt);
        Assert.All(tokens.Where(token => token.Id != live.Id), token => Assert.NotNull(token.UsedAt));

        var notices = await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset);
        Assert.Equal(2, notices.Count);
        var newest = notices[^1];
        Assert.Equal(SecureToken.Hash(TokenFrom(newest.BodyHtml, ResetMarker)), live.Hash);
        Assert.Equal(ClientAccessService.PasswordResetDedupKey(ana.AccountId, live.Hash), newest.DedupKey);

        // The completion and the password agree: the write happened only when the completion won the race against the
        // rotation that invalidated the first link.
        var hasher = new IdentityPasswordHasher();
        var account = await ReadAccountAsync(connectionString, businessId, Email);
        if (completion.Succeeded)
        {
            Assert.True(hasher.Verify(account!.PasswordHash, NewPassword));
        }
        else
        {
            Assert.Equal("password_reset_link_unavailable", completion.Error!.Code);
            Assert.True(hasher.Verify(account!.PasswordHash, PlainPassword));
        }

        output.WriteLine("Mixed-race assertions passed: the rotation and the completion both started from the account padlock, neither surfaced SQL 1205, and the newest link ended up as the only live one. This observes one forced overlap, not the absence of deadlock in every interleaving.");
    }

    // --- Rollback of a failed completion ------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_password_update_rolls_back_the_consumed_token()
    {
        var target = await ResolveTargetAsync("_Rollback");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);

        string rawToken;
        await using (var seedSession = new DbSession(connectionString))
        {
            var seedService = CreateAccessService(seedSession, new SqlClientAccessRepository(seedSession));
            Assert.True((await seedService.RequestPasswordResetAsync(
                new ClientPasswordResetRequest(Slug, Email))).Succeeded);
            rawToken = TokenFrom(
                Assert.Single(await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset)).BodyHtml,
                ResetMarker);
        }

        var hashBefore = (await ReadAccountAsync(connectionString, businessId, Email))!.PasswordHash;

        // The guarded write reports the account is no longer active, exactly as a deactivation between the consume and
        // the write would, so the completion fails and its transaction must undo the consumed token.
        await using var session = new DbSession(connectionString);
        var service = CreateAccessService(session, new FailingHashUpdateRepository(new SqlClientAccessRepository(session)));

        var result = await service.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, rawToken, NewPassword));

        Assert.False(result.Succeeded);
        Assert.Equal("password_reset_link_unavailable", result.Error!.Code);

        var stored = Assert.Single(await ReadResetTokensAsync(connectionString, ana.AccountId));
        Assert.Null(stored.UsedAt);
        var hashAfter = (await ReadAccountAsync(connectionString, businessId, Email))!.PasswordHash;
        Assert.Equal(hashBefore, hashAfter);
        Assert.True(new IdentityPasswordHasher().Verify(hashAfter, PlainPassword));

        // The rollback returned the link to its usable state: the same token still completes once the write can succeed.
        await using var retrySession = new DbSession(connectionString);
        var retry = CreateAccessService(retrySession, new SqlClientAccessRepository(retrySession));
        Assert.True((await retry.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(Slug, rawToken, NewPassword))).Succeeded);
        Assert.True(new IdentityPasswordHasher().Verify(
            (await ReadAccountAsync(connectionString, businessId, Email))!.PasswordHash, NewPassword));

        output.WriteLine("Rollback assertions passed: a failed password write left the password untouched and the SQL rollback returned the consumed token to its unused state, so the same link worked afterwards.");
    }

    // --- Trusted link and no plaintext secret -------------------------------------------------------------------

    [Fact]
    public async Task The_recovery_link_uses_the_trusted_base_url_and_no_plaintext_secret_reaches_the_database()
    {
        var target = await ResolveTargetAsync("_Outbox");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName, FixedNow);
        var ana = await RegisterVerifiedAccountAsync(target, businessId, Slug, Email);

        await using var session = new DbSession(connectionString);
        var service = CreateAccessService(session, new SqlClientAccessRepository(session));
        Assert.True((await service.RequestPasswordResetAsync(
            new ClientPasswordResetRequest(Slug, Email))).Succeeded);

        var notice = Assert.Single(await ReadNotificationsAsync(connectionString, businessId, NotificationType.PasswordReset));
        var link = LinkFrom(notice.BodyHtml);
        Assert.StartsWith($"{TrustedBaseUrl}/r/{Slug}/cuenta/restablecer/", link);
        var rawToken = TokenFrom(notice.BodyHtml, ResetMarker);

        var account = await ReadAccountAsync(connectionString, businessId, Email);
        var stored = Assert.Single(await ReadResetTokensAsync(connectionString, ana.AccountId));

        // The raw token exists only inside the queued email; the token row keeps its 32-byte SHA-256 hash.
        Assert.Equal(SecureToken.Hash(rawToken), stored.Hash);
        Assert.Equal(32, stored.Hash.Length);
        Assert.Contains(rawToken, notice.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, Convert.ToHexString(stored.Hash), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawToken, notice.DedupKey, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, notice.Subject, StringComparison.Ordinal);
        Assert.Equal(ClientAccessService.PasswordResetDedupKey(ana.AccountId, stored.Hash), notice.DedupKey);

        // No plaintext password is stored anywhere; the Identity hash is what the database keeps.
        Assert.DoesNotContain(PlainPassword, account!.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, account.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(PlainPassword, notice.BodyHtml, StringComparison.Ordinal);
        var hasher = new IdentityPasswordHasher();
        Assert.True(hasher.Verify(account.PasswordHash, PlainPassword));
        Assert.False(hasher.Verify(account.PasswordHash, NewPassword));

        output.WriteLine("Outbox assertions passed: the link was built from the trusted base URL and only the SHA-256 hash of the token, plus the Identity password hash, reached the database.");
    }

    // --- Seeding ------------------------------------------------------------------------------------------------

    private static string Hash(string password) => new IdentityPasswordHasher().Hash(password);

    private static ClientRegistrationService CreateRegistrationService(DbSession session, IClock clock) =>
        new(new SqlBusinessRepository(session), new SqlClientRegistrationRepository(session), session,
            new IdentityPasswordHasher(), new TrustedUrlBuilder(), clock);

    private static ClientAccessService CreateAccessService(
        DbSession session, IClientAccessRepository repository, IClock? clock = null) =>
        new(new SqlBusinessRepository(session), repository, session, new IdentityPasswordHasher(),
            new TrustedUrlBuilder(), clock ?? new FakeClock { Now = FixedNow });

    private static async Task RegisterAsync(
        ClientRegistrationService service, string slug, string email, string password = PlainPassword)
    {
        var result = await service.RegisterAsync(new RegisterClientAccountRequest(slug, "Ana", "Gómez", email, password));
        Assert.True(result.Succeeded, result.Error?.Code);
    }

    private static async Task<SeededAccount> RegisterVerifiedAccountAsync(
        SqlTarget target, int businessId, string slug, string email, string password = PlainPassword)
    {
        var clock = new FakeClock { Now = FixedNow };
        await using var session = new DbSession(target.ConnectionString);
        var service = CreateRegistrationService(session, clock);
        await RegisterAsync(service, slug, email, password);

        var notice = await ReadNotificationByTypeAsync(
            target.ConnectionString, businessId, NotificationType.EmailVerification);
        Assert.NotNull(notice);
        var rawToken = TokenFrom(notice!.BodyHtml, VerificationMarker);

        var verified = await service.VerifyEmailAsync(slug, rawToken);
        Assert.True(verified.Succeeded, verified.Error?.Code);
        return new SeededAccount(verified.Value.ClientAccountId, verified.Value.ClientId);
    }

    private static async Task<int> RegisterPendingAccountAsync(
        SqlTarget target, int businessId, string slug, string email, string password = PlainPassword)
    {
        var clock = new FakeClock { Now = FixedNow };
        await using var session = new DbSession(target.ConnectionString);
        var service = CreateRegistrationService(session, clock);
        await RegisterAsync(service, slug, email, password);

        var account = await ReadAccountAsync(target.ConnectionString, businessId, email);
        Assert.NotNull(account);
        Assert.Null(account!.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        return account.Id;
    }

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

    private static async Task<int> InsertAccountRowAsync(
        string connectionString, int businessId, int? clientId, string email, string passwordHash,
        DateTime? verifiedAt) =>
        await InsertIdAsync(connectionString,
            "INSERT INTO ClientAccounts (BusinessId, ClientId, Email, PasswordHash, EmailVerifiedAt, CreatedAt, PendingFirstName, PendingLastName) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @email, @passwordHash, @verifiedAt, @createdAt, NULL, NULL)",
            ("@businessId", businessId), ("@clientId", clientId), ("@email", email),
            ("@passwordHash", passwordHash), ("@verifiedAt", verifiedAt), ("@createdAt", FixedNow));

    private static async Task InsertTokenRowAsync(
        string connectionString, int clientAccountId, AccountTokenPurpose purpose, byte[] tokenHash,
        DateTime expiresAt, DateTime now) =>
        await InsertIdAsync(connectionString,
            "INSERT INTO AccountTokens (ClientAccountId, Purpose, TokenHash, ExpiresAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@clientAccountId, @purpose, @tokenHash, @expiresAt, @createdAt)",
            ("@clientAccountId", clientAccountId), ("@purpose", purpose), ("@tokenHash", tokenHash),
            ("@expiresAt", expiresAt), ("@createdAt", now));

    // --- Plain reads of the persisted state ---------------------------------------------------------------------

    private sealed record SeededAccount(int AccountId, int ClientId);

    private sealed record StoredAccount(
        int Id, int? ClientId, string Email, string PasswordHash, DateTime? EmailVerifiedAt,
        string? PendingFirstName, string? PendingLastName);

    private sealed record StoredToken(int Id, byte[] Hash, byte Purpose, DateTime ExpiresAt, DateTime? UsedAt, DateTime CreatedAt);

    private sealed record StoredNotification(int Id, byte Type, string Recipient, string Subject, string BodyHtml, string DedupKey);

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

    /// <summary>
    /// Reads one account's stored tokens for a single purpose. Recovery assertions use the
    /// <c>PasswordReset</c> scope: preparing a verified account consumes its <c>EmailVerification</c> link and keeps
    /// that row, so a purpose-agnostic read would mix both flows in every count instead of asserting the flow under
    /// test.
    /// </summary>
    private static Task<List<StoredToken>> ReadTokensAsync(
        string connectionString, int clientAccountId, AccountTokenPurpose purpose) =>
        QueryListAsync(connectionString,
            "SELECT Id, TokenHash, Purpose, ExpiresAt, UsedAt, CreatedAt FROM AccountTokens WHERE ClientAccountId = @clientAccountId AND Purpose = @purpose ORDER BY Id",
            MapToken, ("@clientAccountId", clientAccountId), ("@purpose", purpose));

    /// <summary>Recovery tokens of one account: the scope every password-recovery assertion reads.</summary>
    private static Task<List<StoredToken>> ReadResetTokensAsync(string connectionString, int clientAccountId) =>
        ReadTokensAsync(connectionString, clientAccountId, AccountTokenPurpose.PasswordReset);

    private static StoredToken MapToken(SqlDataReader reader) => new(
        reader.GetInt32(0), reader.GetFieldValue<byte[]>(1), reader.GetByte(2), reader.GetDateTime(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.GetDateTime(5));

    private const string NotificationColumns =
        "Id, Type, Recipient, Subject, BodyHtml, DedupKey";

    private static async Task<List<StoredNotification>> ReadNotificationsAsync(
        string connectionString, int businessId, NotificationType? type = null)
    {
        var all = await QueryListAsync(connectionString,
            $"SELECT {NotificationColumns} FROM Notifications WHERE BusinessId = @businessId ORDER BY Id",
            MapNotification, ("@businessId", businessId));
        return type is null ? all : all.Where(notification => notification.Type == (byte)type.Value).ToList();
    }

    private static async Task<StoredNotification?> ReadNotificationByTypeAsync(
        string connectionString, int businessId, NotificationType type) =>
        (await ReadNotificationsAsync(connectionString, businessId, type)).FirstOrDefault();

    private static StoredNotification MapNotification(SqlDataReader reader) => new(
        reader.GetInt32(0), reader.GetByte(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5));

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
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT20AccessSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
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
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT20AccessSql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T20 access SQL integration test",
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
        Assert.True(match.Success, "The recovery email must contain a link.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string TokenFrom(string bodyHtml, string marker)
    {
        var link = LinkFrom(bodyHtml);
        var index = link.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The recovery email must link to the client-account route.");
        return link[(index + marker.Length)..];
    }

    // --- Test-only observers ------------------------------------------------------------------------------------

    /// <summary>
    /// Decorator over the real repository that can hold both concurrent callers before the padlocked rotation or the
    /// single-use consume, so the race is forced into the real SQL statement instead of being left to timing.
    /// </summary>
    private sealed class GatedAccessRepository(IClientAccessRepository inner) : IClientAccessRepository
    {
        public TwoPartyGate? RotationGate { get; init; }

        public TwoPartyGate? ConsumeGate { get; init; }

        public Task<ActiveClientAccount?> GetActiveAccountByEmailAsync(
            int businessId, string normalizedEmail, CancellationToken ct = default) =>
            inner.GetActiveAccountByEmailAsync(businessId, normalizedEmail, ct);

        public async Task<bool> ReplacePasswordResetTokenAsync(
            int businessId, int clientAccountId, AccountToken replacement, CancellationToken ct = default)
        {
            if (RotationGate is not null)
                await RotationGate.ArriveAndWaitAsync();
            return await inner.ReplacePasswordResetTokenAsync(businessId, clientAccountId, replacement, ct);
        }

        public Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default) =>
            inner.InsertNotificationAsync(notification, ct);

        public async Task<int?> ConsumePasswordResetTokenAsync(
            byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default)
        {
            if (ConsumeGate is not null)
                await ConsumeGate.ArriveAndWaitAsync();
            return await inner.ConsumePasswordResetTokenAsync(tokenHash, businessId, now, ct);
        }

        public Task<bool> UpdatePasswordHashAsync(
            int businessId, int clientAccountId, string passwordHash, CancellationToken ct = default) =>
            inner.UpdatePasswordHashAsync(businessId, clientAccountId, passwordHash, ct);
    }

    /// <summary>
    /// Decorator that reports the guarded "account is not active" outcome of the password write while delegating
    /// everything else, so a real completion can reach the failure inside a real transaction and prove the rollback.
    /// </summary>
    private sealed class FailingHashUpdateRepository(IClientAccessRepository inner) : IClientAccessRepository
    {
        public Task<ActiveClientAccount?> GetActiveAccountByEmailAsync(
            int businessId, string normalizedEmail, CancellationToken ct = default) =>
            inner.GetActiveAccountByEmailAsync(businessId, normalizedEmail, ct);

        public Task<bool> ReplacePasswordResetTokenAsync(
            int businessId, int clientAccountId, AccountToken replacement, CancellationToken ct = default) =>
            inner.ReplacePasswordResetTokenAsync(businessId, clientAccountId, replacement, ct);

        public Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default) =>
            inner.InsertNotificationAsync(notification, ct);

        public Task<int?> ConsumePasswordResetTokenAsync(
            byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default) =>
            inner.ConsumePasswordResetTokenAsync(tokenHash, businessId, now, ct);

        public Task<bool> UpdatePasswordHashAsync(
            int businessId, int clientAccountId, string passwordHash, CancellationToken ct = default) =>
            Task.FromResult(false);
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

    /// <summary>
    /// Runs one service call and captures its outcome instead of letting a SQL failure — for instance a deadlock
    /// victim reported as error 1205 — escape as an unhandled exception, so a mixed-race assertion can name it.
    /// </summary>
    private static async Task<CapturedResult> CaptureAsync(Func<Task<Result>> action)
    {
        try
        {
            return new CapturedResult(await action(), null);
        }
        catch (Exception exception)
        {
            return new CapturedResult(null, exception);
        }
    }

    private sealed record CapturedResult(Result? Value, Exception? Exception);

    private sealed class TrustedUrlBuilder : IClientAccountUrlBuilder
    {
        public string BuildVerificationUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildVerificationUrl(TrustedBaseUrl, slug, token);

        public string BuildPasswordResetUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildPasswordResetUrl(TrustedBaseUrl, slug, token);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
