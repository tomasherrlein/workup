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

    public Task<int> InsertAsync(MembershipPrice price, CancellationToken ct = default) =>
        InsertAsync(
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

    private static MembershipPrice Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        WeeklyFrequency = reader.Get<int>("WeeklyFrequency"),
        Price = reader.Get<decimal>("Price")
    };
}
