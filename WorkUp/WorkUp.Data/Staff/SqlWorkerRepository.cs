using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Staff;

public sealed class SqlWorkerRepository(DbSession session) : SqlRepositoryBase(session), IWorkerRepository
{
    private const string WorkerColumns = "Id, BusinessId, FirstName, LastName, Phone, Cuil, IsActive";

    public async Task<List<Worker>> ListByBusinessAsync(int businessId, CancellationToken ct = default)
    {
        var workerList = await QueryAsync(
            $"SELECT {WorkerColumns} FROM Workers WHERE BusinessId = @businessId ORDER BY LastName, FirstName",
            MapWorker,
            p => p.AddValue("@businessId", businessId),
            ct);

        if (workerList.Count == 0)
            return workerList;

        // WorkerSchedules and WorkerBreaks have no BusinessId of their own: they are filtered through their worker.
        var schedules = await QueryAsync(
            """
            SELECT ws.Id, ws.WorkerId, ws.DayOfWeek, ws.StartTime, ws.EndTime
              FROM WorkerSchedules ws
              JOIN Workers w ON w.Id = ws.WorkerId
             WHERE w.BusinessId = @businessId
             ORDER BY ws.WorkerId, ws.DayOfWeek
            """,
            MapSchedule,
            p => p.AddValue("@businessId", businessId),
            ct);

        var breaks = await QueryAsync(
            """
            SELECT wb.Id, wb.WorkerId, wb.DayOfWeek, wb.StartTime, wb.EndTime
              FROM WorkerBreaks wb
              JOIN Workers w ON w.Id = wb.WorkerId
             WHERE w.BusinessId = @businessId
             ORDER BY wb.WorkerId, wb.DayOfWeek, wb.StartTime
            """,
            MapBreak,
            p => p.AddValue("@businessId", businessId),
            ct);

        var schedulesByWorker = schedules.ToLookup(s => s.WorkerId);
        var breaksByWorker = breaks.ToLookup(b => b.WorkerId);
        foreach (var worker in workerList)
        {
            worker.Schedules = schedulesByWorker[worker.Id].ToList();
            worker.Breaks = breaksByWorker[worker.Id].ToList();
        }

        return workerList;
    }

    public async Task<Worker?> GetByIdAsync(int businessId, int id, CancellationToken ct = default)
    {
        var worker = await QuerySingleOrDefaultAsync(
            $"SELECT {WorkerColumns} FROM Workers WHERE BusinessId = @businessId AND Id = @id",
            MapWorker,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct);

        if (worker is null)
            return null;

        // The worker row above already proved tenant ownership; its child rows are looked up by WorkerId alone.
        worker.Schedules = await QueryAsync(
            "SELECT Id, WorkerId, DayOfWeek, StartTime, EndTime FROM WorkerSchedules WHERE WorkerId = @workerId ORDER BY DayOfWeek",
            MapSchedule,
            p => p.AddValue("@workerId", id),
            ct);

        worker.Breaks = await QueryAsync(
            "SELECT Id, WorkerId, DayOfWeek, StartTime, EndTime FROM WorkerBreaks WHERE WorkerId = @workerId ORDER BY DayOfWeek, StartTime",
            MapBreak,
            p => p.AddValue("@workerId", id),
            ct);

        return worker;
    }

    public async Task<bool> CuilExistsAsync(int businessId, string cuil, int? excludingId = null, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM Workers
             WHERE BusinessId = @businessId AND Cuil = @cuil AND (@excludingId IS NULL OR Id <> @excludingId)
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@cuil", cuil).AddValue("@excludingId", excludingId),
            ct) > 0;

    public async Task<WorkerInsertResult> InsertAsync(Worker worker, CancellationToken ct = default)
    {
        try
        {
            var id = await InsertAsync(
                """
                INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil, IsActive)
                OUTPUT INSERTED.Id
                VALUES (@businessId, @firstName, @lastName, @phone, @cuil, @isActive)
                """,
                p => p
                    .AddValue("@businessId", worker.BusinessId)
                    .AddValue("@firstName", worker.FirstName)
                    .AddValue("@lastName", worker.LastName)
                    .AddValue("@phone", worker.Phone)
                    .AddValue("@cuil", worker.Cuil)
                    .AddValue("@isActive", worker.IsActive),
                ct);
            return WorkerInsertResult.Inserted(id);
        }
        catch (SqlException exception) when (IsCuilDuplicate(exception))
        {
            // The per-business unique CUIL is the last race guard: losing the check-then-insert window is an expected
            // business outcome, never an unhandled 500. The caller returns before saving schedules, so the losing
            // create leaves no orphan worker or schedule behind.
            return WorkerInsertResult.CuilTaken();
        }
    }

