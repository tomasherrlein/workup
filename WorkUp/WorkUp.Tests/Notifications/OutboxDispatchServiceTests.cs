using System.Data;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Outbox dispatch core (T23). It proves the service contract against hand-written ports: <c>Retain</c> mode leaves
/// every notice <c>Pending</c> and touches nothing, <c>Deliver</c> mode claims the due row with the oldest effective due
/// instant and commits that claim in its own transaction <em>before</em> the sender runs, the completion opens a fresh
/// transaction, a confirmed send is the only outcome marked <c>Sent</c>, a provably-not-sent transient failure retries
/// with a bounded backoff up to three attempts and then becomes terminal <c>Failed</c>, a permanent failure is
/// immediately terminal, and an unknown outcome or an exception becomes <c>ReviewRequired</c> that is never reclaimed
/// automatically. Attempts are counted at claim time, before the send, so an interrupted or uncertain attempt is
/// still persisted, and the due predicate is the effective instant <c>NextAttemptAt ?? CreatedAt</c>, so a rewound
/// simulated clock (T26) makes a row wait instead of treating a null <c>NextAttemptAt</c> as unconditionally due.
/// <para>
/// The fake cannot prove the SQL padlock or the atomic <c>UPDATE ... OUTPUT</c> claim, so a SQL fixture is still
/// required for that. What this file proves is the transaction shape the sequential SQL repository must honour: the
/// claim is committed and its owned transaction disposed before the send, and the completion begins a new transaction.
/// </para>
/// </summary>
public sealed class OutboxDispatchServiceTests
{
    private static readonly DateTime Start = new(2026, 3, 10, 9, 0, 0);

    private readonly FakeOutboxRepository _repository = new();
    private readonly SessionUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new() { Now = Start };
    private readonly FakeEmailSender _sender = new();

    private OutboxDispatchService NewService(OutboxMode mode = OutboxMode.Deliver) =>
        new(_repository, _sender, _unitOfWork, _clock, mode);

    private Task<int> DispatchAsync(OutboxDispatchService service, int batchSize = 10) =>
        service.DispatchDueAsync(batchSize, CancellationToken.None);

    [Fact]
    public void Notification_status_keeps_the_persisted_values_and_adds_dispatching_and_review_required()
    {
        Assert.Equal(1, (byte)NotificationStatus.Pending);
        Assert.Equal(2, (byte)NotificationStatus.Sent);
        Assert.Equal(3, (byte)NotificationStatus.Failed);
        Assert.Equal(4, (byte)NotificationStatus.Dispatching);
        Assert.Equal(5, (byte)NotificationStatus.ReviewRequired);
    }

    [Theory]
    [InlineData("Outbox", OutboxMode.Retain)]
    [InlineData("outbox", OutboxMode.Retain)]
    [InlineData("Smtp", OutboxMode.Deliver)]
    [InlineData("SMTP", OutboxMode.Deliver)]
    [InlineData(" smtp ", OutboxMode.Deliver)]
    [InlineData("", OutboxMode.Retain)]
    [InlineData("   ", OutboxMode.Retain)]
    [InlineData(null, OutboxMode.Retain)]
    public void Email_mode_parses_known_values_and_defaults_blank_to_retain(string? configured, OutboxMode expected)
    {
        Assert.Equal(expected, OutboxModeParser.Parse(configured));
    }

