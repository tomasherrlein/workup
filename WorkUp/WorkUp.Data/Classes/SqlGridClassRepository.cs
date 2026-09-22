using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Classes;

public sealed class SqlGridClassRepository(DbSession session) : SqlRepositoryBase(session), IGridClassRepository
{
    private const string Columns = "Id, BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime, IsActive";

    public Task<List<GridClass>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        QueryAsync(
            $"SELECT {Columns} FROM GridClasses WHERE BusinessId = @businessId ORDER BY DayOfWeek, StartTime",
            Map,
            p => p.AddValue("@businessId", businessId),
            ct);

    public Task<GridClass?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            $"SELECT {Columns} FROM GridClasses WHERE BusinessId = @businessId AND Id = @id",
            Map,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct);

    public Task<List<GridClass>> ListActiveByWorkerAndDayAsync(int businessId, int workerId, DayOfWeek dayOfWeek, int? excludingId = null, CancellationToken ct = default) =>
        QueryAsync(
            $"""
            SELECT {Columns} FROM GridClasses
             WHERE BusinessId = @businessId AND WorkerId = @workerId AND DayOfWeek = @dayOfWeek AND IsActive = 1
               AND (@excludingId IS NULL OR Id <> @excludingId)
            """,
            Map,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@workerId", workerId)
                .AddValue("@dayOfWeek", dayOfWeek)
                .AddValue("@excludingId", excludingId),
            ct);

    public Task<int> InsertAsync(GridClass gridClass, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO GridClasses (BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime, IsActive)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @serviceId, @workerId, @dayOfWeek, @startTime, @isActive)
            """,
            p => p
                .AddValue("@businessId", gridClass.BusinessId)
                .AddValue("@serviceId", gridClass.ServiceId)
                .AddValue("@workerId", gridClass.WorkerId)
                .AddValue("@dayOfWeek", gridClass.DayOfWeek)
                .AddValue("@startTime", gridClass.StartTime)
                .AddValue("@isActive", gridClass.IsActive),
            ct);

    public Task UpdateAsync(GridClass gridClass, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            UPDATE GridClasses
               SET ServiceId = @serviceId, WorkerId = @workerId, DayOfWeek = @dayOfWeek, StartTime = @startTime, IsActive = @isActive
             WHERE Id = @id
            """,
            p => p
                .AddValue("@id", gridClass.Id)
                .AddValue("@serviceId", gridClass.ServiceId)
                .AddValue("@workerId", gridClass.WorkerId)
                .AddValue("@dayOfWeek", gridClass.DayOfWeek)
                .AddValue("@startTime", gridClass.StartTime)
                .AddValue("@isActive", gridClass.IsActive),
            ct);

    // EnrollmentFixedClasses belongs to Memberships (T15 creates the assignment, T17 owns the guided teacher/day
    // change), which do not exist yet. Queried directly here in the meantime, the same way SqlServiceRepository
    // (T02) reached into Appointments and GridClasses before their own modules existed. The id already proved
    // tenant ownership through GetByIdAsync above, so this does not re-filter by BusinessId.
    public async Task<bool> HasActiveFixedAssignmentAsync(int businessId, int id, DateOnly today, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT CASE WHEN EXISTS (
                       SELECT 1 FROM EnrollmentFixedClasses
                        WHERE GridClassId = @id AND (ValidTo IS NULL OR ValidTo >= @today)
                   ) THEN 1 ELSE 0 END
            """,
            p => p.AddValue("@id", id).AddValue("@today", today),
            ct) > 0;

    private static GridClass Map(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        ServiceId = reader.Get<int>("ServiceId"),
        WorkerId = reader.Get<int>("WorkerId"),
        DayOfWeek = reader.GetEnum<DayOfWeek>("DayOfWeek"),
        StartTime = reader.Get<TimeOnly>("StartTime"),
        IsActive = reader.Get<bool>("IsActive")
    };
}
