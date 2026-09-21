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
        return Task.FromResult(worker is null ? null : Attach(worker));
    }

    public Task<bool> CuilExistsAsync(int businessId, string cuil, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(_workers.Any(w => w.BusinessId == businessId && w.Cuil == cuil && w.Id != excludingId));

    public Task<int> InsertAsync(Worker worker, CancellationToken ct = default)
    {
        worker.Id = _nextId++;
        _workers.Add(worker);
        return Task.FromResult(worker.Id);
    }

    public Task UpdateAsync(Worker worker, CancellationToken ct = default)
    {
        var index = _workers.FindIndex(w => w.Id == worker.Id);
        if (index < 0)
            throw new InvalidOperationException($"Worker {worker.Id} was not inserted.");
        _workers[index] = worker;
        return Task.CompletedTask;
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
}
