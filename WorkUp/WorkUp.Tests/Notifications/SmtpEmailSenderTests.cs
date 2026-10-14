using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using MimeKit;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Notifications;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// SMTP adapter (T23). The tests talk to a real loopback <see cref="TcpListener"/> that speaks just enough SMTP, so
/// the transport is exercised without any external network, credential or real mailbox.
/// <para>
/// The contract under test: global settings (<c>From</c>, <c>Smtp:Host</c>, <c>Smtp:Port</c>) are validated once by
/// <see cref="SmtpEmailSender.ValidateSettings"/> so the host can fail fast before the scheduler claims a single
/// notice, and a typo there never marks queued notices <c>Failed</c>; constructing the adapter stays safe for the
/// default <c>Outbox</c> configuration with an empty host; only a completed <c>SendMailAsync</c> counts as
/// <see cref="EmailSendOutcome.Sent"/>; a locally invalid recipient or subject is
/// <see cref="EmailSendOutcome.DefinitelyNotSentPermanent"/> before any socket opens; and every SMTP or transport
/// failure after the send started — including a 450 and a disconnect after <c>DATA</c> — is conservatively
/// <see cref="EmailSendOutcome.OutcomeUnknown"/>, which is never retried automatically. Cancellation propagates
/// <see cref="OperationCanceledException"/> so the claimed notice stays <c>Dispatching</c> for a human. Nothing here
/// logs or returns provider text, credentials, the body or the recipient.
/// </para>
/// </summary>
public sealed class SmtpEmailSenderTests
{
    private const string ValidFrom = "WorkUp <no-responder@workup.local>";
    private const string Recipient = "ana@example.com";
    private const string Subject = "Turno confirmado";
    private const string BodyHtml = "<p>Cuerpo del aviso</p>";

    /// <summary>Synthetic single-use-link body; the token is fabricated for the test and is never a real secret.</summary>
    private const string BodyWithLink =
        "<p>Turno confirmado. Confirmá en <a href=\"https://localhost:7160/t/token-sintetico-T23\">este enlace</a>.</p>";

    // --- global configuration: fail fast, never echo the value -------------------------------------------------

    [Fact]
    public void A_valid_configuration_passes_validation()
    {
        SmtpEmailSender.ValidateSettings(Config());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no es una direccion")]
    [InlineData("dos@example.com, otro@example.com")]
    [InlineData("ana@example.com\r\nBcc: evil@example.com")]
    public void An_invalid_From_fails_validation_without_echoing_the_value(string from)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SmtpEmailSender.ValidateSettings(Config(from: from)));

        Assert.Contains("WorkUp:Email:From", error.Message, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(from))
            Assert.DoesNotContain(from.Trim(), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host name")]
    [InlineData("no/route")]
    [InlineData("smtp.example.com\r\nX-Injected: 1")]
    public void An_unusable_host_fails_validation_without_echoing_the_value(string host)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SmtpEmailSender.ValidateSettings(Config(host: host)));

