namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// One recurring background job of the scheduler (T23). Jobs are registered as scoped services in the application
/// composition root, so every tick of the hosted worker runs inside a fresh scope with its own <c>DbSession</c>.
/// <para>
/// <see cref="Key"/> is the stable identifier the manual runner uses (T26): it must not change once a job is
/// published because the test tools and the operator run a job by that exact string.
/// </para>
/// <para>
/// <paramref name="now"/> is read from <c>IClock</c> at the start of every invocation and passed in, so a job never
/// reads the clock itself, never caches it and works with the real clock or the simulated T26 clock without changes.
/// </para>
/// </summary>
public interface IScheduledJob
{
    /// <summary>Stable, unique, lowercase identifier of the job, for example <c>outbox</c>.</summary>
    string Key { get; }

    /// <summary>
    /// Runs one invocation of the job. <paramref name="now"/> is the instant the runner read from <c>IClock</c> for
    /// this invocation, and <paramref name="ct"/> is the scheduler's shutdown token.
    /// </summary>
    Task RunAsync(DateTime now, CancellationToken ct = default);
}
