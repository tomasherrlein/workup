using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.ClientAccounts;

/// <summary>
/// ADO.NET persistence for self-registration, email verification and verification resend (RF-25, D-18). Every command
/// runs on the request connection and joins the caller's transaction, so a verification that fails leaves nothing
/// behind. Two invariants of the schema are what the guards rely on:
/// <list type="bullet">
/// <item><c>UQ_Clients_Email</c> and <c>UQ_ClientAccounts_Email</c> are unique per business, so a tenant/email key is
/// never duplicated even under a race;</item>
/// <item><c>UQ_AccountTokens_TokenHash</c> is unique, so one stored hash consumes one link.</item>
/// </list>
/// The guarded inserts read <c>UPDLOCK, HOLDLOCK</c> over those unique keys, so two simultaneous registrations with
/// the same email serialize on the key range instead of both passing a free-email check, and the unique index is the
/// last line of defence when the lock is not enough. Unique-key failures are translated only where the port defines
/// that outcome — a taken email key — never swallowed as a generic error.
/// </summary>
public sealed class SqlClientRegistrationRepository(DbSession session)
    : SqlRepositoryBase(session), IClientRegistrationRepository
{
    private const string ClientColumns = "Id, BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt";

    private const string HasClientEmailSql =
        "SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email";

    private const string HasAccountEmailSql =
        "SELECT COUNT(*) FROM ClientAccounts WHERE BusinessId = @businessId AND Email = @email";

    /// <summary>
    /// Guarded pending-account insert. The business row is taken first <c>UPDLOCK, ROWLOCK</c> as the deterministic
    /// padlock of a per-business email key, so two simultaneous registrations of the same business always read the two
    /// key ranges in the same order and cannot deadlock on them (same convention as the slot padlocks). It then refuses
    /// the row when this business already has a ficha or an account with that email, and both guard reads are
    /// <c>UPDLOCK, HOLDLOCK</c> over the unique email keys, so the key range stays locked until the caller commits or
    /// rolls back. The unique index <c>UQ_ClientAccounts_Email</c> still catches a competing insert that slipped past
    /// the guard read, which is why that specific violation is translated to "0".
    /// </summary>
    private const string InsertPendingAccountSql = """
        SET NOCOUNT ON;

        DECLARE @padlock INT;
        SELECT @padlock = Id FROM Businesses WITH (UPDLOCK, ROWLOCK) WHERE Id = @businessId;

        INSERT INTO ClientAccounts
            (BusinessId, ClientId, Email, PasswordHash, EmailVerifiedAt, CreatedAt, PendingFirstName, PendingLastName)
        OUTPUT INSERTED.Id
        SELECT @businessId, NULL, @email, @passwordHash, NULL, @createdAt, @pendingFirstName, @pendingLastName
        WHERE NOT EXISTS (
                  SELECT 1 FROM Clients WITH (UPDLOCK, HOLDLOCK)
                   WHERE BusinessId = @businessId AND Email = @email)
          AND NOT EXISTS (
                  SELECT 1 FROM ClientAccounts WITH (UPDLOCK, HOLDLOCK)
                   WHERE BusinessId = @businessId AND Email = @email);
        """;

    private const string InsertAccountTokenSql = """
        INSERT INTO AccountTokens (ClientAccountId, Purpose, TokenHash, ExpiresAt, CreatedAt)
        OUTPUT INSERTED.Id
        VALUES (@clientAccountId, @purpose, @tokenHash, @expiresAt, @createdAt);
        """;

    private const string InsertNotificationSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, CreatedAt)
        OUTPUT INSERTED.Id
        VALUES (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, @createdAt);
        """;

    /// <summary>
    /// Consumes one usable verification token and marks it used in the same statement. The tenant, the purpose, the
    /// expiry and the unused state are all part of the match, and the candidate row is read <c>UPDLOCK, ROWLOCK</c>
    /// inside the caller's transaction: a second attempt blocks until the first commits and then finds
    /// <c>UsedAt</c> set, so a replayed, foreign, expired or wrong-purpose hash updates nothing and returns no row.
    /// </summary>
    private const string ConsumeEmailVerificationTokenSql = """
        UPDATE AccountTokens
           SET UsedAt = @now
        OUTPUT INSERTED.ClientAccountId
         WHERE Id = (
                   SELECT TOP (1) token.Id
                     FROM AccountTokens AS token WITH (UPDLOCK, ROWLOCK)
                     JOIN ClientAccounts AS account WITH (UPDLOCK, ROWLOCK)
                       ON account.Id = token.ClientAccountId
                    WHERE token.TokenHash = @tokenHash
                      AND token.Purpose = @purpose
                      AND token.UsedAt IS NULL
                      AND token.ExpiresAt > @now
                      AND account.BusinessId = @businessId
                      AND account.ClientId IS NULL
                      AND account.EmailVerifiedAt IS NULL)
           AND UsedAt IS NULL;
        """;

    private const string SelectPendingAccountSql = """
        SELECT Id, Email, PendingFirstName, PendingLastName
          FROM ClientAccounts
         WHERE Id = @clientAccountId
           AND BusinessId = @businessId
           AND ClientId IS NULL
           AND EmailVerifiedAt IS NULL;
        """;

    private const string SelectPendingAccountByEmailSql = """
        SELECT Id, Email, PendingFirstName, PendingLastName
          FROM ClientAccounts
         WHERE BusinessId = @businessId
           AND Email = @email
           AND ClientId IS NULL
           AND EmailVerifiedAt IS NULL;
        """;

    private const string SelectClientByEmailSql =
        $"SELECT {ClientColumns} FROM Clients WHERE BusinessId = @businessId AND Email = @email";

    /// <summary>
    /// Guarded ficha insert. It refuses the row when the email is already taken inside the business and reads the
    /// guard <c>UPDLOCK, HOLDLOCK</c> over <c>UQ_Clients_Email</c>, so a competing booking that commits first makes
    /// this insert return 0 instead of racing the unique index. The caller treats that 0 as a closed failure, never
    /// as "read the winner and attach": attaching would merge two people's history (RF-25, D-18).
    /// </summary>
    private const string InsertClientSql = """
        INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt)
        OUTPUT INSERTED.Id
        SELECT @businessId, @firstName, @lastName, @email, @phone, @healthNotes, @createdAt
        WHERE NOT EXISTS (
                  SELECT 1 FROM Clients WITH (UPDLOCK, HOLDLOCK)
                   WHERE BusinessId = @businessId AND Email = @email);
        """;

    /// <summary>
    /// Attaches the ficha and the verification instant to a still-pending account of the business and clears the
    /// temporary names in one statement. A linked, verified or foreign account matches no row, so the update reports
    /// false instead of activating the wrong account. <c>UX_ClientAccounts_Client</c> is the last guard against two
    /// accounts sharing one ficha, and its violation is also reported as a closed false.
    /// </summary>
    private const string ActivateVerifiedAccountSql = """
        UPDATE ClientAccounts
           SET ClientId = @clientId,
               EmailVerifiedAt = @verifiedAt,
               PendingFirstName = NULL,
               PendingLastName = NULL
         WHERE Id = @clientAccountId
           AND BusinessId = @businessId
           AND ClientId IS NULL
           AND EmailVerifiedAt IS NULL
           AND EXISTS (
                   SELECT 1 FROM Clients WITH (UPDLOCK, ROWLOCK)
                    WHERE Id = @clientId AND BusinessId = @businessId)
           AND NOT EXISTS (
                   SELECT 1 FROM ClientAccounts AS other WITH (UPDLOCK, ROWLOCK)
                    WHERE other.ClientId = @clientId AND other.Id <> @clientAccountId);
        """;

    /// <summary>
    /// Rotates the pending verification link under a lock. The account row is held <c>UPDLOCK, HOLDLOCK</c> so a
    /// verification racing the resend cannot interleave, every previous unused verification token of the account is
    /// invalidated, and the replacement is inserted before the statement reports success. A verified or foreign
    /// account matches no row and nothing is written. The batch returns 1 only when the rotation happened.
    /// </summary>
    private const string ReplaceVerificationTokenSql = """
        SET NOCOUNT ON;

        DECLARE @rotated INT = 0;

        IF EXISTS (
                SELECT 1
                  FROM ClientAccounts WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
                 WHERE Id = @clientAccountId
                   AND BusinessId = @businessId
                   AND ClientId IS NULL
                   AND EmailVerifiedAt IS NULL)
        BEGIN
            UPDATE AccountTokens
               SET UsedAt = @now
             WHERE ClientAccountId = @clientAccountId
               AND Purpose = @purpose
               AND UsedAt IS NULL;

            INSERT INTO AccountTokens (ClientAccountId, Purpose, TokenHash, ExpiresAt, CreatedAt)
            VALUES (@clientAccountId, @purpose, @tokenHash, @expiresAt, @createdAt);

            SET @rotated = 1;
        END

        SELECT @rotated;
        """;

    public async Task<ClientRegistrationEmailState> GetEmailStateAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default)
    {
        // Both keys are read for the same business: a ficha and an account are equally a collision for RF-25.
        var hasClient = await ScalarAsync<int>(
            HasClientEmailSql,
            p => p.AddValue("@businessId", businessId).AddValue("@email", normalizedEmail),
            ct) > 0;
        var hasAccount = await ScalarAsync<int>(
            HasAccountEmailSql,
            p => p.AddValue("@businessId", businessId).AddValue("@email", normalizedEmail),
            ct) > 0;

        return new ClientRegistrationEmailState(hasClient, hasAccount);
    }

    public async Task<int> InsertPendingAccountAsync(
        ClientAccount account, string pendingFirstName, string pendingLastName, CancellationToken ct = default)
    {
        try
        {
            // ExecuteScalar through ScalarAsync: the guarded insert writes no row when it refuses, and that empty
            // result is the port's "email already taken" answer instead of an error.
            return await ScalarAsync<int>(
                InsertPendingAccountSql,
                p => p
                    .AddValue("@businessId", account.BusinessId)
                    .AddValue("@email", account.Email)
                    .AddValue("@passwordHash", account.PasswordHash)
                    .AddDateTime2("@createdAt", account.CreatedAt)
                    .AddValue("@pendingFirstName", pendingFirstName)
                    .AddValue("@pendingLastName", pendingLastName),
                ct);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_ClientAccounts_Email: a concurrent registration took the same tenant/email key between the guard
            // read and the insert. The caller asked for that key collision to be named, not for a raw failure.
            return 0;
        }
    }

    public async Task<int> InsertAccountTokenAsync(AccountToken token, CancellationToken ct = default)
    {
        try
        {
            var id = await ScalarAsync<int>(
                InsertAccountTokenSql,
                p =>
                {
                    p.AddValue("@clientAccountId", token.ClientAccountId)
                        .AddValue("@purpose", token.Purpose)
                        .AddDateTime2("@expiresAt", token.ExpiresAt)
                        .AddDateTime2("@createdAt", token.CreatedAt);
                    AddTokenHash(p, token.TokenHash);
                },
                ct);

            if (id > 0)
                token.Id = id;
            return id;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_AccountTokens_TokenHash: that exact hash is already stored, so the caller must not claim it queued a
            // second link for it. A random 32-byte collision is not a case the flow can recover by retrying blindly.
            return 0;
        }
    }

    public async Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        try
        {
            var id = await ScalarAsync<int>(
                InsertNotificationSql,
                p => p
                    .AddValue("@businessId", notification.BusinessId)
                    .AddValue("@type", notification.Type)
                    .AddValue("@recipient", notification.Recipient)
                    .AddValue("@subject", notification.Subject)
                    .AddValue("@bodyHtml", notification.BodyHtml)
                    .AddValue("@dedupKey", notification.DedupKey)
                    .AddValue("@status", notification.Status)
                    .AddDateTime2("@createdAt", notification.CreatedAt),
                ct);

            if (id > 0)
                notification.Id = id;
            return id;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_Notifications_DedupKey: a technical retry re-enqueued the same notice, which is a no-op rather than a
            // second email or a failure the caller cannot fix.
            return 0;
        }
    }

    public async Task<PendingClientAccount?> ConsumeEmailVerificationTokenAsync(
        byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default)
    {
        var consumedAccountIds = await QueryAsync(
            ConsumeEmailVerificationTokenSql,
            reader => reader.GetInt32(0),
            p =>
            {
                p.AddValue("@purpose", AccountTokenPurpose.EmailVerification)
                    .AddValue("@businessId", businessId)
                    .AddDateTime2("@now", now);
                AddTokenHash(p, tokenHash);
            },
            ct);

        if (consumedAccountIds.Count == 0)
            return null;

        // The account row was locked by the consume and is still pending inside this transaction, so this read cannot
        // drift before the caller commits or rolls back.
        return await QuerySingleOrDefaultAsync(
            SelectPendingAccountSql,
            MapPendingAccount,
            p => p.AddValue("@clientAccountId", consumedAccountIds[0]).AddValue("@businessId", businessId),
            ct);
    }

    public Task<Client?> GetClientByEmailAsync(int businessId, string normalizedEmail, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            SelectClientByEmailSql,
            MapClient,
            p => p.AddValue("@businessId", businessId).AddValue("@email", normalizedEmail),
            ct);

    public async Task<int> InsertClientAsync(Client client, CancellationToken ct = default)
    {
        try
        {
            var id = await ScalarAsync<int>(
                InsertClientSql,
                p => p
                    .AddValue("@businessId", client.BusinessId)
                    .AddValue("@firstName", client.FirstName)
                    .AddValue("@lastName", client.LastName)
                    .AddValue("@email", client.Email)
                    .AddValue("@phone", client.Phone)
                    .AddValue("@healthNotes", client.HealthNotes)
                    .AddDateTime2("@createdAt", client.CreatedAt),
                ct);

            if (id > 0)
                client.Id = id;
            return id;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_Clients_Email: a public booking created the ficha with that email first. The caller fails closed; it
            // never re-reads and attaches this account to the winner's ficha.
            return 0;
        }
    }

    public async Task<bool> ActivateVerifiedAccountAsync(
        int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default)
    {
        try
        {
            return await ExecuteAsync(
                ActivateVerifiedAccountSql,
                p => p
                    .AddValue("@businessId", businessId)
                    .AddValue("@clientAccountId", clientAccountId)
                    .AddValue("@clientId", clientId)
                    .AddDateTime2("@verifiedAt", verifiedAt),
                ct) == 1;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UX_ClientAccounts_Client: another account already owns that ficha. Attaching a second one is exactly what
            // RF-26 forbids, so the activation is refused like any other no-longer-pending account.
            return false;
        }
    }

    public Task<PendingClientAccount?> GetPendingAccountAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            SelectPendingAccountByEmailSql,
            MapPendingAccount,
            p => p.AddValue("@businessId", businessId).AddValue("@email", normalizedEmail),
            ct);

    public async Task<bool> ReplaceVerificationTokenAsync(
        int businessId, int clientAccountId, AccountToken replacement, CancellationToken ct = default)
    {
        var rotated = await ScalarAsync<int>(
            ReplaceVerificationTokenSql,
            p =>
            {
                p.AddValue("@businessId", businessId)
                    .AddValue("@clientAccountId", clientAccountId)
                    .AddValue("@purpose", replacement.Purpose)
                    .AddDateTime2("@expiresAt", replacement.ExpiresAt)
                    .AddDateTime2("@createdAt", replacement.CreatedAt)
                    // The rotation instant invalidates every previous link of the account.
                    .AddDateTime2("@now", replacement.CreatedAt);
                AddTokenHash(p, replacement.TokenHash);
            },
            ct);

        return rotated == 1;
    }

    /// <summary>
    /// Binds a SHA-256 hash explicitly as <c>BINARY(32)</c> so the parameter matches <c>UQ_AccountTokens_TokenHash</c>
    /// exactly; the raw token never reaches this layer.
    /// </summary>
    private static void AddTokenHash(SqlParameterCollection parameters, byte[] hash) =>
        parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = hash;

    private static PendingClientAccount MapPendingAccount(SqlDataReader reader) => new(
        reader.Get<int>("Id"),
        reader.Get<string>("Email"),
        reader.GetNullableString("PendingFirstName"),
        reader.GetNullableString("PendingLastName"));

    private static Client MapClient(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        FirstName = reader.Get<string>("FirstName"),
        LastName = reader.Get<string>("LastName"),
        Email = reader.Get<string>("Email"),
        Phone = reader.GetNullableString("Phone"),
        HealthNotes = reader.GetNullableString("HealthNotes"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };
}
