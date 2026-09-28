using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeWorkerRepository : IWorkerRepository
{
    private readonly List<Worker> _workers = [];
    private readonly Dictionary<int, List<WorkerSchedule>> _schedules = new();
    private readonly Dictionary<int, List<WorkerBreak>> _breaks = new();
    private int _nextId = 1;
    private int _nextScheduleId = 1;
    private int _nextBreakId = 1;

    public IReadOnlyList<Worker> All => _workers.Select(Attach).ToList();

    /// <summary>
    /// Deterministic model of losing the check-then-insert window: while set, the CUIL precheck reports the CUIL free
    /// and the insert meets the unique key because a competing create committed after the check, so it reports
    /// <see cref="WorkerInsertResult.CuilTaken"/> and writes nothing.
    /// </summary>
    public bool LoseCuilRaceOnInsert { get; set; }

    /// <summary>
    /// Same lost window on the edit: while set, <see cref="CuilExistsAsync"/> reports the new CUIL free and
    /// <see cref="UpdateAsync"/> reports <see cref="WorkerUpdateResult.CuilTaken"/> because a competing edit
    /// committed after the check.
    /// </summary>
    public bool LoseCuilRaceOnUpdate { get; set; }

    /// <summary>When set, <see cref="InsertAsync"/> throws it unchanged, standing in for an unexpected storage failure.</summary>
    public Exception? InsertFailure { get; set; }

    /// <summary>When set, <see cref="UpdateAsync"/> throws it unchanged, standing in for an unexpected storage failure.</summary>
    public Exception? UpdateFailure { get; set; }

    public Task<List<Worker>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_workers
            .Where(w => w.BusinessId == businessId)
            .OrderBy(w => w.LastName, StringComparer.Ordinal)
            .ThenBy(w => w.FirstName, StringComparer.Ordinal)
            .Select(Attach)
            .ToList());

    public Task<Worker?> GetByIdAsync(int businessId, int id, CancellationToken ct = default)
    {
        var worker = _workers.SingleOrDefault(w => w.BusinessId == businessId && w.Id == id);
        return Task.FromResult(worker is null ? null : Detach(worker));
    }

    public Task<bool> CuilExistsAsync(int businessId, string cuil, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(!LoseCuilRaceOnInsert && !LoseCuilRaceOnUpdate
            && _workers.Any(w => w.BusinessId == businessId && w.Cuil == cuil && w.Id != excludingId));

    public Task<WorkerInsertResult> InsertAsync(Worker worker, CancellationToken ct = default)
    {
        if (InsertFailure is not null)
            throw InsertFailure;

        if (LoseCuilRaceOnInsert)
            return Task.FromResult(WorkerInsertResult.CuilTaken());

        worker.Id = _nextId++;
        _workers.Add(worker);
        return Task.FromResult(WorkerInsertResult.Inserted(worker.Id));
    }

    public Task<WorkerUpdateResult> UpdateAsync(Worker worker, CancellationToken ct = default)
    {
        if (UpdateFailure is not null)
            throw UpdateFailure;

        if (LoseCuilRaceOnUpdate)
            return Task.FromResult(WorkerUpdateResult.CuilTaken());

        var index = _workers.FindIndex(w => w.Id == worker.Id);
        if (index < 0)
            throw new InvalidOperationException($"Worker {worker.Id} was not inserted.");
        _workers[index] = worker;
        return Task.FromResult(WorkerUpdateResult.Updated());
    }

    public Task ReplaceSchedulesAsync(int workerId, List<WorkerSchedule> schedules, CancellationToken ct = default)
    {
        foreach (var schedule in schedules)
        {
            schedule.Id = _nextScheduleId++;
            schedule.WorkerId = workerId;
        }

        _schedules[workerId] = schedules;
        return Task.CompletedTask;
    }

    public Task ReplaceBreaksAsync(int workerId, List<WorkerBreak> breaks, CancellationToken ct = default)
    {
        foreach (var brk in breaks)
        {
            brk.Id = _nextBreakId++;
            brk.WorkerId = workerId;
        }

        _breaks[workerId] = breaks;
        return Task.CompletedTask;
    }

    private Worker Attach(Worker worker)
    {
        worker.Schedules = _schedules.TryGetValue(worker.Id, out var schedules) ? schedules : [];
        worker.Breaks = _breaks.TryGetValue(worker.Id, out var breaks) ? breaks : [];
        return worker;
    }

    /// <summary>
    /// Returns a detached aggregate, the way a real repository does: the service edits its own copy and the stored row
    /// only changes when <see cref="UpdateAsync"/> succeeds. Without this, a losing edit would mutate the tracked row
    /// through the returned reference and the atomicity assertions could not fail.
    /// </summary>
    private Worker Detach(Worker worker) => new()
    {
        Id = worker.Id,
        BusinessId = worker.BusinessId,
        FirstName = worker.FirstName,
        LastName = worker.LastName,
        Phone = worker.Phone,
        Cuil = worker.Cuil,
        IsActive = worker.IsActive,
        Schedules = _schedules.TryGetValue(worker.Id, out var schedules) ? [.. schedules] : [],
        Breaks = _breaks.TryGetValue(worker.Id, out var breaks) ? [.. breaks] : []
    };
}
