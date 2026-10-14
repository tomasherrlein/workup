using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.ClientAccounts;

/// <summary>
/// ADO.NET persistence for the owner-assisted invitation (RF-26). Every command runs on the request connection and
/// joins the caller's transaction, so an issue that fails after the invitation write leaves nothing behind. The
/// invariant of the schema it relies on is <c>UQ_AccountInvitations_TokenHash</c>, so one stored hash is one link.
/// <para>
/// Both write paths share one deterministic lock order that never breaks the T20 order: they take the <c>Clients</c>
/// row of the business <c>UPDLOCK, ROWLOCK, HOLDLOCK</c> as the padlock of one ficha and only then touch
/// <c>AccountInvitations</c>. They never read or lock <c>ClientAccounts</c> inside that transaction, so they never
/// request an account lock while holding a client row; the linked-account read happens before the lock, in its own
/// statement, and the acceptance step of RF-26 fails closed for whatever linkage commits in between.
/// </para>
/// The issue revokes every live invitation of the ficha and inserts the replacement under the same lock, so two
/// simultaneous issues serialize on the Clients row and leave exactly one live invitation. The revoke is idempotent:
/// it marks whatever is still live and reports the ficha as found, so a second call changes nothing new.
/// </summary>
public sealed class SqlClientInvitationRepository(DbSession session)
    : SqlRepositoryBase(session), IClientInvitationRepository
{
    private const string ClientColumns = "Id, BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt";

    private const string SelectClientSql =
        $"SELECT {ClientColumns} FROM Clients WHERE Id = @clientId AND BusinessId = @businessId";

    /// <summary>
    /// Linked-account read of the ficha, scoped to the business. It runs in its own statement, before any client row
    /// is locked, so it never holds an account lock across the Clients row lock (T20 order).
    /// </summary>
    private const string HasLinkedAccountSql = """
        SELECT COUNT(*)
          FROM ClientAccounts
         WHERE BusinessId = @businessId
           AND ClientId = @clientId;
        """;

    /// <summary>
    /// Issues one invitation under the padlock of the ficha. The <c>Clients</c> row is taken
    /// <c>UPDLOCK, ROWLOCK, HOLDLOCK</c> scoped to the business; a missing or foreign ficha matches no row and the
    /// batch returns an empty result, which the caller reads as "nothing written". Only then are the previous live
    /// invitations revoked and the new one inserted, so the revocation and the insert share the lock and no ficha
    /// keeps two live invitations. <c>ClientAccounts</c> is never touched here.
    /// </summary>
    private const string IssueSql = """
        SET NOCOUNT ON;

        DECLARE @lockedClientId INT;
        SELECT @lockedClientId = client.Id
          FROM Clients AS client WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
         WHERE client.Id = @clientId
           AND client.BusinessId = @businessId;

        IF @lockedClientId IS NOT NULL
        BEGIN
            UPDATE AccountInvitations
               SET RevokedAt = @now
             WHERE BusinessId = @businessId
               AND ClientId = @clientId
               AND RevokedAt IS NULL
               AND UsedAt IS NULL;

            INSERT INTO AccountInvitations (BusinessId, ClientId, Email, TokenHash, ExpiresAt, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @clientId, @email, @tokenHash, @expiresAt, @createdAt);
        END
        """;

    /// <summary>
    /// Revokes every live invitation of the ficha under the same padlock as the issue, so a revoke and an issue of the
    /// same ficha cannot interleave. It returns 0 for a missing or foreign ficha and 1 otherwise, which makes the
    /// no-op second revoke a success instead of a failure.
    /// </summary>
    private const string RevokeSql = """
        SET NOCOUNT ON;

        DECLARE @lockedClientId INT;
        SELECT @lockedClientId = client.Id
          FROM Clients AS client WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
         WHERE client.Id = @clientId
           AND client.BusinessId = @businessId;

        IF @lockedClientId IS NULL
        BEGIN
            SELECT 0;
        END
        ELSE
        BEGIN
            UPDATE AccountInvitations
               SET RevokedAt = @now
             WHERE BusinessId = @businessId
               AND ClientId = @clientId
               AND RevokedAt IS NULL
               AND UsedAt IS NULL;

            SELECT 1;
        END
        """;

    private const string InsertNotificationSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, CreatedAt)
        OUTPUT INSERTED.Id
        VALUES (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, @createdAt);
        """;

    public Task<Client?> GetClientAsync(int businessId, int clientId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            SelectClientSql,
            MapClient,
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId),
            ct);

    public async Task<bool> HasLinkedAccountAsync(
        int businessId, int clientId, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            HasLinkedAccountSql,
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId),
            ct) > 0;

    public async Task<int> IssueAsync(AccountInvitation invitation, CancellationToken ct = default)
    {
        try
        {
            return await ScalarAsync<int>(
                IssueSql,
                p => BindInvitation(p, invitation)
                    // The rotation instant invalidates every previous link of the ficha.
                    .AddDateTime2("@now", invitation.CreatedAt),
                ct);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_AccountInvitations_TokenHash: that exact hash is already stored. A random 32-byte collision is not a
            // case the flow can recover by retrying blindly, so the issue reports the generic unavailable outcome.
            return 0;
        }
    }

    public async Task<bool> RevokeAsync(
        int businessId, int clientId, DateTime now, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            RevokeSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@clientId", clientId)
                .AddDateTime2("@now", now),
            ct) == 1;

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

    private static SqlParameterCollection BindInvitation(SqlParameterCollection parameters, AccountInvitation invitation)
    {
        parameters
            .AddValue("@businessId", invitation.BusinessId)
            .AddValue("@clientId", invitation.ClientId)
            .AddValue("@email", invitation.Email)
            .AddDateTime2("@expiresAt", invitation.ExpiresAt)
            .AddDateTime2("@createdAt", invitation.CreatedAt);
        // The hash is bound explicitly as BINARY(32) so it matches UQ_AccountInvitations_TokenHash exactly; the raw
        // token never reaches this layer.
        parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = invitation.TokenHash;
        return parameters;
    }

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
