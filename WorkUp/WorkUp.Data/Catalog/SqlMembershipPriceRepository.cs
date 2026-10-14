using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Catalog;

public sealed class SqlMembershipPriceRepository(DbSession session) : SqlRepositoryBase(session), IMembershipPriceRepository
{
    private const string Columns = "Id, BusinessId, WeeklyFrequency, Price";

    public Task<List<MembershipPrice>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        QueryAsync(
            $"SELECT {Columns} FROM MembershipPrices WHERE BusinessId = @businessId ORDER BY WeeklyFrequency",
            Map,
            p => p.AddValue("@businessId", businessId),
            ct);

    public Task<MembershipPrice?> GetByFrequencyAsync(int businessId, int weeklyFrequency, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            $"SELECT {Columns} FROM MembershipPrices WHERE BusinessId = @businessId AND WeeklyFrequency = @weeklyFrequency",
            Map,
            p => p.AddValue("@businessId", businessId).AddValue("@weeklyFrequency", weeklyFrequency),
            ct);

    public async Task<MembershipPriceInsertResult> InsertAsync(MembershipPrice price, CancellationToken ct = default)
    {
        try
        {
            var id = await InsertAsync(
                """
                INSERT INTO MembershipPrices (BusinessId, WeeklyFrequency, Price)
                OUTPUT INSERTED.Id
                VALUES (@businessId, @weeklyFrequency, @price)
                """,
                p => p
                    .AddValue("@businessId", price.BusinessId)
                    .AddValue("@weeklyFrequency", price.WeeklyFrequency)
                    .AddValue("@price", price.Price),
                ct);
            return MembershipPriceInsertResult.Inserted(id);
        }
        catch (SqlException exception) when (IsFrequencyDuplicate(exception))
        {
            // The per-tenant unique frequency is the last race guard: losing the check-then-insert window is an expected
            // business outcome, never an unhandled 500 and never a second row. The caller re-reads the winner's row.
            return MembershipPriceInsertResult.AlreadyPresent();
        }
    }

    public Task UpdateAsync(MembershipPrice price, CancellationToken ct = default) =>
        ExecuteAsync(
            "UPDATE MembershipPrices SET Price = @price WHERE Id = @id",
            p => p.AddValue("@id", price.Id).AddValue("@price", price.Price),
            ct);

    public Task DeleteAsync(int businessId, int weeklyFrequency, CancellationToken ct = default) =>
        ExecuteAsync(
            "DELETE FROM MembershipPrices WHERE BusinessId = @businessId AND WeeklyFrequency = @weeklyFrequency",
            p => p.AddValue("@businessId", businessId).AddValue("@weeklyFrequency", weeklyFrequency),
            ct);

    private static bool IsFrequencyDuplicate(SqlException exception) =>
        exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is 2601 or 2627
                && error.Message.Contains("UQ_MembershipPrices_Frequency", StringComparison.Ordinal));

    private static MembershipPrice Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        WeeklyFrequency = reader.Get<int>("WeeklyFrequency"),
        Price = reader.Get<decimal>("Price")
    };
}
