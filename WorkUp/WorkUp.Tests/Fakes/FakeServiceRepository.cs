using WorkUp.BusinessLogic.Catalog;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeServiceRepository : IServiceRepository
{
    private readonly List<Service> _services = [];
    private readonly HashSet<int> _serviceIdsWithBookingHistory = [];
    private int _nextId = 1;

    public IReadOnlyList<Service> All => _services;

    /// <summary>Test setup only: marks a service as already booked, to exercise the capacity-kind guard.</summary>
    public void SeedBookingHistory(int serviceId) => _serviceIdsWithBookingHistory.Add(serviceId);

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
}
