using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure.Jobs;

/// <summary>
/// The outbox job of the scheduler (T23). It exposes the dispatcher under its stable key so the per-minute tick and
/// the manual run of T26 execute exactly the same code path.
/// <para>
/// <paramref name="now"/> is part of the <see cref="IScheduledJob"/> contract for the jobs that need the instant of
/// their invocation; the dispatcher already reads the same <c>IClock</c> on every claim and completion, so it is not
/// forwarded and cannot become a stale second source of time.
/// </para>
/// </summary>
public sealed class OutboxScheduledJob(OutboxDispatchService dispatcher) : IScheduledJob
{
    /// <summary>Stable key used by the manual runner of T26.</summary>
    public const string JobKey = "outbox";

    /// <inheritdoc />
    public string Key => JobKey;

    /// <inheritdoc />
    public Task RunAsync(DateTime now, CancellationToken ct = default) => dispatcher.DispatchDueAsync(ct: ct);
}
