using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure.Jobs;

/// <summary>
/// Hosted worker of the scheduler (T23). It runs one immediate tick at startup and then repeats on a
/// <see cref="PeriodicTimer"/> every minute, forever until the host stops.
/// <para>
/// Every tick runs inside a scope created and disposed by the tick itself, so two ticks can never share a
/// <c>DbSession</c> and no scoped state leaks between them. The ticks are strictly sequential: the worker awaits the
/// current tick before waiting for the next timer signal, so a slow tick never overlaps the next one and missed ticks
/// are not queued up.
/// </para>
/// <para>
/// A failing tick is never fatal: it is logged and the next tick still runs, because a transient database or provider
/// problem must not silently stop every future email. The log carries only the fixed <see cref="TickFailedMessage"/>
/// and the exception <em>type</em>; the exception object, its message and the notice content are never logged, since a
/// provider error can contain a password and a notice can contain a single-use link.
/// </para>
/// <para>
/// Every tick runs through the singleton <see cref="ScheduledExecutionGate"/>: it takes the execution lease before
/// <see cref="ScheduledJobRunner.RunAllAsync"/> and releases it when the tick finishes, so a tick and an owner-only
/// manual run can never overlap and the manual run is answered Busy instead of queueing behind the tick. The lease is
/// released on cancellation and failure alike, so a failed or cancelled tick never blocks the next one.
/// </para>
/// <para>
/// The worker deliberately has no job-enabled gate. In the default <c>Outbox</c> mode the outbox job claims nothing and
/// contacts no provider, but it is not SQL-free: the reminder, daily-summary, stock-review and membership-expiry jobs
/// of T24/T25 each perform their own bounded scan every tick, so a running installation does read SQL even while
/// notices are only retained. <see cref="TickCompletedMessage"/> still proves that the immediate tick ran.
/// </para>
/// </summary>
public class ScheduledJobsHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledJobsHostedService> logger) : BackgroundService
{
    /// <summary>How often the scheduler repeats its tick after the immediate one.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Fixed, content-free marker logged after every completed tick.</summary>
    public const string TickCompletedMessage = "WorkUp scheduled jobs tick completed.";

    /// <summary>Fixed, content-free message logged when a tick fails and the next tick will still run.</summary>
    public const string TickFailedMessage = "WorkUp scheduled jobs tick failed; the next tick will still run.";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // One immediate tick: a restart reconciles the outbox without waiting a whole interval.
        do
        {
            if (!await RunTickAsync(stoppingToken))
                return;
        }
        while (await WaitForNextTickOrStopAsync(timer, stoppingToken));
    }

    /// <summary>
    /// Waits for the next periodic signal. It is virtual only as a testing seam so a test can drive several ticks
    /// without waiting a real minute; production always waits on the <see cref="PeriodicTimer"/>.
    /// </summary>
    protected virtual Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct) =>
        timer.WaitForNextTickAsync(ct).AsTask();

    private async Task<bool> WaitForNextTickOrStopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await WaitForNextTickAsync(timer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs one tick in a fresh scope. Returns false when the worker must stop, true when it must keep ticking.
    /// </summary>
    private async Task<bool> RunTickAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var gate = scope.ServiceProvider.GetRequiredService<ScheduledExecutionGate>();
            var runner = scope.ServiceProvider.GetRequiredService<ScheduledJobRunner>();
            // The singleton lease serializes this tick with the manual run action; the runner and the jobs stay scoped.
            await gate.RunTickAsync(runner, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown while a job was running: the scope is already disposed and the worker stops without reporting.
            return false;
        }
        catch (Exception exception)
        {
            // Only the type is logged, never the exception object or its message: a provider error can carry a
            // password and a notice can carry a single-use link.
            logger.LogError("{Message} Error type: {ErrorType}", TickFailedMessage, exception.GetType().Name);
            return true;
        }

        logger.LogInformation(TickCompletedMessage);
        return true;
    }
}
