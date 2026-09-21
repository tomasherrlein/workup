using WorkUp.Domain.Entities;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Businesses;
// The entity is aliased: this file's namespace nests under WorkUp.*, and the bare name "Business" resolves to the
// WorkUp.BusinessLogic project's own root namespace rather than the entity type (CS0118).

namespace WorkUp.Data.Businesses;

public sealed class SqlBusinessRepository(DbSession session) : SqlRepositoryBase(session), IBusinessRepository
{
    private const string Columns = """
        Id, Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, ReminderLeadHours, DailySummaryTime,
        StockReviewEveryDays, CreatedAt
        """;

    public Task<Business?> GetByIdAsync(int id, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync($"SELECT {Columns} FROM Businesses WHERE Id = @id", Map, p => p.AddValue("@id", id), ct);

    public Task<Business?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync($"SELECT {Columns} FROM Businesses WHERE Slug = @slug", Map, p => p.AddValue("@slug", slug), ct);

    public async Task<bool> SlugExistsAsync(string slug, int? excludingId = null, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            "SELECT COUNT(*) FROM Businesses WHERE Slug = @slug AND (@excludingId IS NULL OR Id <> @excludingId)",
            p => p.AddValue("@slug", slug).AddValue("@excludingId", excludingId),
            ct) > 0;

    public Task<int> InsertAsync(Business business, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO Businesses (Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, ReminderLeadHours,
                                     DailySummaryTime, StockReviewEveryDays, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@name, @slug, @contactPhone, @notificationEmail, @allowsWalkIns, @reminderLeadHours,
                    @dailySummaryTime, @stockReviewEveryDays, @createdAt)
            """,
            p => p
                .AddValue("@name", business.Name)
                .AddValue("@slug", business.Slug)
                .AddValue("@contactPhone", business.ContactPhone)
                .AddValue("@notificationEmail", business.NotificationEmail)
                .AddValue("@allowsWalkIns", business.AllowsWalkIns)
                .AddValue("@reminderLeadHours", business.ReminderLeadHours)
                .AddValue("@dailySummaryTime", business.DailySummaryTime)
                .AddValue("@stockReviewEveryDays", business.StockReviewEveryDays)
                .AddValue("@createdAt", business.CreatedAt),
            ct);

    public Task UpdateAsync(Business business, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            UPDATE Businesses
               SET Name = @name, Slug = @slug, ContactPhone = @contactPhone, NotificationEmail = @notificationEmail,
                   AllowsWalkIns = @allowsWalkIns, ReminderLeadHours = @reminderLeadHours,
                   DailySummaryTime = @dailySummaryTime, StockReviewEveryDays = @stockReviewEveryDays
             WHERE Id = @id
            """,
            p => p
                .AddValue("@id", business.Id)
                .AddValue("@name", business.Name)
                .AddValue("@slug", business.Slug)
                .AddValue("@contactPhone", business.ContactPhone)
                .AddValue("@notificationEmail", business.NotificationEmail)
                .AddValue("@allowsWalkIns", business.AllowsWalkIns)
                .AddValue("@reminderLeadHours", business.ReminderLeadHours)
                .AddValue("@dailySummaryTime", business.DailySummaryTime)
                .AddValue("@stockReviewEveryDays", business.StockReviewEveryDays),
            ct);

    private static Business Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        Name = reader.Get<string>("Name"),
        Slug = reader.Get<string>("Slug"),
        ContactPhone = reader.Get<string>("ContactPhone"),
        NotificationEmail = reader.GetNullableString("NotificationEmail"),
        AllowsWalkIns = reader.Get<bool>("AllowsWalkIns"),
        ReminderLeadHours = reader.Get<int>("ReminderLeadHours"),
        DailySummaryTime = reader.Get<TimeOnly>("DailySummaryTime"),
        StockReviewEveryDays = reader.Get<int>("StockReviewEveryDays"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };
}