        Assert.Contains("WorkUp:Email:Smtp:Host", error.Message, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(host))
            Assert.DoesNotContain(host.Trim(), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(70000)]
    public void An_unusable_port_fails_validation_without_echoing_the_value(int port)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SmtpEmailSender.ValidateSettings(Config(port: port)));

        Assert.Contains("WorkUp:Email:Smtp:Port", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(port.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Construction_is_safe_for_the_default_outbox_configuration_with_an_empty_host()
    {
        // The adapter may be constructed while the mode is Retain; the default host is empty and must not throw here.
        var sender = NewSender(port: 587, host: "");

        Assert.NotNull(sender);
    }

    [Fact]
    public void Credentials_without_TLS_fail_validation_without_echoing_them()
    {
        // A username and password must never be transmitted over a plaintext transport.
        var error = Assert.Throws<InvalidOperationException>(
            () => SmtpEmailSender.ValidateSettings(Config(enableSsl: false, user: "workup", password: "SuperSecreto")));

        Assert.Contains("WorkUp:Email:Smtp:EnableSsl", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("workup", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecreto", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_with_TLS_pass_validation()
    {
        SmtpEmailSender.ValidateSettings(Config(enableSsl: true, user: "workup", password: "SuperSecreto"));
    }

    [Fact]
    public async Task Unusable_settings_at_send_time_are_unknown_and_never_permanent()
    {
        using var server = new LoopbackSmtpServer(SuccessSession);
        var sender = NewSender(server.Port, host: "");

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), CancellationToken.None);

        // A global misconfiguration must never mark queued notices Failed; a human resolves the unknown outcome.
        Assert.Equal(EmailSendOutcome.OutcomeUnknown, outcome);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task An_invalid_From_at_send_time_is_unknown_and_never_permanent()
    {
        using var server = new LoopbackSmtpServer(SuccessSession);
        var sender = NewSender(server.Port, from: "no es una direccion");

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), CancellationToken.None);

        Assert.Equal(EmailSendOutcome.OutcomeUnknown, outcome);
        Assert.Equal(0, server.ConnectionCount);
    }

    // --- per-notice local validation: permanent before any network call -----------------------------------------

    [Theory]
    [InlineData("ana@example.com\r\nBcc: evil@example.com")]
    [InlineData("ana@example.com, otro@example.com")]
    [InlineData("ana@example.com; otro@example.com")]
    [InlineData("no es una direccion")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_invalid_recipient_is_permanent_before_any_network_call(string recipient)
    {
        using var server = new LoopbackSmtpServer(SuccessSession);
        var sender = NewSender(server.Port);

        var outcome = await sender.SendAsync(new EmailMessage(recipient, Subject, BodyHtml), CancellationToken.None);

        Assert.Equal(EmailSendOutcome.DefinitelyNotSentPermanent, outcome);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Theory]
    [InlineData("Asunto\r\nBcc: evil@example.com")]
    [InlineData("Asunto\nX-Injected: 1")]
    [InlineData("Asunto\0oculto")]
    public async Task An_invalid_subject_is_permanent_before_any_network_call(string subject)
    {
        using var server = new LoopbackSmtpServer(SuccessSession);
        var sender = NewSender(server.Port);

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, subject, BodyHtml), CancellationToken.None);

        Assert.Equal(EmailSendOutcome.DefinitelyNotSentPermanent, outcome);
        Assert.Equal(0, server.ConnectionCount);
    }

    // --- the only Sent outcome is a completed SendMailAsync ------------------------------------------------------

    [Fact]
    public async Task A_completed_SendMailAsync_is_the_only_Sent_outcome()
    {
        using var server = new LoopbackSmtpServer(SuccessSession);
        var sender = NewSender(server.Port);

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, Subject, BodyWithLink), CancellationToken.None);

        Assert.Equal(EmailSendOutcome.Sent, outcome);
        Assert.Equal(1, server.ConnectionCount);
        Assert.Contains($"RCPT TO:<{Recipient}>", server.Transcript);
        Assert.Contains("DATA", server.Transcript, StringComparer.Ordinal);
        Assert.Contains(
            server.Transcript,
            line => line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase)
                && line.Contains("no-responder@workup.local", StringComparison.Ordinal));

        // Decode the actual MIME payload, so the user-facing subject, mailboxes and HTML link are verified rather than
        // only the SMTP envelope.
        var delivered = ParseCapturedMessage(server);
        Assert.Equal(Subject, delivered.Subject);
        Assert.Equal("no-responder@workup.local", delivered.From.Mailboxes.Single().Address);
        Assert.Equal(Recipient, delivered.To.Mailboxes.Single().Address);
        Assert.Contains(BodyWithLink, delivered.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("token-sintetico-T23", delivered.HtmlBody, StringComparison.Ordinal);
    }

    // --- ambiguous SMTP failures after the send started are conservatively unknown ------------------------------

    [Fact]
    public async Task A_missing_final_acknowledgement_after_the_whole_body_is_unknown_and_never_retryable()
    {
        // The server receives the complete body through the terminating dot and then closes gracefully without the
        // final 2xx reply. No acknowledgement means the send is not confirmed, so it must not be reported as Sent.
        using var server = new LoopbackSmtpServer(FullBodyNoFinalReplySession);
        var sender = NewSender(server.Port);

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), CancellationToken.None);

        Assert.Contains(".", server.Transcript, StringComparer.Ordinal);
        Assert.Equal(EmailSendOutcome.OutcomeUnknown, outcome);
        Assert.NotEqual(EmailSendOutcome.DefinitelyNotSentRetryable, outcome);
    }

    [Fact]
    public async Task TLS_is_required_and_never_silently_downgraded_to_plaintext()
    {
        // The fixture advertises no STARTTLS. With EnableSsl on, the adapter must fail rather than send in plaintext.
        using var server = new LoopbackSmtpServer(SuccessSession);
        var sender = NewSender(server.Port, enableSsl: true);

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), CancellationToken.None);

        Assert.Equal(EmailSendOutcome.OutcomeUnknown, outcome);
    }

    [Fact]
    public async Task A_temporary_450_after_DATA_is_unknown_and_never_retryable()
    {
        using var server = new LoopbackSmtpServer(TemporaryRejectionAfterDataSession);
        var sender = NewSender(server.Port);

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), CancellationToken.None);

        Assert.Equal(EmailSendOutcome.OutcomeUnknown, outcome);
        Assert.NotEqual(EmailSendOutcome.DefinitelyNotSentRetryable, outcome);
    }

    [Fact]
    public async Task A_disconnect_after_the_DATA_command_is_unknown_and_never_retryable()
    {
        using var server = new LoopbackSmtpServer(DisconnectAfterDataSession);
        var sender = NewSender(server.Port);

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), CancellationToken.None);

        Assert.Equal(EmailSendOutcome.OutcomeUnknown, outcome);
        Assert.NotEqual(EmailSendOutcome.DefinitelyNotSentRetryable, outcome);
    }

    [Fact]
    public async Task A_transport_failure_is_unknown_and_never_retryable()
    {
        var port = ReserveClosedLoopbackPort();
        var sender = NewSender(port);

        var outcome = await sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), CancellationToken.None);

        Assert.Equal(EmailSendOutcome.OutcomeUnknown, outcome);
        Assert.NotEqual(EmailSendOutcome.DefinitelyNotSentRetryable, outcome);
    }

    // --- cancellation propagates --------------------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_while_the_send_is_in_flight_propagates_OperationCanceledException()
    {
        using var server = new LoopbackSmtpServer(StallAfterGreetingSession);
        var sender = NewSender(server.Port);
        using var cts = new CancellationTokenSource();

        var send = sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => send.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task An_already_cancelled_token_throws_without_connecting()
    {
        using var server = new LoopbackSmtpServer(SuccessSession);
        var sender = NewSender(server.Port);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsync(new EmailMessage(Recipient, Subject, BodyHtml), cts.Token));
        Assert.Equal(0, server.ConnectionCount);
    }

    // --- helpers ------------------------------------------------------------------------------------------------

    private static EmailOptions Config(
        string from = ValidFrom,
        string host = "127.0.0.1",
        int port = 587,
        bool enableSsl = false,
        string user = "",
        string password = "") =>
        new()
        {
            Mode = "Smtp",
            From = from,
            Smtp = new SmtpOptions
            {
                Host = host,
                Port = port,
                EnableSsl = enableSsl,
                User = user,
                Password = password
            }
        };

    private static SmtpEmailSender NewSender(
        int port,
        string host = "127.0.0.1",
        string from = ValidFrom,
        bool enableSsl = false,
        string user = "",
        string password = "") =>
        new(Options.Create(new WorkUpOptions { Email = Config(from, host, port, enableSsl, user, password) }));

    private static int ReserveClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool StartsWith(string line, string command) =>
        line.StartsWith(command, StringComparison.OrdinalIgnoreCase);

    private static async Task ReadDataBlockAsync(SmtpSession session)
    {
        string? line;
        while ((line = await session.ReadLineAsync()) is not null && line != ".")
        {
            session.CaptureDataLine(line);
        }
    }

    private static MimeMessage ParseCapturedMessage(LoopbackSmtpServer server)
    {
        var payload = string.Join("\r\n", server.DataPayload.Select(Unstuff));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        return MimeMessage.Load(stream);
    }

    /// <summary>Reverses SMTP dot-stuffing on a received data line.</summary>
    private static string Unstuff(string line) =>
        line.StartsWith(".", StringComparison.Ordinal) ? line[1..] : line;

    private static async Task SuccessSession(SmtpSession session)
    {
        await session.WriteAsync("220 workup-test ESMTP ready");
        string? line;
        while ((line = await session.ReadLineAsync()) is not null)
        {
            if (StartsWith(line, "EHLO"))
            {
                await session.WriteAsync("250-workup-test");
                await session.WriteAsync("250 OK");
            }
            else if (StartsWith(line, "DATA"))
            {
                await session.WriteAsync("354 End data with <CR><LF>.<CR><LF>");
                await ReadDataBlockAsync(session);
                await session.WriteAsync("250 OK queued as workup-test");
            }
            else if (StartsWith(line, "QUIT"))
            {
                await session.WriteAsync("221 Bye");
                return;
            }
            else
            {
                await session.WriteAsync("250 OK");
            }
        }
    }

    private static async Task TemporaryRejectionAfterDataSession(SmtpSession session)
    {
        await session.WriteAsync("220 workup-test ESMTP ready");
        string? line;
        while ((line = await session.ReadLineAsync()) is not null)
        {
            if (StartsWith(line, "EHLO"))
            {
                await session.WriteAsync("250-workup-test");
                await session.WriteAsync("250 OK");
            }
            else if (StartsWith(line, "DATA"))
            {
                await session.WriteAsync("354 End data with <CR><LF>.<CR><LF>");
                await ReadDataBlockAsync(session);
                await session.WriteAsync("450 Requested mail action not taken: mailbox unavailable");
            }
            else if (StartsWith(line, "QUIT"))
            {
                await session.WriteAsync("221 Bye");
                return;
            }
            else
            {
                await session.WriteAsync("250 OK");
            }
        }
    }

    private static async Task FullBodyNoFinalReplySession(SmtpSession session)
    {
        await session.WriteAsync("220 workup-test ESMTP ready");
        string? line;
        while ((line = await session.ReadLineAsync()) is not null)
        {
            if (StartsWith(line, "EHLO"))
            {
                await session.WriteAsync("250-workup-test");
                await session.WriteAsync("250 OK");
            }
            else if (StartsWith(line, "DATA"))
            {
                await session.WriteAsync("354 End data with <CR><LF>.<CR><LF>");
                await ReadDataBlockAsync(session);
                // Close gracefully after receiving the whole body, but never send the final 2xx acknowledgement.
                return;
            }
            else
            {
                await session.WriteAsync("250 OK");
            }
        }
    }

    private static async Task DisconnectAfterDataSession(SmtpSession session)
    {
        await session.WriteAsync("220 workup-test ESMTP ready");
        string? line;
        while ((line = await session.ReadLineAsync()) is not null)
        {
            if (StartsWith(line, "EHLO"))
            {
                await session.WriteAsync("250-workup-test");
                await session.WriteAsync("250 OK");
            }
            else if (StartsWith(line, "DATA"))
            {
                // Abort right after the DATA command, before the 354 acknowledgement, so the client can never learn
                // whether the server would have accepted the body.
                session.Abort();
                return;
            }
            else
            {
                await session.WriteAsync("250 OK");
            }
        }
    }

    private static async Task StallAfterGreetingSession(SmtpSession session)
    {
        await session.WriteAsync("220 workup-test ESMTP ready");
        // Read the EHLO command but never answer it, so the sender blocks inside SendMailAsync until cancelled.
        while (await session.ReadLineAsync() is not null)
        {
        }
    }

    /// <summary>One accepted connection: line-oriented SMTP with an automatic transcript for assertions.</summary>
    private sealed class SmtpSession(
        StreamReader reader,
        StreamWriter writer,
        List<string> transcript,
        List<string> dataPayload,
        TcpClient client)
    {
        public async Task<string?> ReadLineAsync()
        {
            var line = await reader.ReadLineAsync();
            if (line is not null)
            {
                lock (transcript)
                    transcript.Add(line);
            }

            return line;
        }

        /// <summary>Records one line of the DATA block so the test can parse the real MIME payload.</summary>
        public void CaptureDataLine(string line)
        {
            lock (dataPayload)
                dataPayload.Add(line);
        }

        public async Task WriteAsync(string line)
        {
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
        }

        /// <summary>Aborts the TCP connection (RST) so the client sees an abrupt disconnect rather than a shutdown.</summary>
        public void Abort()
        {
            try
            {
                client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
                client.Client.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Minimal real SMTP server on a random loopback port. It never touches the external network and runs one scripted
    /// session per accepted connection.
    /// </summary>
    private sealed class LoopbackSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<SmtpSession, Task> _session;
        private readonly List<string> _transcript = [];
        private readonly List<string> _dataPayload = [];
        private readonly Task _acceptLoop;
        private int _connections;

        public LoopbackSmtpServer(Func<SmtpSession, Task> session)
        {
            _session = session;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int ConnectionCount => Volatile.Read(ref _connections);

        public IReadOnlyList<string> Transcript
        {
            get
            {
                lock (_transcript)
                    return _transcript.ToArray();
            }
        }

        /// <summary>The lines of the last DATA block, excluding the terminating dot.</summary>
        public IReadOnlyList<string> DataPayload
        {
            get
            {
                lock (_dataPayload)
                    return _dataPayload.ToArray();
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(
                        stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                    using var writer = new StreamWriter(stream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                    {
                        NewLine = "\r\n",
                        AutoFlush = true
                    };
                    await _session(new SmtpSession(reader, writer, _transcript, _dataPayload, client));
                }
                catch (Exception)
                {
                    // The client aborted the connection; the scripted session simply ends.
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            _cts.Dispose();
        }
    }
}
