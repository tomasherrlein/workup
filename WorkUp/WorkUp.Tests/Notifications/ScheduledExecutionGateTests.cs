using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Infrastructure.Jobs;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// T26 manual execution (plan assumptions 10 and 11): the singleton lease that serializes the automatic hosted tick
/// with the owner-only manual run. What is pinned here is the serialization itself, not the wiring: a tick waits for a
/// manual run and a manual run reports Busy instead of overlapping a tick, two manual attempts never overlap, an
/// unknown key is reported without running anything, and neither cancellation nor a failing job can leak the lease.
/// The API shape is deliberately small: <see cref="ScheduledExecutionGate.RunTickAsync"/> wraps the whole tick and
/// <see cref="ScheduledExecutionGate.TryRunOneAsync"/> answers with one of the three fixed
/// <see cref="ScheduledRunOutcome"/> values.
/// </summary>
public sealed class ScheduledExecutionGateTests
{
    private static readonly DateTime Start = new(2026, 3, 10, 9, 0, 0);

    private static ScheduledJobRunner Runner(params IScheduledJob[] jobs) =>
        new(jobs, new FakeClock { Now = Start });

    // ---- The automatic tick and a manual run never overlap ------------------------------------------------------

    [Fact]
    public async Task Manual_run_reports_busy_while_a_tick_holds_the_lease()
    {
        var gate = new ScheduledExecutionGate();
        var state = new RunState();
        var tickStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tickRunner = Runner(new GateJob("outbox", "tick", state, _ =>
        {
            tickStarted.TrySetResult();
            return release.Task;
        }));
        var manualRunner = Runner(new GateJob("outbox", "manual", state));

        var tick = gate.RunTickAsync(tickRunner, CancellationToken.None);
        await tickStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var outcome = await gate.TryRunOneAsync(manualRunner, "outbox", CancellationToken.None);

        // A manual run is a nonblocking attempt, so a tick in flight is a Busy answer instead of an overlap.
        Assert.Equal(ScheduledRunOutcome.Busy, outcome);
        Assert.Equal(new[] { "tick" }, state.Runs);

        release.TrySetResult();
        await tick.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, state.MaxActive);
    }

    [Fact]
    public async Task Tick_waits_for_a_running_manual_run_and_the_two_never_overlap()
    {
        var gate = new ScheduledExecutionGate();
        var state = new RunState();
        var manualStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tickStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualRunner = Runner(new GateJob("outbox", "manual", state, _ =>
        {
            manualStarted.TrySetResult();
            return release.Task;
        }));
        var tickRunner = Runner(new GateJob("outbox", "tick", state, _ =>
        {
            tickStarted.TrySetResult();
            return Task.CompletedTask;
        }));

        var manual = gate.TryRunOneAsync(manualRunner, "outbox", CancellationToken.None);
        await manualStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var tick = gate.RunTickAsync(tickRunner, CancellationToken.None);

        // The tick must sit on the lease instead of running beside the manual job: without the singleton it would
        // start its job immediately, which is exactly what this window catches.
        await Task.WhenAny(tickStarted.Task, Task.Delay(200));
        Assert.False(tickStarted.Task.IsCompleted, "the automatic tick overlapped a running manual run.");

        release.TrySetResult();
        Assert.Equal(ScheduledRunOutcome.Completed, await manual.WaitAsync(TimeSpan.FromSeconds(10)));
        await tick.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "manual", "tick" }, state.Runs);
        Assert.Equal(1, state.MaxActive);
    }

    [Fact]
    public async Task Two_manual_attempts_never_overlap_and_the_second_reports_busy()
    {
        var gate = new ScheduledExecutionGate();
        var state = new RunState();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Runner(new GateJob("outbox", "manual", state, _ =>
        {
            started.TrySetResult();
            return release.Task;
        }));

        var first = gate.TryRunOneAsync(runner, "outbox", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await gate.TryRunOneAsync(runner, "outbox", CancellationToken.None);

        Assert.Equal(ScheduledRunOutcome.Busy, second);
        Assert.Single(state.Runs);

        release.TrySetResult();
        Assert.Equal(ScheduledRunOutcome.Completed, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, state.MaxActive);
    }

    // ---- Cancellation and failure cannot leak the lease ----------------------------------------------------------

    [Fact]
    public async Task A_tick_cancelled_while_waiting_for_the_lease_does_not_leak_it()
    {
        var gate = new ScheduledExecutionGate();
        var state = new RunState();
        var manualStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualRunner = Runner(new GateJob("outbox", "manual", state, _ =>
        {
            manualStarted.TrySetResult();
            return release.Task;
        }));
        var quickRunner = Runner(new GateJob("outbox", "quick", state));

        var manual = gate.TryRunOneAsync(manualRunner, "outbox", CancellationToken.None);
        await manualStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var cancellation = new CancellationTokenSource();
        var tick = gate.RunTickAsync(quickRunner, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);

        release.TrySetResult();
        Assert.Equal(ScheduledRunOutcome.Completed, await manual.WaitAsync(TimeSpan.FromSeconds(10)));

        // A waiter cancelled before taking the lease took nothing with it: a fresh tick still runs.
        await gate.RunTickAsync(quickRunner, CancellationToken.None);
        Assert.Equal(new[] { "manual", "quick" }, state.Runs);
    }

    [Fact]
    public async Task A_manual_run_cancelled_mid_job_releases_the_lease()
    {
        var gate = new ScheduledExecutionGate();
        var state = new RunState();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingRunner = Runner(new GateJob("outbox", "manual", state, ct =>
        {
            started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }));
        var quickRunner = Runner(new GateJob("outbox", "quick", state));

        using var cancellation = new CancellationTokenSource();
        var manual = gate.TryRunOneAsync(blockingRunner, "outbox", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);

        // The finally released the lease even though the job threw, so the tick can still run.
        await gate.RunTickAsync(quickRunner, CancellationToken.None);
        Assert.Equal(new[] { "manual", "quick" }, state.Runs);
    }

    [Fact]
    public async Task A_failing_manual_run_releases_the_lease_and_propagates_the_raw_error()
    {
        var gate = new ScheduledExecutionGate();
        var state = new RunState();
        var failingRunner = Runner(new GateJob("outbox", "manual", state, _ =>
            Task.FromException(new InvalidOperationException("fallo-del-trabajo"))));
        var quickRunner = Runner(new GateJob("outbox", "quick", state));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.TryRunOneAsync(failingRunner, "outbox", CancellationToken.None));

        // The gate serializes; it does not sanitize. Mapping the raw failure to fixed owner-facing text is the
        // controller's job, so the error reaches it unchanged.
        Assert.Equal("fallo-del-trabajo", error.Message);

        await gate.RunTickAsync(quickRunner, CancellationToken.None);
        Assert.Equal(new[] { "manual", "quick" }, state.Runs);
    }

    // ---- Unknown key: nothing runs and nothing is echoed ---------------------------------------------------------

    [Fact]
    public async Task An_unknown_key_never_runs_a_job_and_is_reported_without_echoing_it()
    {
        var gate = new ScheduledExecutionGate();
        var state = new RunState();
        var runner = Runner(new GateJob("outbox", "outbox", state));

        Assert.Equal(
            ScheduledRunOutcome.UnknownKey,
            await gate.TryRunOneAsync(runner, "outbox-typo", CancellationToken.None));
        Assert.Equal(
            ScheduledRunOutcome.UnknownKey,
            await gate.TryRunOneAsync(runner, null!, CancellationToken.None));

        Assert.Empty(state.Runs);
    }

    // ---- Test harness -------------------------------------------------------------------------------------------

    private sealed class RunState
    {
        private readonly object _sync = new();
        private int _active;

        public List<string> Runs { get; } = [];

        public int MaxActive { get; private set; }

        public async Task RecordAsync(string label, Func<Task>? behavior)
        {
            lock (_sync)
            {
                Runs.Add(label);
                _active++;
                MaxActive = Math.Max(MaxActive, _active);
            }

            try
            {
                if (behavior is not null)
                    await behavior();
            }
            finally
            {
                lock (_sync)
                    _active--;
            }
        }
    }

    private sealed class GateJob(
        string key,
        string label,
        RunState state,
        Func<CancellationToken, Task>? behavior = null) : IScheduledJob
    {
        public string Key => key;

        public Task RunAsync(DateTime now, CancellationToken ct = default) =>
            state.RecordAsync(label, behavior is null ? null : () => behavior(ct));
    }
}
