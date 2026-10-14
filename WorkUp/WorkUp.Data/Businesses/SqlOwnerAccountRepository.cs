using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Businesses;

public sealed class SqlOwnerAccountRepository(DbSession session) : SqlRepositoryBase(session), IOwnerAccountRepository
{
    private const string Columns = "Id, BusinessId, Username, PasswordHash, CreatedAt";

    public Task<OwnerAccount?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync($"SELECT {Columns} FROM OwnerAccounts WHERE Username = @username", Map, p => p.AddValue("@username", username), ct);

    public Task<OwnerAccount?> GetByBusinessIdAsync(int businessId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync($"SELECT {Columns} FROM OwnerAccounts WHERE BusinessId = @businessId", Map, p => p.AddValue("@businessId", businessId), ct);

    public async Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM OwnerAccounts WHERE Username = @username", p => p.AddValue("@username", username), ct) > 0;

    public async Task<OwnerAccountInsertResult> InsertAsync(OwnerAccount account, CancellationToken ct = default)
    {
        try
        {
            var id = await InsertAsync(
                """
                INSERT INTO OwnerAccounts (BusinessId, Username, PasswordHash, CreatedAt)
                OUTPUT INSERTED.Id
                VALUES (@businessId, @username, @passwordHash, @createdAt)
                """,
                p => p
                    .AddValue("@businessId", account.BusinessId)
                    .AddValue("@username", account.Username)
                    .AddValue("@passwordHash", account.PasswordHash)
                    .AddValue("@createdAt", account.CreatedAt),
                ct);
            return OwnerAccountInsertResult.Inserted(id);
        }
        catch (SqlException exception) when (IsUsernameDuplicate(exception))
        {
            // The unique username is the last race guard: losing the check-then-insert window is an expected business
            // outcome, never an unhandled 500.
            return OwnerAccountInsertResult.UsernameTaken();
        }
    }

    /// <summary>
    /// Only <c>UQ_OwnerAccounts_Username</c> is a benign lost race. Any other unique violation — including
    /// <c>UQ_OwnerAccounts_Business</c> — or any other <see cref="SqlException"/> propagates instead of being
    /// swallowed as a username conflict.
    /// </summary>
    private static bool IsUsernameDuplicate(SqlException exception) =>
        exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is 2601 or 2627
                && error.Message.Contains("UQ_OwnerAccounts_Username", StringComparison.Ordinal));

    private static OwnerAccount Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        Username = reader.Get<string>("Username"),
        PasswordHash = reader.Get<string>("PasswordHash"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };
}
