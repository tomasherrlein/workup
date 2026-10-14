using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeGridClassRepository : IGridClassRepository
{
    private readonly List<GridClass> _gridClasses = [];
    private readonly Dictionary<int, List<DateOnly?>> _activeFixedAssignmentValidTo = new();
    private int _nextId = 1;

    public IReadOnlyList<GridClass> All => _gridClasses;

    /// <summary>
    /// Test setup only: records a fixed-class assignment on this grid class, valid up to <paramref name="validTo"/>
    /// (null means open-ended), to exercise the T17 guard without needing the Memberships module (T15).
    /// </summary>
    public void SeedFixedAssignment(int gridClassId, DateOnly? validTo = null)
    {
        if (!_activeFixedAssignmentValidTo.TryGetValue(gridClassId, out var list))
            _activeFixedAssignmentValidTo[gridClassId] = list = [];
        list.Add(validTo);
    }

    public Task<List<GridClass>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_gridClasses
            .Where(g => g.BusinessId == businessId)
            .OrderBy(g => g.DayOfWeek)
            .ThenBy(g => g.StartTime)
            .ToList());

    public Task<GridClass?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_gridClasses.SingleOrDefault(g => g.BusinessId == businessId && g.Id == id));

    public Task<List<GridClass>> ListActiveByWorkerAndDayAsync(int businessId, int workerId, DayOfWeek dayOfWeek, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(_gridClasses
            .Where(g => g.BusinessId == businessId && g.WorkerId == workerId && g.DayOfWeek == dayOfWeek && g.IsActive && g.Id != excludingId)
            .ToList());

    public Task<int> InsertAsync(GridClass gridClass, CancellationToken ct = default)
    {
        gridClass.Id = _nextId++;
        _gridClasses.Add(gridClass);
        return Task.FromResult(gridClass.Id);
    }

    public Task UpdateAsync(GridClass gridClass, CancellationToken ct = default)
    {
        var index = _gridClasses.FindIndex(g => g.Id == gridClass.Id);
        if (index < 0)
            throw new InvalidOperationException($"Grid class {gridClass.Id} was not inserted.");
        _gridClasses[index] = gridClass;
        return Task.CompletedTask;
    }

    public Task<bool> HasActiveFixedAssignmentAsync(int businessId, int id, DateOnly today, CancellationToken ct = default) =>
        Task.FromResult(_activeFixedAssignmentValidTo.TryGetValue(id, out var validTos) && validTos.Any(v => v is null || v >= today));
}
