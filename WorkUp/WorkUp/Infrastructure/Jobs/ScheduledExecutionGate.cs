using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure.Jobs;

/// <summary>
/// Outcome of one owner-only manual attempt (<c>TryRunOneAsync</c>), fixed to three cases so the controller never has
/// to interpret an exception to decide between a run, a refusal and a missing job.
/// </summary>
public enum ScheduledRunOutcome
{
    /// <summary>The single requested job ran to completion.</summary>
    Completed,

    /// <summary>The lease was already taken by a tick or another manual run, so nothing was executed.</summary>
    Busy,

    /// <summary>No registered job carries that key, so nothing was executed and the key is never echoed.</summary>
    UnknownKey
}

/// <summary>
/// Singleton execution lease of the scheduler (T26, plan assumptions 10 and 11). It serializes the automatic tick of
/// <see cref="ScheduledJobsHostedService"/> with the owner-only manual run of the DevTools controller, so the same
/// scoped <see cref="ScheduledJobRunner"/> graph can never run one job twice at once.
/// <para>
/// The lease is a <see cref="SemaphoreSlim"/> of one permit and lasts exactly as long as the wrapped execution:
/// <see cref="RunTickAsync"/> waits for the permit (honouring the shutdown token) before <c>RunAllAsync</c> and always
/// releases it afterwards, while <see cref="TryRunOneAsync"/> takes it nonblocking and answers
/// <see cref="ScheduledRunOutcome.Busy"/> instead of queueing a manual click behind a tick.
/// </para>
/// <para>
/// Cancellation and failure cannot leak the lease: the permit is only released in a <c>finally</c> that runs after the
/// permit was really taken, so a waiter cancelled before acquiring releases nothing, and a job that throws — including
/// <see cref="OperationCanceledException"/> — still frees the lease for the next tick. The gate serializes; it does not
/// sanitize, so a job's raw failure propagates unchanged and the controller is the layer that maps it to fixed,
/// content-free owner-facing text.
/// </para>
/// </summary>
public class ScheduledExecutionGate
{
    private readonly SemaphoreSlim _lease = new(1, 1);

    /// <summary>
    /// Runs one whole tick while holding the lease. It waits for the permit with <paramref name="ct"/>, so a manual run
    /// in flight only delays the tick instead of overlapping it, and a shutdown while waiting throws
    /// <see cref="OperationCanceledException"/> without having taken the lease. Virtual only as a testing seam so the
    /// hosted worker's use of the lease can be observed without racing; production always runs this implementation.
    /// </summary>
    public virtual async Task RunTickAsync(ScheduledJobRunner runner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);

        await _lease.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await runner.RunAllAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lease.Release();
        }
    }

    /// <summary>
    /// Attempts exactly one manual run. An unknown (or missing) key is answered with
    /// <see cref="ScheduledRunOutcome.UnknownKey"/> before the lease is touched, so an untrusted key can never select a
    /// job and is never echoed. A lease already taken by a tick or another manual run is answered nonblocking with
    /// <see cref="ScheduledRunOutcome.Busy"/>. Otherwise the requested job runs and the lease is released even when the
    /// job throws or is cancelled.
    /// </summary>
    public async Task<ScheduledRunOutcome> TryRunOneAsync(
        ScheduledJobRunner runner,
        string key,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);

        // Keys are immutable once the runner is built, so the check is race-free and keeps a bogus key from ever
        // reaching the job graph or the execution lease.
        if (key is null || !runner.Keys.Contains(key, StringComparer.Ordinal))
            return ScheduledRunOutcome.UnknownKey;

        if (!await _lease.WaitAsync(0, ct).ConfigureAwait(false))
            return ScheduledRunOutcome.Busy;

        try
        {
            await runner.RunOneAsync(key, ct).ConfigureAwait(false);
            return ScheduledRunOutcome.Completed;
        }
        finally
        {
            _lease.Release();
        }
    }
}
