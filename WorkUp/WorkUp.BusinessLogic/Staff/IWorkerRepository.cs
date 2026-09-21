using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Staff;

public interface IWorkerRepository
{
    /// <summary>All workers of the business, active and archived, with their schedules and breaks loaded, ordered by name.</summary>
    Task<List<Worker>> ListByBusinessAsync(int businessId, CancellationToken ct = default);

    /// <summary>A worker with its schedules and breaks loaded, or null when it does not exist or belongs to another business.</summary>
    Task<Worker?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>CUIL uniqueness check scoped to the business. Pass the worker's own id when editing it.</summary>
    Task<bool> CuilExistsAsync(int businessId, string cuil, int? excludingId = null, CancellationToken ct = default);

    /// <summary>Inserts the worker row only (its schedules and breaks are saved separately, in the same transaction).</summary>
    Task<int> InsertAsync(Worker worker, CancellationToken ct = default);

    /// <summary>Updates the worker row only (FirstName, LastName, Phone, Cuil, IsActive).</summary>
    Task UpdateAsync(Worker worker, CancellationToken ct = default);

    /// <summary>Replaces every working-day range of the worker with the given list.</summary>
    Task ReplaceSchedulesAsync(int workerId, List<WorkerSchedule> schedules, CancellationToken ct = default);

    /// <summary>Replaces every break of the worker with the given list.</summary>
    Task ReplaceBreaksAsync(int workerId, List<WorkerBreak> breaks, CancellationToken ct = default);
}
