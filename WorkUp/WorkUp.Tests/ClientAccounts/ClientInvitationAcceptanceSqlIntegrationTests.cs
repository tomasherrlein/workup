using System.Data;
using System.Globalization;
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
/// Opt-in, retained synthetic LocalDB fixture for <see cref="SqlClientInvitationAcceptanceRepository"/> driven by
/// <see cref="ClientInvitationAcceptanceService"/> (RF-26, T22 slice 2). It requires a new, dedicated database
/// supplied through <c>WORKUP_T22_ACCEPT_SQL_CONNECTION</c>; without it every test returns N/A and no SQL connection
/// is opened. Each test derives its own database name from the supplied one, so the whole class runs in a single
/// command, the preflight asserts <c>DB_ID</c> is absent before migrating, and nothing is torn down: the target is
/// retained on purpose and never reused.
/// <para>
/// The service tests prove the acceptance rules against an in-memory port; they cannot prove what real SQL does with
/// the row locks, the guarded insert or a transaction after the link is consumed. This fixture is the runtime proof
/// of the port: the read-only inspection that consumes nothing, a new account inserted already verified with the
/// invited destination email even when the ficha stores another one, the single-use consume that makes the replay
/// fail, an existing pending account bound only with its own password, the old T20 verification link that stops
/// matching, the generic refusal of a revoked, expired or foreign invitation, the owner-assistance refusal without
/// reassignment, the stale issuer target that is refused because its ficha is linked, and a failure after the consume
/// whose rollback returns the link to its usable state, and two concurrent SQL consumers of the same invitation
/// where exactly one conditional update wins, two complete acceptances racing on one token, a revoke that commits
/// after the token probe, and expiry while the acceptance holds a ficha lock before the post-lock clock read.
/// </para>
/// </summary>
public sealed class ClientInvitationAcceptanceSqlIntegrationTests(ITestOutputHelper output)
{
    private const string EnvVar = "WORKUP_T22_ACCEPT_SQL_CONNECTION";
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private const string DatabasePrefix = "WorkUpDemoT22AcceptSql";
    private const string Slug = "peluqueria-t22-aceptar";
    private const string OtherSlug = "yoga-t22-aceptar";
    private const string BusinessName = "Peluquería Marina";
    private const string OtherBusinessName = "Estudio de Yoga";
    private const string FichaEmail = "ana@example.com";
    private const string InvitedEmail = "nueva@example.com";
    private const string RevokedInvitedEmail = "revocada@example.com";
    private const string ExpiredInvitedEmail = "vencida@example.com";
    private const string ForeignInvitedEmail = "ajena@example.com";
    private const string LinkedInvitedEmail = "vinculada@example.com";
    private const string StaleInvitedEmail = "obsoleta@example.com";
    private const string FirstName = "Ana";
    private const string LastName = "Gómez";
    private const string Password = "clave-segura";
    private const string WrongPassword = "otra-clave-larga";
    private const string RawInvitationToken = "raw-invitation-token";
    private const string RawRevokedToken = "raw-revoked-token";
    private const string RawExpiredToken = "raw-expired-token";
    private const string RawForeignToken = "raw-foreign-token";
    private const string RawLinkedToken = "raw-linked-token";
    private const string RawStaleToken = "raw-stale-token";
    private const string RawVerificationToken = "raw-t20-verification-token";
    private static readonly Regex SafeDatabaseName = new(@"\AWorkUpDemoT22AcceptSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>The single clock instant every scenario starts from.</summary>
    private static readonly DateTime FixedNow = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task Accept_new_inserts_a_verified_account_with_the_invited_email_and_consumes_the_link()
    {
        var target = await PrepareAsync("_New");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstName, LastName, FichaEmail);
        var invitationId = await SeedInvitationAsync(
            connectionString, businessId, clientId, InvitedEmail, SecureToken.Hash(RawInvitationToken),
            FixedNow.AddHours(48));
        var clock = new FakeClock { Now = FixedNow };

        await using var session = new DbSession(connectionString);
        var service = NewService(session, clock: clock);

        // The GET of the public link only proves the invited destination and the deadline: it consumes nothing and
        // discloses no ficha. The invitation is issued for an existing ficha whose stored email is a different one.
        var inspected = await service.InspectAsync(Slug, RawInvitationToken);
        Assert.True(inspected.Succeeded, inspected.Error?.Code);
        Assert.Equal(InvitedEmail, inspected.Value.Email);
        Assert.Equal(FixedNow.AddHours(48), inspected.Value.ExpiresAt);
        var inspectionProperties = string.Join(',', typeof(ClientInvitationInspection).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Client", inspectionProperties);
        Assert.DoesNotContain("Name", inspectionProperties);
        Assert.DoesNotContain(FirstName, inspected.Value.ToString());
        Assert.DoesNotContain(LastName, inspected.Value.ToString());
        Assert.DoesNotContain(FichaEmail, inspected.Value.ToString());
        Assert.Null((await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);
        Assert.Equal(0, await CountAccountsAsync(connectionString, businessId));

        // AcceptNew: the invited destination email becomes the verified account email and the account binds to the
        // invited existing ficha.
        var accepted = await service.AcceptNewAsync(Slug, RawInvitationToken, Password);
        Assert.True(accepted.Succeeded, accepted.Error?.Code);
        Assert.Equal(InvitedEmail, accepted.Value.Email);
        Assert.Equal(clientId, accepted.Value.ClientId);
        Assert.Equal(FirstName, accepted.Value.FirstName);
        Assert.Equal(LastName, accepted.Value.LastName);

        var account = await ReadAccountAsync(connectionString, businessId, InvitedEmail);
        Assert.NotNull(account);
        Assert.Equal(clientId, account!.ClientId);
        Assert.Equal(InvitedEmail, account.Email);
        Assert.Equal(FixedNow, account.EmailVerifiedAt);
        Assert.Null(account.PendingFirstName);
        Assert.Null(account.PendingLastName);
        Assert.True(new IdentityPasswordHasher().Verify(account.PasswordHash, Password));
        Assert.DoesNotContain(Password, account.PasswordHash, StringComparison.Ordinal);

        // No new ficha: the existing one keeps its own email and its identity.
        Assert.Equal(1, await CountClientsAsync(connectionString, businessId));
        var ficha = await ReadClientAsync(connectionString, businessId, clientId);
        Assert.NotNull(ficha);
        Assert.Equal(FichaEmail, ficha!.Email);
        Assert.Equal(FirstName, ficha.FirstName);

        // The link was consumed exactly once; a replay is refused generically and inserts nothing.
        Assert.Equal(FixedNow, (await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);
        var replay = await service.AcceptNewAsync(Slug, RawInvitationToken, Password);
        Assert.False(replay.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", replay.Error!.Code);
        Assert.Equal(1, await CountAccountsAsync(connectionString, businessId));

        output.WriteLine(
            "SQL accept-new assertions passed: the read-only inspection consumed nothing and disclosed no ficha, the " +
            "new account was inserted verified with the invited destination email and the exact invited ficha, no " +
            "second ficha was created, and the single-use consume made the replay fail.");
    }

    [Fact]
    public async Task Accept_existing_binds_a_pending_account_and_a_wrong_password_writes_nothing()
    {
        var target = await PrepareAsync("_Existing");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstName, LastName, FichaEmail);
        var invitationId = await SeedInvitationAsync(
            connectionString, businessId, clientId, InvitedEmail, SecureToken.Hash(RawInvitationToken),
            FixedNow.AddHours(48));
        var hasher = new IdentityPasswordHasher();
        var pendingId = await SeedPendingAccountAsync(connectionString, businessId, InvitedEmail, hasher.Hash(Password));
        await SeedVerificationTokenAsync(
            connectionString, pendingId, SecureToken.Hash(RawVerificationToken), FixedNow.AddHours(48));
        var clock = new FakeClock { Now = FixedNow };

        await using var session = new DbSession(connectionString);
        var service = NewService(session, clock: clock);

        // A wrong password leaves the link and the account exactly as they were.
        var wrong = await service.AcceptExistingAsync(Slug, RawInvitationToken, WrongPassword);
        Assert.False(wrong.Succeeded);
        Assert.Equal("invalid_credentials", wrong.Error!.Code);
        var afterWrong = await ReadAccountByIdAsync(connectionString, pendingId);
        Assert.NotNull(afterWrong);
        Assert.Null(afterWrong!.ClientId);
        Assert.Null(afterWrong.EmailVerifiedAt);
        Assert.Equal("Nueva", afterWrong.PendingFirstName);
        Assert.Null((await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);

        // The correct password proves ownership and binds the pending, unlinked account to the invited ficha.
        var accepted = await service.AcceptExistingAsync(Slug, RawInvitationToken, Password);
        Assert.True(accepted.Succeeded, accepted.Error?.Code);
        Assert.Equal(pendingId, accepted.Value.ClientAccountId);
        Assert.Equal(clientId, accepted.Value.ClientId);
        Assert.Equal(InvitedEmail, accepted.Value.Email);

        var bound = await ReadAccountByIdAsync(connectionString, pendingId);
        Assert.NotNull(bound);
        Assert.Equal(clientId, bound!.ClientId);
        Assert.Equal(FixedNow, bound.EmailVerifiedAt);
        Assert.Equal(InvitedEmail, bound.Email);
        Assert.Null(bound.PendingFirstName);
        Assert.Null(bound.PendingLastName);

        // No new ficha and the link is consumed.
        Assert.Equal(1, await CountClientsAsync(connectionString, businessId));
        Assert.Equal(FixedNow, (await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);

        // The old T20 email-verification link no longer matches its guard: the account is no longer pending or
        // unlinked, so the consume returns nothing and the stored token stays unused.
        await using (var registrationSession = new DbSession(connectionString))
        {
            var registration = new SqlClientRegistrationRepository(registrationSession);
            var consumed = await registration.ConsumeEmailVerificationTokenAsync(
                SecureToken.Hash(RawVerificationToken), businessId, FixedNow);
            Assert.Null(consumed);
        }

        Assert.Equal(1, await CountAsync(
            connectionString,
            "SELECT COUNT(*) FROM AccountTokens WHERE TokenHash = @tokenHash AND UsedAt IS NULL",
            ("@tokenHash", SecureToken.Hash(RawVerificationToken))));

        output.WriteLine(
            "SQL accept-existing assertions passed: a wrong password rolled back with no write, the correct password " +
            "bound and verified the pending account, no new ficha was created, and the old T20 verification link " +
            "stopped matching its own guard.");
    }

    [Fact]
    public async Task Unusable_foreign_and_linked_invitations_are_refused_without_writing()
    {
        var target = await PrepareAsync("_Guards");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstName, LastName, FichaEmail);
        var otherBusinessId = await SeedBusinessAsync(connectionString, OtherSlug, OtherBusinessName);
        var foreignClientId = await SeedClientAsync(
            connectionString, otherBusinessId, "Bruno", "Pérez", "bruno@example.com");
        var hasher = new IdentityPasswordHasher();
        var clock = new FakeClock { Now = FixedNow };

        // Revoked, expired and foreign invitations: all three are one generic refusal.
        await SeedInvitationAsync(
            connectionString, businessId, clientId, RevokedInvitedEmail, SecureToken.Hash(RawRevokedToken),
            FixedNow.AddHours(48), revokedAt: FixedNow);
        await SeedInvitationAsync(
            connectionString, businessId, clientId, ExpiredInvitedEmail, SecureToken.Hash(RawExpiredToken),
            FixedNow);
        var foreignInvitationId = await SeedInvitationAsync(
            connectionString, otherBusinessId, foreignClientId, ForeignInvitedEmail, SecureToken.Hash(RawForeignToken),
            FixedNow.AddHours(48));

        // The linked-other scenario: an account of the invited email already lives on a different ficha with its own
        // history, and the invitation still targets the first ficha.
        var linkedFichaId = await SeedClientAsync(
            connectionString, businessId, "Beto", "Pérez", "beto@example.com");
        await SeedClientNoteAsync(connectionString, linkedFichaId, "Historial de Beto");
        var linkedAccountId = await SeedLinkedAccountAsync(
            connectionString, businessId, linkedFichaId, LinkedInvitedEmail, hasher.Hash(Password));
        var linkedInvitationId = await SeedInvitationAsync(
            connectionString, businessId, clientId, LinkedInvitedEmail, SecureToken.Hash(RawLinkedToken),
            FixedNow.AddHours(48));

        // The stale issuer scenario: the invitation's target ficha was linked by acceptance time.
        var staleFichaId = await SeedClientAsync(
            connectionString, businessId, "Carla", "Díaz", "carla@example.com");
        await SeedLinkedAccountAsync(
            connectionString, businessId, staleFichaId, "dueno@example.com", hasher.Hash(Password));
        var staleInvitationId = await SeedInvitationAsync(
            connectionString, businessId, staleFichaId, StaleInvitedEmail, SecureToken.Hash(RawStaleToken),
            FixedNow.AddHours(48));

        await using var session = new DbSession(connectionString);
        var service = NewService(session, clock: clock);

        var revoked = await service.AcceptNewAsync(Slug, RawRevokedToken, Password);
        var expired = await service.AcceptNewAsync(Slug, RawExpiredToken, Password);
        var foreign = await service.AcceptNewAsync(Slug, RawForeignToken, Password);

        Assert.False(revoked.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", revoked.Error!.Code);
        Assert.False(expired.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", expired.Error!.Code);
        Assert.False(foreign.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", foreign.Error!.Code);
        Assert.Equal(revoked.Error, expired.Error);
        Assert.Equal(revoked.Error, foreign.Error);

        // The account linked to another ficha gets the owner-assistance message and is never reassigned, and no
        // history moves from its ficha.
        var linked = await service.AcceptExistingAsync(Slug, RawLinkedToken, Password);
        Assert.False(linked.Succeeded);
        Assert.Equal("client_invitation_owner_assistance", linked.Error!.Code);
        Assert.Contains("Contactá al negocio", linked.Error.Message, StringComparison.Ordinal);
        Assert.Equal(linkedFichaId, (await ReadAccountByIdAsync(connectionString, linkedAccountId))!.ClientId);
        Assert.Equal(1, await CountAsync(
            connectionString, "SELECT COUNT(*) FROM ClientNotes WHERE ClientId = @clientId",
            ("@clientId", linkedFichaId)));

        // The stale issuer target is refused because its ficha is already linked, and no second account is inserted.
        var stale = await service.AcceptNewAsync(Slug, RawStaleToken, Password);
        Assert.False(stale.Succeeded);
        Assert.Equal("client_invitation_owner_assistance", stale.Error!.Code);
        Assert.Equal(0, await CountAsync(
            connectionString,
            "SELECT COUNT(*) FROM ClientAccounts WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", StaleInvitedEmail)));

        // A pending account of the invited email must not be bound to that already-linked ficha either. The
        // conditional consume happens first, but the guarded bind returns false and the transaction rolls it back.
        var stalePendingId = await SeedPendingAccountAsync(
            connectionString, businessId, StaleInvitedEmail, hasher.Hash(Password));
        var staleExisting = await service.AcceptExistingAsync(Slug, RawStaleToken, Password);
        Assert.False(staleExisting.Succeeded);
        Assert.Equal("client_invitation_owner_assistance", staleExisting.Error!.Code);
        Assert.Null((await ReadAccountByIdAsync(connectionString, stalePendingId))!.ClientId);
        Assert.Null((await ReadAccountByIdAsync(connectionString, stalePendingId))!.EmailVerifiedAt);
        Assert.Null((await ReadInvitationAsync(connectionString, staleInvitationId))!.UsedAt);

        // Every refused invitation is still live and nothing was written for the invited emails.
        Assert.Null((await ReadInvitationAsync(connectionString, linkedInvitationId))!.UsedAt);
        Assert.Null((await ReadInvitationAsync(connectionString, staleInvitationId))!.UsedAt);
        Assert.Null((await ReadInvitationAsync(connectionString, foreignInvitationId))!.UsedAt);
        Assert.Equal(0, await CountAsync(
            connectionString,
            "SELECT COUNT(*) FROM ClientAccounts WHERE BusinessId = @businessId AND Email IN (@revoked, @expired, @foreign)",
            ("@businessId", businessId), ("@revoked", RevokedInvitedEmail), ("@expired", ExpiredInvitedEmail),
            ("@foreign", ForeignInvitedEmail)));
        Assert.Equal(3, await CountClientsAsync(connectionString, businessId));

        output.WriteLine(
            "SQL guard assertions passed: revoked, expired and foreign links shared the generic refusal, the account " +
            "linked to another ficha got the owner-assistance message with no reassignment and no history moved, and " +
            "the stale issuer target was refused for both new and pending accounts because its ficha was already linked.");
    }

    [Fact]
    public async Task A_failure_after_the_consume_rolls_the_link_back_to_usable()
    {
        var target = await PrepareAsync("_Rollback");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstName, LastName, FichaEmail);
        var invitationId = await SeedInvitationAsync(
            connectionString, businessId, clientId, InvitedEmail, SecureToken.Hash(RawInvitationToken),
            FixedNow.AddHours(48));
        var clock = new FakeClock { Now = FixedNow };

        // The decorator consumes the invitation inside the acceptance transaction and then fails, so only the real
        // SQL rollback can keep the link live.
        await using (var session = new DbSession(connectionString))
        {
            var throwing = new ThrowingAfterConsumeRepository(new SqlClientInvitationAcceptanceRepository(session));
            var service = NewService(session, throwing, clock);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.AcceptNewAsync(Slug, RawInvitationToken, Password));
        }

        Assert.Null((await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);
        Assert.Equal(0, await CountAccountsAsync(connectionString, businessId));
        Assert.Equal(1, await CountClientsAsync(connectionString, businessId));

        // The rollback left the link usable: the same invitation is accepted once by the real repository.
        await using var retrySession = new DbSession(connectionString);
        var retry = await NewService(retrySession, clock: clock).AcceptNewAsync(Slug, RawInvitationToken, Password);
        Assert.True(retry.Succeeded, retry.Error?.Code);
        Assert.Equal(clientId, retry.Value.ClientId);
        Assert.Equal(FixedNow, (await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);
        Assert.Equal(1, await CountAccountsAsync(connectionString, businessId));

        output.WriteLine(
            "SQL rollback assertions passed: the failure after the consume rolled the invitation row back to its " +
            "unused state with no account inserted, and the same link was accepted exactly once afterwards.");
    }

    [Fact]
    public async Task Expiry_during_a_ficha_lock_wait_refuses_to_consume_the_link()
    {
        var target = await PrepareAsync("_ExpiryWait");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstName, LastName, FichaEmail);
        var invitationId = await SeedInvitationAsync(
            connectionString, businessId, clientId, InvitedEmail, SecureToken.Hash(RawInvitationToken),
            FixedNow.AddHours(48));
        var clock = new FakeClock { Now = FixedNow };
        var locked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var session = new DbSession(connectionString);
        var repository = new PausingAfterClientLockRepository(
            new SqlClientInvitationAcceptanceRepository(session), locked, release);
        var service = NewService(session, repository, clock);
        var attempt = service.AcceptNewAsync(Slug, RawInvitationToken, Password);
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Now = FixedNow.AddHours(48);
        release.TrySetResult(true);
        var result = await attempt.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        Assert.Null((await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);
        Assert.Equal(0, await CountAccountsAsync(connectionString, businessId));
        output.WriteLine("SQL expiry-wait assertions passed: the post-lock clock refused the expired invitation without consuming it.");
    }

    [Fact]
    public async Task Revocation_after_the_probe_but_before_acceptance_refuses_the_link()
    {
        var target = await PrepareAsync("_RevokeRace");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstName, LastName, FichaEmail);
        var invitationId = await SeedInvitationAsync(
            connectionString, businessId, clientId, InvitedEmail, SecureToken.Hash(RawInvitationToken),
            FixedNow.AddHours(48));
        var probed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var acceptingSession = new DbSession(connectionString);
        var gated = new GatedAfterProbeRepository(
            new SqlClientInvitationAcceptanceRepository(acceptingSession), release,
            () => probed.TrySetResult(true));
        var attempt = NewService(acceptingSession, gated).AcceptNewAsync(Slug, RawInvitationToken, Password);
        await probed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using (var revokingSession = new DbSession(connectionString))
        {
            var revoked = await new SqlClientInvitationRepository(revokingSession)
                .RevokeAsync(businessId, clientId, FixedNow);
            Assert.True(revoked);
        }

        release.TrySetResult(true);
        var result = await attempt.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_link_unavailable", result.Error!.Code);
        var stored = await ReadInvitationAsync(connectionString, invitationId);
        Assert.Equal(FixedNow, stored!.RevokedAt);
        Assert.Null(stored.UsedAt);
        Assert.Equal(0, await CountAccountsAsync(connectionString, businessId));
        output.WriteLine("SQL revoke-race assertions passed: a committed revoke after the probe prevented consumption or binding.");
    }

    [Fact]
    public async Task Two_complete_acceptances_of_one_link_bind_exactly_one_account()
    {
        var target = await PrepareAsync("_ServiceRace");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstName, LastName, FichaEmail);
        var invitationId = await SeedInvitationAsync(
            connectionString, businessId, clientId, InvitedEmail, SecureToken.Hash(RawInvitationToken),
            FixedNow.AddHours(48));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probed = 0;

        async Task<Result<AcceptedClientAccount>> AcceptAsync()
        {
            await using var session = new DbSession(connectionString);
            var gated = new GatedAfterProbeRepository(
                new SqlClientInvitationAcceptanceRepository(session), ready, () =>
                {
                    if (Interlocked.Increment(ref probed) == 2)
                        ready.TrySetResult(true);
                });
            return await NewService(session, gated).AcceptNewAsync(Slug, RawInvitationToken, Password);
        }

        // Both transactions have observed the usable token before either obtains the shared business/account
        // padlock. Only the first may bind and consume; the second must fail without a second account or ficha.
        var results = await Task.WhenAll(AcceptAsync(), AcceptAsync()).WaitAsync(TimeSpan.FromSeconds(40));
        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => !result.Succeeded);
        Assert.Equal(clientId, results.Single(result => result.Succeeded).Value.ClientId);
        Assert.Equal(1, await CountAccountsAsync(connectionString, businessId));
        Assert.Equal(1, await CountClientsAsync(connectionString, businessId));
        Assert.Equal(clientId, (await ReadAccountAsync(connectionString, businessId, InvitedEmail))!.ClientId);
        Assert.Equal(FixedNow, (await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);
        output.WriteLine("SQL acceptance race assertions passed: two complete attempts saw the token, exactly one bound and consumed it.");
    }

    [Fact]
    public async Task Two_concurrent_consumers_of_one_link_leave_exactly_one_winner()
    {
        var target = await PrepareAsync("_Race");
        if (target is null)
            return;

        var connectionString = target.ConnectionString;
        var businessId = await SeedBusinessAsync(connectionString, Slug, BusinessName);
        var clientId = await SeedClientAsync(connectionString, businessId, FirstName, LastName, FichaEmail);
        var invitationId = await SeedInvitationAsync(
            connectionString, businessId, clientId, InvitedEmail, SecureToken.Hash(RawInvitationToken),
            FixedNow.AddHours(48));
        var bothStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        async Task<bool> ConsumeAsync()
        {
            await using var session = new DbSession(connectionString);
            await using var transaction = await session.BeginAsync();
            if (Interlocked.Increment(ref started) == 2)
                bothStarted.TrySetResult(true);
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var consumed = await new SqlClientInvitationAcceptanceRepository(session)
                .ConsumeInvitationAsync(businessId, invitationId, FixedNow);
            await transaction.CommitAsync();
            return consumed;
        }

        // Both transactions are open before either conditional UPDATE begins; the first locks the row and commits,
        // while the second observes UsedAt and must return false without changing the persisted timestamp.
        var results = await Task.WhenAll(ConsumeAsync(), ConsumeAsync()).WaitAsync(TimeSpan.FromSeconds(40));
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        Assert.Equal(FixedNow, (await ReadInvitationAsync(connectionString, invitationId))!.UsedAt);
        Assert.Equal(0, await CountAccountsAsync(connectionString, businessId));
        output.WriteLine("SQL single-use race assertions passed: two concurrent consumers left exactly one winner and one used invitation.");
    }

    // --- Service wiring -----------------------------------------------------------------------------------------

    private static ClientInvitationAcceptanceService NewService(
        DbSession session,
        IClientInvitationAcceptanceRepository? repository = null,
        IClock? clock = null) =>
        new(
            new SqlBusinessRepository(session),
            repository ?? new SqlClientInvitationAcceptanceRepository(session),
            session,
            new IdentityPasswordHasher(),
            clock ?? new FakeClock { Now = FixedNow });

    // --- Seeding ------------------------------------------------------------------------------------------------

    private static Task<int> SeedBusinessAsync(string connectionString, string slug, string name) =>
        InsertIdAsync(
            connectionString,
            "INSERT INTO Businesses (Name, Slug, ContactPhone, CreatedAt) OUTPUT INSERTED.Id VALUES (@name, @slug, '0000000000', @createdAt)",
            ("@name", name), ("@slug", slug), ("@createdAt", FixedNow));

    private static Task<int> SeedClientAsync(
        string connectionString, int businessId, string firstName, string lastName, string email) =>
        InsertIdAsync(
            connectionString,
            "INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @firstName, @lastName, @email, @createdAt)",
            ("@businessId", businessId), ("@firstName", firstName), ("@lastName", lastName),
            ("@email", email), ("@createdAt", FixedNow));

    private static Task<int> SeedClientNoteAsync(string connectionString, int clientId, string text) =>
        InsertIdAsync(
            connectionString,
            "INSERT INTO ClientNotes (ClientId, AppointmentId, Text, CreatedAt) OUTPUT INSERTED.Id VALUES (@clientId, NULL, @text, @createdAt)",
            ("@clientId", clientId), ("@text", text), ("@createdAt", FixedNow));

    private static Task<int> SeedInvitationAsync(
        string connectionString, int businessId, int clientId, string email, byte[] tokenHash, DateTime expiresAt,
        DateTime? usedAt = null, DateTime? revokedAt = null) =>
        InsertIdAsync(
            connectionString,
            "INSERT INTO AccountInvitations (BusinessId, ClientId, Email, TokenHash, ExpiresAt, UsedAt, RevokedAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @email, @tokenHash, @expiresAt, @usedAt, @revokedAt, @createdAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@email", email), ("@tokenHash", tokenHash),
            ("@expiresAt", expiresAt), ("@usedAt", usedAt), ("@revokedAt", revokedAt), ("@createdAt", FixedNow));

    private static Task<int> SeedPendingAccountAsync(
        string connectionString, int businessId, string email, string passwordHash) =>
        InsertIdAsync(
            connectionString,
            "INSERT INTO ClientAccounts (BusinessId, ClientId, Email, PasswordHash, EmailVerifiedAt, CreatedAt, PendingFirstName, PendingLastName) OUTPUT INSERTED.Id VALUES (@businessId, NULL, @email, @passwordHash, NULL, @createdAt, @pendingFirstName, @pendingLastName)",
            ("@businessId", businessId), ("@email", email), ("@passwordHash", passwordHash), ("@createdAt", FixedNow),
            ("@pendingFirstName", "Nueva"), ("@pendingLastName", "Persona"));

    private static Task<int> SeedLinkedAccountAsync(
        string connectionString, int businessId, int clientId, string email, string passwordHash) =>
        InsertIdAsync(
            connectionString,
            "INSERT INTO ClientAccounts (BusinessId, ClientId, Email, PasswordHash, EmailVerifiedAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@businessId, @clientId, @email, @passwordHash, @verifiedAt, @createdAt)",
            ("@businessId", businessId), ("@clientId", clientId), ("@email", email), ("@passwordHash", passwordHash),
            ("@verifiedAt", FixedNow), ("@createdAt", FixedNow));

    private static Task<int> SeedVerificationTokenAsync(
        string connectionString, int clientAccountId, byte[] tokenHash, DateTime expiresAt) =>
        InsertIdAsync(
            connectionString,
            "INSERT INTO AccountTokens (ClientAccountId, Purpose, TokenHash, ExpiresAt, CreatedAt) OUTPUT INSERTED.Id VALUES (@clientAccountId, @purpose, @tokenHash, @expiresAt, @createdAt)",
            ("@clientAccountId", clientAccountId),
            ("@purpose", (byte)AccountTokenPurpose.EmailVerification),
            ("@tokenHash", tokenHash), ("@expiresAt", expiresAt), ("@createdAt", FixedNow));

    // --- Plain reads of the persisted state ---------------------------------------------------------------------

    private sealed record StoredInvitation(int Id, int ClientId, string Email, DateTime ExpiresAt, DateTime? UsedAt, DateTime? RevokedAt);

    private sealed record StoredAccount(
        int Id, int? ClientId, string Email, string PasswordHash, DateTime? EmailVerifiedAt,
        string? PendingFirstName, string? PendingLastName);

    private sealed record StoredClient(int Id, string FirstName, string LastName, string Email);

    private static Task<StoredInvitation?> ReadInvitationAsync(string connectionString, int invitationId) =>
        QueryOneAsync(
            connectionString,
            "SELECT Id, ClientId, Email, ExpiresAt, UsedAt, RevokedAt FROM AccountInvitations WHERE Id = @id",
            reader => new StoredInvitation(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5)),
            ("@id", invitationId));

    private static Task<StoredAccount?> ReadAccountAsync(string connectionString, int businessId, string email) =>
        QueryOneAsync(
            connectionString,
            $"SELECT {AccountColumns} FROM ClientAccounts WHERE BusinessId = @businessId AND Email = @email",
            MapAccount, ("@businessId", businessId), ("@email", email));

    private static Task<StoredAccount?> ReadAccountByIdAsync(string connectionString, int id) =>
        QueryOneAsync(
            connectionString,
            $"SELECT {AccountColumns} FROM ClientAccounts WHERE Id = @id",
            MapAccount, ("@id", id));

    private static Task<StoredClient?> ReadClientAsync(string connectionString, int businessId, int clientId) =>
        QueryOneAsync(
            connectionString,
            "SELECT Id, FirstName, LastName, Email FROM Clients WHERE Id = @id AND BusinessId = @businessId",
            reader => new StoredClient(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
            ("@id", clientId), ("@businessId", businessId));

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

    private static Task<int> CountClientsAsync(string connectionString, int businessId) =>
        CountAsync(
            connectionString, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId",
            ("@businessId", businessId));

    private static Task<int> CountAccountsAsync(string connectionString, int businessId) =>
        CountAsync(
            connectionString, "SELECT COUNT(*) FROM ClientAccounts WHERE BusinessId = @businessId",
            ("@businessId", businessId));

    private static async Task<int> CountAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, sql, parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<T?> QueryOneAsync<T>(
        string connectionString, string sql, Func<SqlDataReader, T> map,
        params (string Name, object? Value)[] parameters) where T : class
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = Create(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? map(reader) : null;
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
        Assert.Contains("002_client_account_pending_names.sql", scripts);
        output.WriteLine(
            $"Target {target.DatabaseName}: DB_ID absent before migration; applied {scripts.Count} schema script(s) including 001 and 002; retained permanently without teardown.");
        return target;
    }

    /// <summary>
    /// Derives this test's own fresh database by appending <paramref name="testSuffix"/> to the configured catalog,
    /// so one <c>WORKUP_T22_ACCEPT_SQL_CONNECTION</c> value serves every fact. Only the validated LocalDB instance and
    /// the derived database survive into the connection string.
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
            ApplicationName = "WorkUp T22 acceptance SQL integration test",
            TrustServerCertificate = true,
            MultipleActiveResultSets = false
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    // --- Test-only observers ------------------------------------------------------------------------------------

    /// <summary>
    /// Decorator over the real repository that fails on the new-account insert, which runs after the invitation was
    /// consumed inside the acceptance transaction. Nothing catches it, so the disposal of the acceptance transaction
    /// is what has to roll the consumed link back.
    /// </summary>
    private sealed class ThrowingAfterConsumeRepository(IClientInvitationAcceptanceRepository inner)
        : IClientInvitationAcceptanceRepository
    {
        public Task<InvitationAcceptanceTarget?> FindInvitationAsync(
            int businessId, byte[] tokenHash, DateTime now, CancellationToken ct = default) =>
            inner.FindInvitationAsync(businessId, tokenHash, now, ct);

        public Task<InvitationAccountState?> LockAccountByEmailAsync(
            int businessId, string email, CancellationToken ct = default) =>
            inner.LockAccountByEmailAsync(businessId, email, ct);

        public Task<InvitationAccountKeyState> LockAccountKeyAsync(
            int businessId, string email, int clientId, CancellationToken ct = default) =>
            inner.LockAccountKeyAsync(businessId, email, clientId, ct);

        public Task<Client?> LockClientAsync(int businessId, int clientId, CancellationToken ct = default) =>
            inner.LockClientAsync(businessId, clientId, ct);

        public Task<bool> ConsumeInvitationAsync(
            int businessId, int invitationId, DateTime now, CancellationToken ct = default) =>
            inner.ConsumeInvitationAsync(businessId, invitationId, now, ct);

        public Task<bool> BindPendingAccountAsync(
            int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default) =>
            inner.BindPendingAccountAsync(businessId, clientAccountId, clientId, verifiedAt, ct);

        public Task<int> InsertVerifiedAccountAsync(ClientAccount account, CancellationToken ct = default) =>
            throw new InvalidOperationException("synthetic acceptance failure after the invitation was consumed");
    }

    private sealed class GatedAfterProbeRepository(
        IClientInvitationAcceptanceRepository inner,
        TaskCompletionSource<bool> ready,
        Action signal) : IClientInvitationAcceptanceRepository
    {
        public async Task<InvitationAcceptanceTarget?> FindInvitationAsync(
            int businessId, byte[] tokenHash, DateTime now, CancellationToken ct = default)
        {
            var target = await inner.FindInvitationAsync(businessId, tokenHash, now, ct);
            if (target is not null)
            {
                signal();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return target;
        }

        public Task<InvitationAccountState?> LockAccountByEmailAsync(
            int businessId, string email, CancellationToken ct = default) =>
            inner.LockAccountByEmailAsync(businessId, email, ct);

        public Task<InvitationAccountKeyState> LockAccountKeyAsync(
            int businessId, string email, int clientId, CancellationToken ct = default) =>
            inner.LockAccountKeyAsync(businessId, email, clientId, ct);

        public Task<Client?> LockClientAsync(int businessId, int clientId, CancellationToken ct = default) =>
            inner.LockClientAsync(businessId, clientId, ct);

        public Task<bool> ConsumeInvitationAsync(
            int businessId, int invitationId, DateTime now, CancellationToken ct = default) =>
            inner.ConsumeInvitationAsync(businessId, invitationId, now, ct);

        public Task<bool> BindPendingAccountAsync(
            int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default) =>
            inner.BindPendingAccountAsync(businessId, clientAccountId, clientId, verifiedAt, ct);

        public Task<int> InsertVerifiedAccountAsync(ClientAccount account, CancellationToken ct = default) =>
            inner.InsertVerifiedAccountAsync(account, ct);
    }

    private sealed class PausingAfterClientLockRepository(
        IClientInvitationAcceptanceRepository inner,
        TaskCompletionSource<bool> locked,
        TaskCompletionSource<bool> release) : IClientInvitationAcceptanceRepository
    {
        public Task<InvitationAcceptanceTarget?> FindInvitationAsync(
            int businessId, byte[] tokenHash, DateTime now, CancellationToken ct = default) =>
            inner.FindInvitationAsync(businessId, tokenHash, now, ct);

        public Task<InvitationAccountState?> LockAccountByEmailAsync(
            int businessId, string email, CancellationToken ct = default) =>
            inner.LockAccountByEmailAsync(businessId, email, ct);

        public Task<InvitationAccountKeyState> LockAccountKeyAsync(
            int businessId, string email, int clientId, CancellationToken ct = default) =>
            inner.LockAccountKeyAsync(businessId, email, clientId, ct);

        public async Task<Client?> LockClientAsync(int businessId, int clientId, CancellationToken ct = default)
        {
            var client = await inner.LockClientAsync(businessId, clientId, ct);
            Assert.NotNull(client);
            locked.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return client;
        }

        public Task<bool> ConsumeInvitationAsync(
            int businessId, int invitationId, DateTime now, CancellationToken ct = default) =>
            inner.ConsumeInvitationAsync(businessId, invitationId, now, ct);

        public Task<bool> BindPendingAccountAsync(
            int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default) =>
            inner.BindPendingAccountAsync(businessId, clientAccountId, clientId, verifiedAt, ct);

        public Task<int> InsertVerifiedAccountAsync(ClientAccount account, CancellationToken ct = default) =>
            inner.InsertVerifiedAccountAsync(account, ct);
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
