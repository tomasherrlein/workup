using WorkUp.BusinessLogic.Catalog;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeServiceRepository : IServiceRepository
{
    private readonly List<Service> _services = [];
    private readonly HashSet<int> _serviceIdsWithBookingHistory = [];
    private readonly List<(int BusinessId, int ServiceId, bool IsActive)> _gridClasses = [];
    private int _nextId = 1;

    public IReadOnlyList<Service> All => _services;

    /// <summary>Test setup only: marks a service as already booked, to exercise the capacity-kind guard.</summary>
    public void SeedBookingHistory(int serviceId) => _serviceIdsWithBookingHistory.Add(serviceId);

    /// <summary>
    /// Test setup only: registers a grid class row in the given business referencing this service, to exercise the
    /// archive guard without depending on the Classes module (T04).
    /// </summary>
    public void SeedGridClass(int businessId, int serviceId, bool isActive = true) =>
        _gridClasses.Add((businessId, serviceId, isActive));

    /// <summary>Test setup only: flips the stored row's <c>IsActive</c>, standing in for a competing archive or reactivation that committed first.</summary>
    public void SetActive(int serviceId, bool isActive)
    {
        var index = _services.FindIndex(s => s.Id == serviceId);
        if (index < 0)
            throw new InvalidOperationException($"Service {serviceId} was not inserted.");
        _services[index].IsActive = isActive;
    }

    /// <summary>
    /// Deterministic model of the T27 race window: while set, it runs inside <see cref="LockForGridClassWriteAsync"/>,
    /// standing in for the competing writer that committed just before this transaction acquired the row lock. The
    /// guard that follows the lock must therefore see that writer's committed state.
    /// </summary>
    public Action<int>? OnLockForGridClassWrite { get; set; }

    public Task<List<Service>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_services.Where(s => s.BusinessId == businessId).OrderBy(s => s.Name, StringComparer.Ordinal).ToList());

    public Task<Service?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_services.SingleOrDefault(s => s.BusinessId == businessId && s.Id == id));

    public Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(_services.Any(s => s.BusinessId == businessId && s.Name == name && s.Id != excludingId));

    public Task<int> InsertAsync(Service service, CancellationToken ct = default)
    {
        service.Id = _nextId++;
        _services.Add(service);
        return Task.FromResult(service.Id);
    }

    public Task UpdateAsync(Service service, CancellationToken ct = default)
    {
        var index = _services.FindIndex(s => s.Id == service.Id);
        if (index < 0)
            throw new InvalidOperationException($"Service {service.Id} was not inserted.");
        _services[index] = service;
        return Task.CompletedTask;
    }

    public Task<bool> HasBookingHistoryAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_serviceIdsWithBookingHistory.Contains(id));

    public Task<bool> HasActiveGroupServiceAsync(int businessId, CancellationToken ct = default) =>
        Task.FromResult(_services.Any(s => s.BusinessId == businessId && s.IsActive && s.MaxCapacity > 1));

    public Task<bool> HasActiveGridClassAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_gridClasses.Any(g => g.BusinessId == businessId && g.ServiceId == id && g.IsActive));

    /// <summary>
    /// No-op row lock for the in-memory model: there is no SQL to serialize. It exists so the fake stays consistent
    /// with <see cref="IServiceRepository"/>, and it fires <see cref="OnLockForGridClassWrite"/> so a test can model
    /// deterministically the competing writer that committed inside the lock window.
    /// </summary>
    public Task LockForGridClassWriteAsync(int businessId, int id, CancellationToken ct = default)
    {
        OnLockForGridClassWrite?.Invoke(id);
        return Task.CompletedTask;
    }
}
