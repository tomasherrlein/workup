namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Read-only persistence boundary for the week generation preview (RF-06). Every query is scoped by
/// <c>businessId</c>, and the returned snapshot carries its tenant so the service can reject a cross-business leak.
/// This unit of work writes nothing.
/// </summary>
public interface IWeekGenerationRepository
{
    /// <summary>
    /// Active grid rows of the business, the sessions already generated for the inclusive week range, and the
    /// fixed-class assignments with their membership periods.
    /// </summary>
    Task<WeekGenerationSnapshot> GetWeekSnapshotAsync(
        int businessId, DateOnly weekStart, DateOnly weekEnd, CancellationToken ct = default);
}