    [Theory]
    [InlineData("imap")]
    [InlineData("Smpt")]
    [InlineData("Smtp2")]
    [InlineData("true")]
    public void An_unknown_email_mode_fails_fast_instead_of_silently_retaining(string configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() => OutboxModeParser.Parse(configured));
        Assert.Contains(configured, error.Message);
    }

    [Fact]
    public async Task Retain_mode_leaves_the_outbox_untouched()
    {
        var notice = _repository.Seed("booking:1", Start);
        var service = NewService(OutboxMode.Retain);

        var processed = await DispatchAsync(service);

        Assert.Equal(0, processed);
        Assert.Equal(0, _repository.ClaimCount);
        Assert.Equal(0, _sender.CallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(NotificationStatus.Pending, notice.Status);
        Assert.Null(notice.ClaimedAt);
        Assert.Null(notice.DispatchKey);
        Assert.Null(notice.LastError);
    }

    [Fact]
    public async Task Claim_is_committed_and_disposed_before_the_sender_runs_and_completion_is_a_fresh_transaction()
    {
        _repository.Seed("booking:1", Start);
        var openAtSend = true;
        var commitsAtSend = -1;
        _sender.OnSend = _ =>
        {
            openAtSend = _unitOfWork.HasOpenTransaction;
            commitsAtSend = _unitOfWork.CommitCount;
        };

        var processed = await DispatchAsync(NewService(), batchSize: 1);

        Assert.Equal(1, processed);
        Assert.False(openAtSend);
        Assert.Equal(1, commitsAtSend);
        Assert.Equal(2, _unitOfWork.BeginCount);
        Assert.Equal(2, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.JoinedCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Confirmed_success_is_the_only_outcome_marked_sent()
    {
        var notice = _repository.Seed("booking:1", Start);

        var processed = await DispatchAsync(NewService());

        Assert.Equal(1, processed);
        Assert.Equal(NotificationStatus.Sent, notice.Status);
        Assert.Equal(Start, notice.SentAt);
        Assert.Equal(1, notice.Attempts);
        Assert.Null(notice.LastError);
        Assert.Null(notice.NextAttemptAt);
        Assert.Null(notice.ClaimedAt);
        Assert.Null(notice.DispatchKey);
        var message = Assert.Single(_sender.Messages);
        Assert.Equal("ana@example.com", message.Recipient);
        Assert.Equal("Turno confirmado", message.Subject);
        Assert.Equal("<p>Cuerpo del aviso</p>", message.BodyHtml);
    }

    [Fact]
    public async Task Oldest_due_notice_is_claimed_first_and_the_batch_is_finite()
    {
        var oldest = _repository.Seed("booking:1", Start.AddMinutes(-30), subject: "Aviso 1");
        var middle = _repository.Seed("booking:2", Start.AddMinutes(-20), subject: "Aviso 2");
        var newest = _repository.Seed("booking:3", Start.AddMinutes(-10), subject: "Aviso 3");
        var service = NewService();

        var processed = await DispatchAsync(service, batchSize: 2);

        Assert.Equal(2, processed);
        Assert.Equal(["Aviso 1", "Aviso 2"], _sender.Messages.Select(message => message.Subject));
        Assert.Equal(NotificationStatus.Sent, oldest.Status);
        Assert.Equal(NotificationStatus.Sent, middle.Status);
        Assert.Equal(NotificationStatus.Pending, newest.Status);

        var rest = await DispatchAsync(service, batchSize: 2);

        Assert.Equal(1, rest);
        Assert.Equal(NotificationStatus.Sent, newest.Status);
    }

    [Fact]
    public async Task Claim_order_follows_the_effective_due_instant_not_the_enqueue_time()
    {
        // An older enqueue whose retry only becomes due now, versus a newer enqueue that is already due.
        var retry = _repository.Seed("booking:1", Start.AddMinutes(-60), nextAttemptAt: Start, subject: "Reintento");
        var fresh = _repository.Seed("booking:2", Start.AddMinutes(-10), subject: "Nuevo");
        var service = NewService();

        await DispatchAsync(service, batchSize: 1);
        Assert.Equal(["Nuevo"], _sender.Messages.Select(message => message.Subject));

        await DispatchAsync(service, batchSize: 1);
        Assert.Equal(["Nuevo", "Reintento"], _sender.Messages.Select(message => message.Subject));
        Assert.Equal(NotificationStatus.Sent, retry.Status);
        Assert.Equal(NotificationStatus.Sent, fresh.Status);
    }

    [Fact]
    public async Task The_claim_counts_the_started_attempt_before_the_sender_runs()
    {
        var notice = _repository.Seed("booking:1", Start);
        var attemptsAtSend = -1;
        _sender.OnSend = _ => attemptsAtSend = notice.Attempts;

        await DispatchAsync(NewService(), batchSize: 1);

        Assert.Equal(1, attemptsAtSend);
        Assert.Equal(1, notice.Attempts);
    }

    [Fact]
    public async Task A_fresh_notice_is_not_due_before_its_creation_instant_when_the_clock_is_rewound()
    {
        var notice = _repository.Seed("booking:1", Start);
        _clock.Now = Start.AddMinutes(-1);
        var service = NewService();

        Assert.Equal(0, await DispatchAsync(service));
        Assert.Equal(0, _repository.ClaimCount);
        Assert.Equal(0, _sender.CallCount);
        Assert.Equal(NotificationStatus.Pending, notice.Status);

        _clock.Now = Start;
        Assert.Equal(1, await DispatchAsync(service));
        Assert.Equal(NotificationStatus.Sent, notice.Status);
    }

    [Fact]
    public async Task A_retry_scheduled_for_the_future_is_not_due()
    {
        var notice = _repository.Seed("booking:1", Start, nextAttemptAt: Start.AddMinutes(1));

        var processed = await DispatchAsync(NewService());

        Assert.Equal(0, processed);
        Assert.Equal(0, _repository.ClaimCount);
        Assert.Equal(0, _sender.CallCount);
        Assert.Equal(NotificationStatus.Pending, notice.Status);
    }

    [Fact]
    public async Task Retryable_failure_backs_off_and_fails_terminally_on_the_third_attempt()
    {
        var notice = _repository.Seed("booking:1", Start);
        _sender.Handler = _ => EmailSendOutcome.DefinitelyNotSentRetryable;
        var service = NewService();

        await DispatchAsync(service);
        Assert.Equal(NotificationStatus.Pending, notice.Status);
        Assert.Equal(1, notice.Attempts);
        Assert.Equal(Start.AddMinutes(1), notice.NextAttemptAt);
        Assert.NotNull(notice.LastError);

        _clock.Now = Start.AddMinutes(1);
        await DispatchAsync(service);
        Assert.Equal(NotificationStatus.Pending, notice.Status);
        Assert.Equal(2, notice.Attempts);
        Assert.Equal(Start.AddMinutes(6), notice.NextAttemptAt);

        _clock.Now = Start.AddMinutes(6);
        await DispatchAsync(service);
        Assert.Equal(NotificationStatus.Failed, notice.Status);
        Assert.Equal(3, notice.Attempts);
        Assert.Null(notice.NextAttemptAt);
        Assert.Equal(3, _sender.CallCount);

        // A terminal failure is never reclaimed, however far the clock advances.
        _clock.Now = Start.AddDays(30);
        await DispatchAsync(service);
        Assert.Equal(3, _sender.CallCount);
        Assert.Equal(NotificationStatus.Failed, notice.Status);
    }

    [Fact]
    public async Task Completion_stamps_the_retry_backoff_from_the_clock_read_after_the_send()
    {
        var notice = _repository.Seed("booking:1", Start);
        _sender.Handler = _ => EmailSendOutcome.DefinitelyNotSentRetryable;
        _sender.OnSend = _ => _clock.Now = Start.AddMinutes(7);

        await DispatchAsync(NewService(), batchSize: 1);

        // ClaimedAt was read at claim; the retry deadline must come from the completion clock, not a stale capture.
        Assert.Equal(1, notice.Attempts);
        Assert.Equal(Start.AddMinutes(8), notice.NextAttemptAt);
        Assert.Equal(Start, notice.CreatedAt);
    }

    [Fact]
    public async Task Permanent_definite_failure_is_immediately_terminal()
    {
        var notice = _repository.Seed("booking:1", Start);
        _sender.Handler = _ => EmailSendOutcome.DefinitelyNotSentPermanent;

        var processed = await DispatchAsync(NewService());

        Assert.Equal(0, processed);
        Assert.Equal(NotificationStatus.Failed, notice.Status);
        Assert.Equal(1, notice.Attempts);
        Assert.Null(notice.NextAttemptAt);
        Assert.NotNull(notice.LastError);

        _clock.Now = Start.AddDays(1);
        await DispatchAsync(NewService());
        Assert.Equal(1, _sender.CallCount);
    }

    [Fact]
    public async Task Unknown_outcome_requires_manual_review_and_is_never_reclaimed()
    {
        var bodyToken = "token-de-un-solo-uso";
        var notice = _repository.Seed("booking:1", Start, bodyHtml: $"<p>{bodyToken}</p>");
        _sender.Handler = _ => EmailSendOutcome.OutcomeUnknown;

        var processed = await DispatchAsync(NewService());

        Assert.Equal(0, processed);
        Assert.Equal(NotificationStatus.ReviewRequired, notice.Status);
        Assert.Equal(Start, notice.ClaimedAt);
        Assert.NotNull(notice.DispatchKey);
        Assert.Equal(1, notice.Attempts);
        Assert.Equal(OutboxDispatchService.UnknownReason, notice.LastError);
        Assert.DoesNotContain(bodyToken, notice.LastError);

        _clock.Now = Start.AddDays(30);
        await DispatchAsync(NewService());
        Assert.Equal(1, _sender.CallCount);
        Assert.Equal(NotificationStatus.ReviewRequired, notice.Status);
    }

    [Fact]
    public async Task Sender_exception_becomes_review_required_without_storing_provider_text()
    {
        const string secret = "smtp-password=SuperSecreto";
        var bodyToken = "token-de-un-solo-uso";
        var notice = _repository.Seed("booking:1", Start, bodyHtml: $"<p>{bodyToken}</p>");
        _sender.Throw = new InvalidOperationException($"535 auth failed {secret}");

        var processed = await DispatchAsync(NewService());

        Assert.Equal(0, processed);
        Assert.Equal(NotificationStatus.ReviewRequired, notice.Status);
        Assert.Equal(OutboxDispatchService.UnknownReason, notice.LastError);
        Assert.Equal(1, notice.Attempts);
        Assert.DoesNotContain("SuperSecreto", notice.LastError);
        Assert.DoesNotContain(secret, notice.LastError);
        Assert.DoesNotContain(bodyToken, notice.LastError);
        Assert.DoesNotContain(notice.Subject, notice.LastError);
        Assert.DoesNotContain(notice.Recipient, notice.LastError);
    }

    [Fact]
    public async Task Cancellation_while_dispatching_leaves_the_claim_for_manual_review()
    {
        var notice = _repository.Seed("booking:1", Start);
        _sender.Throw = new OperationCanceledException("envío cancelado");
        var service = NewService();

        await Assert.ThrowsAsync<OperationCanceledException>(() => DispatchAsync(service));

        Assert.Equal(NotificationStatus.Dispatching, notice.Status);
        Assert.Equal(Start, notice.ClaimedAt);
        Assert.NotNull(notice.DispatchKey);
        Assert.Null(notice.SentAt);
        // The claim already counted the started attempt, before the cancelled send.
        Assert.Equal(1, notice.Attempts);
        Assert.Equal(1, _unitOfWork.CommitCount);

        // A later tick must not reclaim a row left Dispatching: only a human resolves it.
        _clock.Now = Start.AddDays(1);
        await DispatchAsync(service);
        Assert.Equal(1, _sender.CallCount);
        Assert.Equal(NotificationStatus.Dispatching, notice.Status);
    }

    [Fact]
    public async Task A_stale_claim_is_not_completed()
    {
        var notice = _repository.Seed("booking:1", Start);
        _sender.OnSend = _ => _repository.StealClaim(notice.Id);

        var processed = await DispatchAsync(NewService(), batchSize: 1);

        Assert.Equal(0, processed);
        Assert.Equal(NotificationStatus.Dispatching, notice.Status);
        Assert.Null(notice.SentAt);
        Assert.Equal(1, _repository.StaleFinalizeCount);
    }

    /// <summary>
    /// Faithful stand-in for <see cref="WorkUp.Data.DbSession"/> as <see cref="IUnitOfWork"/>: the owned transaction
    /// stays attached after <see cref="ITransaction.CommitAsync"/> and is only detached at disposal, and a
    /// <c>BeginAsync</c> while one is still attached returns a joined no-op transaction. The shared
    /// <see cref="FakeUnitOfWork"/> does not model that, so it cannot catch the bug this class exists to catch: a
    /// completion write silently joining the already committed claim transaction and never reaching SQL.
    /// </summary>
    private sealed class SessionUnitOfWork : IUnitOfWork
    {
        public int BeginCount { get; private set; }

        public int CommitCount { get; private set; }

        public int JoinedCount { get; private set; }

        public int RolledBackCount { get; private set; }

        public bool HasOpenTransaction { get; private set; }

        public Task<ITransaction> BeginAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default)
        {
            BeginCount++;
            if (HasOpenTransaction)
            {
                JoinedCount++;
                return Task.FromResult<ITransaction>(Joined.Instance);
            }

            HasOpenTransaction = true;
            return Task.FromResult<ITransaction>(new Owned(this));
        }

        private sealed class Owned(SessionUnitOfWork owner) : ITransaction
        {
            private bool _committed;

            public Task CommitAsync(CancellationToken ct = default)
            {
                _committed = true;
                owner.CommitCount++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!_committed)
                    owner.RolledBackCount++;

                // Only disposal detaches the transaction, exactly like DbSession.
                owner.HasOpenTransaction = false;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class Joined : ITransaction
        {
            public static readonly Joined Instance = new();

            public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
