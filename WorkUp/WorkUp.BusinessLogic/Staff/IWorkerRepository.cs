using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Staff;

/// <summary>
/// Outcome of inserting a worker. The CUIL is unique per business (Regla 1), so a create that passed the sequential
/// precheck can still lose the insert to a competing create that committed in between. That lost race is a business
/// outcome, not a 500: the repository reports <see cref="WorkerInsertResultType.CuilTaken"/> instead of letting the
/// unique-key violation escape as an unhandled <c>SqlException</c>.
/// </summary>
public enum WorkerInsertResultType
{
    Inserted = 1,
    CuilTaken = 2
}

/// <summary>Result of the guarded worker insert. <see cref="WorkerId"/> is only meaningful when it inserted.</summary>
public sealed record WorkerInsertResult(WorkerInsertResultType Result, int WorkerId)
{
    public static WorkerInsertResult Inserted(int workerId) => new(WorkerInsertResultType.Inserted, workerId);

    /// <summary>No row was written: the unique CUIL was taken by a competing create that committed after the precheck.</summary>
    public static WorkerInsertResult CuilTaken() => new(WorkerInsertResultType.CuilTaken, 0);

    public bool IsInserted => Result == WorkerInsertResultType.Inserted;
}

/// <summary>
/// Outcome of updating a worker. Changing the CUIL can lose the same check-then-write window; the repository reports
/// <see cref="WorkerUpdateResultType.CuilTaken"/> so the caller maps it to the existing <c>cuil_taken</c> field error
/// without inventing a different worker.
/// </summary>
public enum WorkerUpdateResultType
{
    Updated = 1,
    CuilTaken = 2
}

/// <summary>Result of the guarded worker update.</summary>
public sealed record WorkerUpdateResult(WorkerUpdateResultType Result)
{
    public static WorkerUpdateResult Updated() => new(WorkerUpdateResultType.Updated);

    public static WorkerUpdateResult CuilTaken() => new(WorkerUpdateResultType.CuilTaken);

    public bool IsUpdated => Result == WorkerUpdateResultType.Updated;
}

public interface IWorkerRepository
{
    /// <summary>All workers of the business, active and archived, with their schedules and breaks loaded, ordered by name.</summary>
    Task<List<Worker>> ListByBusinessAsync(int businessId, CancellationToken ct = default);

    /// <summary>A worker with its schedules and breaks loaded, or null when it does not exist or belongs to another business.</summary>
    Task<Worker?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>CUIL uniqueness check scoped to the business. Pass the worker's own id when editing it.</summary>
    Task<bool> CuilExistsAsync(int businessId, string cuil, int? excludingId = null, CancellationToken ct = default);

    /// <summary>
    /// Inserts the worker row only (its schedules and breaks are saved separately, in the same transaction), or returns
    /// <see cref="WorkerInsertResult.CuilTaken"/> when the per-business <c>UQ_Workers_Cuil</c> unique constraint rejects
    /// the row because a concurrent create took the CUIL first. Only that constraint is reported as a business
    /// outcome; every other storage failure — including a foreign key violation — propagates.
    /// </summary>
    Task<WorkerInsertResult> InsertAsync(Worker worker, CancellationToken ct = default);

    /// <summary>
    /// Updates the worker row only (FirstName, LastName, Phone, Cuil, IsActive), or returns
    /// <see cref="WorkerUpdateResult.CuilTaken"/> when <c>UQ_Workers_Cuil</c> rejects the new CUIL. The statement is
    /// atomic: a rejected update leaves the stored row unchanged. Only that constraint is reported as a business
    /// outcome; every other storage failure propagates.
    /// </summary>
    Task<WorkerUpdateResult> UpdateAsync(Worker worker, CancellationToken ct = default);

    /// <summary>Replaces every working-day range of the worker with the given list.</summary>
    Task ReplaceSchedulesAsync(int workerId, List<WorkerSchedule> schedules, CancellationToken ct = default);

    /// <summary>Replaces every break of the worker with the given list.</summary>
    Task ReplaceBreaksAsync(int workerId, List<WorkerBreak> breaks, CancellationToken ct = default);
}
