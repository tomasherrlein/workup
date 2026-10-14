using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure.Jobs;

/// <summary>
/// The reminder job of the scheduler (T24, RF-17). It exposes <see cref="ReminderService"/> under its stable key so
/// the per-minute tick and the manual run of T26 execute exactly the same code path.
/// <para>
/// Unlike <see cref="OutboxScheduledJob"/>, this job <em>forwards</em> <paramref name="now"/>: the reminder core
/// decides eligibility with the instant of its invocation (<c>StartsAt - leadHours &lt;= now &lt; StartsAt</c>) and
/// dates the notice with it, so the runner-supplied instant is the only source of time. The job has no
/// <c>IClock</c> dependency at all, which keeps a single tick coherent and makes the simulated T26 clock work without
/// changing this adapter.
/// </para>
/// <para>
/// It also forwards <paramref name="ct"/> and never swallows a failure: shutdown cancels the batch, and a repository
/// or composition failure propagates to <see cref="ScheduledJobRunner.RunAllAsync"/>, which reports one fixed safe
/// failure while the remaining jobs of the tick still run.
/// </para>
/// </summary>
public sealed class ReminderScheduledJob(ReminderService service) : IScheduledJob
{
    /// <summary>Stable key used by the per-minute tick and by the manual runner of T26.</summary>
    public const string JobKey = "reminders";

    /// <inheritdoc />
    public string Key => JobKey;

    /// <inheritdoc />
    /// <remarks>
    /// The enqueue count is intentionally dropped: the scheduler only needs the invocation to complete, and the
    /// resulting notices are observable in the outbox.
    /// </remarks>
    public Task RunAsync(DateTime now, CancellationToken ct = default) => service.RunAsync(now, ct);
}
