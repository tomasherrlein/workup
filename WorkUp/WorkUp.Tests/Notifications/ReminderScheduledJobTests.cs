using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure.Jobs;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

// T24 host wiring (RF-17): the `reminders` job is the only adapter between the T23 scheduler and the reminder core.
// It must expose the stable key the manual runner of T26 uses, delegate one invocation to `ReminderService.RunAsync`
// with the instant the runner read from `IClock`, and stay free of a clock of its own, so the simulated T26 clock
// works without changing the job and the instant of a tick is never cached or re-read.
public class ReminderScheduledJobTests
{
    private static readonly DateTime Start = new(2026, 3, 12, 10, 0, 0);

    private readonly FakeReminderRepository _repository = new();

    [Fact]
    public void Job_exposes_the_stable_reminders_key()
    {
        var job = NewJob();

        Assert.Equal("reminders", ReminderScheduledJob.JobKey);
        Assert.Equal("reminders", job.Key);
    }

    [Fact]
    public async Task Job_delegates_the_run_to_the_reminder_service_with_the_supplied_instant()
    {
        var booking = _repository.AddIndividual(1, Start.AddHours(24), Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(48));
        var job = NewJob();

        await job.RunAsync(Start);

        Assert.Equal(1, _repository.ListCount);
        Assert.Equal(1, _repository.EnqueueAttempts);
        Assert.Equal(ReminderService.BatchSize, _repository.LastBatchSize);
        var notice = Assert.Single(_repository.Notifications);
        Assert.Equal(ReminderService.PendingSubject, notice.Subject);
        Assert.Equal("reminder:booking:1", notice.DedupKey);
        // The instant of the invocation, not DateTime.Now and not a value captured when the job was built.
        Assert.Equal(Start, notice.CreatedAt);
    }

    [Fact]
    public async Task Job_uses_the_instant_of_every_invocation_and_never_a_cached_one()
    {
        var first = _repository.AddIndividual(1, Start.AddHours(24), Start.AddHours(-30));
        _repository.AddLink(first, "token-1", Start.AddHours(48));
        // Its lead instant is one minute later, so only the second invocation may reach it.
        var second = _repository.AddIndividual(2, Start.AddHours(24).AddMinutes(1), Start.AddHours(-30));
        _repository.AddLink(second, "token-2", Start.AddHours(48));
        var job = NewJob();

        await job.RunAsync(Start);
        await job.RunAsync(Start.AddMinutes(1));

        Assert.Equal(
            new[] { Start, Start.AddMinutes(1) },
            _repository.Notifications.Select(notice => notice.CreatedAt).ToArray());
    }

    [Fact]
    public void Job_depends_only_on_the_reminder_service_and_holds_no_clock_it_could_re_read()
    {
        var constructor = Assert.Single(typeof(ReminderScheduledJob).GetConstructors());
        var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Contains(typeof(ReminderService), parameters);
        // A clock here would be a second, potentially stale source of time; the runner-supplied instant is the only one.
        Assert.DoesNotContain(typeof(IClock), parameters);
    }

    [Fact]
    public async Task Job_forwards_the_callers_token_and_enqueues_nothing_when_it_is_already_cancelled()
    {
        var booking = _repository.AddIndividual(1, Start.AddHours(24), Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(48));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewJob().RunAsync(Start, cancellation.Token));

        Assert.Empty(_repository.Notifications);
    }

    private ReminderScheduledJob NewJob() =>
        new(new ReminderService(_repository, new TestLinkTokenProtector(), new TestAppointmentUrlBuilder()));

    [Fact]
    public async Task Job_forwards_the_live_token_and_enqueues_nothing_when_the_caller_cancels_mid_batch()
    {
        var booking = _repository.AddIndividual(1, Start.AddHours(24), Start.AddHours(-30));
        _repository.AddLink(booking, "token-1", Start.AddHours(48));
        using var cancellation = new CancellationTokenSource();
        // The token is cancelled while the scan is running, i.e. after the invocation already started.
        _repository.AfterList = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewJob().RunAsync(Start, cancellation.Token));

        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Job_propagates_a_repository_failure_unchanged_instead_of_swallowing_it()
    {
        var job = new ReminderScheduledJob(
            new ReminderService(new FailingReminderRepository(), new TestLinkTokenProtector(), new TestAppointmentUrlBuilder()));

        // Sanitising the failure is the runner's job; this adapter must not swallow it nor rewrite its type.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(Start));

        Assert.Equal("fallo-del-repositorio", error.Message);
    }

    /// <summary>Repository whose scan fails, so the adapter's failure-propagation contract can be observed.</summary>
    private sealed class FailingReminderRepository : IReminderRepository
    {
        public Task<IReadOnlyList<ReminderCandidate>> ListCandidatesAsync(
            DateTime now, int batchSize, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<ReminderCandidate>>(
                new InvalidOperationException("fallo-del-repositorio"));

        public Task<bool> TryEnqueueReminderAsync(ReminderNotice notice, CancellationToken ct = default) =>
            throw new InvalidOperationException("fallo-del-repositorio");
    }

    /// <summary>Fake protector, so no real data-protection keyring is read or written by these tests.</summary>
    private sealed class TestLinkTokenProtector : ILinkTokenProtector
    {
        public string Protect(string token) => "protected:" + token;

        public string Unprotect(string protectedToken) => protectedToken["protected:".Length..];
    }

    private sealed class TestAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://workup.test/t/" + token;
    }
}
