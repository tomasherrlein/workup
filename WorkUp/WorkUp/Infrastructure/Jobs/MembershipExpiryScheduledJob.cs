using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure.Jobs;

/// <summary>
/// The membership-expiry job of the scheduler (T25, RF-30). It exposes <see cref="MembershipExpiryService"/> under its
/// stable key so the per-minute tick and the manual run of T26 execute exactly the same code path.
/// <para>
/// Like <see cref="ReminderScheduledJob"/> and unlike <see cref="OutboxScheduledJob"/>, this job <em>forwards</em>
/// <paramref name="now"/>: the expiry core derives the RF-30 stage (<c>[ExpiresOn - 3, ExpiresOn)</c> for the
/// three-days-before reminder and the expiry date itself for the expired notice) and dates the notice with the instant
/// of its invocation, so the runner-supplied instant is the only source of time. The job has no <c>IClock</c>
/// dependency at all, which keeps a single tick coherent and makes the simulated T26 clock work without changing this
/// adapter.
/// </para>
/// <para>
/// It also forwards <paramref name="ct"/> and never swallows a failure: shutdown cancels the batch, and a repository
/// or composition failure propagates to <see cref="ScheduledJobRunner.RunAllAsync"/>, which reports one fixed safe
/// failure while the remaining jobs of the tick still run.
/// </para>
/// </summary>
public sealed class MembershipExpiryScheduledJob(MembershipExpiryService service) : IScheduledJob
{
    /// <summary>Stable key used by the per-minute tick and by the manual runner of T26.</summary>
    public const string JobKey = "membership-expiry";

    /// <inheritdoc />
    public string Key => JobKey;

    /// <inheritdoc />
    /// <remarks>
    /// The enqueue count is intentionally dropped: the scheduler only needs the invocation to complete, and the
    /// resulting notices are observable in the outbox.
    /// </remarks>
    public Task RunAsync(DateTime now, CancellationToken ct = default) => service.RunAsync(now, ct);
}
