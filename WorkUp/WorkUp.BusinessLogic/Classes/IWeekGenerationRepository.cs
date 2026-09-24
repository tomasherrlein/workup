namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Persistence boundary for week generation (RF-06). Every query and write is scoped by <c>businessId</c>, and the
/// returned snapshot carries its tenant so the service can reject a cross-business leak.
/// </summary>
public interface IWeekGenerationRepository
{
    /// <summary>
    /// Active grid rows of the business, the sessions already generated for the inclusive week range, and the
    /// fixed-class assignments with their membership periods.
    /// </summary>
    Task<WeekGenerationSnapshot> GetWeekSnapshotAsync(
        int businessId, DateOnly weekStart, DateOnly weekEnd, CancellationToken ct = default);

    /// <summary>
    /// Writes the resolved week: the sessions that do not exist yet and the fixed annotations still missing from
    /// every planned session. It runs inside the caller's ambient transaction, which owns the commit, so the caller
    /// can still read the snapshot in the same transaction before writing.
    /// <para>
    /// It must be idempotent: creating a session is guarded by the unique session identity (business, grid class and
    /// date), and annotating a fixed student is guarded by the unique fixed annotation (session and enrollment).
    /// Reconfirming the same week therefore duplicates nothing, deletes nothing, and never resurrects a cancelled
    /// annotation; it only reports what it actually created.
    /// </para>
    /// </summary>
    Task<WeekGenerationWriteOutcome> ConfirmWeekAsync(WeekGenerationPlan plan, CancellationToken ct = default);
}
