using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.ClientAccounts;

/// <summary>
/// ADO.NET persistence for the invitation acceptance (RF-26, T22 slice 2). Every command runs on the request
/// connection and joins the caller's transaction, so an acceptance that fails after consuming the link leaves the
/// invitation live again. The invariants it relies on are <c>UQ_AccountInvitations_TokenHash</c> (one stored hash is
/// one link) and the two account keys <c>UQ_ClientAccounts_Email</c> and <c>UX_ClientAccounts_Client</c>.
/// <para>
/// The statements implement one deterministic lock order and never request an account row after the ficha:
/// </para>
/// <list type="bullet">
/// <item>the probe (<see cref="FindInvitationAsync"/>) is a single read-only statement that holds no lock past
/// itself, so the acceptance never holds a token while it waits for the account;</item>
/// <item>both paths take the per-business padlock before account keys, matching T20 registration. The
/// existing-account path then takes the <c>ClientAccounts</c> row of the invited email
/// <c>UPDLOCK, HOLDLOCK</c>, followed by the <c>Clients</c> row and the invitation;</item>
/// <item>the new-account path takes the per-business padlock and the two account key guards (the T20 register order)
/// before the <c>Clients</c> row and the invitation, so the account keys are locked before the ficha and never
/// after it.</item>
/// </list>
/// The guarded inserts and the conditional consume read their key ranges <c>UPDLOCK, HOLDLOCK</c>, so two
/// simultaneous acceptances of one token serialize on the invitation row and exactly one wins. A violation of a unique
/// account key is translated to the port's closed outcome — never a re-read of the winner — so a lost race can never
/// attach the account to a ficha the flow never verified.
/// </summary>
public sealed class SqlClientInvitationAcceptanceRepository(DbSession session)
    : SqlRepositoryBase(session), IClientInvitationAcceptanceRepository
{
    private const string ClientColumns = "Id, BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt";

    /// <summary>
    /// Read-only probe scoped to the business. The unique token hash maps to one invitation; the usable predicates
    /// are only a pre-filter, because the acceptance revalidates them under the lock after this statement has released
    /// its shared locks. A missing, foreign, used, revoked or expired hash returns no row.
    /// </summary>
    private const string FindInvitationSql = """
        SELECT TOP (1) Id, ClientId, Email, ExpiresAt
          FROM AccountInvitations
         WHERE TokenHash = @tokenHash
           AND BusinessId = @businessId
           AND UsedAt IS NULL
           AND RevokedAt IS NULL
           AND ExpiresAt > @now;
        """;

    /// <summary>
    /// Existing-account path, step 1: takes the same business padlock as T20 registration before taking the account
    /// row of the invited email <c>UPDLOCK, HOLDLOCK</c>. This keeps registration's Clients-email guard from being
    /// held while acceptance owns an account lock and waits on the ficha. The invited email selects the account
    /// whose password is validated; it is never used to look up a ficha. A foreign or missing email returns no row.
    /// </summary>
    private const string LockAccountByEmailSql = """
        SET NOCOUNT ON;

        DECLARE @padlock INT;
        SELECT @padlock = Id FROM Businesses WITH (UPDLOCK, ROWLOCK) WHERE Id = @businessId;

        SELECT TOP (1) account.Id,
                       account.PasswordHash,
                       CASE WHEN account.ClientId IS NULL AND account.EmailVerifiedAt IS NULL THEN 1 ELSE 0 END AS IsPending
          FROM ClientAccounts AS account WITH (UPDLOCK, HOLDLOCK)
         WHERE account.BusinessId = @businessId
           AND account.Email = @email;
        """;

    /// <summary>
    /// New-account path, step 1. The business row is taken <c>UPDLOCK, ROWLOCK</c> first as the deterministic padlock
    /// of a per-business account email key, shared with T20 registration and the existing-account acceptance, so
    /// these writers of one business serialize here. It then reads both account key ranges <c>UPDLOCK, HOLDLOCK</c>: the invited email and
    /// the invited ficha. Both ranges stay locked until the caller commits or rolls back, so an account row is never
    /// acquired after the ficha, and the ficha-linked guard closes the documented issuer race by refusing an
    /// invitation whose ficha was linked by acceptance time.
    /// </summary>
    private const string LockAccountKeySql = """
        SET NOCOUNT ON;

        DECLARE @padlock INT;
        SELECT @padlock = Id FROM Businesses WITH (UPDLOCK, ROWLOCK) WHERE Id = @businessId;

        SELECT
            CASE WHEN EXISTS (
                     SELECT 1 FROM ClientAccounts WITH (UPDLOCK, HOLDLOCK)
                      WHERE BusinessId = @businessId AND Email = @email)
                 THEN 1 ELSE 0 END AS EmailTaken,
            CASE WHEN EXISTS (
                     SELECT 1 FROM ClientAccounts WITH (UPDLOCK, HOLDLOCK)
                      WHERE BusinessId = @businessId AND ClientId = @clientId)
                 THEN 1 ELSE 0 END AS ClientLinked;
        """;

    /// <summary>
    /// Locks the ficha of the business. A ficha of another tenant matches no row, so the acceptance reports the same
    /// generic unavailable outcome and never touches it.
    /// </summary>
    private const string LockClientSql = $"""
        SELECT {ClientColumns}
          FROM Clients AS client WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
         WHERE client.Id = @clientId
           AND client.BusinessId = @businessId;
        """;

    /// <summary>
    /// Revalidates the usable state under the lock and consumes the invitation in one conditional update. The
    /// <c>UPDLOCK, ROWLOCK</c> subquery resolves the exact invitation row and the outer predicates re-check
    /// <c>UsedAt</c> after the lock, so a second simultaneous consumer blocks until the first commits and then
    /// updates nothing. The tenant, the unused state, the revocation and the expiry (at the instant the caller read
    /// the clock after the locks) are all part of the match.
    /// </summary>
    private const string ConsumeInvitationSql = """
        UPDATE AccountInvitations
           SET UsedAt = @now
         WHERE Id = (
                   SELECT TOP (1) invitation.Id
                     FROM AccountInvitations AS invitation WITH (UPDLOCK, ROWLOCK)
                    WHERE invitation.Id = @invitationId
                      AND invitation.BusinessId = @businessId
                      AND invitation.UsedAt IS NULL
                      AND invitation.RevokedAt IS NULL
                      AND invitation.ExpiresAt > @now)
           AND UsedAt IS NULL;
        """;

    /// <summary>
    /// Existing-account path, final step: binds the still-pending, still-unlinked account to the ficha and verifies
    /// it, clearing the temporary names in the same statement. The account row is the padlock already held; the ficha
    /// of the same business is required and the linked-ficha key is checked, so a consumed invitation can never bind
    /// a second account to a ficha. <c>UX_ClientAccounts_Client</c> is the last guard and its violation is also a
    /// closed false.
    /// </summary>
    private const string BindPendingAccountSql = """
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
                   SELECT 1 FROM Clients AS client WITH (UPDLOCK, ROWLOCK)
                    WHERE client.Id = @clientId AND client.BusinessId = @businessId)
           AND NOT EXISTS (
                   SELECT 1 FROM ClientAccounts AS other WITH (UPDLOCK, ROWLOCK)
                    WHERE other.ClientId = @clientId AND other.Id <> @clientAccountId);
        """;

    /// <summary>
    /// New-account path, final step: inserts the already-verified account bound to the invited ficha under the
    /// business padlock and both account key guards. The guard ranges were already locked before the ficha, so this
    /// statement re-checks them without acquiring a new account order, and it refuses the row when the invited email
    /// already has an account or the ficha is already linked. The unique indexes remain the last line of defence.
    /// </summary>
    private const string InsertVerifiedAccountSql = """
        SET NOCOUNT ON;

        DECLARE @padlock INT;
        SELECT @padlock = Id FROM Businesses WITH (UPDLOCK, ROWLOCK) WHERE Id = @businessId;

        INSERT INTO ClientAccounts
            (BusinessId, ClientId, Email, PasswordHash, EmailVerifiedAt, CreatedAt)
        OUTPUT INSERTED.Id
        SELECT @businessId, @clientId, @email, @passwordHash, @emailVerifiedAt, @createdAt
        WHERE NOT EXISTS (
                  SELECT 1 FROM ClientAccounts WITH (UPDLOCK, HOLDLOCK)
                   WHERE BusinessId = @businessId AND Email = @email)
          AND NOT EXISTS (
                  SELECT 1 FROM ClientAccounts WITH (UPDLOCK, HOLDLOCK)
                   WHERE BusinessId = @businessId AND ClientId = @clientId);
        """;

    public Task<InvitationAcceptanceTarget?> FindInvitationAsync(
        int businessId, byte[] tokenHash, DateTime now, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            FindInvitationSql,
            reader => new InvitationAcceptanceTarget(
                reader.Get<int>("Id"),
                reader.Get<int>("ClientId"),
                reader.Get<string>("Email"),
                reader.Get<DateTime>("ExpiresAt")),
            p =>
            {
                p.AddValue("@businessId", businessId).AddDateTime2("@now", now);
                AddTokenHash(p, tokenHash);
            },
            ct);

    public Task<InvitationAccountState?> LockAccountByEmailAsync(
        int businessId, string email, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            LockAccountByEmailSql,
            reader => new InvitationAccountState(
                reader.Get<int>("Id"),
                reader.Get<string>("PasswordHash"),
                reader.Get<int>("IsPending") == 1),
            p => p.AddValue("@businessId", businessId).AddValue("@email", email),
            ct);

    public Task<InvitationAccountKeyState> LockAccountKeyAsync(
        int businessId, string email, int clientId, CancellationToken ct = default) =>
        ScalarKeyStateAsync(
            LockAccountKeySql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@email", email)
                .AddValue("@clientId", clientId),
            ct);

    public Task<Client?> LockClientAsync(int businessId, int clientId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            LockClientSql,
            MapClient,
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId),
            ct);

    public async Task<bool> ConsumeInvitationAsync(
        int businessId, int invitationId, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            ConsumeInvitationSql,
            p => p
                .AddValue("@invitationId", invitationId)
                .AddValue("@businessId", businessId)
                .AddDateTime2("@now", now),
            ct) == 1;

    public async Task<bool> BindPendingAccountAsync(
        int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default)
    {
        try
        {
            return await ExecuteAsync(
                BindPendingAccountSql,
                p => p
                    .AddValue("@businessId", businessId)
                    .AddValue("@clientAccountId", clientAccountId)
                    .AddValue("@clientId", clientId)
                    .AddDateTime2("@verifiedAt", verifiedAt),
                ct) == 1;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UX_ClientAccounts_Client: another account already owns that ficha. Binding a second one is exactly what
            // RF-26 forbids, so the bind is refused like any other no-longer-pending account.
            return false;
        }
    }

    public async Task<int> InsertVerifiedAccountAsync(ClientAccount account, CancellationToken ct = default)
    {
        try
        {
            var id = await ScalarAsync<int>(
                InsertVerifiedAccountSql,
                p => p
                    .AddValue("@businessId", account.BusinessId)
                    .AddValue("@clientId", account.ClientId)
                    .AddValue("@email", account.Email)
                    .AddValue("@passwordHash", account.PasswordHash)
                    .AddDateTime2("@emailVerifiedAt", account.EmailVerifiedAt ?? account.CreatedAt)
                    .AddDateTime2("@createdAt", account.CreatedAt),
                ct);

            if (id > 0)
                account.Id = id;
            return id;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_ClientAccounts_Email or UX_ClientAccounts_Client: a competing acceptance took the email key or the
            // ficha between the guard read and the insert. The caller fails closed; it never re-reads and attaches
            // this account to the winner.
            return 0;
        }
    }

    /// <summary>
    /// Reads the two key booleans of the new-account guard from the single result row of the batch. The guard always
    /// produces exactly one row; an unexpected empty result is closed as "both taken" so a shape the code does not
    /// understand can never be read as a free account key.
    /// </summary>
    private async Task<InvitationAccountKeyState> ScalarKeyStateAsync(
        string sql,
        Action<SqlParameterCollection> parameters,
        CancellationToken ct)
    {
        var state = await QuerySingleOrDefaultAsync(
            sql,
            reader => new InvitationAccountKeyState(
                reader.Get<int>("EmailTaken") == 1,
                reader.Get<int>("ClientLinked") == 1),
            parameters,
            ct);
        return state ?? new InvitationAccountKeyState(EmailTaken: true, ClientLinked: true);
    }

    /// <summary>
    /// Binds a SHA-256 hash explicitly as <c>BINARY(32)</c> so the parameter matches
    /// <c>UQ_AccountInvitations_TokenHash</c> exactly; the raw token never reaches this layer.
    /// </summary>
    private static void AddTokenHash(SqlParameterCollection parameters, byte[] hash) =>
        parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = hash;

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
