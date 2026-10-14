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

    /// <summary>
    /// Whether an active grid class of this business still references the service (RF-04, T27). Archiving a service
    /// that an active class still uses would leave that class without a valid class type, so the archive is rejected
    /// with <c>service_has_active_grid_class</c> and the stored row keeps its <c>IsActive</c>.
    /// <para>
    /// This read is only half of the T27 guard: on its own it is not a race boundary, because a grid class created
    /// between the check and the archive commit could still land with an archived service type. The callers pair it
    /// with <see cref="LockForGridClassWriteAsync"/> inside one transaction, so the check and the write no longer
    /// straddle a concurrent grid-class write.
    /// </para>
    /// </summary>
    Task<bool> HasActiveGridClassAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>
    /// Takes the tenant-bound update lock on the service row and holds it until the current transaction ends
    /// (T27). Both writers that can break the invariant take it first: a grid-class create, update or reactivation
    /// before it validates the class type and writes, and the archive of the service before its
    /// <see cref="HasActiveGridClassAsync"/> guard and its <c>IsActive</c> update. Two such writers therefore
    /// serialize on this row, so neither can slip a write into the other's check-then-write window.
    /// <para>
    /// Must be called inside a transaction: outside one the lock is released with the statement and protects nothing.
    /// </para>
    /// </summary>
    Task LockForGridClassWriteAsync(int businessId, int id, CancellationToken ct = default);
}