    public async Task<WorkerUpdateResult> UpdateAsync(Worker worker, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync(
                """
                UPDATE Workers
                   SET FirstName = @firstName, LastName = @lastName, Phone = @phone, Cuil = @cuil, IsActive = @isActive
                 WHERE Id = @id
                """,
                p => p
                    .AddValue("@id", worker.Id)
                    .AddValue("@firstName", worker.FirstName)
                    .AddValue("@lastName", worker.LastName)
                    .AddValue("@phone", worker.Phone)
                    .AddValue("@cuil", worker.Cuil)
                    .AddValue("@isActive", worker.IsActive),
                ct);
            return WorkerUpdateResult.Updated();
        }
        catch (SqlException exception) when (IsCuilDuplicate(exception))
        {
            // The single statement is atomic: a rejected edit leaves the stored row unchanged. The caller returns
            // before replacing schedules or breaks, so the losing edit leaves no orphan child rows.
            return WorkerUpdateResult.CuilTaken();
        }
    }

    /// <summary>
    /// Only <c>UQ_Workers_Cuil</c> is a benign lost race. Any other unique violation — or any other
    /// <see cref="SqlException"/> — propagates instead of being swallowed as a CUIL conflict.
    /// </summary>
    private static bool IsCuilDuplicate(SqlException exception) =>
        exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is 2601 or 2627
                && error.Message.Contains("UQ_Workers_Cuil", StringComparison.Ordinal));

    public async Task ReplaceSchedulesAsync(int workerId, List<WorkerSchedule> schedules, CancellationToken ct = default)
    {
        await ExecuteAsync("DELETE FROM WorkerSchedules WHERE WorkerId = @workerId", p => p.AddValue("@workerId", workerId), ct);
        foreach (var schedule in schedules)
        {
            schedule.WorkerId = workerId;
            schedule.Id = await InsertAsync(
                """
                INSERT INTO WorkerSchedules (WorkerId, DayOfWeek, StartTime, EndTime)
                OUTPUT INSERTED.Id
                VALUES (@workerId, @dayOfWeek, @startTime, @endTime)
                """,
                p => p
                    .AddValue("@workerId", workerId)
                    .AddValue("@dayOfWeek", schedule.DayOfWeek)
                    .AddValue("@startTime", schedule.StartTime)
                    .AddValue("@endTime", schedule.EndTime),
                ct);
        }
    }

    public async Task ReplaceBreaksAsync(int workerId, List<WorkerBreak> breaks, CancellationToken ct = default)
    {
        await ExecuteAsync("DELETE FROM WorkerBreaks WHERE WorkerId = @workerId", p => p.AddValue("@workerId", workerId), ct);
        foreach (var brk in breaks)
        {
            brk.WorkerId = workerId;
            brk.Id = await InsertAsync(
                """
                INSERT INTO WorkerBreaks (WorkerId, DayOfWeek, StartTime, EndTime)
                OUTPUT INSERTED.Id
                VALUES (@workerId, @dayOfWeek, @startTime, @endTime)
                """,
                p => p
                    .AddValue("@workerId", workerId)
                    .AddValue("@dayOfWeek", brk.DayOfWeek)
                    .AddValue("@startTime", brk.StartTime)
                    .AddValue("@endTime", brk.EndTime),
                ct);
        }
    }

    // T27: the guard reads the active grid classes of this business only. BusinessId is part of the predicate even
    // though Id is a global identity, because GridClasses only constrains WorkerId by foreign key: a row whose
    // BusinessId belongs to another tenant must never block this tenant's archive.
    public async Task<bool> HasActiveGridClassAsync(int businessId, int id, CancellationToken ct = default) =>
        await ScalarAsync<int>(
            """
            SELECT CASE WHEN EXISTS (
                     SELECT 1 FROM GridClasses
                      WHERE BusinessId = @businessId AND WorkerId = @id AND IsActive = 1
                   ) THEN 1 ELSE 0 END
            """,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct) > 0;

    // T27: the update lock is what turns the guard above into a race boundary. UPDLOCK is held until the transaction
    // ends, so a grid-class write and the archive of the worker serialize on this row: whichever gets here first
    // commits before the other reads, and the loser sees the committed state instead of the stale one. ROWLOCK keeps
    // the lock on the row. BusinessId is part of the predicate even though Id is a global identity, so a row of
    // another tenant is never locked (nor blocked) by this tenant's write; a missing row simply locks nothing and the
    // caller's own read reports the not-found outcome. Grid-class writers always take the class type's lock first and
    // this one second, so the pair can never form a lock cycle between two workers and their class types.
    public async Task LockForGridClassWriteAsync(int businessId, int id, CancellationToken ct = default) =>
        await ScalarAsync<int?>(
            "SELECT Id FROM Workers WITH (UPDLOCK, ROWLOCK) WHERE BusinessId = @businessId AND Id = @id",
            p => p.AddValue("@businessId", businessId).AddValue("@id", id),
            ct);

    private static Worker MapWorker(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        FirstName = reader.Get<string>("FirstName"),
        LastName = reader.Get<string>("LastName"),
        Phone = reader.Get<string>("Phone"),
        Cuil = reader.Get<string>("Cuil"),
        IsActive = reader.Get<bool>("IsActive")
    };

    private static WorkerSchedule MapSchedule(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        WorkerId = reader.Get<int>("WorkerId"),
        DayOfWeek = reader.GetEnum<DayOfWeek>("DayOfWeek"),
        StartTime = reader.Get<TimeOnly>("StartTime"),
        EndTime = reader.Get<TimeOnly>("EndTime")
    };

    private static WorkerBreak MapBreak(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        WorkerId = reader.Get<int>("WorkerId"),
        DayOfWeek = reader.GetEnum<DayOfWeek>("DayOfWeek"),
        StartTime = reader.Get<TimeOnly>("StartTime"),
        EndTime = reader.Get<TimeOnly>("EndTime")
    };
}
