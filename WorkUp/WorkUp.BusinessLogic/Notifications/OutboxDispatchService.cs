using WorkUp.BusinessLogic.Abstractions;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Notifications;

/// <summary>
/// Dispatches the queued outbox across every business (T23). The claim is global rather than tenant-scoped: the sender
/// is a single installation-wide queue, each notice keeps its own <see cref="Notification.BusinessId"/>, and the
/// recipient is already resolved inside the notice. One call processes a finite batch: it claims due notices one by
/// one and finishes each one before moving to the next.
/// <para>
/// The transaction shape is the core of the contract. A claim commits in its own transaction and that transaction is
/// disposed <em>before</em> the sender runs, so the completion opens a genuinely new transaction. This is not cosmetic:
/// <c>DbSession</c> keeps a committed transaction attached to the session until disposal, so a completion issued while
/// the claim transaction was still attached would join it as a no-op and never reach SQL.
/// </para>
/// <para>
/// No storage can promise exactly-once SMTP delivery across a crash between the send and the confirmation. The service
/// therefore prioritises avoiding a duplicate automatic send over guaranteeing immediate delivery: only a confirmed
/// send marks the notice <c>Sent</c>, only a failure that was provably before the send is retried (at most
/// <see cref="MaxAttempts"/> attempts, with a bounded backoff), and everything uncertain becomes <c>ReviewRequired</c>
/// for a human, never redelivered automatically. A notice left <c>Dispatching</c> by a cancellation is also resolved by
/// a human, because the dispatcher cannot know whether the provider accepted it.
/// </para>
/// <para>
/// Nothing here logs or stores raw email content or provider text: <see cref="Notification.LastError"/> only ever
/// receives one of the fixed safe reasons of this class.
/// </para>
/// </summary>
public sealed class OutboxDispatchService(
    IOutboxRepository repository,
    IEmailSender sender,
    IUnitOfWork unitOfWork,
    IClock clock,
    OutboxMode mode)
{
    /// <summary>Default number of notices one tick processes, so a large outbox never blocks the loop.</summary>
    public const int DefaultBatchSize = 20;

    /// <summary>Total attempts of a notice before a retryable failure becomes terminal <c>Failed</c>.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Delay before the second attempt, after the first retryable failure.</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>Delay before the third and last attempt, after the second retryable failure.</summary>
    public static readonly TimeSpan SecondRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>Safe reason stored after a failure that was provably before the send.</summary>
    public const string RetryReason = "El envío falló antes de salir; se reintentará.";

    /// <summary>Safe reason stored after a permanent failure.</summary>
    public const string PermanentReason = "El proveedor rechazó el envío de forma definitiva.";

    /// <summary>Safe reason stored when the retry budget is exhausted.</summary>
    public const string RetriesExhaustedReason = "Se agotaron los reintentos del envío.";

    /// <summary>Safe reason stored after an uncertain outcome or an exception.</summary>
    public const string UnknownReason = "Resultado de envío incierto; requiere revisión manual.";

    public OutboxMode Mode => mode;

    /// <summary>
    /// Processes at most <paramref name="batchSize"/> due notices and returns how many were confirmed <c>Sent</c>.
    /// In <see cref="OutboxMode.Retain"/> it does nothing at all: no claim, no send and no status change. It stops
    /// early when the outbox has no more due notices, so a tick never spins.
    /// </summary>
    public async Task<int> DispatchDueAsync(int batchSize = DefaultBatchSize, CancellationToken ct = default)
    {
        if (mode != OutboxMode.Deliver || batchSize <= 0)
            return 0;

        var sent = 0;
        for (var processed = 0; processed < batchSize; processed++)
        {
            var dispatchKey = Guid.NewGuid();
            var notification = await ClaimOneAsync(dispatchKey, ct);
            if (notification is null)
                break;

            if (await CompleteAsync(notification, dispatchKey, ct))
                sent++;
        }

        return sent;
    }

    /// <summary>
    /// Claims the next due notice and commits the claim in its own transaction. The lexically closed scope matters: the
    /// owned transaction must be disposed here, before the sender runs, so the completion starts a fresh transaction.
    /// </summary>
    private async Task<Notification?> ClaimOneAsync(Guid dispatchKey, CancellationToken ct)
    {
        Notification? notification;
        await using (var transaction = await unitOfWork.BeginAsync(ct: ct))
        {
            notification = await repository.ClaimNextDueAsync(dispatchKey, clock.Now, ct);
            if (notification is null)
                return null;

            await transaction.CommitAsync(ct);
        }

        return notification;
    }

    /// <summary>
    /// Sends one claimed notice and finalises it in a separate transaction. Returns whether the notice was confirmed
    /// <c>Sent</c>.
    /// </summary>
    private async Task<bool> CompleteAsync(Notification notification, Guid dispatchKey, CancellationToken ct)
    {
        EmailSendOutcome outcome;
        try
        {
            outcome = await sender.SendAsync(
                new EmailMessage(notification.Recipient, notification.Subject, notification.BodyHtml), ct);
        }
        catch (OperationCanceledException)
        {
            // The dispatcher is stopping while the provider call is in flight and cannot know whether the message was
            // accepted. The claim is deliberately left Dispatching with its ClaimedAt and DispatchKey: the row is not
            // Pending, so no later tick reclaims it, and a human resolves it.
            throw;
        }
        catch (Exception)
        {
            // Provider text can carry a password or an SMTP error; only the typed outcome crosses this boundary.
            outcome = EmailSendOutcome.OutcomeUnknown;
        }

        // Read the clock again at completion; a time captured at claim time would misdate the retry deadline.
        var completedAt = clock.Now;
        // The atomic claim already counted this started attempt before the sender ran, so never increment again here.
        var attempts = notification.Attempts;
        bool applied;

        await using (var transaction = await unitOfWork.BeginAsync(ct: ct))
        {
            applied = outcome switch
            {
                EmailSendOutcome.Sent =>
                    await repository.MarkSentAsync(notification.Id, dispatchKey, completedAt, attempts, ct),
                EmailSendOutcome.DefinitelyNotSentPermanent =>
                    await repository.MarkFailedAsync(notification.Id, dispatchKey, PermanentReason, attempts, ct),
                EmailSendOutcome.DefinitelyNotSentRetryable =>
                    await ScheduleRetryAsync(notification.Id, dispatchKey, attempts, completedAt, ct),
                _ => await repository.MarkForReviewAsync(notification.Id, dispatchKey, UnknownReason, attempts, ct)
            };
            await transaction.CommitAsync(ct);
        }

        return outcome == EmailSendOutcome.Sent && applied;
    }

    /// <summary>
    /// Re-<c>Pending</c>s a notice that was provably not sent, with the bounded backoff, or makes it terminal once the
    /// attempt budget is exhausted.
    /// </summary>
    private async Task<bool> ScheduleRetryAsync(int notificationId, Guid dispatchKey, int attempts, DateTime now, CancellationToken ct)
    {
        if (attempts >= MaxAttempts)
            return await repository.MarkFailedAsync(notificationId, dispatchKey, RetriesExhaustedReason, attempts, ct);

        var delay = attempts == 1 ? FirstRetryDelay : SecondRetryDelay;
        return await repository.MarkForRetryAsync(notificationId, dispatchKey, now + delay, RetryReason, attempts, ct);
    }
}
