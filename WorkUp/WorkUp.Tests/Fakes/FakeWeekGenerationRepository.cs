using WorkUp.BusinessLogic.Classes;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the read-only week generation snapshot. It mimics the tenant scope of the SQL
/// implementation: only sessions inside the requested week are returned, and
/// <see cref="ReportedBusinessId"/> can be set to a different tenant to simulate a leaked snapshot.
/// </summary>
public sealed class FakeWeekGenerationRepository : IWeekGenerationRepository
{
    public List<WeekGridClassRow> GridClasses { get; } = [];
    public List<WeekSessionRow> Sessions { get; } = [];
    public List<WeekFixedAssignmentRow> FixedAssignments { get; } = [];

    /// <summary>Tenant echoed by the snapshot. Null means the requested business; set it to simulate a cross-business leak.</summary>
    public int? ReportedBusinessId { get; set; }

    public int? RequestedBusinessId { get; private set; }
    public DateOnly? RequestedWeekStart { get; private set; }
    public DateOnly? RequestedWeekEnd { get; private set; }
    public int CallCount { get; private set; }

    public Task<WeekGenerationSnapshot> GetWeekSnapshotAsync(
        int businessId, DateOnly weekStart, DateOnly weekEnd, CancellationToken ct = default)
    {
        CallCount++;
        RequestedBusinessId = businessId;
        RequestedWeekStart = weekStart;
        RequestedWeekEnd = weekEnd;

        var sessions = Sessions
            .Where(session => session.Date >= weekStart && session.Date <= weekEnd)
            .ToList();

        return Task.FromResult(new WeekGenerationSnapshot(
            ReportedBusinessId ?? businessId,
            GridClasses.ToList(),
            sessions,
            FixedAssignments.ToList()));
    }
}
