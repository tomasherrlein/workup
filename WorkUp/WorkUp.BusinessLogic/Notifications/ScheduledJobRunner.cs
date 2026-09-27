using WorkUp.BusinessLogic.Abstractions;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// Scoped registry and runner of the scheduled jobs (T23). It is the single entry point of a tick and of the manual
/// runs of T26: <see cref="RunAllAsync"/> runs every registered job and <see cref="RunOneAsync"/> runs exactly one job
/// by its stable key.
/// <para>
/// The clock is read inside every invocation, never in the constructor and never cached in a field, so each job
/// receives the current instant and a mutated or simulated clock (T26) is picked up on the next run without rebuilding
/// the graph. A job that fails still consumes its own instant; the following job reads the clock again.
/// </para>
/// <para>
/// <see cref="RunAllAsync"/> is failure-isolated on purpose: one failing job must never starve the remaining jobs of
/// the same tick, because an outbox outage cannot be allowed to delay a time-sensitive reminder or the daily summary
/// (T24/T25). Every job is attempted, then the tick reports a single fixed safe failure. Cancellation is the one
/// exception: a cancelled caller never reaches a job invocation, and as soon as cancellation is observed — before a
/// job, between two jobs or after the last one — the tick stops immediately and <see cref="OperationCanceledException"/>
/// propagates unchanged instead of being folded into the reported failure.
/// </para>
/// <para>
/// The registry is validated once at construction, before any key is projected: a null job or an empty, whitespace or
/// duplicate <see cref="IScheduledJob.Key"/> is rejected with a fixed message that never echoes the rejected key, so
/// an untrusted key coming from a manual tool can never be reflected into a log and a keyed run can never select a
/// duplicate ambiguously.
/// </para>
/// </summary>
public sealed class ScheduledJobRunner
{
    /// <summary>
    /// Fixed, non-echoing message of a rejected registry: it never includes the rejected key, so an untrusted key
    /// coming from a manual tool cannot be reflected into a log.
    /// </summary>
    public const string InvalidKeyMessage =
        "Cada trabajo programado debe tener una clave estable, no vacía y única.";

    /// <summary>Fixed, non-echoing message of an unknown manual run key.</summary>
    public const string UnknownKeyMessage = "No hay un trabajo programado registrado con esa clave.";

    /// <summary>
    /// Fixed, non-echoing message of the failure <see cref="RunAllAsync"/> reports once after every job of the tick was
    /// attempted. It never embeds the failing exception, its message, its inner exception or any job key, so the
    /// worker's log stays safe while the next tick still retries.
    /// </summary>
    public const string JobFailureMessage = "Al menos un trabajo programado falló durante la ejecución.";

    private readonly IReadOnlyList<IScheduledJob> _jobs;
    private readonly string[] _keys;
    private readonly IClock _clock;

    public ScheduledJobRunner(IEnumerable<IScheduledJob> jobs, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(clock);

        _jobs = jobs.ToList();
        _clock = clock;

        // The null guard has to run before the key projection: projecting first would surface a bare
        // NullReferenceException instead of the fixed, documented failure.
        if (_jobs.Any(job => job is null))
            throw new InvalidOperationException(InvalidKeyMessage);

        _keys = _jobs.Select(job => job.Key).ToArray();
        if (_keys.Any(string.IsNullOrWhiteSpace)
            || _keys.Distinct(StringComparer.Ordinal).Count() != _keys.Length)
            throw new InvalidOperationException(InvalidKeyMessage);
    }

    /// <summary>Stable keys of every registered job, in registration order.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>
    /// Runs every registered job in registration order, each one with the clock instant of its own invocation. A
    /// failing job does not stop the remaining jobs; once all of them were attempted the tick reports one fixed safe
    /// failure. A cancelled caller stops the tick immediately and skips the remaining jobs.
    /// </summary>
    public async Task RunAllAsync(CancellationToken ct = default)
    {
        var failed = false;

        foreach (var job in _jobs)
        {
            // A cancelled caller never reaches an invocation, even for a job that ignores the token: without this
            // pre-check a job could still start with the clock and the database during shutdown.
            ct.ThrowIfCancellationRequested();

            try
            {
                await job.RunAsync(_clock.Now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown while a job was running: the exception propagates unchanged, the remaining jobs are not
                // started and any failure already recorded by an earlier job is not reported. Nothing is retried here,
                // so a notice the dispatcher already claimed is never redelivered by this catch.
                throw;
            }
            catch (Exception)
            {
                // The raw exception is deliberately dropped: its message, data or inner exception can carry provider
                // text, a password or a single-use link. Only one fixed failure is reported after the loop.
                failed = true;
            }
        }

        // Cancellation between the last job and the report stops the tick too, and wins over the recorded failure.
        ct.ThrowIfCancellationRequested();

        if (failed)
            throw new InvalidOperationException(JobFailureMessage);
    }

    /// <summary>
    /// Runs the single job registered with <paramref name="key"/>. An unknown key throws without running anything and
    /// without echoing the key; a cancelled caller does not reach the invocation; the job's own failure propagates
    /// unchanged, since a manual run has exactly one job to report.
    /// </summary>
    public async Task RunOneAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var job = _jobs.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(UnknownKeyMessage);

        await job.RunAsync(_clock.Now, ct).ConfigureAwait(false);
    }
}
