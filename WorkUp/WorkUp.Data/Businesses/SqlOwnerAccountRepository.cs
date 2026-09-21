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

    public Task<int> InsertAsync(OwnerAccount account, CancellationToken ct = default) =>
        InsertAsync(
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

    private static OwnerAccount Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        Username = reader.Get<string>("Username"),
        PasswordHash = reader.Get<string>("PasswordHash"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };
}
