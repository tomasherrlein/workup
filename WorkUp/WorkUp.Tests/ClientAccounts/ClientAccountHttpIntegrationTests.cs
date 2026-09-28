using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Security;
using WorkUp.Data.DemoData;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using Xunit.Abstractions;

namespace WorkUp.Tests.ClientAccounts;

/// <summary>
/// Opt-in HTTP/SQL smoke of the public client account pages (RF-25, RF-15, RF-27) over the real MVC host, the real
/// dependency injection of the composition root and a real SQL Server database. The business layer of T20 is already
/// covered against in-memory ports and against SQL with a service call; what only this harness can prove is the whole
/// wired path: routing, model binding, the anti-CSRF filter, the real cookie scheme, the data-protection keyring and
/// the rows the flow leaves behind.
/// <para>
/// The single smoke drives one synthetic client from the first form to the signed-in panel: registration leaves a
/// pending account with no ficha and no session, the queued verification link is built from the configured trusted
/// base URL and opening it consumes nothing, the anti-CSRF POST confirms it exactly once and creates the ficha, a
/// replayed link is refused, sign-in answers one generic error for an absent, a wrong-password and a pending account
/// and only then issues the <c>workup.client</c> cookie, the panel renders for its own business while a foreign
/// business slug is redirected to that business login, an unknown email gets the same recovery answer with no notice,
/// a real recovery queues one link that survives a spoofed request Host, the reset POST without its anti-CSRF token is
/// refused, the anti-CSRF reset spends the token and replaces the password hash, a replay refuses, and the old
/// password stops opening a session while the new one does. Between the sign-in and the recovery the smoke plants one
/// future individual turn for the signed-in ficha and one turn of another ficha of the same business straight into
/// the fresh database and drives the T21.2 panel actions: the agenda renders only the own row and its confirm/cancel
/// forms, a scanner GET of the POST-only action routes writes nothing, confirm without its anti-CSRF token is refused,
/// confirm with it persists <c>Confirmed</c>, a repeat keeps the original <c>ConfirmedAt</c>, another ficha's booking
/// answers a generic 404, cancel persists <c>Cancelled</c> by the client without touching payments, and a late
/// confirmation is refused. Then one future class session carrying a drop-in annotation of the signed-in ficha and
/// another of the same other ficha drives the T21.3 cancel-class checkpoint: the agenda renders only the own
/// <c>Cancelar clase</c> form, a scanner GET of the POST-only route writes nothing, the POST without its anti-CSRF
/// token is refused, another ficha's annotation answers a generic 404, the own POST persists <c>Cancelled</c> by the
/// client while the seeded payment stays untouched, a repeat is refused without moving the original timestamp, and
/// the panel renders neither the seeded owner note nor a private appointment link. Then the T21.5 panel reservations
/// run from that same agenda: the two entry links are rendered, the individual screen offers the active service, the
/// active worker and the real free starts of a future day and its GET writes nothing, the save without its anti-CSRF
/// token is refused, and the anti-CSRF save ignores a forged email and client id to persist a Pending turn of the
/// cookie's own ficha with the chosen worker, start and price, a private 32-byte-hash link expiring 24 h after the
/// start and the booking-keyed notice addressed to the stored ficha email, and to add no client row. The class screen
/// lists a seeded future session, its GET writes nothing, the save without its anti-CSRF token is refused, the
/// anti-CSRF save persists a Pending drop-in of the cookie's ficha without a payment or a new client, a duplicate is
/// refused safely without a second row, and a class session of another tenant answers the generic 404. A hostile
/// return URL closes the open-redirect surface.
/// </para>
/// <para>
/// The fixture is opt-in through the pair <c>WORKUP_T20_HTTP_CONNECTION</c> and <c>WORKUP_T20_HTTP_KEYS_PATH</c>. Without
/// it the test logs N/A and returns before any SQL connection, database, keyring or HTTP host is used, so the default
/// suite is unaffected. With it set, the harness refuses to run unless it can prove the runtime is isolated first: a
/// fresh <c>WorkUpDemoT20Http*</c> database that does not exist yet, an encrypted keyring folder under the OS temp
/// directory whose leaf starts with <c>WorkUpDemoT12Keys</c>, no reparse point ancestor on that path, and a free
/// loopback port. Both opt-in facts are validated before anything connects, the database is created atomically after
/// <c>DB_ID</c> proved it absent, and the database and the encrypted keys are retained -- never reused and never
/// dropped or deleted. This harness does not replace an independent run: until the smoke is executed by a human on a
/// real LocalDB instance, the flow is compiled and N/A only, never proven.
/// </para>
/// </summary>
public sealed class ClientAccountHttpIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string ConnectionVariable = "WORKUP_T20_HTTP_CONNECTION";
    private const string KeysPathVariable = "WORKUP_T20_HTTP_KEYS_PATH";
    private const string KeysLeafPrefix = "WorkUpDemoT12Keys";
    private const string DatabasePrefix = "WorkUpDemoT20Http";

    /// <summary>Cookie of the client authentication scheme; the only session this flow can open.</summary>
    private const string ClientCookieName = "workup.client";

    /// <summary>Seeded demo business every request reaches through its public slug (RF-25 tenant resolution).</summary>
    private const string DemoSlug = "peluqueria-marina-demo";

    /// <summary>Second seeded demo business, used only to prove the panel refuses a foreign tenant.</summary>
    private const string OtherDemoSlug = "estudio-yoga-lucia-demo";

    /// <summary>
    /// Owner note planted for the T21.3 checkpoint. A client panel must never render an owner note or a health
    /// observation, so the smoke seeds exactly this text and proves it absent from the rendered agenda.
    /// </summary>
    private const string OwnerNoteMarker = "Observacion clinica sintetica T21.3";

    private const string VerificationMarker = "/cuenta/verificar/";
    private const string ResetMarker = "/cuenta/restablecer/";

    /// <summary>
    /// Host one request claims on purpose. Emailed links are built from <c>WorkUp:PublicBaseUrl</c>, never from the
    /// request Host, so the smoke sends this header on the recovery request and then proves the queued link still
    /// points at the configured base URL.
    /// </summary>
    private const string SpoofedHost = "spoofed.invalid";

    private static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Client_account_registration_verification_sign_in_and_recovery_run_on_an_isolated_http_host()
    {
        var suppliedConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var suppliedKeysPath = Environment.GetEnvironmentVariable(KeysPathVariable);
        if (string.IsNullOrWhiteSpace(suppliedConnection) || string.IsNullOrWhiteSpace(suppliedKeysPath))
        {
            // The two variables are one opt-in pair: with either one absent the fixture is not opted in, and it returns
            // here, before the keyring path is read and long before any SQL connection or HTTP host exists.
            var absent = string.IsNullOrWhiteSpace(suppliedConnection) ? ConnectionVariable : KeysPathVariable;
            output.WriteLine(
                $"N/A: {absent} is not set, so the opt-in pair {ConnectionVariable} and {KeysPathVariable} is incomplete; " +
                "no SQL connection, database, keyring, or HTTP host was used.");
            return;
        }

        // Preflight runs before any SQL connection: an unsafe target or a reused database or keyring stops the run here.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var demoPassword = "T20-" + Guid.NewGuid().ToString("N") + "!aA1";

        // First owned host migrates the fresh database and materializes the isolated encrypted keyring.
        var migrations = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(migrations);
            Assert.False(migrations.Process.HasExited, "The owned migration host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned migration host (pid {migrations.Process.Id}) became ready on {migrations.BaseUrl}.");
        }
        finally
        {
            await StopWebHostAsync(migrations.Process);
        }
        AssertKeyringCreated(plan.KeysPath);

        // The explicit T07 seed into the migrated, dedicated database, reusing the real hasher and clock.
        await SeedDemoAsync(plan.ConnectionString, demoPassword);
        output.WriteLine("SQL: T07 demo seeded (peluqueria-marina-demo and estudio-yoga-lucia-demo) into the isolated database.");

        // Second owned host serves the public account flow with a brand-new loopback port.
        var host = StartWebHost(plan, FindFreeLoopbackPort());
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            await RunClientAccountFlowAsync(host.BaseUrl, plan, demoPassword);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    /// <summary>
    /// The real-host smoke of RF-25, RF-15 and RF-27. Every diagnostic line goes through the recorder, so the last
    /// checkpoint can prove that no password, token or cookie value ever reached the test output, and every assertion
    /// reports only booleans, identifiers and paths.
    /// </summary>
    private async Task RunClientAccountFlowAsync(string baseUrl, HarnessPlan plan, string demoPassword)
    {
        var log = new FlowRecorder(output);
        log.KeepSecret(demoPassword);

        // Both passwords exist only in this process and are never written to output or to any log.
        var password = "T20-" + Guid.NewGuid().ToString("N") + "!aA1";
        var newPassword = "T20n-" + Guid.NewGuid().ToString("N") + "!aA1";
        log.KeepSecret(password);
        log.KeepSecret(newPassword);

        var email = $"t20http{Guid.NewGuid():N}@example.invalid";
        var pendingEmail = $"t20pending{Guid.NewGuid():N}@example.invalid";
        var missingEmail = $"t20missing{Guid.NewGuid():N}@example.invalid";
        var hostileReturnUrl = "https://evil.example.invalid/robo";
        var trustedAuthority = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var hasher = new IdentityPasswordHasher();

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();
        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = @slug", ("@slug", DemoSlug));
        var otherBusinessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = @slug", ("@slug", OtherDemoSlug));
        Assert.NotEqual(businessId, otherBusinessId);
        log.Note($"SQL: demo businesses #{businessId} and #{otherBusinessId} located by slug in the seeded database.");

        // --- Registration (RF-25) --------------------------------------------------------------------------
        var jar = new CookieContainer();
        using var client = CreateBrowser(baseUrl, jar);

        var (registerStatus, registerHtml) = await GetHtmlAsync(client, $"/r/{DemoSlug}/cuenta/registrarse");
        Assert.Equal(HttpStatusCode.OK, registerStatus);
        Assert.Contains("account-register-title", registerHtml, StringComparison.Ordinal);
        var antiforgery = ExtractAntiforgeryToken(registerHtml);
        log.KeepSecret(antiforgery);

        using (var registration = await client.PostAsync($"/r/{DemoSlug}/cuenta/registrarse", Form(new Dictionary<string, string>
               {
                   ["FirstName"] = "Synthetic",
                   ["LastName"] = "T20Http",
                   ["Email"] = email,
                   ["Password"] = password,
                   ["ConfirmPassword"] = password,
                   ["__RequestVerificationToken"] = antiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta/reenviar", DescribeRedirect(registration));
        }
        // Registering never opens a session: the account only becomes usable when the emailed link is consumed (D-18).
        Assert.False(HasClientCookie(jar), "A successful registration must not issue the client scheme cookie.");

        // A second registration stays pending on purpose, so the sign-in enumeration check later is not vacuous.
        using (var pendingRegistration = await client.PostAsync($"/r/{DemoSlug}/cuenta/registrarse", Form(new Dictionary<string, string>
               {
                   ["FirstName"] = "Pendiente",
                   ["LastName"] = "T20Http",
                   ["Email"] = pendingEmail,
                   ["Password"] = password,
                   ["ConfirmPassword"] = password,
                   ["__RequestVerificationToken"] = antiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, pendingRegistration.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta/reenviar", DescribeRedirect(pendingRegistration));
        }
        log.Note("HTTP: two registrations accepted with anti-CSRF; both redirected to the resend page and neither opened a session.");

        var account = await ReadAccountAsync(connection, businessId, email);
        Assert.NotNull(account);
        Assert.Null(account!.ClientId);
        Assert.Null(account.EmailVerifiedAt);
        Assert.Equal("Synthetic", account.PendingFirstName);
        Assert.Equal("T20Http", account.PendingLastName);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", email)));
        // The account column keeps only the Identity hash of the password: no plaintext secret is stored anywhere.
        Assert.True(!account.PasswordHash.Contains(password, StringComparison.Ordinal),
            "The account column must never keep the plaintext password.");
        log.Note($"SQL: pending account #{account.Id} stored with no ficha, no verified instant and a hashed password only.");

        var verificationTokenRow = Assert.Single(await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.EmailVerification));
        Assert.Null(verificationTokenRow.UsedAt);
        Assert.Equal(32, verificationTokenRow.Hash.Length);
        var verificationNotice = Assert.Single(
            await ReadNotificationsAsync(connection, businessId, NotificationType.EmailVerification, email));
        Assert.Equal(email, verificationNotice.Recipient);
        var verificationLink = LinkFrom(verificationNotice.BodyHtml);
        var verificationToken = TokenFrom(verificationNotice.BodyHtml, VerificationMarker);
        log.KeepSecret(verificationToken);
        Assert.True(verificationLink.StartsWith(trustedAuthority + "/", StringComparison.Ordinal),
            "The verification email must link to the configured trusted base URL and nothing else.");
        Assert.True(SecureToken.Hash(verificationToken).SequenceEqual(verificationTokenRow.Hash),
            "AccountTokens must keep the SHA-256 hash of the emailed token, never the token itself.");
        Assert.True(!verificationNotice.BodyHtml.Contains(password, StringComparison.Ordinal),
            "A queued notice must never carry a plaintext password.");
        Assert.True(!verificationNotice.DedupKey.Contains(verificationToken, StringComparison.Ordinal),
            "The dedup key must never carry the raw token.");
        log.Note("SQL: one fresh EmailVerification token (hash only) and one queued notice whose link uses the trusted base URL.");

        // Opening the emailed URL is a pure read: a scanner that follows it must not burn the single use.
        var (verifyStatus, verifyHtml, verifyCache, verifyReferrer) =
            await GetTokenPageAsync(client, new Uri(verificationLink).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, verifyStatus);
        Assert.Contains("account-verify-title", verifyHtml, StringComparison.Ordinal);
        Assert.True(HasCacheDirective(verifyCache, "no-store"),
            $"The confirmation page must send the no-store Cache-Control directive; it sent '{verifyCache}'.");
        Assert.Equal("no-referrer", verifyReferrer);
        Assert.True((await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.EmailVerification))
                .All(token => token.UsedAt is null),
            "GET of the emailed verification link must not consume the token.");
        Assert.Null((await ReadAccountAsync(connection, businessId, email))!.EmailVerifiedAt);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", email)));
        Assert.False(HasClientCookie(jar), "The confirmation screen must not open a session.");
        log.Note("HTTP: GET of the emailed verification link -> 200 with no-store/no-referrer, and the token stayed unused.");

        var verifyAntiforgery = ExtractAntiforgeryToken(verifyHtml);
        log.KeepSecret(verifyAntiforgery);
        using (var verification = await client.PostAsync($"/r/{DemoSlug}/cuenta/verificar", Form(new Dictionary<string, string>
               {
                   ["Token"] = verificationToken,
                   ["__RequestVerificationToken"] = verifyAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, verification.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta/ingresar", DescribeRedirect(verification));
        }
        Assert.False(HasClientCookie(jar), "Confirming the email must not open a session.");

        var verifiedAccount = await ReadAccountAsync(connection, businessId, email);
        Assert.NotNull(verifiedAccount);
        Assert.NotNull(verifiedAccount!.ClientId);
        Assert.NotNull(verifiedAccount.EmailVerifiedAt);
        Assert.Null(verifiedAccount.PendingFirstName);
        Assert.Null(verifiedAccount.PendingLastName);
        var clientId = verifiedAccount.ClientId!.Value;
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", email)));
        var fichaId = await ScalarIntAsync(connection,
            "SELECT Id FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", email));
        Assert.Equal(clientId, fichaId);
        Assert.Equal("Synthetic", await ScalarStringAsync(connection, "SELECT FirstName FROM Clients WHERE Id = @id", ("@id", clientId)));
        Assert.NotNull(Assert.Single(await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.EmailVerification)).UsedAt);
        log.Note($"SQL: the anti-CSRF POST created ficha #{clientId}, activated the account and marked the single-use token used.");

        // The single use is spent by the POST: a replay must answer generically and write nothing at all.
        using (var replay = await client.PostAsync($"/r/{DemoSlug}/cuenta/verificar", Form(new Dictionary<string, string>
               {
                   ["Token"] = verificationToken,
                   ["__RequestVerificationToken"] = verifyAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayHtml = await replay.Content.ReadAsStringAsync();
            Assert.Contains("account-status-title", replayHtml, StringComparison.Ordinal);
            Assert.Contains("El enlace no est", replayHtml, StringComparison.Ordinal);
        }
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email",
            ("@businessId", businessId), ("@email", email)));
        Assert.Single(await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.EmailVerification));
        log.Note("HTTP/SQL: replay of the spent verification link -> 200 generic page, no second ficha and no new token.");

        // --- Sign-in (RF-15) -------------------------------------------------------------------------------
        var pendingAccount = await ReadAccountAsync(connection, businessId, pendingEmail);
        Assert.NotNull(pendingAccount);
        Assert.Null(pendingAccount!.ClientId);
        Assert.True(hasher.Verify(pendingAccount.PasswordHash, password),
            "The pending account must hold the hash of the password it registered with, or the refusal proves nothing.");
        Assert.Null(await ReadAccountAsync(connection, businessId, missingEmail));

        var missingAttempt = await PostLoginAsync(client, jar, log, missingEmail, password, returnUrl: null);
        var wrongPasswordAttempt = await PostLoginAsync(client, jar, log, email, "no-es-la-contrasena", returnUrl: null);
        var pendingAttempt = await PostLoginAsync(client, jar, log, pendingEmail, password, returnUrl: null);
        foreach (var attempt in new[] { missingAttempt, wrongPasswordAttempt, pendingAttempt })
        {
            Assert.Equal(HttpStatusCode.OK, attempt.Status);
            // One ASCII-safe prefix of the single generic message: an absent, a wrong-password and a pending account
            // are indistinguishable on screen, so the form never reveals who has an account.
            Assert.Contains("Email o contrase", attempt.Body, StringComparison.Ordinal);
            Assert.False(attempt.HasClientCookie, "A refused sign-in must not issue the client scheme cookie.");
        }
        log.Note("HTTP/SQL: absent, wrong-password and pending sign-in attempts all answered the same generic error with no cookie.");

        var signedIn = await PostLoginAsync(client, jar, log, email, password, returnUrl: null);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.Status);
        Assert.Equal($"/r/{DemoSlug}/cuenta", signedIn.RedirectPath);
        Assert.True(signedIn.HasClientCookie, "A successful sign-in must issue the client scheme cookie.");
        log.KeepSecret(ReadClientCookieValue(jar));
        log.Note("HTTP: correct credentials -> 302 to the client panel and the Client scheme cookie was issued.");

        // T21 replaced the /cuenta placeholder with the real panel: the legacy URL now answers 302 to the safe agenda
        // of the authenticated business, and the agenda itself is what renders for that business.
        using (var panelRedirect = await client.GetAsync($"/r/{DemoSlug}/cuenta"))
        {
            Assert.Equal(HttpStatusCode.Redirect, panelRedirect.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(panelRedirect));
        }
        var (panelStatus, panelHtml) = await GetHtmlAsync(client, $"/r/{DemoSlug}/mis-turnos");
        Assert.Equal(HttpStatusCode.OK, panelStatus);
        Assert.Contains("client-appointments-title", panelHtml, StringComparison.Ordinal);
        using (var foreignPanel = await client.GetAsync($"/r/{OtherDemoSlug}/cuenta"))
        {
            Assert.Equal(HttpStatusCode.Redirect, foreignPanel.StatusCode);
            Assert.Equal($"/r/{OtherDemoSlug}/cuenta/ingresar", DescribeRedirect(foreignPanel));
        }
        log.Note("HTTP: the legacy /cuenta URL redirected its own business to the agenda, the agenda answered 200, and a foreign business slug was redirected to that business login.");

        // --- Own agenda and individual turn actions (T21.2, RF-08, RF-22) -----------------------------------
        // One own future individual reserved turn and one turn of another ficha of the same business are planted
        // straight into the fresh fixture database. The identity used to plant the own turn is the trusted ClientId
        // read from the verified account row; the session email is never used to resolve who the client is, and the
        // service and worker belong to the seeded demo tenant. The foreign email is synthetic and stays a secret.
        var otherEmail = $"t21other{Guid.NewGuid():N}@example.invalid";
        log.KeepSecret(otherEmail);
        var seeded = await SeedClientTurnAsync(connection, businessId, clientId, otherEmail);
        var ownConfirmPath = $"/r/{DemoSlug}/mis-turnos/{seeded.OwnBookingId}/confirmar";
        var ownCancelPath = $"/r/{DemoSlug}/mis-turnos/{seeded.OwnBookingId}/cancelar";
        var foreignConfirmPath = $"/r/{DemoSlug}/mis-turnos/{seeded.OtherBookingId}/confirmar";
        var bookingsBefore = await ReadBookingStatesAsync(connection, businessId);
        var paymentsBefore = await ReadPaymentStatesAsync(connection, businessId);
        log.Note($"SQL: seeded own pending individual turn #{seeded.OwnBookingId} and a foreign ficha turn #{seeded.OtherBookingId} of business #{businessId} into the fresh database.");

        // The real GET shows only the session's own row: the foreign ficha's booking id never reaches the page.
        var (agendaStatus, agendaHtml) = await GetHtmlAsync(client, $"/r/{DemoSlug}/mis-turnos");
        Assert.Equal(HttpStatusCode.OK, agendaStatus);
        Assert.Contains("client-appointments-title", agendaHtml, StringComparison.Ordinal);
        Assert.Contains(ownConfirmPath, agendaHtml, StringComparison.Ordinal);
        Assert.Contains($"/mis-turnos/{seeded.OwnBookingId}/cancelar", agendaHtml, StringComparison.Ordinal);
        Assert.True(!agendaHtml.Contains($"/mis-turnos/{seeded.OtherBookingId}/", StringComparison.Ordinal),
            "The own agenda must never render a row, a label or a form for another ficha's booking.");
        // The labels show the intention and both wired actions; an ASCII-safe fragment is used because Razor may
        // escape the accented character of the sentence. The confirm and cancel forms exist only for the own turn.
        Assert.Contains("confirmar que vas a asistir, cancelar o reprogramar.", agendaHtml, StringComparison.Ordinal);
        Assert.Contains("Confirmar que voy a asistir", agendaHtml, StringComparison.Ordinal);
        Assert.Contains("Cancelar turno", agendaHtml, StringComparison.Ordinal);
        Assert.Equal(new[] { seeded.OwnBookingId }, ExtractActionBookingIds(agendaHtml));
        Assert.Equal(1, CountOccurrences(agendaHtml, ownConfirmPath));
        Assert.Equal(1, CountOccurrences(agendaHtml, ownCancelPath));
        log.Note("HTTP: the agenda rendered the own future individual turn with its intention label and its confirm/cancel forms, and no row or form for the foreign ficha.");

        // A GET is a pure read: the router may answer 404 or 405 for a POST-only route, but neither can write.
        Assert.Equal(bookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        foreach (var scannerPath in new[] { ownConfirmPath, ownCancelPath })
        {
            using var prefetch = await client.GetAsync(scannerPath);
            Assert.True(prefetch.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"A scanner GET of a POST-only route must be refused; got {(int)prefetch.StatusCode}.");
        }
        Assert.Equal(bookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        log.Note("HTTP/SQL: GET of the panel and scanner GETs of both POST-only action routes were refused without changing any booking.");

        var agendaAntiforgery = ExtractAntiforgeryToken(agendaHtml);
        log.KeepSecret(agendaAntiforgery);

        // Without its anti-CSRF token the confirm POST is refused before any row is touched (RF-08).
        using (var noTokenConfirm = await client.PostAsync(ownConfirmPath, Form(new Dictionary<string, string>())))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenConfirm.StatusCode);
        }
        var afterNoTokenConfirm = await ReadBookingStateAsync(connection, seeded.OwnBookingId);
        Assert.Equal((byte)BookingStatus.Pending, afterNoTokenConfirm.Status);
        Assert.Null(afterNoTokenConfirm.ConfirmedAt);
        Assert.Null(afterNoTokenConfirm.CancelledAt);
        Assert.Equal((byte)BookingStatus.Pending, (await ReadBookingStateAsync(connection, seeded.OtherBookingId)).Status);
        log.Note("HTTP/SQL: the confirm POST without its anti-CSRF token -> 400 and neither booking row moved.");

        using (var confirm = await client.PostAsync(ownConfirmPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = agendaAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(confirm));
        }
        var afterConfirm = await ReadBookingStateAsync(connection, seeded.OwnBookingId);
        Assert.Equal((byte)BookingStatus.Confirmed, afterConfirm.Status);
        Assert.NotNull(afterConfirm.ConfirmedAt);
        Assert.Null(afterConfirm.CancelledAt);
        Assert.Null(afterConfirm.CancelledBy);
        var confirmedAt = afterConfirm.ConfirmedAt;
        log.Note($"SQL: confirm -> 302 and booking #{seeded.OwnBookingId} persisted Confirmed with ConfirmedAt set.");

        // RF-08 step 2: repeating the confirmation is a no-op that keeps the original ConfirmedAt.
        using (var repeatConfirm = await client.PostAsync(ownConfirmPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = agendaAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, repeatConfirm.StatusCode);
        }
        var afterRepeatConfirm = await ReadBookingStateAsync(connection, seeded.OwnBookingId);
        Assert.Equal((byte)BookingStatus.Confirmed, afterRepeatConfirm.Status);
        Assert.Equal(confirmedAt, afterRepeatConfirm.ConfirmedAt);
        Assert.Null(afterRepeatConfirm.CancelledAt);
        log.Note("SQL: repeating the confirmation -> 302 and the original ConfirmedAt did not move.");

        // Another ficha's booking answers the same generic 404 as one that does not exist, even with a valid token.
        using (var foreign = await client.PostAsync(foreignConfirmPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = agendaAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }
        var untouchedForeign = await ReadBookingStateAsync(connection, seeded.OtherBookingId);
        Assert.Equal((byte)BookingStatus.Pending, untouchedForeign.Status);
        Assert.Null(untouchedForeign.ConfirmedAt);
        Assert.Null(untouchedForeign.CancelledAt);
        log.Note("HTTP/SQL: confirming another ficha's booking, even with a valid token -> 404 and its row stayed Pending.");

        using (var cancel = await client.PostAsync(ownCancelPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = agendaAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(cancel));
        }
        var afterCancel = await ReadBookingStateAsync(connection, seeded.OwnBookingId);
        Assert.Equal((byte)BookingStatus.Cancelled, afterCancel.Status);
        Assert.Equal((byte)CancelledBy.Client, afterCancel.CancelledBy);
        Assert.NotNull(afterCancel.CancelledAt);
        Assert.Equal(confirmedAt, afterCancel.ConfirmedAt);
        // RF-22 and D-24: cancelling touches the booking only; every payment row of the business stays as it was.
        Assert.Equal(paymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        log.Note($"SQL: cancel -> 302, booking #{seeded.OwnBookingId} became Cancelled by Client and no payment row moved.");

        // A confirmation that arrives after the cancellation is refused and writes nothing.
        using (var lateConfirm = await client.PostAsync(ownConfirmPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = agendaAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, lateConfirm.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(lateConfirm));
        }
        Assert.Equal(afterCancel, await ReadBookingStateAsync(connection, seeded.OwnBookingId));
        log.Note("HTTP/SQL: confirming after the cancellation redirected back to the agenda and the cancelled row did not move.");

        // --- Own drop-in class cancel (T21.3, RF-22) --------------------------------------------------------
        // One future class session of the seeded demo tenant carries two drop-in annotations of that same session:
        // the own one of the trusted cookie ficha and one of the other ficha this smoke planted for the T21.2
        // checkpoints. Both, the payment the own annotation left behind and an owner note, are planted straight into
        // the fresh database. The ficha of the foreign annotation is read back from its own row instead of being
        // invented again, so both annotations really belong to different clients of the same business.
        var otherClientId = await ScalarIntAsync(connection, "SELECT ClientId FROM Bookings WHERE Id = @id", ("@id", seeded.OtherBookingId));
        Assert.NotEqual(clientId, otherClientId);
        var classSeed = await SeedDropInClassAsync(connection, businessId, clientId, otherClientId);
        var ownClassCancelPath = $"/r/{DemoSlug}/mis-turnos/{classSeed.OwnBookingId}/cancelar-clase";
        var foreignClassCancelPath = $"/r/{DemoSlug}/mis-turnos/{classSeed.OtherBookingId}/cancelar-clase";
        var classBookingsBefore = await ReadBookingStatesAsync(connection, businessId);
        var classPaymentsBefore = await ReadPaymentStatesAsync(connection, businessId);
        // The seeded payment is what makes "the cancellation leaves the payments alone" a real check instead of an
        // equality between two empty lists, so its presence in the snapshot is asserted before the POST.
        Assert.Contains(classPaymentsBefore,
            payment => payment.Id == classSeed.PaymentId && payment.BookingId == classSeed.OwnBookingId);
        log.Note($"SQL: seeded class drop-in #{classSeed.OwnBookingId} of the cookie ficha and drop-in #{classSeed.OtherBookingId} of the other ficha into the fresh database, with payment #{classSeed.PaymentId} and an owner note.");

        // The real GET wires one form for the own drop-in and nothing at all for the other ficha's annotation.
        var (classAgendaStatus, classAgendaHtml) = await GetHtmlAsync(client, $"/r/{DemoSlug}/mis-turnos");
        Assert.Equal(HttpStatusCode.OK, classAgendaStatus);
        Assert.Contains("client-appointments-title", classAgendaHtml, StringComparison.Ordinal);
        Assert.Contains(ownClassCancelPath, classAgendaHtml, StringComparison.Ordinal);
        Assert.Contains("Cancelar clase", classAgendaHtml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(classAgendaHtml, ownClassCancelPath));
        Assert.Equal(1, CountOccurrences(classAgendaHtml, "/cancelar-clase"));
        Assert.True(!classAgendaHtml.Contains($"/mis-turnos/{classSeed.OwnBookingId}/cancelar\"", StringComparison.Ordinal),
            "The drop-in row must offer the class cancel and never the individual turn cancel.");
        Assert.True(!classAgendaHtml.Contains($"/mis-turnos/{classSeed.OtherBookingId}/", StringComparison.Ordinal),
            "The own agenda must never render a row, a label or a form for another ficha's class annotation.");
        // The safe projection is proven by absence: the seeded owner note and the private appointment link route
        // must never reach the page, whatever the row renders.
        Assert.True(!classAgendaHtml.Contains(OwnerNoteMarker, StringComparison.Ordinal),
            "The panel must never render an owner note or health observation.");
        Assert.True(!classAgendaHtml.Contains("href=\"/t/", StringComparison.Ordinal),
            "The panel must never render a private appointment link.");
        log.Note("HTTP: the agenda rendered the own drop-in class with its 'Cancelar clase' POST form and no action, note or private link for anything else.");

        // A GET is a pure read: the router may answer 404 or 405 for the POST-only route, but neither can write.
        Assert.Equal(classBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        foreach (var scannerPath in new[] { ownClassCancelPath, foreignClassCancelPath })
        {
            using var classPrefetch = await client.GetAsync(scannerPath);
            Assert.True(classPrefetch.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"A scanner GET of the class cancel POST-only route must be refused; got {(int)classPrefetch.StatusCode}.");
        }
        Assert.Equal(classBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        log.Note("HTTP/SQL: scanner GETs of the class cancel route were refused without changing any booking.");

        var classAntiforgery = ExtractAntiforgeryToken(classAgendaHtml);
        log.KeepSecret(classAntiforgery);

        // Without its anti-CSRF token the class cancel POST is refused before any row is touched (RF-22).
        using (var noTokenClassCancel = await client.PostAsync(ownClassCancelPath, Form(new Dictionary<string, string>())))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenClassCancel.StatusCode);
        }
        var afterNoTokenClassCancel = await ReadBookingStateAsync(connection, classSeed.OwnBookingId);
        Assert.Equal((byte)BookingStatus.Pending, afterNoTokenClassCancel.Status);
        Assert.Null(afterNoTokenClassCancel.CancelledAt);
        Assert.Null(afterNoTokenClassCancel.CancelledBy);
        log.Note("HTTP/SQL: the class cancel POST without its anti-CSRF token -> 400 and the drop-in row stayed Pending.");

        // Another ficha's class annotation answers the same generic 404 as one that does not exist, even with a
        // valid token, and its row stays Pending.
        using (var foreignClassCancel = await client.PostAsync(foreignClassCancelPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = classAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignClassCancel.StatusCode);
        }
        var untouchedForeignClass = await ReadBookingStateAsync(connection, classSeed.OtherBookingId);
        Assert.Equal((byte)BookingStatus.Pending, untouchedForeignClass.Status);
        Assert.Null(untouchedForeignClass.CancelledAt);
        Assert.Null(untouchedForeignClass.CancelledBy);
        log.Note("HTTP/SQL: cancelling another ficha's drop-in class, even with a valid token -> 404 and its row stayed Pending.");

        using (var classCancel = await client.PostAsync(ownClassCancelPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = classAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, classCancel.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(classCancel));
        }
        var afterClassCancel = await ReadBookingStateAsync(connection, classSeed.OwnBookingId);
        Assert.Equal((byte)BookingStatus.Cancelled, afterClassCancel.Status);
        Assert.Equal((byte)CancelledBy.Client, afterClassCancel.CancelledBy);
        Assert.NotNull(afterClassCancel.CancelledAt);
        Assert.Null(afterClassCancel.ConfirmedAt);
        var classCancelledAt = afterClassCancel.CancelledAt;
        // RF-22 and D-24: cancelling the annotation touches the booking only; every payment row of the business, the
        // seeded one included, stays exactly as it was.
        Assert.Equal(classPaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        log.Note($"SQL: class cancel -> 302, booking #{classSeed.OwnBookingId} became Cancelled by Client and every payment row stayed as it was.");

        // A repeated cancel is refused as unavailable and never overwrites the original instant or author.
        using (var repeatClassCancel = await client.PostAsync(ownClassCancelPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = classAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, repeatClassCancel.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(repeatClassCancel));
        }
        var afterRepeatClassCancel = await ReadBookingStateAsync(connection, classSeed.OwnBookingId);
        Assert.Equal((byte)BookingStatus.Cancelled, afterRepeatClassCancel.Status);
        Assert.Equal((byte)CancelledBy.Client, afterRepeatClassCancel.CancelledBy);
        Assert.Equal(classCancelledAt, afterRepeatClassCancel.CancelledAt);
        log.Note("HTTP/SQL: repeating the class cancel returned to the own agenda and the original CancelledAt and author did not move.");

        // --- Own individual turn reprogramming (T21.4, RF-09) ----------------------------------------------
        // The T21.2 own turn was cancelled and the T21.3 class was cancelled, so neither can be moved again; one
        // fresh own future reserved individual turn of the cookie ficha is planted with a synthetic private link.
        // It shares the day and the worker of the T21.2 foreign booking and the T21.3 class, so the real
        // availability the reprogramming screen reads already knows occupied starts the chosen slot must avoid.
        var rescheduleSeed = await SeedRescheduleTurnAsync(connection, businessId, clientId);
        var ownReschedulePath = $"/r/{DemoSlug}/mis-turnos/{rescheduleSeed.BookingId}/reprogramar";
        var foreignReschedulePath = $"/r/{DemoSlug}/mis-turnos/{seeded.OtherBookingId}/reprogramar";
        var rescheduleDate = DateOnly.FromDateTime(rescheduleSeed.OriginalStart);
        log.KeepSecret(rescheduleSeed.ProtectedToken);
        var rescheduleBookingsBefore = await ReadBookingStatesAsync(connection, businessId);
        var rescheduleAppointmentBefore = await ReadAppointmentRangeAsync(connection, rescheduleSeed.AppointmentId);
        var rescheduleLinkBefore = await ReadRescheduleLinkAsync(connection, rescheduleSeed.LinkId);
        var reschedulePaymentsBefore = await ReadPaymentStatesAsync(connection, businessId);
        // The class payment of T21.3 is reused here: it keeps the "payments unchanged" check from comparing two
        // empty lists, so its presence in the snapshot is asserted before the save.
        Assert.NotEmpty(reschedulePaymentsBefore);
        Assert.Empty(await ReadNotificationsAsync(connection, businessId, NotificationType.Rescheduled, email));
        log.Note($"SQL: seeded own confirmed individual turn #{rescheduleSeed.BookingId} with private link #{rescheduleSeed.LinkId} into business #{businessId}; a foreign turn and a class share its worker and day.");

        // The real GET wires one reprogramming link for the own eligible turn and nothing at all for another ficha.
        var (rescheduleAgendaStatus, rescheduleAgendaHtml) = await GetHtmlAsync(client, $"/r/{DemoSlug}/mis-turnos");
        Assert.Equal(HttpStatusCode.OK, rescheduleAgendaStatus);
        Assert.Contains("client-appointments-title", rescheduleAgendaHtml, StringComparison.Ordinal);
        Assert.Contains("Reprogramar", rescheduleAgendaHtml, StringComparison.Ordinal);
        Assert.Contains(ownReschedulePath, rescheduleAgendaHtml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(rescheduleAgendaHtml, "/reprogramar"));
        Assert.True(!rescheduleAgendaHtml.Contains($"/mis-turnos/{seeded.OtherBookingId}/", StringComparison.Ordinal),
            "The own agenda must never render a reprogramming link for another ficha's booking.");
        Assert.True(!rescheduleAgendaHtml.Contains(rescheduleSeed.ProtectedToken, StringComparison.Ordinal),
            "The panel must never render the private appointment link token.");
        Assert.True(!rescheduleAgendaHtml.Contains(OwnerNoteMarker, StringComparison.Ordinal),
            "The panel must never render an owner note or health observation.");
        log.Note("HTTP: the agenda rendered exactly one reprogramming link for the own eligible turn and no link, note or token for anything else.");

        // The reprogramming GET is a pure read: it offers the same worker's real free starts and writes nothing.
        var (rescheduleFormStatus, rescheduleFormHtml) = await GetHtmlAsync(client, ownReschedulePath);
        Assert.Equal(HttpStatusCode.OK, rescheduleFormStatus);
        Assert.Contains("client-reschedule-title", rescheduleFormHtml, StringComparison.Ordinal);
        Assert.True(!rescheduleFormHtml.Contains(rescheduleSeed.ProtectedToken, StringComparison.Ordinal),
            "The reprogramming screen must never render the private appointment link token.");
        Assert.True(!rescheduleFormHtml.Contains(OwnerNoteMarker, StringComparison.Ordinal),
            "The reprogramming screen must never render an owner note or health observation.");
        // The offered start is the real availability of the seeded demo worker, never an assumed time: the current
        // start is dropped and the occupied foreign turn and class of the same worker and day are not offered.
        var offeredStarts = ExtractStartTimeOptions(rescheduleFormHtml);
        Assert.NotEmpty(offeredStarts);
        Assert.DoesNotContain(TimeOnly.FromDateTime(rescheduleSeed.OriginalStart), offeredStarts);
        Assert.DoesNotContain(new TimeOnly(12, 0), offeredStarts);
        Assert.DoesNotContain(new TimeOnly(14, 0), offeredStarts);
        var chosenStart = offeredStarts[0];
        Assert.NotEqual(TimeOnly.FromDateTime(rescheduleSeed.OriginalStart), chosenStart);
        Assert.Equal(rescheduleBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        Assert.Equal(rescheduleAppointmentBefore, await ReadAppointmentRangeAsync(connection, rescheduleSeed.AppointmentId));
        AssertLinkUnchanged(rescheduleLinkBefore, await ReadRescheduleLinkAsync(connection, rescheduleSeed.LinkId));
        Assert.Empty(await ReadNotificationsAsync(connection, businessId, NotificationType.Rescheduled, email));
        log.Note("HTTP/SQL: the reprogramming GET offered the own worker's real free starts and left every row in place.");

        var rescheduleAntiforgery = ExtractAntiforgeryToken(rescheduleFormHtml);
        log.KeepSecret(rescheduleAntiforgery);

        // T27 D3 emitted HTML: the clean reprogramming render links its start-time select to its own container and
        // claims no error, while a CSRF-valid save that omits only the start re-renders 200 with the select marked
        // aria-invalid and the field message in that span. The valid day travels in the hidden field, so the refusal
        // is caused by the omitted start alone and must be a read: no appointment, booking, link or payment may move.
        var cleanRescheduleWire = ReadStartTimeSelect(rescheduleFormHtml);
        Assert.Equal("client-reschedule-start-time-error", cleanRescheduleWire.ContainerId);
        Assert.Null(cleanRescheduleWire.AriaInvalid);
        Assert.Equal(string.Empty, cleanRescheduleWire.ContainerText);
        using (var omittedStartReschedule = await client.PostAsync(ownReschedulePath, Form(new Dictionary<string, string>
               {
                   ["Input.Date"] = rescheduleSeed.OriginalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                   ["__RequestVerificationToken"] = rescheduleAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.OK, omittedStartReschedule.StatusCode);
            var omittedStartHtml = await omittedStartReschedule.Content.ReadAsStringAsync();
            var invalidRescheduleWire = ReadStartTimeSelect(omittedStartHtml);
            Assert.Equal("true", invalidRescheduleWire.AriaInvalid);
            Assert.Equal("Elegí un horario.", invalidRescheduleWire.ContainerText);
            // Field-only branch: the omitted start is the sole invalid input, so ModelState holds a field error and
            // no model-level one. The view's <div asp-validation-summary="ModelOnly" role="alert"> is the only
            // role="alert" marker on this screen and the TagHelper suppresses it entirely when there is nothing to
            // summarize, so no alert container is expected here: the one server message lives in the start-time span
            // already asserted above and is never duplicated into a summary.
            Assert.Equal(1, CountOccurrences(WebUtility.HtmlDecode(omittedStartHtml), "Elegí un horario."));
        }
        Assert.Equal(rescheduleBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        Assert.Equal(rescheduleAppointmentBefore, await ReadAppointmentRangeAsync(connection, rescheduleSeed.AppointmentId));
        AssertLinkUnchanged(rescheduleLinkBefore, await ReadRescheduleLinkAsync(connection, rescheduleSeed.LinkId));
        Assert.Equal(reschedulePaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        log.Note("HTTP/SQL: an omitted start on the reprogramming save -> 200 with the start select aria-invalid and its span carrying the server message, and no appointment, booking, link or payment moved.");

        // Another ficha's booking is the same generic 404 for the read and, with a valid token, for the save.
        using (var foreignRescheduleForm = await client.GetAsync(foreignReschedulePath))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignRescheduleForm.StatusCode);
        }
        using (var foreignReschedule = await client.PostAsync(foreignReschedulePath, Form(new Dictionary<string, string>
               {
                   ["Input.Date"] = rescheduleSeed.OriginalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                   ["Input.StartTime"] = chosenStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                   ["__RequestVerificationToken"] = rescheduleAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignReschedule.StatusCode);
        }
        Assert.Equal(rescheduleBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        log.Note("HTTP/SQL: reading and saving another ficha's reprogramming, even with a valid token, answered 404 without touching any row.");

        // Without its anti-CSRF token the save is refused before any appointment, booking, link or payment moves.
        using (var noTokenReschedule = await client.PostAsync(ownReschedulePath, Form(new Dictionary<string, string>
               {
                   ["Input.Date"] = rescheduleSeed.OriginalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                   ["Input.StartTime"] = chosenStart.ToString("HH:mm", CultureInfo.InvariantCulture)
               })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenReschedule.StatusCode);
        }
        Assert.Equal(rescheduleAppointmentBefore, await ReadAppointmentRangeAsync(connection, rescheduleSeed.AppointmentId));
        AssertLinkUnchanged(rescheduleLinkBefore, await ReadRescheduleLinkAsync(connection, rescheduleSeed.LinkId));
        Assert.Equal(reschedulePaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        Assert.Equal((byte)BookingStatus.Confirmed, (await ReadBookingStateAsync(connection, rescheduleSeed.BookingId)).Status);
        log.Note("HTTP/SQL: the save POST without its anti-CSRF token -> 400 and no appointment, booking, link or payment moved.");

        // The valid save moves the appointment with its own worker, confirms it and refreshes only the link expiry.
        using (var reschedule = await client.PostAsync(ownReschedulePath, Form(new Dictionary<string, string>
               {
                   ["Input.Date"] = rescheduleSeed.OriginalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                   ["Input.StartTime"] = chosenStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                   ["__RequestVerificationToken"] = rescheduleAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, reschedule.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(reschedule));
        }
        var newStart = rescheduleDate.ToDateTime(chosenStart);
        var movedAppointment = await ReadAppointmentRangeAsync(connection, rescheduleSeed.AppointmentId);
        Assert.Equal(newStart, movedAppointment.StartsAt);
        Assert.Equal(newStart.AddMinutes(rescheduleSeed.DurationMinutes), movedAppointment.EndsAt);
        Assert.Equal(rescheduleSeed.WorkerId, movedAppointment.WorkerId);
        Assert.NotEqual(rescheduleAppointmentBefore.StartsAt, movedAppointment.StartsAt);
        var afterReschedule = await ReadBookingStateAsync(connection, rescheduleSeed.BookingId);
        Assert.Equal((byte)BookingStatus.Confirmed, afterReschedule.Status);
        // The booking was already confirmed, so the instant the client first confirmed must not be restamped.
        Assert.Equal(rescheduleSeed.ConfirmedAt, afterReschedule.ConfirmedAt);
        Assert.Null(afterReschedule.CancelledAt);
        Assert.Null(afterReschedule.CancelledBy);
        // RF-09: the private link keeps every byte of its token material; only the expiry moves to the new start + 24 h.
        var movedLink = await ReadRescheduleLinkAsync(connection, rescheduleSeed.LinkId);
        Assert.Equal(rescheduleLinkBefore.Id, movedLink.Id);
        Assert.Equal(rescheduleLinkBefore.ProtectedToken, movedLink.ProtectedToken);
        Assert.True(rescheduleLinkBefore.TokenHash.AsSpan().SequenceEqual(movedLink.TokenHash),
            "The private appointment-link token hash changed on reprogramming.");
        Assert.Null(movedLink.RevokedAt);
        Assert.Equal(newStart.AddHours(24), movedLink.ExpiresAt);
        Assert.Equal((byte)NotificationType.Rescheduled,
            Assert.Single(await ReadNotificationsAsync(connection, businessId, NotificationType.Rescheduled, email)).Type);
        Assert.Equal(reschedulePaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        log.Note($"HTTP/SQL: reprogram -> 302, appointment #{rescheduleSeed.AppointmentId} moved with the same worker, the confirmed instant stayed, the link expiry became the new start + 24 h, exactly one Rescheduled notice was queued and no payment row moved.");

        // Repeating the same slot is a safe same-slot refusal: no move and no second notification.
        using (var repeatReschedule = await client.PostAsync(ownReschedulePath, Form(new Dictionary<string, string>
               {
                   ["Input.Date"] = rescheduleSeed.OriginalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                   ["Input.StartTime"] = chosenStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                   ["__RequestVerificationToken"] = rescheduleAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.OK, repeatReschedule.StatusCode);
            var repeatHtml = await repeatReschedule.Content.ReadAsStringAsync();
            Assert.Contains("un horario distinto al actual.", repeatHtml, StringComparison.Ordinal);
        }
        Assert.Equal(movedAppointment, await ReadAppointmentRangeAsync(connection, rescheduleSeed.AppointmentId));
        Assert.Equal(rescheduleSeed.ConfirmedAt, (await ReadBookingStateAsync(connection, rescheduleSeed.BookingId)).ConfirmedAt);
        AssertLinkUnchanged(movedLink, await ReadRescheduleLinkAsync(connection, rescheduleSeed.LinkId));
        Assert.Single(await ReadNotificationsAsync(connection, businessId, NotificationType.Rescheduled, email));
        Assert.Equal(reschedulePaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        log.Note("HTTP/SQL: repeating the same slot -> the form with its safe message, no move, no link change and no extra Rescheduled notice.");

        // --- Own individual turn and drop-in class reservation (T21.5, RF-07, RF-22) ------------------------
        // The two reservation screens are reached from the own agenda. Both GET screens are pure reads, and the
        // request contract of both saves carries only the catalogue selection: the tenant and the ficha always come
        // from the client session cookie, so a forged identity field posted along with the form must be ignored by
        // the binder instead of moving the reservation to another ficha.
        var reservationNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));
        var reservationReadBookingsBefore = await ReadBookingStatesAsync(connection, businessId);
        var reservationReadPaymentsBefore = await ReadPaymentStatesAsync(connection, businessId);
        var reservationReadClientsBefore = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId", ("@businessId", businessId));
        var reservationReadAppointmentsBefore = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", businessId));

        var reserveTurnPath = $"/r/{DemoSlug}/mis-turnos/reservar/turno";
        var reserveClassPath = $"/r/{DemoSlug}/mis-turnos/reservar/clase";
        var (reserveEntryStatus, reserveEntryHtml) = await GetHtmlAsync(client, $"/r/{DemoSlug}/mis-turnos");
        Assert.Equal(HttpStatusCode.OK, reserveEntryStatus);
        Assert.Contains("client-appointments-title", reserveEntryHtml, StringComparison.Ordinal);
        Assert.Contains($"href=\"{reserveTurnPath}\"", reserveEntryHtml, StringComparison.Ordinal);
        Assert.Contains($"href=\"{reserveClassPath}\"", reserveEntryHtml, StringComparison.Ordinal);

        // The screen's own resolved selection is read back from it, so the service and the worker are the real active
        // ones of the cookie's business and never an id invented by the checkpoint.
        var (reserveTurnStatus, reserveTurnHtml) = await GetHtmlAsync(client, reserveTurnPath);
        Assert.Equal(HttpStatusCode.OK, reserveTurnStatus);
        Assert.Contains("client-reserve-title", reserveTurnHtml, StringComparison.Ordinal);
        var reserveServiceId = FirstOptionValue(reserveTurnHtml, "serviceId");
        var reserveWorkerId = FirstOptionValue(reserveTurnHtml, "workerId");
        Assert.True(reserveServiceId > 0 && reserveWorkerId > 0,
            "The individual screen must offer an active service and an active worker of the cookie's business.");

        // One future business day is requested and the offered starts are parsed from the returned HTML, so the time
        // the save posts is the real availability of that service and worker, never an assumed grid.
        var reservationDate = DateOnly.FromDateTime(PanelReservationDay(reservationNow));
        var reservationDateText = reservationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (reserveSlotsStatus, reserveSlotsHtml) = await GetHtmlAsync(client,
            $"{reserveTurnPath}?serviceId={reserveServiceId.ToString(CultureInfo.InvariantCulture)}" +
            $"&workerId={reserveWorkerId.ToString(CultureInfo.InvariantCulture)}&date={reservationDateText}");
        Assert.Equal(HttpStatusCode.OK, reserveSlotsStatus);
        Assert.Contains("client-reserve-title", reserveSlotsHtml, StringComparison.Ordinal);
        var reserveStarts = ExtractStartTimeOptions(reserveSlotsHtml);
        Assert.NotEmpty(reserveStarts);
        var chosenReserveStart = reserveStarts[0];
        var chosenReserveStartAt = reservationDate.ToDateTime(chosenReserveStart);
        Assert.True(chosenReserveStartAt > reservationNow,
            "The offered individual start must be in the future of the host clock.");
        // The rebuilt form echoes the selection it resolved and carries the chosen day, so the save posts exactly it.
        Assert.Equal(reserveServiceId.ToString(CultureInfo.InvariantCulture),
            HiddenInputValue(reserveSlotsHtml, "Input.ServiceId"));
        Assert.Equal(reserveWorkerId.ToString(CultureInfo.InvariantCulture),
            HiddenInputValue(reserveSlotsHtml, "Input.WorkerId"));
        Assert.Contains("name=\"Input.Date\"", reserveSlotsHtml, StringComparison.Ordinal);

        // Both GET screens are pure reads: no appointment, booking, payment or client row moved.
        Assert.Equal(reservationReadBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        Assert.Equal(reservationReadPaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        Assert.Equal(reservationReadClientsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal(reservationReadAppointmentsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", businessId)));
        log.Note($"HTTP/SQL: the agenda and the individual screen offered service #{reserveServiceId}, worker #{reserveWorkerId} and the real starts of a future day without writing a row.");

        var reserveAntiforgery = ExtractAntiforgeryToken(reserveSlotsHtml);
        log.KeepSecret(reserveAntiforgery);

        // T27 D3 emitted HTML: the clean individual render links its start-time select to its own container and
        // claims no error, while a CSRF-valid save that omits only the start re-renders 200 with the select marked
        // aria-invalid and the field message in that span. The service, worker and day are the valid hidden selectors,
        // so the refusal is caused by the omitted start alone and must be a read: no appointment, booking, payment or
        // client row may move.
        var cleanReserveWire = ReadStartTimeSelect(reserveSlotsHtml);
        Assert.Equal("client-reserve-start-time-error", cleanReserveWire.ContainerId);
        Assert.Null(cleanReserveWire.AriaInvalid);
        Assert.Equal(string.Empty, cleanReserveWire.ContainerText);
        using (var omittedStartReserve = await client.PostAsync(reserveTurnPath, Form(new Dictionary<string, string>
               {
                   ["Input.ServiceId"] = reserveServiceId.ToString(CultureInfo.InvariantCulture),
                   ["Input.WorkerId"] = reserveWorkerId.ToString(CultureInfo.InvariantCulture),
                   ["Input.Date"] = reservationDateText,
                   ["__RequestVerificationToken"] = reserveAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.OK, omittedStartReserve.StatusCode);
            var omittedStartHtml = await omittedStartReserve.Content.ReadAsStringAsync();
            var invalidReserveWire = ReadStartTimeSelect(omittedStartHtml);
            Assert.Equal("true", invalidReserveWire.AriaInvalid);
            Assert.Equal("Elegí un horario.", invalidReserveWire.ContainerText);
            // Field-only branch: the omitted start is the sole invalid input, so ModelState holds a field error and
            // no model-level one. The view's <div asp-validation-summary="ModelOnly" role="alert"> is the only
            // role="alert" marker on this screen and the TagHelper suppresses it entirely when there is nothing to
            // summarize, so no alert container is expected here: the one server message lives in the start-time span
            // already asserted above and is never duplicated into a summary.
            Assert.Equal(1, CountOccurrences(WebUtility.HtmlDecode(omittedStartHtml), "Elegí un horario."));
        }
        Assert.Equal(reservationReadBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        Assert.Equal(reservationReadPaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        Assert.Equal(reservationReadAppointmentsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal(reservationReadClientsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId", ("@businessId", businessId)));
        log.Note("HTTP/SQL: an omitted start on the individual save -> 200 with the start select aria-invalid and its span carrying the server message, and no appointment, booking, payment or client row moved.");

        // Without its anti-CSRF token the save is refused before any appointment or booking can be created.
        using (var noTokenReserve = await client.PostAsync(reserveTurnPath, Form(new Dictionary<string, string>
               {
                   ["Input.ServiceId"] = reserveServiceId.ToString(CultureInfo.InvariantCulture),
                   ["Input.WorkerId"] = reserveWorkerId.ToString(CultureInfo.InvariantCulture),
                   ["Input.Date"] = reservationDateText,
                   ["Input.StartTime"] = chosenReserveStart.ToString("HH:mm", CultureInfo.InvariantCulture)
               })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenReserve.StatusCode);
        }
        Assert.Equal(reservationReadAppointmentsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal(reservationReadBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        log.Note("HTTP/SQL: the individual reservation POST without its anti-CSRF token -> 400 and no appointment or booking row was created.");

        // The forbidden identity fields are posted on purpose: an email and another ficha's client id. The binder has
        // no field for either, so the persisted ficha stays the one of the cookie and no client row is created.
        var forgedReserveEmail = $"t21forged{Guid.NewGuid():N}@example.invalid";
        log.KeepSecret(forgedReserveEmail);
        using (var reserve = await client.PostAsync(reserveTurnPath, Form(new Dictionary<string, string>
               {
                   ["Input.ServiceId"] = reserveServiceId.ToString(CultureInfo.InvariantCulture),
                   ["Input.WorkerId"] = reserveWorkerId.ToString(CultureInfo.InvariantCulture),
                   ["Input.Date"] = reservationDateText,
                   ["Input.StartTime"] = chosenReserveStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                   ["Email"] = forgedReserveEmail,
                   ["ClientId"] = otherClientId.ToString(CultureInfo.InvariantCulture),
                   ["__RequestVerificationToken"] = reserveAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, reserve.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(reserve));
        }
        Assert.Equal(1, await ScalarIntAsync(connection,
            """
            SELECT COUNT(*) FROM Appointments
             WHERE BusinessId = @businessId AND WorkerId = @workerId AND StartsAt = @startsAt AND Kind = @kind
            """,
            ("@businessId", businessId), ("@workerId", reserveWorkerId), ("@startsAt", chosenReserveStartAt),
            ("@kind", (byte)AppointmentKind.Individual)));
        var reservedAppointmentId = await ScalarIntAsync(connection,
            """
            SELECT TOP (1) Id FROM Appointments
             WHERE BusinessId = @businessId AND WorkerId = @workerId AND StartsAt = @startsAt AND Kind = @kind
             ORDER BY Id
            """,
            ("@businessId", businessId), ("@workerId", reserveWorkerId), ("@startsAt", chosenReserveStartAt),
            ("@kind", (byte)AppointmentKind.Individual));
        // The created turn is the chosen one: same service, same worker, the chosen start and the real service span.
        Assert.Equal(reserveServiceId, await ScalarIntAsync(connection,
            "SELECT ServiceId FROM Appointments WHERE Id = @appointmentId", ("@appointmentId", reservedAppointmentId)));
        var reservedAppointment = await ReadAppointmentRangeAsync(connection, reservedAppointmentId);
        Assert.Equal(reserveWorkerId, reservedAppointment.WorkerId);
        Assert.Equal(chosenReserveStartAt, reservedAppointment.StartsAt);
        var reserveDurationMinutes = await ScalarIntAsync(connection,
            "SELECT DurationMinutes FROM Services WHERE Id = @serviceId", ("@serviceId", reserveServiceId));
        Assert.Equal(chosenReserveStartAt.AddMinutes(reserveDurationMinutes), reservedAppointment.EndsAt);
        var reservedBookingId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId ORDER BY Id",
            ("@businessId", businessId), ("@appointmentId", reservedAppointmentId));
        var reservedBooking = await ReadCreatedBookingAsync(connection, reservedBookingId);
        Assert.Equal(businessId, reservedBooking.BusinessId);
        Assert.Equal(reservedAppointmentId, reservedBooking.AppointmentId);
        // The saved turn belongs to the cookie's ficha, never to the forged client id, and it is a pending turn.
        Assert.Equal(clientId, reservedBooking.ClientId);
        Assert.Equal((byte)BookingStatus.Pending, reservedBooking.Status);
        Assert.Null(reservedBooking.Modality);
        var reserveServicePrice = await ScalarDecimalAsync(connection,
            "SELECT Price FROM Services WHERE Id = @serviceId", ("@serviceId", reserveServiceId));
        Assert.Equal(reserveServicePrice, reservedBooking.AgreedPrice);
        // RF-09: the private link carries a real 32-byte hash and expires 24 h after the start of the new turn.
        var reservedLinkId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM AppointmentLinks WHERE BookingId = @bookingId ORDER BY Id",
            ("@bookingId", reservedBookingId));
        var reservedLink = await ReadRescheduleLinkAsync(connection, reservedLinkId);
        Assert.Equal(32, reservedLink.TokenHash.Length);
        Assert.False(string.IsNullOrWhiteSpace(reservedLink.ProtectedToken),
            "The private appointment link must carry its protected token material.");
        Assert.Equal(chosenReserveStartAt.AddHours(24), reservedLink.ExpiresAt);
        Assert.Null(reservedLink.RevokedAt);
        // The notice goes to the stored ficha address, keyed by the booking, and never to the forged address.
        var reservationNotice = Assert.Single(
            await ReadNotificationsAsync(connection, businessId, NotificationType.BookingCreated, email));
        Assert.Equal(email, reservationNotice.Recipient);
        Assert.Equal($"booking:{reservedBookingId}", reservationNotice.DedupKey);
        Assert.Empty(await ReadNotificationsAsync(connection, businessId, NotificationType.BookingCreated, forgedReserveEmail));
        Assert.Equal(reservationReadClientsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @forgedEmail",
            ("@businessId", businessId), ("@forgedEmail", forgedReserveEmail)));
        log.Note($"HTTP/SQL: the anti-CSRF individual save -> 302, appointment #{reservedAppointmentId} and booking #{reservedBookingId} belong to the cookie ficha with the chosen worker, start and price, link #{reservedLinkId} expires 24 h after the start, exactly one BookingCreated notice was queued to the stored address, and no client row was added.");

        // The drop-in offer is fed by one synthetic session planted inside the panel window: the shared +30 day
        // fixture of the earlier checkpoints can fall outside the panel's next thirty days, so this checkpoint plants
        // its own future session on the reused class service of the tenant.
        var panelClassSessionId = await SeedFuturePanelClassSessionAsync(connection, businessId, new TimeOnly(11, 0));
        var classOfferPath = $"{reserveClassPath}/{panelClassSessionId}";
        var classReadBookingsBefore = await ReadBookingStatesAsync(connection, businessId);
        var classReadPaymentsBefore = await ReadPaymentStatesAsync(connection, businessId);
        var classReadAppointmentsBefore = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", businessId));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId",
            ("@businessId", businessId), ("@appointmentId", panelClassSessionId)));
        log.Note($"SQL: seeded panel class session #{panelClassSessionId} one week ahead of the business clock on the class service of business #{businessId}.");

        var (classOfferStatus, classOfferHtml) = await GetHtmlAsync(client, reserveClassPath);
        Assert.Equal(HttpStatusCode.OK, classOfferStatus);
        Assert.Contains("client-classes-title", classOfferHtml, StringComparison.Ordinal);
        Assert.Contains(classOfferPath, classOfferHtml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(classOfferHtml, $"reservar/clase/{panelClassSessionId}\""));
        Assert.Equal(classReadBookingsBefore, await ReadBookingStatesAsync(connection, businessId));
        Assert.Equal(classReadPaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        Assert.Equal(classReadAppointmentsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Appointments WHERE BusinessId = @businessId", ("@businessId", businessId)));
        log.Note("HTTP/SQL: the drop-in offer rendered exactly one booking form for the seeded future session and the read wrote nothing.");

        var panelClassAntiforgery = ExtractAntiforgeryToken(classOfferHtml);
        log.KeepSecret(panelClassAntiforgery);

        // Without its anti-CSRF token the class save is refused before any booking row exists.
        using (var noTokenClassBook = await client.PostAsync(classOfferPath, Form(new Dictionary<string, string>())))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenClassBook.StatusCode);
        }
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId",
            ("@businessId", businessId), ("@appointmentId", panelClassSessionId)));
        log.Note("HTTP/SQL: the class booking POST without its anti-CSRF token -> 400 and the session kept no booking.");

        // The identity fields posted along with the class form are forged for the same reason: the route carries only
        // the appointment, so the cookie's ficha is the only client that can be annotated.
        var forgedClassEmail = $"t21class{Guid.NewGuid():N}@example.invalid";
        log.KeepSecret(forgedClassEmail);
        using (var classBook = await client.PostAsync(classOfferPath, Form(new Dictionary<string, string>
               {
                   ["Email"] = forgedClassEmail,
                   ["ClientId"] = otherClientId.ToString(CultureInfo.InvariantCulture),
                   ["__RequestVerificationToken"] = panelClassAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, classBook.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/mis-turnos", DescribeRedirect(classBook));
        }
        var classBookedId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId ORDER BY Id",
            ("@businessId", businessId), ("@appointmentId", panelClassSessionId));
        var classBooked = await ReadCreatedBookingAsync(connection, classBookedId);
        Assert.Equal(clientId, classBooked.ClientId);
        Assert.Equal((byte)BookingStatus.Pending, classBooked.Status);
        Assert.Equal((byte)BookingModality.DropIn, classBooked.Modality);
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId",
            ("@businessId", businessId), ("@appointmentId", panelClassSessionId)));
        // RF-22 and D-24: annotating the class writes the booking only, without a payment and without a new client.
        Assert.Equal(classReadPaymentsBefore, await ReadPaymentStatesAsync(connection, businessId));
        Assert.Equal(reservationReadClientsBefore, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId", ("@businessId", businessId)));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @forgedEmail",
            ("@businessId", businessId), ("@forgedEmail", forgedClassEmail)));
        log.Note($"HTTP/SQL: the anti-CSRF class save -> 302, booking #{classBookedId} is a Pending drop-in of the cookie ficha, and no payment or client row was added.");

        // A repeated annotation is a safe refusal: the screen is rebuilt with the fresh offer and no second row.
        using (var duplicateClassBook = await client.PostAsync(classOfferPath, Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = panelClassAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.OK, duplicateClassBook.StatusCode);
            var duplicateClassHtml = await duplicateClassBook.Content.ReadAsStringAsync();
            Assert.Contains(classOfferPath, duplicateClassHtml, StringComparison.Ordinal);
        }
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId",
            ("@businessId", businessId), ("@appointmentId", panelClassSessionId)));
        Assert.Equal(classBooked, await ReadCreatedBookingAsync(connection, classBookedId));
        log.Note("HTTP/SQL: the duplicate class annotation -> the rebuilt offer and no second booking row.");

        // A session of another tenant answers the same generic 404 as one that does not exist, even with a valid
        // token, and writes nothing for either tenant.
        var foreignClassSessionId = await SeedFuturePanelClassSessionAsync(connection, otherBusinessId, new TimeOnly(15, 0));
        using (var foreignClassBook = await client.PostAsync($"{reserveClassPath}/{foreignClassSessionId}", Form(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = panelClassAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignClassBook.StatusCode);
        }
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId",
            ("@businessId", businessId), ("@appointmentId", foreignClassSessionId)));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Bookings WHERE BusinessId = @businessId AND AppointmentId = @appointmentId",
            ("@businessId", otherBusinessId), ("@appointmentId", foreignClassSessionId)));
        log.Note($"HTTP/SQL: annotating class session #{foreignClassSessionId} of another tenant, even with a valid token, -> 404 and no booking row was written for either tenant.");

        // --- Password recovery (RF-27) ---------------------------------------------------------------------
        var recoveryJar = new CookieContainer();
        using var recoveryClient = CreateBrowser(baseUrl, recoveryJar);

        var (recoverStatus, recoverHtml) = await GetHtmlAsync(recoveryClient, $"/r/{DemoSlug}/cuenta/recuperar");
        Assert.Equal(HttpStatusCode.OK, recoverStatus);
        Assert.Contains("account-recover-title", recoverHtml, StringComparison.Ordinal);
        var recoverAntiforgery = ExtractAntiforgeryToken(recoverHtml);
        log.KeepSecret(recoverAntiforgery);

        using (var unknownRecovery = await recoveryClient.PostAsync($"/r/{DemoSlug}/cuenta/recuperar", Form(new Dictionary<string, string>
               {
                   ["Email"] = missingEmail,
                   ["__RequestVerificationToken"] = recoverAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, unknownRecovery.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta/recuperar", DescribeRedirect(unknownRecovery));
        }
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId AND Type = @type AND Recipient = @email",
            ("@businessId", businessId), ("@type", (byte)NotificationType.PasswordReset), ("@email", missingEmail)));
        Assert.Empty(await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.PasswordReset));
        log.Note("HTTP/SQL: an unknown email got the same 302 answer and queued no recovery notice and no token.");

        // The request claims another Host on purpose: the queued link must still point at WorkUp:PublicBaseUrl.
        var recoveryRequest = new HttpRequestMessage(HttpMethod.Post, $"/r/{DemoSlug}/cuenta/recuperar")
        {
            Content = Form(new Dictionary<string, string>
            {
                ["Email"] = email,
                ["__RequestVerificationToken"] = recoverAntiforgery
            })
        };
        recoveryRequest.Headers.Host = SpoofedHost;
        using (var recovery = await recoveryClient.SendAsync(recoveryRequest))
        {
            Assert.Equal(HttpStatusCode.Redirect, recovery.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta/recuperar", DescribeRedirect(recovery));
        }

        var resetNotice = Assert.Single(
            await ReadNotificationsAsync(connection, businessId, NotificationType.PasswordReset, email));
        Assert.Equal(email, resetNotice.Recipient);
        var resetLink = LinkFrom(resetNotice.BodyHtml);
        var resetToken = TokenFrom(resetNotice.BodyHtml, ResetMarker);
        log.KeepSecret(resetToken);
        var resetTokenRow = Assert.Single(await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.PasswordReset));
        Assert.Null(resetTokenRow.UsedAt);
        Assert.True(resetLink.StartsWith(trustedAuthority + "/", StringComparison.Ordinal),
            "The recovery link must be built from the configured trusted base URL.");
        Assert.True(!resetLink.Contains(SpoofedHost, StringComparison.OrdinalIgnoreCase),
            "A request Host header must never move the host of an emailed link.");
        Assert.True(SecureToken.Hash(resetToken).SequenceEqual(resetTokenRow.Hash),
            "AccountTokens must keep the SHA-256 hash of the emailed recovery token.");
        Assert.True(!resetNotice.BodyHtml.Contains(password, StringComparison.Ordinal),
            "A queued notice must never carry the old plaintext password.");
        Assert.True(!resetNotice.BodyHtml.Contains(newPassword, StringComparison.Ordinal),
            "A queued notice must never carry a plaintext password.");
        log.Note("HTTP/SQL: the recovery request queued exactly one link built from the trusted base URL, not from the request Host.");

        var (resetPageStatus, resetHtml, resetCache, resetReferrer) =
            await GetTokenPageAsync(recoveryClient, new Uri(resetLink).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, resetPageStatus);
        Assert.Contains("account-reset-title", resetHtml, StringComparison.Ordinal);
        Assert.True(HasCacheDirective(resetCache, "no-store"),
            $"The recovery page must send the no-store Cache-Control directive; it sent '{resetCache}'.");
        Assert.Equal("no-referrer", resetReferrer);
        Assert.Null((await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.PasswordReset)).Single().UsedAt);
        log.Note("HTTP: GET of the emailed recovery link -> 200 with no-store/no-referrer and the token stayed unused.");

        var resetAntiforgery = ExtractAntiforgeryToken(resetHtml);
        log.KeepSecret(resetAntiforgery);

        // The anti-CSRF token is mandatory: without it the POST is refused before the link is spent or a hash written.
        using (var rejectedReset = await recoveryClient.PostAsync($"/r/{DemoSlug}/cuenta/restablecer", Form(new Dictionary<string, string>
               {
                   ["Token"] = resetToken,
                   ["NewPassword"] = newPassword,
                   ["ConfirmPassword"] = newPassword
               })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejectedReset.StatusCode);
        }
        Assert.Null((await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.PasswordReset)).Single().UsedAt);
        Assert.True(hasher.Verify((await ReadAccountAsync(connection, businessId, email))!.PasswordHash, password),
            "A reset POST without its anti-CSRF token must not change the stored password.");
        log.Note("HTTP/SQL: reset POST without the anti-CSRF token -> 400 and the link and the password stayed untouched.");

        using (var reset = await recoveryClient.PostAsync($"/r/{DemoSlug}/cuenta/restablecer", Form(new Dictionary<string, string>
               {
                   ["Token"] = resetToken,
                   ["NewPassword"] = newPassword,
                   ["ConfirmPassword"] = newPassword,
                   ["__RequestVerificationToken"] = resetAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta/ingresar", DescribeRedirect(reset));
        }
        Assert.NotNull((await ReadTokensAsync(connection, account.Id, AccountTokenPurpose.PasswordReset)).Single().UsedAt);
        var changedAccount = await ReadAccountAsync(connection, businessId, email);
        Assert.NotNull(changedAccount);
        Assert.True(hasher.Verify(changedAccount!.PasswordHash, newPassword),
            "The stored password must verify the new password after a completed recovery.");
        Assert.False(hasher.Verify(changedAccount.PasswordHash, password),
            "The stored password must no longer verify the old password after a completed recovery.");
        Assert.True(!changedAccount.PasswordHash.Contains(newPassword, StringComparison.Ordinal),
            "The account column must never keep the new plaintext password.");
        var hashAfterReset = changedAccount.PasswordHash;
        log.Note("HTTP/SQL: the anti-CSRF reset -> 302, the single-use token was spent and the stored hash now verifies only the new password.");

        // The recovery link is spent too: a replay must answer generically and leave the stored hash alone.
        using (var replayedReset = await recoveryClient.PostAsync($"/r/{DemoSlug}/cuenta/restablecer", Form(new Dictionary<string, string>
               {
                   ["Token"] = resetToken,
                   ["NewPassword"] = password,
                   ["ConfirmPassword"] = password,
                   ["__RequestVerificationToken"] = resetAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.OK, replayedReset.StatusCode);
            var replayedResetHtml = await replayedReset.Content.ReadAsStringAsync();
            Assert.Contains("account-status-title", replayedResetHtml, StringComparison.Ordinal);
            Assert.Contains("El enlace no est", replayedResetHtml, StringComparison.Ordinal);
        }
        Assert.Equal(hashAfterReset, (await ReadAccountAsync(connection, businessId, email))!.PasswordHash);
        log.Note("HTTP/SQL: replay of the spent recovery link -> 200 generic page and the stored password hash did not move.");

        var oldPasswordAttempt = await PostLoginAsync(recoveryClient, recoveryJar, log, email, password, returnUrl: null);
        Assert.Equal(HttpStatusCode.OK, oldPasswordAttempt.Status);
        Assert.Contains("Email o contrase", oldPasswordAttempt.Body, StringComparison.Ordinal);
        Assert.False(oldPasswordAttempt.HasClientCookie, "The old password must no longer open a session.");

        var newPasswordLogin = await PostLoginAsync(recoveryClient, recoveryJar, log, email, newPassword, returnUrl: null);
        Assert.Equal(HttpStatusCode.Redirect, newPasswordLogin.Status);
        Assert.Equal($"/r/{DemoSlug}/cuenta", newPasswordLogin.RedirectPath);
        Assert.True(newPasswordLogin.HasClientCookie, "The new password must open a session.");
        log.KeepSecret(ReadClientCookieValue(recoveryJar));
        log.Note("HTTP: the old password is refused generically and the new one signs in with the Client scheme cookie.");

        // --- Hostile return URL (open redirect) -------------------------------------------------------------
        var hostileJar = new CookieContainer();
        using var hostileClient = CreateBrowser(baseUrl, hostileJar);

        var (hostileFormStatus, hostileFormHtml) = await GetHtmlAsync(hostileClient,
            $"/r/{DemoSlug}/cuenta/ingresar?returnUrl={Uri.EscapeDataString(hostileReturnUrl)}");
        Assert.Equal(HttpStatusCode.OK, hostileFormStatus);
        // The controller drops a destination that is not a local path of this business, so the form never echoes it.
        Assert.True(!hostileFormHtml.Contains("evil.example.invalid", StringComparison.OrdinalIgnoreCase),
            "The sign-in form must not echo a destination outside this business.");
        var hostileAntiforgery = ExtractAntiforgeryToken(hostileFormHtml);
        log.KeepSecret(hostileAntiforgery);

        using (var hostile = await hostileClient.PostAsync($"/r/{DemoSlug}/cuenta/ingresar", Form(new Dictionary<string, string>
               {
                   ["Email"] = email,
                   ["Password"] = newPassword,
                   ["ReturnUrl"] = hostileReturnUrl,
                   ["__RequestVerificationToken"] = hostileAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, hostile.StatusCode);
            var location = hostile.Headers.Location;
            Assert.NotNull(location);
            Assert.False(location!.IsAbsoluteUri, "A successful sign-in must never redirect outside this host.");
            Assert.Equal($"/r/{DemoSlug}/cuenta", DescribeRedirect(hostile));
        }

        // A local path of another business is dropped too: the session of this tenant never lands in that route.
        using (var foreignReturn = await hostileClient.PostAsync($"/r/{DemoSlug}/cuenta/ingresar", Form(new Dictionary<string, string>
               {
                   ["Email"] = email,
                   ["Password"] = newPassword,
                   ["ReturnUrl"] = $"/r/{OtherDemoSlug}/cuenta",
                   ["__RequestVerificationToken"] = hostileAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, foreignReturn.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta", DescribeRedirect(foreignReturn));
        }
        log.KeepSecret(ReadClientCookieValue(hostileJar));
        log.Note("HTTP: a hostile or foreign returnUrl was dropped and the sign-in landed on this business panel.");

        log.AssertNoSecretLeaked();
        log.Note($"Output check: {log.WrittenCount} diagnostic line(s) were written and none of them carried one of the {log.SecretCount} password, token or cookie values.");
    }

    // --- Browser helpers -----------------------------------------------------------------------------------

    private static HttpClient CreateBrowser(string baseUrl, CookieContainer jar)
    {
        var handler = new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Sign-in attempt that always renders the form first, so the anti-CSRF token belongs to a live cookie.</summary>
    private static async Task<LoginOutcome> PostLoginAsync(
        HttpClient client, CookieContainer jar, FlowRecorder log, string email, string password, string? returnUrl)
    {
        var (status, html) = await GetHtmlAsync(client, $"/r/{DemoSlug}/cuenta/ingresar");
        Assert.Equal(HttpStatusCode.OK, status);
        var antiforgery = ExtractAntiforgeryToken(html);
        log.KeepSecret(antiforgery);

        using var response = await client.PostAsync($"/r/{DemoSlug}/cuenta/ingresar", Form(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl ?? "",
            ["__RequestVerificationToken"] = antiforgery
        }));
        return new LoginOutcome(
            response.StatusCode, await response.Content.ReadAsStringAsync(), DescribeRedirect(response), HasClientCookie(jar));
    }

    private static bool HasClientCookie(CookieContainer jar) =>
        jar.GetAllCookies().Any(cookie => string.Equals(cookie.Name, ClientCookieName, StringComparison.Ordinal));

    private static string? ReadClientCookieValue(CookieContainer jar) =>
        jar.GetAllCookies().FirstOrDefault(cookie => string.Equals(cookie.Name, ClientCookieName, StringComparison.Ordinal))?.Value;

    private static async Task<(HttpStatusCode Status, string Body)> GetHtmlAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>GET of a page that receives a token; the response headers are read before the body is consumed.</summary>
    private static async Task<(HttpStatusCode Status, string Body, string? CacheControl, string? ReferrerPolicy)> GetTokenPageAsync(
        HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        var referrerPolicy = response.Headers.TryGetValues("Referrer-Policy", out var values)
            ? string.Join(",", values)
            : null;
        return (response.StatusCode, body, response.Headers.CacheControl?.ToString(), referrerPolicy);
    }

    /// <summary>
    /// True when <paramref name="directive"/> is one whole directive of a <c>Cache-Control</c> header. Membership is
    /// required instead of an exact header equality because the host may append compatible directives such as
    /// <c>no-cache</c> to the <c>no-store</c> this application sets, and a substring check would also accept an
    /// unrelated directive such as <c>x-no-store</c>. The value is split on commas outside quoted strings, so a quoted
    /// directive value that happens to contain the text cannot produce a false match either.
    /// </summary>
    private static bool HasCacheDirective(string? headerValue, string directive)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return false;

        var start = 0;
        var quoted = false;
        for (var index = 0; index <= headerValue.Length; index++)
        {
            var atEnd = index == headerValue.Length;
            if (!atEnd && headerValue[index] == '"')
                quoted = !quoted;
            if (!atEnd && (headerValue[index] != ',' || quoted))
                continue;

            if (string.Equals(headerValue[start..index].Trim(), directive, StringComparison.OrdinalIgnoreCase))
                return true;
            start = index + 1;
        }

        return false;
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    /// <summary>Redirect path without host or query, so diagnostics never expose cookies, tokens or other secrets.</summary>
    private static string DescribeRedirect(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
            return "<none>";
        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", RegexOptions.CultureInvariant);
        if (!tag.Success)
            throw new InvalidOperationException("No antiforgery token was rendered in the expected form.");
        var value = Regex.Match(tag.Value, "value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        if (!value.Success)
            throw new InvalidOperationException("The antiforgery token input had no value.");
        return value.Groups[1].Value;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// Distinct booking ids targeted by the individual-turn forms (not the drop-in cancelar-clase route).
    /// This assertion checks only the individual actions on the agenda before the drop-in fixture is seeded.
    /// </summary>
    private static int[] ExtractActionBookingIds(string html) =>
        Regex.Matches(html, "/mis-turnos/(\\d+)/(?:confirmar|cancelar)(?![-A-Za-z0-9])", RegexOptions.CultureInvariant)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();

    /// <summary>Distinct free start times the reprogramming screen offers, parsed from its option values as HH:mm.</summary>
    private static TimeOnly[] ExtractStartTimeOptions(string html) =>
        Regex.Matches(html, "<option value=\"(\\d{2}:\\d{2})\"", RegexOptions.CultureInvariant)
            .Select(match => TimeOnly.ParseExact(match.Groups[1].Value, "HH:mm", CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();

    /// <summary>
    /// Value of the first option of the named select, so a screen that resolves its own default is read back instead
    /// of the checkpoint inventing a catalogue id it never saw rendered.
    /// </summary>
    private static int FirstOptionValue(string html, string selectName)
    {
        var match = Regex.Match(html,
            $"<select[^>]*name=\"{Regex.Escape(selectName)}\"[^>]*>\\s*<option value=\"(\\d+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"The screen must render a '{selectName}' select with at least one option.");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Value of the named hidden input, read from its whole tag so the rendered attribute order never matters. The
    /// explicit value is the only thing compared: the display format of a date field is not part of this contract.
    /// </summary>
    private static string HiddenInputValue(string html, string name)
    {
        var tag = Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*>", RegexOptions.CultureInvariant);
        Assert.True(tag.Success, $"The screen must render the hidden field '{name}'.");
        var value = Regex.Match(tag.Value, "value=\"([^\"]*)\"", RegexOptions.CultureInvariant);
        Assert.True(value.Success, $"The hidden field '{name}' must carry a value.");
        return value.Groups[1].Value;
    }

    /// <summary>
    /// T27 D3 emitted-HTML contract of one client-authenticated start-time select. The select carries the stable id the
    /// tag helper emits, points its <c>aria-describedby</c> at its own validation span (declared exactly once, so the
    /// link is never ambiguous) and that span names the same bound field through <c>data-valmsg-for</c>. The raw
    /// <c>aria-invalid</c> value is returned so the caller can prove the attribute is omitted on a clean render and
    /// <c>true</c> after a refused save, and the decoded span text is the field error the server wrote.
    /// </summary>
    private static StartTimeSelectWire ReadStartTimeSelect(string html)
    {
        Assert.Single(Regex.Matches(html, "id=\"Input_StartTime\"", RegexOptions.CultureInvariant).Cast<Match>());
        var selectTag = Regex.Match(html, "<select\\b[^>]*id=\"Input_StartTime\"[^>]*>", RegexOptions.CultureInvariant);
        Assert.True(selectTag.Success, "The rendered screen must carry the start-time select the tag helper emits.");
        var select = selectTag.Value;
        Assert.Contains("name=\"Input.StartTime\"", select, StringComparison.Ordinal);

        var describedBy = Regex.Match(select, "aria-describedby=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(describedBy.Success, "The start-time select must link its own error span through aria-describedby.");
        var containerId = describedBy.Groups[1].Value;
        Assert.Single(Regex.Matches(html, $"id=\"{Regex.Escape(containerId)}\"", RegexOptions.CultureInvariant).Cast<Match>());

        var container = Regex.Match(html,
            $"<span\\b[^>]*id=\"{Regex.Escape(containerId)}\"[^>]*>(.*?)</span>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(container.Success, $"The rendered HTML must contain the '{containerId}' validation span.");
        Assert.Contains("data-valmsg-for=\"Input.StartTime\"", container.Value, StringComparison.Ordinal);

        var invalid = Regex.Match(select, "aria-invalid=\"([^\"]*)\"", RegexOptions.CultureInvariant);
        return new StartTimeSelectWire(
            containerId,
            invalid.Success ? invalid.Groups[1].Value : null,
            WebUtility.HtmlDecode(Regex.Replace(container.Groups[1].Value, "<[^>]*>", string.Empty)).Trim());
    }

    private static string LinkFrom(string bodyHtml)
    {
        var match = Regex.Match(bodyHtml, "href=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The queued notice must contain a link.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string TokenFrom(string bodyHtml, string marker)
    {
        var link = LinkFrom(bodyHtml);
        var index = link.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The queued notice must link to the expected client account route.");
        return link[(index + marker.Length)..];
    }

    // --- Opt-in SQL observers ------------------------------------------------------------------------------

    private static async Task<AccountRow?> ReadAccountAsync(SqlConnection connection, int businessId, string email)
    {
        await using var command = CreateCommand(connection, """
            SELECT Id, ClientId, Email, PasswordHash, EmailVerifiedAt, PendingFirstName, PendingLastName
              FROM ClientAccounts
             WHERE BusinessId = @businessId AND Email = @email
            """, [("@businessId", businessId), ("@email", email)]);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return new AccountRow(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static async Task<List<TokenRow>> ReadTokensAsync(
        SqlConnection connection, int clientAccountId, AccountTokenPurpose purpose)
    {
        await using var command = CreateCommand(connection, """
            SELECT Id, Purpose, TokenHash, ExpiresAt, UsedAt
              FROM AccountTokens
             WHERE ClientAccountId = @clientAccountId AND Purpose = @purpose
             ORDER BY Id
            """, [("@clientAccountId", clientAccountId), ("@purpose", (byte)purpose)]);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<TokenRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new TokenRow(
                reader.GetInt32(0),
                reader.GetByte(1),
                (byte[])reader.GetValue(2),
                reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        }
        return rows;
    }

    /// <summary>
    /// Notices of one purpose for one recipient. The recipient is part of the filter on purpose: this smoke registers a
    /// second, deliberately pending account, so a count by business and purpose alone would mix two accounts' notices.
    /// </summary>
    private static async Task<List<NotificationRow>> ReadNotificationsAsync(
        SqlConnection connection, int businessId, NotificationType type, string recipient)
    {
        await using var command = CreateCommand(connection, """
            SELECT Id, Type, Recipient, Subject, BodyHtml, DedupKey
              FROM Notifications
             WHERE BusinessId = @businessId AND Type = @type AND Recipient = @recipient
             ORDER BY Id
            """, [("@businessId", businessId), ("@type", (byte)type), ("@recipient", recipient)]);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<NotificationRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new NotificationRow(
                reader.GetInt32(0),
                reader.GetByte(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return rows;
    }

    /// <summary>
    /// Plants one own future individual reserved turn and one turn of another ficha of the same business straight
    /// into the fresh fixture database. The own ficha is the trusted <paramref name="clientId"/> read from the
    /// verified account row, never resolved from the session email; both turns use an active individual service and
    /// an active worker of the seeded demo tenant, so the panel and the action service see a legitimate turn.
    /// </summary>
    private static async Task<SeededClientTurn> SeedClientTurnAsync(
        SqlConnection connection, int businessId, int clientId, string otherEmail)
    {
        // Business-local now, computed exactly like the host clock, so the planted starts stay in the future for the
        // environment the requests run against whatever the machine time zone is.
        var businessNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));

        var serviceId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Services WHERE BusinessId = @businessId AND MaxCapacity = 1 AND IsActive = 1 ORDER BY Id",
            ("@businessId", businessId));
        Assert.True(serviceId > 0, "The seeded demo tenant must have an active individual service for this checkpoint.");
        var workerId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Workers WHERE BusinessId = @businessId AND IsActive = 1 ORDER BY Id",
            ("@businessId", businessId));
        Assert.True(workerId > 0, "The seeded demo tenant must have an active worker for this checkpoint.");

        var otherClientId = await ScalarIntAsync(connection,
            """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, 'Synthetic', 'T21Other', @email, @createdAt)
            """,
            ("@businessId", businessId), ("@email", otherEmail), ("@createdAt", businessNow));

        var ownAppointmentId = await InsertIndividualTurnAsync(
            connection, businessId, serviceId, workerId, FutureBusinessDay(businessNow).AddHours(10), businessNow);
        var otherAppointmentId = await InsertIndividualTurnAsync(
            connection, businessId, serviceId, workerId, FutureBusinessDay(businessNow).AddHours(12), businessNow);

        var ownBookingId = await InsertPendingTurnBookingAsync(connection, businessId, ownAppointmentId, clientId, businessNow);
        var otherBookingId = await InsertPendingTurnBookingAsync(connection, businessId, otherAppointmentId, otherClientId, businessNow);

        // The planted rows are what the assertions rely on: the own turn belongs to the trusted ficha and the other
        // one belongs to a different ficha of the same business, so a scoping defect is what could surface it.
        Assert.Equal(clientId, await ScalarIntAsync(connection, "SELECT ClientId FROM Bookings WHERE Id = @id", ("@id", ownBookingId)));
        Assert.NotEqual(clientId, await ScalarIntAsync(connection, "SELECT ClientId FROM Bookings WHERE Id = @id", ("@id", otherBookingId)));
        return new SeededClientTurn(ownBookingId, otherBookingId);
    }

    private static Task<int> InsertIndividualTurnAsync(
        SqlConnection connection, int businessId, int serviceId, int workerId, DateTime startsAt, DateTime createdAt)
    {
        Assert.True(startsAt > createdAt, "A seeded individual turn must start in the future of the host clock.");
        return ScalarIntAsync(connection,
            """
            INSERT INTO Appointments
                        (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)
            """,
            ("@businessId", businessId), ("@kind", (byte)AppointmentKind.Individual), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(30)),
            ("@origin", (byte)AppointmentOrigin.Reserved), ("@status", (byte)AppointmentStatus.Scheduled),
            ("@createdAt", createdAt));
    }

    private static Task<int> InsertPendingTurnBookingAsync(
        SqlConnection connection, int businessId, int appointmentId, int clientId, DateTime createdAt) =>
        ScalarIntAsync(connection,
            """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @appointmentId, @clientId, NULL, NULL, @status, @createdAt)
            """,
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@status", (byte)BookingStatus.Pending), ("@createdAt", createdAt));

    /// <summary>
    /// Plants one future class session of the seeded demo tenant with two drop-in annotations of that same session:
    /// the own one of the trusted <paramref name="clientId"/> and the one of <paramref name="otherClientId"/>, a
    /// different ficha of the same business. It also plants the payment the own annotation left behind and an owner
    /// note, so the T21.3 checkpoints can prove both that the cancellation leaves every payment row alone and that
    /// the panel never renders a health observation. The demo salon has no class-capable service, so the fixture
    /// inserts its own active class service instead of borrowing a capacity-1 individual one; the session keeps
    /// <c>GridClassId</c> null like the T21.3 SQL fixture, so it cannot collide with the generated-week unique key.
    /// </summary>
    private static async Task<SeededDropInClass> SeedDropInClassAsync(
        SqlConnection connection, int businessId, int clientId, int otherClientId)
    {
        // Business-local now, computed exactly like the host clock, so the planted start stays in the future for the
        // environment the requests run against whatever the machine time zone is.
        var businessNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));

        var workerId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Workers WHERE BusinessId = @businessId AND IsActive = 1 ORDER BY Id",
            ("@businessId", businessId));
        Assert.True(workerId > 0, "The seeded demo tenant must have an active worker for this checkpoint.");

        var serviceId = await ScalarIntAsync(connection,
            """
            INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity, IsActive)
            OUTPUT INSERTED.Id
            VALUES (@businessId, 'Clase suelta T21.3', 8000.00, 60, 12, 1)
            """,
            ("@businessId", businessId));

        var sessionId = await InsertClassSessionAsync(
            connection, businessId, serviceId, workerId, FutureBusinessDay(businessNow).AddHours(14), 12, businessNow);
        var ownBookingId = await InsertPendingDropInBookingAsync(connection, businessId, sessionId, clientId, businessNow);
        var otherBookingId = await InsertPendingDropInBookingAsync(connection, businessId, sessionId, otherClientId, businessNow);

        var paymentId = await ScalarIntAsync(connection,
            """
            INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @clientId, @concept, @bookingId, NULL, 8000.00, NULL, NULL, @paidAt, @recordedAt)
            """,
            ("@businessId", businessId), ("@clientId", clientId), ("@concept", (byte)PaymentConcept.DropInClass),
            ("@bookingId", ownBookingId), ("@paidAt", businessNow), ("@recordedAt", businessNow));

        await ScalarIntAsync(connection,
            """
            INSERT INTO ClientNotes (ClientId, AppointmentId, Text, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@clientId, @appointmentId, @text, @createdAt)
            """,
            ("@clientId", clientId), ("@appointmentId", sessionId), ("@text", OwnerNoteMarker), ("@createdAt", businessNow));

        // The planted rows are what the assertions rely on: both annotations belong to the same class, the own one to
        // the trusted ficha and the other one to a different ficha, so only a scoping defect could let the session
        // cancel the wrong annotation.
        Assert.Equal(clientId, await ScalarIntAsync(connection, "SELECT ClientId FROM Bookings WHERE Id = @id", ("@id", ownBookingId)));
        Assert.Equal(otherClientId, await ScalarIntAsync(connection, "SELECT ClientId FROM Bookings WHERE Id = @id", ("@id", otherBookingId)));
        return new SeededDropInClass(ownBookingId, otherBookingId, paymentId);
    }

    private static Task<int> InsertClassSessionAsync(
        SqlConnection connection, int businessId, int serviceId, int workerId, DateTime startsAt, int maxCapacity, DateTime createdAt)
    {
        Assert.True(startsAt > createdAt, "A seeded class session must start in the future of the host clock.");
        return ScalarIntAsync(connection,
            """
            INSERT INTO Appointments
                        (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, @maxCapacity, NULL, @status, @createdAt)
            """,
            ("@businessId", businessId), ("@kind", (byte)AppointmentKind.ClassSession), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(60)),
            ("@maxCapacity", maxCapacity), ("@status", (byte)AppointmentStatus.Scheduled), ("@createdAt", createdAt));
    }

    private static Task<int> InsertPendingDropInBookingAsync(
        SqlConnection connection, int businessId, int appointmentId, int clientId, DateTime createdAt) =>
        ScalarIntAsync(connection,
            """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @appointmentId, @clientId, NULL, @modality, @status, @createdAt)
            """,
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@modality", (byte)BookingModality.DropIn), ("@status", (byte)BookingStatus.Pending),
            ("@createdAt", createdAt));

    /// <summary>
    /// The shared future business day of every T21 fixture: thirty days ahead, advanced past Sunday because the
    /// seeded demo salon keeps no schedule that day, so the reprogramming checkpoint always has real availability
    /// whatever the machine date is.
    /// </summary>
    private static DateTime FutureBusinessDay(DateTime businessNow) =>
        businessNow.Date.AddDays(30).DayOfWeek == DayOfWeek.Sunday
            ? businessNow.Date.AddDays(31)
            : businessNow.Date.AddDays(30);

    /// <summary>
    /// Plants one own future reserved individual turn of the trusted <paramref name="clientId"/> with the seeded demo
    /// worker and individual service, already confirmed at a known instant, plus one synthetic private appointment
    /// link. It shares the worker and the future business day of the T21.2 foreign turn and the T21.3 class, so the
    /// real availability the reprogramming screen reads already carries their occupied starts.
    /// </summary>
    private static async Task<SeededReschedule> SeedRescheduleTurnAsync(SqlConnection connection, int businessId, int clientId)
    {
        // Business-local now, computed exactly like the host clock, so the planted start stays in the future for the
        // environment the requests run against whatever the machine time zone is.
        var businessNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));

        var serviceId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Services WHERE BusinessId = @businessId AND MaxCapacity = 1 AND IsActive = 1 ORDER BY Id",
            ("@businessId", businessId));
        Assert.True(serviceId > 0, "The seeded demo tenant must have an active individual service for this checkpoint.");
        var workerId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Workers WHERE BusinessId = @businessId AND IsActive = 1 ORDER BY Id",
            ("@businessId", businessId));
        Assert.True(workerId > 0, "The seeded demo tenant must have an active worker for this checkpoint.");
        var durationMinutes = await ScalarIntAsync(connection,
            "SELECT DurationMinutes FROM Services WHERE Id = @serviceId", ("@serviceId", serviceId));
        Assert.True(durationMinutes > 0, "The seeded individual service must have a positive duration.");

        var startsAt = FutureBusinessDay(businessNow).AddHours(9);
        // ConfirmedAt is DATETIME2(0); truncate to whole seconds so the value read back equals the planted one.
        var confirmedAt = businessNow.AddTicks(-(businessNow.Ticks % TimeSpan.TicksPerSecond)).AddMinutes(-10);
        var appointmentId = await ScalarIntAsync(connection,
            """
            INSERT INTO Appointments
                        (BusinessId, Kind, ServiceId, WorkerId, GridClassId, StartsAt, EndsAt, MaxCapacity, Origin, Status, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @kind, @serviceId, @workerId, NULL, @startsAt, @endsAt, 1, @origin, @status, @createdAt)
            """,
            ("@businessId", businessId), ("@kind", (byte)AppointmentKind.Individual), ("@serviceId", serviceId),
            ("@workerId", workerId), ("@startsAt", startsAt), ("@endsAt", startsAt.AddMinutes(durationMinutes)),
            ("@origin", (byte)AppointmentOrigin.Reserved), ("@status", (byte)AppointmentStatus.Scheduled),
            ("@createdAt", businessNow));

        var bookingId = await ScalarIntAsync(connection,
            """
            INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt, ConfirmedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @appointmentId, @clientId, NULL, NULL, @status, @createdAt, @confirmedAt)
            """,
            ("@businessId", businessId), ("@appointmentId", appointmentId), ("@clientId", clientId),
            ("@status", (byte)BookingStatus.Confirmed), ("@createdAt", businessNow), ("@confirmedAt", confirmedAt));

        // A synthetic private link with real 32-byte hash and protected-token material; its expiry follows the start.
        var protectedToken = "synthetic-protected-" + Guid.NewGuid().ToString("N");
        var tokenHash = Enumerable.Range(0, 32).Select(index => (byte)(index + 7)).ToArray();
        var linkId = await ScalarIntAsync(connection,
            """
            INSERT INTO AppointmentLinks (BookingId, TokenHash, ProtectedToken, ExpiresAt, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@bookingId, @tokenHash, @protectedToken, @expiresAt, @createdAt)
            """,
            ("@bookingId", bookingId), ("@tokenHash", tokenHash), ("@protectedToken", protectedToken),
            ("@expiresAt", startsAt.AddHours(24)), ("@createdAt", businessNow));

        // The planted rows are what the assertions rely on: the turn belongs to the trusted ficha and carries a link.
        Assert.Equal(clientId, await ScalarIntAsync(connection, "SELECT ClientId FROM Bookings WHERE Id = @id", ("@id", bookingId)));
        return new SeededReschedule(bookingId, appointmentId, linkId, workerId, durationMinutes,
            startsAt, confirmedAt, protectedToken);
    }

    /// <summary>
    /// The business day the T21.5 reservations are planted on: one week ahead of the business clock, advanced past
    /// Sunday because the seeded salon keeps no schedule that day, so the real availability always has a start to
    /// offer. It sits far enough inside the panel's thirty-day window that the offer always carries the session.
    /// </summary>
    private static DateTime PanelReservationDay(DateTime businessNow) =>
        businessNow.Date.AddDays(7).DayOfWeek == DayOfWeek.Sunday
            ? businessNow.Date.AddDays(8)
            : businessNow.Date.AddDays(7);

    /// <summary>
    /// Plants one synthetic future class session inside the panel window for <paramref name="businessId"/>, reusing
    /// the class service that business already carries instead of inserting another one. The seeded salon only has
    /// capacity-one individual services, so the T21.3 fixture planted the class service this reuses; the second tenant
    /// brings its own. The session sits one week ahead of the business clock because the panel offer only covers today
    /// plus thirty days and the shared +30 day fixture of the earlier checkpoints can fall outside that window.
    /// </summary>
    private static async Task<int> SeedFuturePanelClassSessionAsync(
        SqlConnection connection, int businessId, TimeOnly startTime)
    {
        // Business-local now, computed exactly like the host clock, so the planted start stays in the future.
        var businessNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));

        var serviceId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Services WHERE BusinessId = @businessId AND MaxCapacity > 1 AND IsActive = 1 ORDER BY Id",
            ("@businessId", businessId));
        Assert.True(serviceId > 0, $"Business #{businessId} must have an active class service for this checkpoint.");
        var maxCapacity = await ScalarIntAsync(connection,
            "SELECT MaxCapacity FROM Services WHERE Id = @serviceId", ("@serviceId", serviceId));
        var workerId = await ScalarIntAsync(connection,
            "SELECT TOP (1) Id FROM Workers WHERE BusinessId = @businessId AND IsActive = 1 ORDER BY Id",
            ("@businessId", businessId));
        Assert.True(workerId > 0, $"Business #{businessId} must have an active worker for this checkpoint.");

        return await InsertClassSessionAsync(
            connection, businessId, serviceId, workerId,
            DateOnly.FromDateTime(PanelReservationDay(businessNow)).ToDateTime(startTime), maxCapacity, businessNow);
    }

    private static async Task<BookingStateRow> ReadBookingStateAsync(SqlConnection connection, int bookingId)
    {
        await using var command = CreateCommand(connection,
            "SELECT Status, ConfirmedAt, CancelledAt, CancelledBy FROM Bookings WHERE Id = @bookingId",
            [("@bookingId", bookingId)]);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Seeded booking {bookingId} was not found.");
        return ReadBookingStateRow(reader, bookingId, offset: 0);
    }

    private static async Task<IReadOnlyList<BookingStateRow>> ReadBookingStatesAsync(SqlConnection connection, int businessId)
    {
        await using var command = CreateCommand(connection,
            "SELECT Id, Status, ConfirmedAt, CancelledAt, CancelledBy FROM Bookings WHERE BusinessId = @businessId ORDER BY Id",
            [("@businessId", businessId)]);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<BookingStateRow>();
        while (await reader.ReadAsync())
            rows.Add(ReadBookingStateRow(reader, reader.GetInt32(0), offset: 1));
        return rows;
    }

    private static BookingStateRow ReadBookingStateRow(SqlDataReader reader, int bookingId, int offset) => new(
        bookingId,
        reader.GetByte(offset),
        reader.IsDBNull(offset + 1) ? null : reader.GetDateTime(offset + 1),
        reader.IsDBNull(offset + 2) ? null : reader.GetDateTime(offset + 2),
        reader.IsDBNull(offset + 3) ? null : reader.GetByte(offset + 3));

    private static async Task<AppointmentRangeRow> ReadAppointmentRangeAsync(SqlConnection connection, int appointmentId)
    {
        await using var command = CreateCommand(connection,
            "SELECT Id, WorkerId, StartsAt, EndsAt FROM Appointments WHERE Id = @appointmentId",
            [("@appointmentId", appointmentId)]);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Seeded appointment {appointmentId} was not found.");
        return new AppointmentRangeRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetDateTime(2), reader.GetDateTime(3));
    }

    private static async Task<RescheduleLinkRow> ReadRescheduleLinkAsync(SqlConnection connection, int linkId)
    {
        await using var command = CreateCommand(connection,
            "SELECT Id, TokenHash, ProtectedToken, ExpiresAt, RevokedAt FROM AppointmentLinks WHERE Id = @linkId",
            [("@linkId", linkId)]);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Seeded appointment link {linkId} was not found.");
        return new RescheduleLinkRow(
            reader.GetInt32(0), (byte[])reader.GetValue(1), reader.GetString(2), reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4));
    }

    /// <summary>Compares every byte of a private link by value; a record equality would compare the hash array by reference.</summary>
    private static void AssertLinkUnchanged(RescheduleLinkRow before, RescheduleLinkRow after)
    {
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ProtectedToken, after.ProtectedToken);
        Assert.True(before.TokenHash.AsSpan().SequenceEqual(after.TokenHash),
            "The private appointment-link token hash changed.");
        Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        Assert.Equal(before.RevokedAt, after.RevokedAt);
    }

    private static async Task<IReadOnlyList<PaymentStateRow>> ReadPaymentStatesAsync(SqlConnection connection, int businessId)
    {
        await using var command = CreateCommand(connection,
            "SELECT Id, BookingId, Amount, PaidAt FROM Payments WHERE BusinessId = @businessId ORDER BY Id",
            [("@businessId", businessId)]);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<PaymentStateRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new PaymentStateRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDecimal(2),
                reader.GetDateTime(3)));
        }

        return rows;
    }

    /// <summary>
    /// The persisted reservation the panel just created: its tenant, its appointment, the ficha it belongs to, its
    /// modality, its state and the negotiated price. Reading the row back — instead of assuming what the service
    /// wrote — is what makes the cookie scoping and the pending state of the new turn or drop-in a real check.
    /// </summary>
    private static async Task<CreatedBookingRow> ReadCreatedBookingAsync(SqlConnection connection, int bookingId)
    {
        await using var command = CreateCommand(connection, """
            SELECT Id, BusinessId, AppointmentId, ClientId, Modality, Status, AgreedPrice
              FROM Bookings
             WHERE Id = @bookingId
            """, [("@bookingId", bookingId)]);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Booking {bookingId} was not found.");
        return new CreatedBookingRow(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetByte(4),
            reader.GetByte(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6));
    }

    private static async Task<int> ScalarIntAsync(SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = CreateCommand(connection, sql, args);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = CreateCommand(connection, sql, args);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task<decimal> ScalarDecimalAsync(SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = CreateCommand(connection, sql, args);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, (string Name, object Value)[] args)
    {
        var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    // --- Isolated runtime preflight ------------------------------------------------------------------------

    /// <summary>
    /// Validates both opt-in facts -- the connection string and the isolated keyring path -- before anything connects.
    /// The database name must be a fresh <c>WorkUpDemoT20Http*</c> catalog on the integrated default LocalDB instance
    /// and the keyring must be an absent folder inside the OS temp directory; nothing is read, created or written when
    /// a validation fails.
    /// </summary>
    private HarnessPlan PlanHarness(string suppliedConnection)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The isolated DataProtection keyring requires Windows DPAPI, so this harness refuses to run on this platform. No SQL connection was opened.");

        SqlConnectionStringBuilder input;
        try
        {
            input = new SqlConnectionStringBuilder(suppliedConnection);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException($"{ConnectionVariable} is not a valid SQL connection string. No SQL connection was opened.", ex);
        }

        var databaseName = input.InitialCatalog;
        if (!string.Equals(input.DataSource, Server, StringComparison.OrdinalIgnoreCase)
            || !input.IntegratedSecurity
            || string.IsNullOrEmpty(databaseName) || databaseName.Length > 128
            || !Regex.IsMatch(databaseName, @"\A" + DatabasePrefix + @"[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)
            || !string.IsNullOrEmpty(input.UserID) || !string.IsNullOrEmpty(input.Password)
            || !string.IsNullOrEmpty(input.AttachDBFilename) || input.UserInstance
            || !string.IsNullOrEmpty(input.FailoverPartner))
            throw new InvalidOperationException($"{ConnectionVariable} must use integrated {Server} with a fresh {DatabasePrefix}* database. No SQL connection was opened.");

        var keysPath = ResolveKeysPath();
        var (webHostPath, webRoot) = LocateWebHost();
        var port = FindFreeLoopbackPort();

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        }.ConnectionString;

        return new HarnessPlan(connectionString, databaseName, keysPath, webHostPath, webRoot, port);
    }

    private static string ResolveKeysPath()
    {
        var value = Environment.GetEnvironmentVariable(KeysPathVariable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{KeysPathVariable} is required once {ConnectionVariable} is set. No SQL connection was opened.");
        if (value != value.Trim() || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{KeysPathVariable} must be an absolute path without surrounding whitespace. No SQL connection was opened.");

        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{KeysPathVariable} is not a valid file system path. No SQL connection was opened.", ex);
        }

        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var relative = Path.GetRelativePath(tempRoot, canonical);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"{KeysPathVariable} must resolve inside the OS temp directory ({tempRoot}). No SQL connection was opened.");

        var leaf = Path.GetFileName(canonical);
        if (!leaf.StartsWith(KeysLeafPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"{KeysPathVariable} must end with a directory whose name starts with \"{KeysLeafPrefix}\". No SQL connection was opened.");

        if (Directory.Exists(canonical) || File.Exists(canonical))
            throw new InvalidOperationException($"{KeysPathVariable} must not already exist so no earlier keyring is reused. No SQL connection was opened.");

        EnsureNoReparseAncestors(canonical, tempRoot);
        return canonical;
    }

    private static void EnsureNoReparseAncestors(string canonicalPath, string tempRoot)
    {
        var reachedTempRoot = false;
        for (var current = new DirectoryInfo(canonicalPath); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{KeysPathVariable} has a reparse point ancestor at '{current.FullName}'. No SQL connection was opened.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                reachedTempRoot = true;
                break;
            }
        }

        if (!reachedTempRoot)
            throw new InvalidOperationException($"{KeysPathVariable} must resolve under the OS temp directory. No SQL connection was opened.");
    }

    private static (string WebHostPath, string WebRoot) LocateWebHost()
    {
        // AppContext.BaseDirectory is <repo>/WorkUp/WorkUp.Tests/bin/<Configuration>/net9.0.
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = testOutput.Parent?.Name;
        var solutionDirectory = testOutput.Parent?.Parent?.Parent?.Parent;
        if (configuration is null || solutionDirectory is null)
            throw new InvalidOperationException("Could not derive the repository layout from the test output directory. No SQL connection was opened.");

        var webProjectDirectory = Path.Combine(solutionDirectory.FullName, "WorkUp");
        var webHostPath = Path.Combine(webProjectDirectory, "bin", configuration, "net9.0", "WorkUp.exe");
        if (!File.Exists(webHostPath))
            throw new InvalidOperationException($"The WorkUp web host '{webHostPath}' was not found; build the solution before running this harness. No SQL connection was opened.");

        return (webHostPath, webProjectDirectory);
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("No free loopback TCP port is available for the owned WorkUp host. No SQL connection was opened.", ex);
        }
        finally
        {
            listener.Stop();
        }
    }

    // --- Owned database and host ---------------------------------------------------------------------------

    private async Task CreateDatabaseAsync(HarnessPlan plan)
    {
        var masterConnectionString = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        }.ConnectionString;

        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync();
        await using (var check = new SqlCommand("SELECT DB_ID(@database)", master))
        {
            check.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = plan.DatabaseName;
            var existing = await check.ExecuteScalarAsync();
            if (existing is not null && !Convert.IsDBNull(existing))
                throw new InvalidOperationException($"Database '{plan.DatabaseName}' already exists; this harness never reuses an earlier database and created nothing.");
            output.WriteLine($"SQL: DB_ID('{plan.DatabaseName}') is NULL (absent) on {Server}.");
        }

        // CREATE DATABASE cannot run inside a transaction. SQL Server runs it atomically, so a competing creator makes
        // this statement fail instead of opening or reusing its database. The name passed the strict allowlist above,
        // which excludes brackets and quotes, so quoting the identifier keeps the statement bound to exactly this name.
        await using (var create = new SqlCommand($"CREATE DATABASE [{plan.DatabaseName}]", master))
            await create.ExecuteNonQueryAsync();

        output.WriteLine($"SQL: created isolated database '{plan.DatabaseName}'; it will be retained without teardown.");
    }

    private static async Task SeedDemoAsync(string connectionString, string password)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        var seeder = new DemoDataSeeder(connectionString, new IdentityPasswordHasher(), new BusinessClock(zone));
        var outcome = await seeder.SeedAsync(password);
        if (outcome.CreatedBusinesses < 1)
            throw new InvalidOperationException("The T07 demo seed created no business in the isolated database.");
    }

    private RunningHost StartWebHost(HarnessPlan plan, int port)
    {
        var baseUrl = $"http://127.0.0.1:{port}";
        var startInfo = new ProcessStartInfo(plan.WebHostPath)
        {
            WorkingDirectory = plan.WebRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__WorkUp"] = plan.ConnectionString;
        startInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
        startInfo.Environment["WorkUp__Development__DataProtectionKeysPath"] = plan.KeysPath;
        startInfo.Environment["WorkUp__PublicBaseUrl"] = baseUrl;
        startInfo.Environment.Remove("ASPNETCORE_HTTPS_PORT");
        startInfo.Environment.Remove("WORKUP_DEMO_PASSWORD");
        startInfo.Environment.Remove("WorkUp__DemoData__Enabled");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the owned WorkUp web host process.");
        // Drain both pipes so a chatty child can never block on a full buffer; the lines are discarded, not logged.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (process.Id <= 0)
        {
            TryKill(process);
            throw new InvalidOperationException("The owned WorkUp web host process did not report a process id.");
        }

        return new RunningHost(process, baseUrl);
    }

    private static async Task WaitUntilReadyAsync(RunningHost host)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + ReadinessBudget;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Process.HasExited)
                throw new InvalidOperationException($"The owned WorkUp host (pid {host.Process.Id}) exited with code {host.Process.ExitCode} before becoming ready.");
            try
            {
                using var response = await client.GetAsync("/panel/Account/Login");
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }

            await Task.Delay(ReadinessPoll);
        }

        throw new TimeoutException($"The owned WorkUp host did not answer GET /panel/Account/Login within {ReadinessBudget.TotalSeconds:0} seconds.");
    }

    private static async Task StopWebHostAsync(Process process)
    {
        TryKill(process);
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The process was already reaped; nothing left to wait for.
        }
        process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited on its own between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited while the kill was being delivered.
        }
    }

    private void AssertKeyringCreated(string keysPath)
    {
        Assert.True(Directory.Exists(keysPath), "The isolated DataProtection keyring directory was not created.");
        var files = Directory.GetFiles(keysPath, "key-*.xml", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(keysPath));
        foreach (var file in files)
        {
            var canonicalFile = Path.GetFullPath(file);
            Assert.True(canonicalFile.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Every DataProtection key file must be inside the dedicated temporary keyring folder.");
            Assert.False(File.ReadAllText(file).Contains("<masterKey", StringComparison.Ordinal),
                "Isolated DataProtection keys must be encrypted at rest with DPAPI.");
        }

        output.WriteLine($"DataProtection: {files.Length} encrypted key file(s) generated only inside the dedicated temp keyring folder.");
    }

    // --- Test-only observers -------------------------------------------------------------------------------

    /// <summary>
    /// Writes each diagnostic line once and remembers which values are secret. The flow ends by proving no recorded
    /// line ever carried one of them, so a password, a token or a cookie value can never leak into the test output.
    /// </summary>
    private sealed class FlowRecorder(ITestOutputHelper output)
    {
        private readonly List<string> written = [];
        private readonly HashSet<string> secrets = new(StringComparer.Ordinal);

        public int WrittenCount => written.Count;

        public int SecretCount => secrets.Count;

        public void Note(string line)
        {
            output.WriteLine(line);
            written.Add(line);
        }

        public void KeepSecret(string? secret)
        {
            if (!string.IsNullOrEmpty(secret))
                secrets.Add(secret);
        }

        public void AssertNoSecretLeaked()
        {
            var joined = string.Join('\n', written);
            foreach (var secret in secrets)
                Assert.True(!joined.Contains(secret, StringComparison.Ordinal),
                    "A password, token or cookie value reached the test output; the flow must report only booleans, identifiers and paths.");
        }
    }

    private sealed record HarnessPlan(string ConnectionString, string DatabaseName, string KeysPath,
        string WebHostPath, string WebRoot, int Port);

    private sealed record RunningHost(Process Process, string BaseUrl);

    private sealed record LoginOutcome(HttpStatusCode Status, string Body, string RedirectPath, bool HasClientCookie);

    private sealed record AccountRow(int Id, int? ClientId, string Email, string PasswordHash,
        DateTime? EmailVerifiedAt, string? PendingFirstName, string? PendingLastName);

    private sealed record TokenRow(int Id, byte Purpose, byte[] Hash, DateTime ExpiresAt, DateTime? UsedAt);

    private sealed record NotificationRow(int Id, byte Type, string Recipient, string Subject, string BodyHtml, string DedupKey);

    private sealed record SeededClientTurn(int OwnBookingId, int OtherBookingId);

    /// <summary>
    /// Parsed T27 D3 wiring of a rendered start-time select: the id of its own validation span, the raw aria-invalid
    /// value when the attribute was emitted (null on a clean render) and the decoded text of that span.
    /// </summary>
    private sealed record StartTimeSelectWire(string ContainerId, string? AriaInvalid, string ContainerText);

    private sealed record SeededDropInClass(int OwnBookingId, int OtherBookingId, int PaymentId);

    private sealed record SeededReschedule(int BookingId, int AppointmentId, int LinkId, int WorkerId,
        int DurationMinutes, DateTime OriginalStart, DateTime ConfirmedAt, string ProtectedToken);

    private sealed record AppointmentRangeRow(int Id, int WorkerId, DateTime StartsAt, DateTime EndsAt);

    private sealed record RescheduleLinkRow(int Id, byte[] TokenHash, string ProtectedToken, DateTime ExpiresAt, DateTime? RevokedAt);

    private sealed record BookingStateRow(int Id, byte Status, DateTime? ConfirmedAt, DateTime? CancelledAt, byte? CancelledBy);

    private sealed record CreatedBookingRow(int Id, int BusinessId, int AppointmentId, int ClientId,
        byte? Modality, byte Status, decimal? AgreedPrice);

    private sealed record PaymentStateRow(int Id, int? BookingId, decimal Amount, DateTime PaidAt);
}
