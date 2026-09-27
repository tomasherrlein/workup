using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure.Notifications;

/// <summary>
/// Fail-closed, unreachable <see cref="IEmailSender"/> used while the mode is <c>Outbox</c> (T23). The registered
/// dispatcher short-circuits in <c>Retain</c> mode before claiming a notice, so this sender must never be called.
/// <para>
/// The choice of failing loudly instead of returning an outcome is deliberate: mapping the call to
/// <see cref="EmailSendOutcome.Sent"/> would violate the explicit decision that an <c>Outbox</c> notice stays
/// <c>Pending</c>, while any other outcome would move the row out of <c>Pending</c> (retry, failed or manual review).
/// Throwing keeps the invariant visible and guarantees that no caller can mistake a retained notice for a delivered
/// one. The message is fixed and never echo the recipient, the subject or the body.
/// </para>
/// </summary>
public sealed class RetainEmailSender : IEmailSender
{
    /// <summary>Fixed, non-echoing message of the invariant violation.</summary>
    public const string UnreachableMessage =
        "El modo Outbox conserva los avisos y no contacta ningún proveedor de correo.";

    /// <inheritdoc />
    public Task<EmailSendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default) =>
        throw new InvalidOperationException(UnreachableMessage);
}
