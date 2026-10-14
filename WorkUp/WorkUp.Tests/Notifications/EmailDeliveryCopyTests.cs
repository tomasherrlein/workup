using WorkUp.BusinessLogic.Notifications;
using WorkUp.Infrastructure;

namespace WorkUp.Tests.Notifications;

/// <summary>
/// Mode-truthful copy (T23) for the Razor views. The two modes really do different things, so the same sentence cannot
/// describe both: <see cref="OutboxMode.Retain"/> keeps every notice <c>Pending</c> without sending it, and
/// <see cref="OutboxMode.Deliver"/> only queues it for the scheduled dispatch, which never promises that the message
/// reaches the mailbox. Every view sentence goes through this helper, so a test can assert the contract "never claim an
/// inbox delivery, never claim something the mode does not do" for all of them at once instead of trusting five
/// separate Razor blocks.
/// <para>
/// What these unit tests do <em>not</em> prove: that each Razor view actually calls the matching helper. That rendering
/// is only observable through the opt-in host fixture, which is a separate verification.
/// </para>
/// </summary>
public sealed class EmailDeliveryCopyTests
{
    private const string Business = "Peluquería Marina";

    /// <summary>
    /// Every copy site for a mode, in view order, so a sentence that lies in one view cannot hide behind a covered one.
    /// </summary>
    private static string[] AllCopy(OutboxMode mode) =>
    [
        EmailDeliveryCopy.DevToolsBanner(mode),
        EmailDeliveryCopy.BookingConfirmationNotice(mode),
        EmailDeliveryCopy.BookingConfirmationHint(mode),
        EmailDeliveryCopy.DropInConfirmationNotice(mode),
        EmailDeliveryCopy.RecoverPrompt(mode, Business),
        EmailDeliveryCopy.ResendPrompt(mode, Business)
    ];

    [Theory]
    [InlineData("Outbox", OutboxMode.Retain)]
    [InlineData("outbox", OutboxMode.Retain)]
    [InlineData("", OutboxMode.Retain)]
    [InlineData(null, OutboxMode.Retain)]
    [InlineData("Smtp", OutboxMode.Deliver)]
    [InlineData(" SMTP ", OutboxMode.Deliver)]
    public void Resolve_mode_parses_with_the_same_parser_as_startup(string? configured, OutboxMode expected)
    {
        Assert.Equal(expected, EmailDeliveryCopy.ResolveMode(configured));
    }

    [Fact]
    public void An_unknown_mode_fails_fast_without_echoing_configuration()
    {
        // A view must never read a typo as Outbox, and the error must not echo a value that can carry credentials.
        var error = Assert.Throws<InvalidOperationException>(() => EmailDeliveryCopy.ResolveMode("Smpt"));

        Assert.Contains("WorkUp:Email:Mode", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Smpt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Retain_mode_copy_retains_the_notice_without_sending_or_promising_an_inbox()
    {
        Assert.All(AllCopy(OutboxMode.Retain), text =>
        {
            Assert.Contains("no está activo", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no podemos garantizar", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Vas a recibir", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("te enviamos", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Deliver_mode_copy_queues_for_automatic_dispatch_without_promising_an_inbox()
    {
        Assert.All(AllCopy(OutboxMode.Deliver), text =>
        {
            Assert.Contains("envío automático", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no podemos garantizar", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no está activo", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Vas a recibir", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("te enviamos", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Every_copy_site_says_something_different_per_mode_so_none_lies_in_one_of_them()
    {
        var retain = AllCopy(OutboxMode.Retain);
        var deliver = AllCopy(OutboxMode.Deliver);

        for (var i = 0; i < retain.Length; i++)
            Assert.NotEqual(retain[i], deliver[i]);
    }

    [Fact]
    public void Account_prompts_keep_the_conditional_guard_so_no_page_confirms_an_account()
    {
        // RF-27: the sentence stays conditional in both modes, so an existent account and an absent one read the same.
        foreach (var mode in new[] { OutboxMode.Retain, OutboxMode.Deliver })
        {
            Assert.StartsWith(
                "Escribí tu email y, si tenés una cuenta", EmailDeliveryCopy.RecoverPrompt(mode, Business),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "Si tu cuenta todavía no está confirmada", EmailDeliveryCopy.ResendPrompt(mode, Business),
                StringComparison.Ordinal);
            Assert.Contains(Business, EmailDeliveryCopy.RecoverPrompt(mode, Business), StringComparison.Ordinal);
            Assert.Contains(Business, EmailDeliveryCopy.ResendPrompt(mode, Business), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Copy_is_a_constant_of_the_parsed_mode_and_never_the_configured_text()
    {
        // Two spellings of the same mode produce identical copy: only the parsed enum, never the configuration string,
        // can reach the rendered sentence, so no configuration value can be injected into the HTML.
        Assert.Equal(
            EmailDeliveryCopy.BookingConfirmationNotice(EmailDeliveryCopy.ResolveMode("smtp")),
            EmailDeliveryCopy.BookingConfirmationNotice(EmailDeliveryCopy.ResolveMode("  SMTP  ")));
    }
}
