using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Catalog;

public interface IServiceRepository
{
    /// <summary>All services of the business, active and archived, ordered by name.</summary>
    Task<List<Service>> ListByBusinessAsync(int businessId, CancellationToken ct = default);

    Task<Service?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>Name uniqueness check scoped to the business, across individual services and classes alike. Pass the service's own id when editing it.</summary>
    Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default);

    Task<int> InsertAsync(Service service, CancellationToken ct = default);

    Task UpdateAsync(Service service, CancellationToken ct = default);

    /// <summary>
    /// Whether the service already has appointments or grid classes referencing it. Used to lock the individual
    /// (max capacity 1) vs. group (max capacity greater than 1) kind of a service that is already in use.
    /// </summary>
    Task<bool> HasBookingHistoryAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>Whether the business has at least one active service with max capacity greater than 1 (T02 acceptance: drives the Clases/Abonos navigation visibility).</summary>
    Task<bool> HasActiveGroupServiceAsync(int businessId, CancellationToken ct = default);
}
