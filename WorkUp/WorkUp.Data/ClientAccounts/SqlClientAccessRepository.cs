using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.ClientAccounts;

/// <summary>
/// ADO.NET persistence for client sign-in and password recovery (RF-15, RF-27). Every command runs on the request
/// connection and joins the caller's transaction, so a completion that fails after consuming a link leaves no trace
/// behind. Two invariants of the schema are what the guards rely on:
/// <list type="bullet">
/// <item><c>UQ_ClientAccounts_Email</c> is unique per business, so a tenant/email key is never duplicated;</item>
/// <item><c>UQ_AccountTokens_TokenHash</c> is unique, so one stored hash consumes one link.</item>
/// </list>
/// An active account is one whose email is verified <em>and</em> whose ficha is linked, so a lookup joins
/// <c>ClientAccounts</c> and <c>Clients</c> on <em>both</em> the account id and the same <c>BusinessId</c>: a
/// verified account of one tenant can never be answered — nor changed — through another tenant, and a corrupt row
/// pointing at a ficha of a different business is invisible instead of leaking that person's history.
/// <para>
/// Both write paths share one deterministic lock order: the <c>ClientAccounts</c> row is taken
/// <c>UPDLOCK, ROWLOCK, HOLDLOCK</c> as the per-account padlock by primary key, the linked ficha of the same business
/// is checked next, and only then is <c>AccountTokens</c> touched. The rotation invalidates the previous unused links
/// and inserts the replacement under that padlock, so two simultaneous requests can never leave two live links or a
/// link that was never delivered. The consume first resolves the owning account from the unique token hash in a
/// read-only statement that holds no lock, then takes the same padlock, re-checks the token purpose, expiry and unused
/// state under it, and marks that single usable row used inside the caller's transaction. Because the token read of the
/// consume never survives its own statement, neither path can hold a token while waiting for the account, so the two
/// can no longer request the account and the token in opposite order. This is a lock <em>order</em> guarantee, not a
/// proof that no interleaving can ever deadlock.
/// </para>
/// The password write is guarded by the same active-account predicates and runs inside the caller's transaction, so a
/// completion that fails after consuming a link leaves nothing behind.
/// </summary>
public sealed class SqlClientAccessRepository(DbSession session)
    : SqlRepositoryBase(session), IClientAccessRepository
{
    /// <summary>
    /// Active account of the business with that email. The join requires the ficha to belong to the same business, so
    /// the answer never crosses the tenant boundary, and the verified/linked filters make a pending or unlinked
    /// account indistinguishable from a missing one.
    /// </summary>
    private const string SelectActiveAccountSql = """
        SELECT account.Id, account.ClientId, account.Email, account.PasswordHash
          FROM ClientAccounts AS account
          JOIN Clients AS client
            ON client.Id = account.ClientId
           AND client.BusinessId = account.BusinessId
         WHERE account.BusinessId = @businessId
           AND account.Email = @email
           AND account.ClientId IS NOT NULL
           AND account.EmailVerifiedAt IS NOT NULL;
        """;

    /// <summary>
    /// Rotates the recovery link under the per-account padlock. The <c>ClientAccounts</c> row is held
    /// <c>UPDLOCK, ROWLOCK, HOLDLOCK</c> as the deterministic lock of one account — the same first step the consume
    /// takes — and the linked ficha of the same business is checked before any token is touched, so a second request
    /// of the same account blocks until the first commits and then invalidates what the first left live. The batch
    /// returns 1 only when a previous unused link was invalidated and the replacement inserted, and 0 when the account
    /// is not active in this business (pending, unlinked or foreign), in which case nothing is written.
    /// </summary>
    private const string ReplacePasswordResetTokenSql = """
        SET NOCOUNT ON;

        DECLARE @rotated INT = 0;
        DECLARE @linkedClientId INT;

        -- Step 1: padlock of the account row by primary key, shared with the consume path.
        SELECT @linkedClientId = account.ClientId
          FROM ClientAccounts AS account WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
         WHERE account.Id = @clientAccountId
           AND account.BusinessId = @businessId
           AND account.ClientId IS NOT NULL
           AND account.EmailVerifiedAt IS NOT NULL;

        -- Step 2: the ficha must belong to the same business; only then is the token table touched.
        IF @linkedClientId IS NOT NULL
           AND EXISTS (
                   SELECT 1
                     FROM Clients AS client WITH (UPDLOCK, ROWLOCK)
                    WHERE client.Id = @linkedClientId
                      AND client.BusinessId = @businessId)
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

    private const string InsertNotificationSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, CreatedAt)
        OUTPUT INSERTED.Id
        VALUES (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, @createdAt);
        """;

    /// <summary>
    /// Consumes one usable recovery token and marks it used. The unique hash resolves the owning account in a
    /// read-only statement that keeps no lock past itself, so this path never holds a token while it waits for the
    /// account; it then takes the same account padlock as the rotation, re-checks the linked ficha of the same
    /// business, and only afterwards re-checks the purpose, the expiry and the unused state of that exact token. The
    /// second of two simultaneous completions blocks on the padlock until the first commits and then finds
    /// <c>UsedAt</c> set, so a replayed, expired, foreign, wrong-purpose, pending or unlinked hash updates nothing and
    /// returns no row.
    /// </summary>
    private const string ConsumePasswordResetTokenSql = """
        SET NOCOUNT ON;

        DECLARE @clientAccountId INT;
        DECLARE @linkedClientId INT;

        -- Step 1: read-only probe. The unique hash maps to one token, and the shared lock of this statement is
        -- released at its end, so no token lock is held across the padlock request below.
        SELECT @clientAccountId = token.ClientAccountId
          FROM AccountTokens AS token
         WHERE token.TokenHash = @tokenHash;

        IF @clientAccountId IS NOT NULL
        BEGIN
            -- Step 2: the same account padlock the rotation takes, before any token is touched.
            SELECT @linkedClientId = account.ClientId
              FROM ClientAccounts AS account WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
             WHERE account.Id = @clientAccountId
               AND account.BusinessId = @businessId
               AND account.ClientId IS NOT NULL
               AND account.EmailVerifiedAt IS NOT NULL;

            -- Step 3: under the padlock, the tenant, purpose, expiry and unused state of that exact token are
            -- re-checked and the single usable row is marked used.
            IF @linkedClientId IS NOT NULL
               AND EXISTS (
                       SELECT 1
                         FROM Clients AS client WITH (UPDLOCK, ROWLOCK)
                        WHERE client.Id = @linkedClientId
                          AND client.BusinessId = @businessId)
            BEGIN
                UPDATE AccountTokens
                   SET UsedAt = @now
                OUTPUT INSERTED.ClientAccountId
                 WHERE Id = (
                           SELECT TOP (1) token.Id
                             FROM AccountTokens AS token
                            WHERE token.ClientAccountId = @clientAccountId
                              AND token.TokenHash = @tokenHash
                              AND token.Purpose = @purpose
                              AND token.UsedAt IS NULL
                              AND token.ExpiresAt > @now)
                   AND UsedAt IS NULL;
            END
        END
        """;

    /// <summary>
    /// Guarded password write. Only an account of this business, linked and verified, and whose ficha belongs to the
    /// same business matches, so a consumed token can never write through a deactivated, pending, unlinked or foreign
    /// account. The update reports the row count, and the caller treats anything but exactly one row as a failure that
    /// rolls its transaction back.
    /// </summary>
    private const string UpdatePasswordHashSql = """
        UPDATE account
           SET account.PasswordHash = @passwordHash
          FROM ClientAccounts AS account
         WHERE account.Id = @clientAccountId
           AND account.BusinessId = @businessId
           AND account.ClientId IS NOT NULL
           AND account.EmailVerifiedAt IS NOT NULL
           AND EXISTS (
                   SELECT 1
                     FROM Clients AS client
                    WHERE client.Id = account.ClientId
                      AND client.BusinessId = @businessId);
        """;

    public Task<ActiveClientAccount?> GetActiveAccountByEmailAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            SelectActiveAccountSql,
            reader => new ActiveClientAccount(
                reader.Get<int>("Id"),
                reader.Get<int>("ClientId"),
                reader.Get<string>("Email"),
                reader.Get<string>("PasswordHash")),
            p => p.AddValue("@businessId", businessId).AddValue("@email", normalizedEmail),
            ct);

    public async Task<bool> ReplacePasswordResetTokenAsync(
        int businessId, int clientAccountId, AccountToken replacement, CancellationToken ct = default)
    {
        var rotated = await ScalarAsync<int>(
            ReplacePasswordResetTokenSql,
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

    public async Task<int?> ConsumePasswordResetTokenAsync(
        byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default)
    {
        var consumedAccountIds = await QueryAsync(
            ConsumePasswordResetTokenSql,
            reader => reader.GetInt32(0),
            p =>
            {
                p.AddValue("@purpose", AccountTokenPurpose.PasswordReset)
                    .AddValue("@businessId", businessId)
                    .AddDateTime2("@now", now);
                AddTokenHash(p, tokenHash);
            },
            ct);

        return consumedAccountIds.Count == 0 ? null : consumedAccountIds[0];
    }

    public async Task<bool> UpdatePasswordHashAsync(
        int businessId, int clientAccountId, string passwordHash, CancellationToken ct = default) =>
        await ExecuteAsync(
            UpdatePasswordHashSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@clientAccountId", clientAccountId)
                .AddValue("@passwordHash", passwordHash),
            ct) == 1;

    /// <summary>
    /// Binds a SHA-256 hash explicitly as <c>BINARY(32)</c> so the parameter matches <c>UQ_AccountTokens_TokenHash</c>
    /// exactly; the raw token never reaches this layer.
    /// </summary>
    private static void AddTokenHash(SqlParameterCollection parameters, byte[] hash) =>
        parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = hash;
}
