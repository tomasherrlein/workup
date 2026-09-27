using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure.Notifications;

/// <summary>
/// The real SMTP adapter behind <see cref="IEmailSender"/> (T23). It sends through MailKit's
/// <see cref="MailKit.Net.Smtp.SmtpClient"/> and turns every attempt into one of the typed outcomes of
/// <see cref="EmailSendOutcome"/> instead of throwing.
/// <para>
/// The outcome rules mirror the dispatcher's duplicate-avoidance contract:
/// <list type="bullet">
/// <item>Only a completed MailKit <c>SendAsync</c> — the server's final acceptance reply — counts as
/// <see cref="EmailSendOutcome.Sent"/>: it means the server accepted the message, not that it reached an inbox. A
/// transport that returns successfully without that final reply (for example a graceful close after the data
/// terminator) is not accepted as <c>Sent</c>.</item>
/// <item>A locally invalid recipient or a subject carrying a header break (<c>CR</c>/<c>LF</c>) or a NUL byte is
/// <see cref="EmailSendOutcome.DefinitelyNotSentPermanent"/> before any socket opens, so an injection attempt or a
/// malformed destination can never be retried or sent.</item>
/// <item>Every SMTP or transport failure <em>after</em> the send started is conservatively
/// <see cref="EmailSendOutcome.OutcomeUnknown"/>, including a 450 temporary rejection and a disconnect during the
/// SMTP transaction. The adapter never reports <see cref="EmailSendOutcome.DefinitelyNotSentRetryable"/> and never
/// risks a duplicate: a human resolves the unknown outcome.</item>
/// <item>A cancellation propagates as <see cref="OperationCanceledException"/> so the claimed notice stays
/// <c>Dispatching</c> for manual review instead of being retried.</item>
/// </list>
/// </para>
/// <para>
/// Global settings — <c>WorkUp:Email:From</c>, <c>WorkUp:Email:Smtp:Host</c>, <c>WorkUp:Email:Smtp:Port</c> and the
/// TLS/credential pairing — are configuration, not per-notice failures, so they are validated once through
/// <see cref="ValidateSettings"/>: the host calls it while wiring SMTP mode, before the scheduler claims a notice, and
/// a typo fails fast instead of marking the whole queue <c>Failed</c>. The constructor itself never throws, because
/// the adapter may be built while the mode is <c>Outbox</c> and the default configuration has an empty host. If
/// unusable settings reach <see cref="SendAsync"/> anyway, it reports <see cref="EmailSendOutcome.OutcomeUnknown"/>
/// (never a permanent failure) without touching the network.
/// </para>
/// <para>
/// TLS is never silently downgraded: with <c>EnableSsl</c> the connection requires <c>STARTTLS</c>
/// (<see cref="SecureSocketOptions.StartTls"/>, not <c>StartTlsWhenAvailable</c>), and a server that cannot offer it
/// fails the send instead of transmitting in the clear. Credentials are only sent when configured and only after the
/// TLS handshake; a username with <c>EnableSsl</c> off fails validation. The adapter logs nothing and never surfaces
/// raw text: no provider message, exception message, password, body or recipient is written anywhere. Validation
/// failures use fixed messages that name only the configuration key, never the supplied value.
/// </para>
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private const string InvalidFromMessage =
        "Configuración WorkUp:Email:From inválida. Debe ser una única dirección de correo válida.";

    private const string InvalidHostMessage =
        "Configuración WorkUp:Email:Smtp:Host inválida. Debe ser un host SMTP válido.";

    private const string InvalidPortMessage =
        "Configuración WorkUp:Email:Smtp:Port inválida. Debe ser un puerto entre 1 y 65535.";

    private const string PlaintextCredentialsMessage =
        "Configuración WorkUp:Email:Smtp inválida. Con credenciales configuradas, WorkUp:Email:Smtp:EnableSsl debe estar activo.";

    private readonly string? _from;
    private readonly SmtpOptions _smtp;

    /// <param name="options">Bound <c>WorkUp</c> configuration. Invalid SMTP settings do not throw here on purpose;
    /// see <see cref="ValidateSettings"/> and the class remarks.</param>
    public SmtpEmailSender(IOptions<WorkUpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var email = options.Value.Email ?? new EmailOptions();
        _from = email.From;
        _smtp = email.Smtp ?? new SmtpOptions();
    }

    /// <summary>
    /// Validates the global SMTP configuration so the host can fail fast before the first outbox claim. It throws a
    /// fixed, non-echoing <see cref="InvalidOperationException"/>; the supplied value is never part of the message.
    /// This is intentionally separate from the constructor: the default <c>Outbox</c> configuration has an empty host
    /// and the adapter may be constructed while the mode retains emails.
    /// </summary>
    public static void ValidateSettings(EmailOptions email)
    {
        ArgumentNullException.ThrowIfNull(email);

        var smtp = email.Smtp ?? new SmtpOptions();
        if (!IsSingleMailbox(email.From))
            throw new InvalidOperationException(InvalidFromMessage);

        if (!IsUsableHost(smtp.Host))
            throw new InvalidOperationException(InvalidHostMessage);

        if (!IsUsablePort(smtp.Port))
            throw new InvalidOperationException(InvalidPortMessage);

        if (HasPlaintextCredentials(smtp))
            throw new InvalidOperationException(PlaintextCredentialsMessage);
    }

    /// <inheritdoc />
    public async Task<EmailSendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        // Per-notice local validation: both are provably before the network, so a retry cannot duplicate anything.
        if (!IsSingleMailbox(message.Recipient) || HasHeaderBreak(message.Subject))
            return EmailSendOutcome.DefinitelyNotSentPermanent;

        // A global misconfiguration must never mark queued notices Failed; report an unknown outcome for manual review.
        if (!IsSingleMailbox(_from) || !IsUsableHost(_smtp.Host) || !IsUsablePort(_smtp.Port)
            || HasPlaintextCredentials(_smtp))
            return EmailSendOutcome.OutcomeUnknown;

        var mail = BuildMessage(message);
        using var client = new SmtpClient();

        try
        {
            var socketOptions = _smtp.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
            await client.ConnectAsync(_smtp.Host, _smtp.Port, socketOptions, ct).ConfigureAwait(false);

            // Credentials cross the wire only after the connection is secured (StartTls above) and only when set.
            if (!string.IsNullOrEmpty(_smtp.User))
                await client.AuthenticateAsync(_smtp.User, _smtp.Password, ct).ConfigureAwait(false);

            await client.SendAsync(mail, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The transport may surface a wrapped failure once the token fires; cancellation must still propagate.
            throw new OperationCanceledException(ct);
        }
        catch (Exception)
        {
            // Any provider or transport error around the send is unknown and is never retried automatically.
            return EmailSendOutcome.OutcomeUnknown;
        }

        // SendAsync completed with the server's final acceptance reply: the message is confirmed. A best-effort QUIT
        // after this point must never downgrade that confirmation, and its error is swallowed without logging.
        try
        {
            await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        return EmailSendOutcome.Sent;
    }

    private MimeMessage BuildMessage(EmailMessage message)
    {
        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(_from!));
        mail.To.Add(MailboxAddress.Parse(message.Recipient));
        mail.Subject = message.Subject ?? string.Empty;

        var body = new BodyBuilder { HtmlBody = message.BodyHtml ?? string.Empty };
        mail.Body = body.ToMessageBody();
        return mail;
    }

    /// <summary>Credentials are only allowed together with TLS; otherwise they would cross the wire in the clear.</summary>
    private static bool HasPlaintextCredentials(SmtpOptions smtp) =>
        !string.IsNullOrEmpty(smtp.User) && !smtp.EnableSsl;

    /// <summary>A single mailbox, allowing a display name; multiple addresses and header breaks are rejected.</summary>
    private static bool IsSingleMailbox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            return false;

        if (value.Contains(',') || value.Contains(';'))
            return false;

        return MailboxAddress.TryParse(value, out _);
    }

    /// <summary>A subject must never smuggle an extra header through a line break or a NUL byte.</summary>
    private static bool HasHeaderBreak(string? value) =>
        value is not null && (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'));

    /// <summary>Generic host shape check; it never resolves DNS and never echoes the value it rejects.</summary>
    private static bool IsUsableHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
            return false;

        foreach (var c in host)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']')
                continue;

            return false;
        }

        return true;
    }

    private static bool IsUsablePort(int port) => port is > 0 and <= 65535;
}
