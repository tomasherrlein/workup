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
/// Opt-in HTTP/SQL smoke of the owner-assisted invitation (RF-26, T22) over the real MVC host, the real dependency
/// injection of the composition root and a real SQL Server database. The business layer of T22 is already covered
/// against in-memory ports and against SQL with a service call; what only this harness can prove is the whole wired
/// path: the owner cookie, the owner-only route, the anti-CSRF filter of both POSTs, the real cookie scheme of the
/// client session, the data-protection keyring and the rows the flow leaves behind.
/// <para>
/// One smoke drives one synthetic ficha from the owner panel to the signed-in client agenda: the owner panel of a
/// ficha without an account renders its invitation form, an unconfirmed destination is rejected before the service
/// writes anything, the POST without its anti-CSRF token is refused, the valid POST queues one 48 h invitation whose
/// row keeps only the SHA-256 hash and one <c>AccountInvitation</c> notice built from the configured trusted base URL,
/// the page that opens the emailed link is a pure read with no-store and no-referrer that discloses neither the
/// ficha's stored email nor its health note, the acceptance POST without its anti-CSRF token is refused and spends
/// nothing, the anti-CSRF acceptance creates a verified account bound to the invited ficha with the destination email
/// as its account email and without a second ficha or a session, the replay answers the same generic page, and only
/// then does the password open the client session and the own-business agenda.
/// </para>
/// <para>
/// The fixture is opt-in through the pair <c>WORKUP_T22_HTTP_CONNECTION</c> and <c>WORKUP_T22_HTTP_KEYS_PATH</c>.
/// Without it the test logs N/A and returns before any SQL connection, database, keyring or HTTP host is used, so the
/// default suite is unaffected. With it set, the harness refuses to run unless it can prove the runtime is isolated
/// first: a fresh <c>WorkUpDemoT22Http*</c> database that does not exist yet, an encrypted keyring folder under the
/// OS temp directory whose leaf starts with <c>WorkUpDemoT12Keys</c>, no reparse point ancestor on that path, and a
/// free loopback port. Both opt-in facts are validated before anything connects, the database is created atomically
/// after <c>DB_ID</c> proved it absent, and the database and the encrypted keys are retained -- never reused and never
/// dropped or deleted. This harness does not replace an independent run: until the smoke is executed by a human on a
/// real LocalDB instance, the flow is compiled and N/A only, never proven.
/// </para>
/// </summary>
public sealed class ClientInvitationHttpIntegrationTests(ITestOutputHelper output)
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string ConnectionVariable = "WORKUP_T22_HTTP_CONNECTION";
    private const string KeysPathVariable = "WORKUP_T22_HTTP_KEYS_PATH";
    private const string KeysLeafPrefix = "WorkUpDemoT12Keys";
    private const string DatabasePrefix = "WorkUpDemoT22Http";

    /// <summary>Cookie of the client authentication scheme; the only session this flow can open.</summary>
    private const string ClientCookieName = "workup.client";

    /// <summary>Seeded demo business and owner the smoke logs in with (RF-26 owner panel).</summary>
    private const string DemoSlug = "peluqueria-marina-demo";
    private const string OwnerUsername = "marina.demo";

    private const string InvitationMarker = "/cuenta/invitacion/";
    private const string SpoofedHost = "spoofed.invalid";

    /// <summary>
    /// Owner health note planted on the invited ficha. The public link and the client agenda must never render it, so
    /// the smoke seeds exactly this text and proves it absent from both pages.
    /// </summary>
    private const string HealthNoteMarker = "Observacion de salud sintetica T22";

    private static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Owner_invitation_acceptance_binds_and_signs_in_a_client_account_on_an_isolated_http_host()
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

        // Validate the input and keyring before any SQL connection; DB_ID is checked on master just before creation.
        var plan = PlanHarness(suppliedConnection);
        output.WriteLine($"Runtime: database '{plan.DatabaseName}', isolated keyring '{plan.KeysPath}', next free port {plan.Port}.");

        await CreateDatabaseAsync(plan);

        // The demo password exists only in memory and is never written to output or to any log.
        var demoPassword = "T22-" + Guid.NewGuid().ToString("N") + "!aA1";

        // The single owned host migrates the fresh database, materializes the isolated keyring and serves the flow.
        var host = StartWebHost(plan, plan.Port);
        try
        {
            await WaitUntilReadyAsync(host);
            Assert.False(host.Process.HasExited, "The owned HTTP host must stay alive until the harness stops it.");
            output.WriteLine($"HTTP: owned HTTP host (pid {host.Process.Id}) became ready on {host.BaseUrl}.");
            AssertKeyringCreated(plan.KeysPath);
            await SeedDemoAsync(plan.ConnectionString, demoPassword);
            output.WriteLine("SQL: T07 demo seeded (peluqueria-marina-demo and estudio-yoga-lucia-demo) into the isolated database.");
            await RunInvitationFlowAsync(host.BaseUrl, plan, demoPassword);
        }
        finally
        {
            await StopWebHostAsync(host.Process);
        }

        output.WriteLine("All HTTP/SQL checkpoints passed; the isolated database and encrypted keyring stay in place (no DROP or DELETE).");
    }

    /// <summary>
    /// The real-host smoke of RF-26. Every diagnostic line reports only booleans, identifiers and paths; the raw token,
    /// both passwords, the client cookie and the connection string never reach the test output.
    /// </summary>
    private async Task RunInvitationFlowAsync(string baseUrl, HarnessPlan plan, string demoPassword)
    {
        var hasher = new IdentityPasswordHasher();
        var invitedPassword = "T22i-" + Guid.NewGuid().ToString("N") + "!aA1";
        var suffix = Guid.NewGuid().ToString("N");
        var fichaEmail = $"t22ficha{suffix}@example.invalid";
        var destinationEmail = $"t22nueva{suffix}@example.invalid";
        var trustedAuthority = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);

        await using var connection = new SqlConnection(plan.ConnectionString);
        await connection.OpenAsync();

        var businessId = await ScalarIntAsync(connection, "SELECT Id FROM Businesses WHERE Slug = @slug", ("@slug", DemoSlug));

        // One synthetic ficha with no account is planted straight into the fresh database. The health note is what
        // proves, by absence, that no public page ever renders an owner observation.
        var fichaId = await InsertIdAsync(connection, """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, HealthNotes, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @firstName, @lastName, @email, @healthNotes, @createdAt)
            """,
            ("@businessId", businessId), ("@firstName", "Ana"), ("@lastName", "T22"), ("@email", fichaEmail),
            ("@healthNotes", HealthNoteMarker), ("@createdAt", DateTime.UtcNow));
        var seededClientCount = await CountClientsAsync(connection, businessId);
        Assert.Equal(1, seededClientCount);
        output.WriteLine($"SQL: seeded ficha #{fichaId} without an account into business #{businessId}.");

        // --- Owner issues the invitation (RF-26) -----------------------------------------------------------
        var ownerJar = new CookieContainer();
        using var owner = CreateBrowser(baseUrl, ownerJar);

        var (loginStatus, loginHtml) = await GetHtmlAsync(owner, "/panel/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        using (var login = await owner.PostAsync("/panel/Account/Login", Form(new Dictionary<string, string>
               {
                   ["Username"] = OwnerUsername,
                   ["Password"] = demoPassword,
                   ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginHtml)
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.Equal("/panel", DescribeRedirect(login));
        }

        var (detailStatus, detailHtml) = await GetHtmlAsync(owner, $"/panel/Clients/Detail/{fichaId}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Contains("Sin cuenta", detailHtml, StringComparison.Ordinal);
        Assert.Contains($"/panel/Clients/Invite/{fichaId}", detailHtml, StringComparison.Ordinal);
        var ownerAntiforgery = ExtractAntiforgeryToken(detailHtml);
        output.WriteLine("HTTP: owner login accepted with anti-CSRF and the ficha detail rendered its invitation form.");

        using (var anonymous = CreateBrowser(baseUrl, new CookieContainer()))
        using (var foreignInvite = await anonymous.PostAsync($"/panel/Clients/Invite/{fichaId}", Form(new Dictionary<string, string>
               {
                   ["Email"] = destinationEmail,
                   ["EmailConfirmed"] = "true"
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, foreignInvite.StatusCode);
            Assert.Equal("/panel/Account/Login", DescribeRedirect(foreignInvite));
        }
        Assert.Equal(0, await CountInvitationsAsync(connection, businessId, fichaId));
        output.WriteLine("HTTP/SQL: an anonymous invite attempt was redirected to owner login and wrote no invitation.");

        // An unchecked confirmation is judged before the service starts a transaction: nothing is stored.
        using (var uncheckedInvite = await owner.PostAsync($"/panel/Clients/Invite/{fichaId}", Form(new Dictionary<string, string>
               {
                   ["Email"] = destinationEmail,
                   ["__RequestVerificationToken"] = ownerAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, uncheckedInvite.StatusCode);
            Assert.Equal($"/panel/Clients/Detail/{fichaId}", DescribeRedirect(uncheckedInvite));
        }
        Assert.Equal(0, await CountInvitationsAsync(connection, businessId, fichaId));
        Assert.Equal(0, await CountNotificationsAsync(connection, businessId, NotificationType.AccountInvitation, destinationEmail));
        output.WriteLine("HTTP/SQL: the invite POST without the confirmation checkbox redirected to the ficha and wrote no invitation.");

        // Without its anti-CSRF token the invite POST is refused before any business logic runs.
        using (var noTokenInvite = await owner.PostAsync($"/panel/Clients/Invite/{fichaId}", Form(new Dictionary<string, string>
               {
                   ["Email"] = destinationEmail,
                   ["EmailConfirmed"] = "true"
               })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenInvite.StatusCode);
        }
        Assert.Equal(0, await CountInvitationsAsync(connection, businessId, fichaId));
        output.WriteLine("HTTP/SQL: the invite POST without an anti-CSRF token -> 400 and wrote no invitation.");

        // A forged Host may not retarget the email. The configured public base URL remains the real loopback
        // authority, so this check distinguishes configuration from a request-derived link.
        using (var inviteRequest = new HttpRequestMessage(HttpMethod.Post, $"/panel/Clients/Invite/{fichaId}")
               {
                   Content = Form(new Dictionary<string, string>
                   {
                       ["Email"] = destinationEmail,
                       ["EmailConfirmed"] = "true",
                       ["__RequestVerificationToken"] = ownerAntiforgery
                   })
               })
        {
            inviteRequest.Headers.Host = SpoofedHost;
            using var invite = await owner.SendAsync(inviteRequest);
            Assert.Equal(HttpStatusCode.Redirect, invite.StatusCode);
            Assert.Equal($"/panel/Clients/Detail/{fichaId}", DescribeRedirect(invite));
        }

        var invitation = await ReadInvitationAsync(connection, businessId, fichaId);
        Assert.NotNull(invitation);
        // The destination the owner confirmed -- not the one stored on the ficha -- is the email the invitation proves.
        Assert.Equal(destinationEmail, invitation!.Email);
        Assert.Equal(TimeSpan.FromHours(48), invitation.ExpiresAt - invitation.CreatedAt);
        Assert.Null(invitation.UsedAt);
        Assert.Null(invitation.RevokedAt);
        Assert.Equal(32, invitation.TokenHash.Length);
        var notice = await ReadNotificationAsync(connection, businessId, NotificationType.AccountInvitation, destinationEmail);
        Assert.NotNull(notice);
        Assert.Equal($"invite:{invitation.Id}", notice!.DedupKey);
        var invitationLink = LinkFrom(notice.BodyHtml);
        var invitationToken = TokenFrom(notice.BodyHtml, InvitationMarker);
        Assert.True(invitationLink.StartsWith(trustedAuthority + "/", StringComparison.Ordinal),
            "The invitation email must link to the configured trusted base URL despite a forged request Host.");
        Assert.False(invitationLink.Contains(SpoofedHost, StringComparison.Ordinal),
            "The forged request Host must never appear in the queued invitation link.");
        Assert.True(SecureToken.Hash(invitationToken).SequenceEqual(invitation.TokenHash),
            "AccountInvitations must keep the SHA-256 hash of the emailed token, never the token itself.");
        output.WriteLine($"SQL: one 48 h invitation #{invitation.Id} (hash only) and one queued AccountInvitation notice built from the trusted base URL.");

        // --- Public link and new-account acceptance (RF-26) ------------------------------------------------
        var publicJar = new CookieContainer();
        using var visitor = CreateBrowser(baseUrl, publicJar);

        // Opening the emailed URL is a pure read: a scanner that follows it must not burn the single use, and the page
        // must disclose neither the ficha's stored email nor an owner note.
        var (linkStatus, linkHtml, linkNoStore, linkReferrer) =
            await GetTokenPageAsync(visitor, new Uri(invitationLink).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, linkStatus);
        Assert.Contains("account-invitation-title", linkHtml, StringComparison.Ordinal);
        Assert.Contains(destinationEmail, linkHtml, StringComparison.Ordinal);
        Assert.True(linkNoStore, "The invitation page must send the no-store Cache-Control directive.");
        Assert.Equal("no-referrer", linkReferrer);
        Assert.True(!linkHtml.Contains(fichaEmail, StringComparison.Ordinal),
            "The invitation page must never render the ficha's stored email.");
        Assert.True(!linkHtml.Contains(HealthNoteMarker, StringComparison.Ordinal),
            "The invitation page must never render an owner health note.");
        Assert.Null((await ReadInvitationAsync(connection, businessId, fichaId))!.UsedAt);
        var linkAntiforgery = ExtractAntiforgeryToken(linkHtml);
        output.WriteLine("HTTP/SQL: GET of the invitation link -> 200 with no-store/no-referrer, no ficha email or note, and the token stayed unused.");

        // Without its anti-CSRF token the acceptance POST is refused and the link stays usable.
        using (var noTokenAccept = await visitor.PostAsync($"/r/{DemoSlug}/cuenta/invitacion", Form(new Dictionary<string, string>
               {
                   ["Token"] = invitationToken,
                   ["Mode"] = "nueva",
                   ["Password"] = invitedPassword,
                   ["ConfirmPassword"] = invitedPassword
               })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noTokenAccept.StatusCode);
        }
        Assert.Null((await ReadInvitationAsync(connection, businessId, fichaId))!.UsedAt);
        output.WriteLine("HTTP/SQL: the acceptance POST without an anti-CSRF token -> 400 and the token stayed unused.");

        using (var accept = await visitor.PostAsync($"/r/{DemoSlug}/cuenta/invitacion", Form(new Dictionary<string, string>
               {
                   ["Token"] = invitationToken,
                   ["Mode"] = "nueva",
                   ["Password"] = invitedPassword,
                   ["ConfirmPassword"] = invitedPassword,
                   ["__RequestVerificationToken"] = linkAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, accept.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta/ingresar", DescribeRedirect(accept));
        }
        // Accepting never opens a session: the person signs in afterwards with the password of the flow.
        Assert.False(HasClientCookie(publicJar), "Accepting an invitation must not issue the client scheme cookie.");

        var account = await ReadAccountAsync(connection, businessId, destinationEmail);
        Assert.NotNull(account);
        Assert.Equal(fichaId, account!.ClientId);
        Assert.Equal(destinationEmail, account.Email);
        Assert.NotNull(account.EmailVerifiedAt);
        Assert.True(hasher.Verify(account.PasswordHash, invitedPassword),
            "The invited account must keep the hash of the password chosen in the flow, never the plaintext.");
        Assert.False(account.PasswordHash.Contains(invitedPassword, StringComparison.Ordinal),
            "The account column must never keep the plaintext password.");
        // No ficha is created: the account binds to the ficha the invitation names.
        Assert.Equal(seededClientCount, await CountClientsAsync(connection, businessId));
        Assert.NotNull((await ReadInvitationAsync(connection, businessId, fichaId))!.UsedAt);
        output.WriteLine($"SQL: the anti-CSRF acceptance created verified account #{account.Id} bound to ficha #{fichaId} with the destination email and no second ficha.");

        // The single use is spent by the POST: a replay must answer generically and write nothing at all.
        using (var replay = await visitor.PostAsync($"/r/{DemoSlug}/cuenta/invitacion", Form(new Dictionary<string, string>
               {
                   ["Token"] = invitationToken,
                   ["Mode"] = "nueva",
                   ["Password"] = invitedPassword,
                   ["ConfirmPassword"] = invitedPassword,
                   ["__RequestVerificationToken"] = linkAntiforgery
               })))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayHtml = await replay.Content.ReadAsStringAsync();
            Assert.Contains("account-status-title", replayHtml, StringComparison.Ordinal);
            Assert.Contains("El enlace no est", replayHtml, StringComparison.Ordinal);
        }
        Assert.Equal(seededClientCount, await CountClientsAsync(connection, businessId));
        Assert.NotNull(await ReadAccountAsync(connection, businessId, destinationEmail));
        output.WriteLine("HTTP/SQL: replay of the spent invitation -> 200 generic page, no second account and no new ficha.");

        // --- Sign-in and own-business panel (RF-15) --------------------------------------------------------
        var (signInPageStatus, signInPageHtml) = await GetHtmlAsync(visitor, $"/r/{DemoSlug}/cuenta/ingresar");
        Assert.Equal(HttpStatusCode.OK, signInPageStatus);
        using (var signIn = await visitor.PostAsync($"/r/{DemoSlug}/cuenta/ingresar", Form(new Dictionary<string, string>
               {
                   ["Email"] = destinationEmail,
                   ["Password"] = invitedPassword,
                   ["ReturnUrl"] = "",
                   ["__RequestVerificationToken"] = ExtractAntiforgeryToken(signInPageHtml)
               })))
        {
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.Equal($"/r/{DemoSlug}/cuenta", DescribeRedirect(signIn));
        }
        Assert.True(HasClientCookie(publicJar), "A successful sign-in must issue the client scheme cookie.");
        var (panelStatus, panelHtml) = await GetHtmlAsync(visitor, $"/r/{DemoSlug}/mis-turnos");
        Assert.Equal(HttpStatusCode.OK, panelStatus);
        Assert.Contains("client-appointments-title", panelHtml, StringComparison.Ordinal);
        Assert.True(!panelHtml.Contains(HealthNoteMarker, StringComparison.Ordinal),
            "The client panel must never render the ficha's owner health note.");
        output.WriteLine("HTTP: sign-in with the chosen password issued the client cookie and rendered the own-business agenda.");
    }

    // --- HTTP helpers --------------------------------------------------------------------------------------

    private static HttpClient CreateBrowser(string baseUrl, CookieContainer jar)
    {
        var handler = new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetHtmlAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>GET of a page that receives a token; the response headers are read before the body is consumed.</summary>
    private static async Task<(HttpStatusCode Status, string Body, bool NoStore, string? ReferrerPolicy)> GetTokenPageAsync(
        HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        var referrerPolicy = response.Headers.TryGetValues("Referrer-Policy", out var values)
            ? string.Join(",", values)
            : null;
        return (response.StatusCode, body, response.Headers.CacheControl?.NoStore == true, referrerPolicy);
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    private static bool HasClientCookie(CookieContainer jar) =>
        jar.GetAllCookies().Any(cookie => string.Equals(cookie.Name, ClientCookieName, StringComparison.Ordinal));

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

    private static async Task<InvitationRow?> ReadInvitationAsync(SqlConnection connection, int businessId, int clientId)
    {
        await using var command = CreateCommand(connection, """
            SELECT TOP 1 Id, ClientId, Email, TokenHash, ExpiresAt, UsedAt, RevokedAt, CreatedAt
              FROM AccountInvitations
             WHERE BusinessId = @businessId AND ClientId = @clientId
             ORDER BY Id DESC
            """, [("@businessId", businessId), ("@clientId", clientId)]);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return new InvitationRow(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            (byte[])reader.GetValue(3),
            reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            reader.GetDateTime(7));
    }

    private static async Task<AccountRow?> ReadAccountAsync(SqlConnection connection, int businessId, string email)
    {
        await using var command = CreateCommand(connection, """
            SELECT Id, ClientId, Email, PasswordHash, EmailVerifiedAt
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
            reader.IsDBNull(4) ? null : reader.GetDateTime(4));
    }

    private static async Task<NotificationRow?> ReadNotificationAsync(
        SqlConnection connection, int businessId, NotificationType type, string recipient)
    {
        await using var command = CreateCommand(connection, """
            SELECT TOP 1 Id, Type, Recipient, BodyHtml, DedupKey
              FROM Notifications
             WHERE BusinessId = @businessId AND Type = @type AND Recipient = @recipient
             ORDER BY Id DESC
            """, [("@businessId", businessId), ("@type", (byte)type), ("@recipient", recipient)]);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return new NotificationRow(
            reader.GetInt32(0),
            reader.GetByte(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    private static Task<int> CountClientsAsync(SqlConnection connection, int businessId) =>
        ScalarIntAsync(connection, "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId", ("@businessId", businessId));

    private static Task<int> CountInvitationsAsync(SqlConnection connection, int businessId, int clientId) =>
        ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM AccountInvitations WHERE BusinessId = @businessId AND ClientId = @clientId",
            ("@businessId", businessId), ("@clientId", clientId));

    private static Task<int> CountNotificationsAsync(
        SqlConnection connection, int businessId, NotificationType type, string recipient) =>
        ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM Notifications WHERE BusinessId = @businessId AND Type = @type AND Recipient = @recipient",
            ("@businessId", businessId), ("@type", (byte)type), ("@recipient", recipient));

    private static async Task<int> ScalarIntAsync(SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = CreateCommand(connection, sql, args);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertIdAsync(SqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = CreateCommand(connection, sql, args);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, (string Name, object Value)[] args)
    {
        var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    // --- Isolation harness ---------------------------------------------------------------------------------

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

        // CREATE DATABASE cannot run inside a transaction. SQL Server runs it atomically, so a competing
        // creator makes this statement fail instead of opening or reusing its database.
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

    private sealed record HarnessPlan(string ConnectionString, string DatabaseName, string KeysPath,
        string WebHostPath, string WebRoot, int Port);

    private sealed record RunningHost(Process Process, string BaseUrl);

    private sealed record InvitationRow(int Id, int ClientId, string Email, byte[] TokenHash,
        DateTime ExpiresAt, DateTime? UsedAt, DateTime? RevokedAt, DateTime CreatedAt);

    private sealed record AccountRow(int Id, int? ClientId, string Email, string PasswordHash, DateTime? EmailVerifiedAt);

    private sealed record NotificationRow(int Id, byte Type, string Recipient, string BodyHtml, string DedupKey);
}
