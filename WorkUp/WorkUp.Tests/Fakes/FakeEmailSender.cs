using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// Scripted email sender (T23). It records every attempted message, runs an optional callback at send time so a test
/// can inspect the dispatcher state mid-attempt, returns a scripted <see cref="EmailSendOutcome"/>, or throws to
/// simulate a provider failure whose raw text must never reach <c>LastError</c>. It stores and logs nothing on its own.
/// </summary>
public sealed class FakeEmailSender : IEmailSender
{
    /// <summary>Scripted outcome per message. Defaults to a confirmed <see cref="EmailSendOutcome.Sent"/>.</summary>
    public Func<EmailMessage, EmailSendOutcome> Handler { get; set; } = _ => EmailSendOutcome.Sent;

    public List<EmailMessage> Messages { get; } = [];

    public int CallCount => Messages.Count;

    /// <summary>Runs before the scripted outcome is returned or the scripted exception is thrown.</summary>
    public Action<EmailMessage>? OnSend { get; set; }

    /// <summary>Exception to throw instead of returning an outcome, to simulate an unknown provider failure.</summary>
    public Exception? Throw { get; set; }

    public Task<EmailSendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Messages.Add(message);
        OnSend?.Invoke(message);
        if (Throw is not null)
            throw Throw;

        return Task.FromResult(Handler(message));
    }
}
