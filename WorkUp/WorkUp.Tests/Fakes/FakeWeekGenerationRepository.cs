using WorkUp.BusinessLogic.Classes;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the week generation persistence. It mimics the tenant scope of the SQL implementation: only
/// sessions inside the requested week are returned, and <see cref="ReportedBusinessId"/> can be set to a different
/// tenant to simulate a leaked snapshot. The write records the plan it received instead of touching a store.
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

    /// <summary>The plan of the last confirm call, or null when the write was never reached.</summary>
    public WeekGenerationPlan? ConfirmedPlan { get; private set; }

    public int ConfirmCallCount { get; private set; }

    /// <summary>What the write reports it created. Defaults to a write that created nothing new.</summary>
    public WeekGenerationWriteOutcome ConfirmOutcome { get; set; } = new(0, 0);

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

    public Task<WeekGenerationWriteOutcome> ConfirmWeekAsync(WeekGenerationPlan plan, CancellationToken ct = default)
    {
        ConfirmCallCount++;
        ConfirmedPlan = plan;
        return Task.FromResult(ConfirmOutcome);
    }
}
