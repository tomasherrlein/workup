using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Catalog;

public sealed class SqlServiceRepository(DbSession session) : SqlRepositoryBase(session), IServiceRepository
{
    private const string Columns = "Id, BusinessId, Name, Price, DurationMinutes, MaxCapacity, IsActive";

    public Task<List<Service>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        QueryAsync($"SELECT {Columns} FROM Services WHERE BusinessId = @businessId ORDER BY Name", Map, p => p.AddValue("@businessId", businessId), ct);

    public Task<Service?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            $"SELECT {Columns} FROM Services WHERE BusinessId = @businessId AND Id = @id",
            Map,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct);

    public async Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM Services
             WHERE BusinessId = @businessId AND Name = @name AND (@excludingId IS NULL OR Id <> @excludingId)
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@name", name).AddValue("@excludingId", excludingId),
            ct) > 0;

    public Task<int> InsertAsync(Service service, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity, IsActive)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @name, @price, @durationMinutes, @maxCapacity, @isActive)
            """,
            p => p
                .AddValue("@businessId", service.BusinessId)
                .AddValue("@name", service.Name)
                .AddValue("@price", service.Price)
                .AddValue("@durationMinutes", service.DurationMinutes)
                .AddValue("@maxCapacity", service.MaxCapacity)
                .AddValue("@isActive", service.IsActive),
            ct);

    public Task UpdateAsync(Service service, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            UPDATE Services
               SET Name = @name, Price = @price, DurationMinutes = @durationMinutes,
                   MaxCapacity = @maxCapacity, IsActive = @isActive
             WHERE Id = @id
            """,
            p => p
                .AddValue("@id", service.Id)
                .AddValue("@name", service.Name)
                .AddValue("@price", service.Price)
                .AddValue("@durationMinutes", service.DurationMinutes)
                .AddValue("@maxCapacity", service.MaxCapacity)
                .AddValue("@isActive", service.IsActive),
            ct);

    // GridClasses (T04) and Appointments (T08-T12) are not owned by this module, but the capacity-kind guard
    // (RF-02) needs to know whether either already references this service.
    public async Task<bool> HasBookingHistoryAsync(int businessId, int id, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM Appointments WHERE BusinessId = @businessId AND ServiceId = @id)
                     OR EXISTS (SELECT 1 FROM GridClasses WHERE BusinessId = @businessId AND ServiceId = @id)
                    THEN 1 ELSE 0 END
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct) > 0;

    public async Task<bool> HasActiveGroupServiceAsync(int businessId, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            "SELECT COUNT(*) FROM Services WHERE BusinessId = @businessId AND IsActive = 1 AND MaxCapacity > 1",
            p => p.AddValue("@businessId", businessId),
            ct) > 0;

    // T27: the guard reads the active grid classes of this business only. BusinessId is part of the predicate even
    // though Id is a global identity, because GridClasses only constrains ServiceId by foreign key: a row whose
    // BusinessId belongs to another tenant must never block this tenant's archive.
    public async Task<bool> HasActiveGridClassAsync(int businessId, int id, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT CASE WHEN EXISTS (
                     SELECT 1 FROM GridClasses
                      WHERE BusinessId = @businessId AND ServiceId = @id AND IsActive = 1
                   ) THEN 1 ELSE 0 END
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct) > 0;

    // T27: the update lock is what turns the guard above into a race boundary. UPDLOCK is held until the transaction
    // ends, so a grid-class write and the archive of the service serialize on this row: whichever gets here first
    // commits before the other reads, and the loser sees the committed state instead of the stale one. ROWLOCK keeps
    // the lock on the row. BusinessId is part of the predicate even though Id is a global identity, so a row of
    // another tenant is never locked (nor blocked) by this tenant's write; a missing row simply locks nothing and the
    // caller's own read reports the not-found outcome.
    public async Task LockForGridClassWriteAsync(int businessId, int id, CancellationToken ct = default) =>
        await ScalarAsync<int?>(
            "SELECT Id FROM Services WITH (UPDLOCK, ROWLOCK) WHERE BusinessId = @businessId AND Id = @id",
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct);

    private static Service Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        Name = reader.Get<string>("Name"),
        Price = reader.Get<decimal>("Price"),
        DurationMinutes = reader.Get<int>("DurationMinutes"),
        MaxCapacity = reader.Get<int>("MaxCapacity"),
        IsActive = reader.Get<bool>("IsActive")
    };
}
