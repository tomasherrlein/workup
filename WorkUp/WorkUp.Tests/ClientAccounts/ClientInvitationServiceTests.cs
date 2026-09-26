using System.Net;
using System.Text.RegularExpressions;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.ClientAccounts;

/// <summary>
/// Owner-assisted invitation, issue and revoke business rules (RF-26, slice 1). It proves the service contract
/// against the in-memory port: the destination email is normalised and validated before any write, a real ficha of the
/// same business without a linked account is required, issuing revokes the previous live invitation so one stays
/// live, revoking is idempotent, the notice is HTML-encoded and reaches only the destination address, and only the
/// SHA-256 hash is stored while the raw token travels exclusively inside the queued email.
/// <para>
/// The known limitation is explicit: the linked-account check runs before the Clients row lock, so a linkage that
/// commits in between cannot stop the issue. The acceptance step of RF-26 must fail closed for that stale invitation,
/// and only the SQL fixture can prove the padlock and the rollback.
/// </para>
/// </summary>
public sealed class ClientInvitationServiceTests
{
    private const string HairdresserSlug = "peluqueria";
    private const string YogaSlug = "yoga";
    private static readonly DateTime IssuedAt = new(2026, 3, 10, 9, 0, 0);

    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeClientInvitationRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new() { Now = IssuedAt };
    private readonly ClientInvitationService _service;

    public ClientInvitationServiceTests()
    {
        _service = new ClientInvitationService(
            _businesses,
            _repository,
            _unitOfWork,
            new TrustedInvitationUrlBuilder(),
            _clock);
    }

    [Fact]
    public async Task Issue_stores_a_live_invitation_with_only_the_token_hash()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        var result = await _service.IssueAsync(business.Id, client.Id, "nueva@example.com");

        Assert.True(result.Succeeded);
        Assert.Equal("nueva@example.com", result.Value.Email);
        Assert.Equal(IssuedAt.AddHours(48), result.Value.ExpiresAt);
        var invitation = Assert.Single(_repository.Invitations);
        Assert.Equal(business.Id, invitation.BusinessId);
        Assert.Equal(client.Id, invitation.ClientId);
        Assert.Equal("nueva@example.com", invitation.Email);
        Assert.Equal(32, invitation.TokenHash.Length);
        Assert.Null(invitation.UsedAt);
        Assert.Null(invitation.RevokedAt);
        Assert.True(invitation.IsUsable(IssuedAt));
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Issue_normalizes_the_destination_email()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        var result = await _service.IssueAsync(business.Id, client.Id, "  Nueva@Example.COM ");

        Assert.True(result.Succeeded);
        Assert.Equal("nueva@example.com", result.Value.Email);
        Assert.Equal("nueva@example.com", Assert.Single(_repository.Invitations).Email);
        Assert.Equal("nueva@example.com", Assert.Single(_repository.Notifications).Recipient);
    }

    [Fact]
    public async Task Issue_never_exposes_the_token_or_a_session_in_its_receipt()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        var result = await _service.IssueAsync(business.Id, client.Id, "ana@example.com");

        var properties = string.Join(',', typeof(ClientInvitationReceipt).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Token", properties);
        Assert.DoesNotContain("Session", properties);
        Assert.DoesNotContain("Claims", properties);
        Assert.DoesNotContain(TokenFrom(Assert.Single(_repository.Notifications)), result.Value.ToString());
    }

    [Fact]
    public async Task Issue_stores_only_the_sha256_hash_and_sends_the_raw_token_in_the_email()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        await _service.IssueAsync(business.Id, client.Id, "nueva@example.com");

        var invitation = Assert.Single(_repository.Invitations);
        var rawToken = TokenFrom(Assert.Single(_repository.Notifications));
        Assert.Equal(SecureToken.Hash(rawToken), invitation.TokenHash);
        Assert.DoesNotContain(rawToken, Convert.ToHexString(invitation.TokenHash));
        Assert.Contains(rawToken, _repository.Notifications.Single().BodyHtml);
    }

    [Fact]
    public async Task Issue_queues_one_account_invitation_notice_to_the_destination_email()
    {
        var business = SeedBusiness(HairdresserSlug, "Peluquería Marina");
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        await _service.IssueAsync(business.Id, client.Id, "nueva@example.com");

        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal(business.Id, notification.BusinessId);
        Assert.Equal(NotificationType.AccountInvitation, notification.Type);
        Assert.Equal("nueva@example.com", notification.Recipient);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(ClientInvitationService.InvitationDedupKey(1), notification.DedupKey);
        Assert.DoesNotContain(TokenFrom(notification), notification.DedupKey);
        Assert.StartsWith("https://reservas.example.com/r/peluqueria/cuenta/invitacion/", LinkFrom(notification));
        Assert.Contains(WebUtility.HtmlEncode("Peluquería Marina"), notification.BodyHtml);
    }

    [Fact]
    public async Task Issue_requires_a_real_ficha_of_the_business()
    {
        var hairdresser = SeedBusiness(HairdresserSlug);
        var yoga = SeedBusiness(YogaSlug, "Estudio de Yoga");
        var foreign = _repository.SeedClient(yoga.Id, "Ana", "Gómez", "ana@example.com");

        var result = await _service.IssueAsync(hairdresser.Id, foreign.Id, "ana@example.com");

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_unavailable", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Invitations);
    }

    [Fact]
    public async Task Issue_refuses_a_missing_ficha_with_the_same_generic_error()
    {
        var business = SeedBusiness(HairdresserSlug);

        var missing = await _service.IssueAsync(business.Id, 999, "ana@example.com");

        Assert.False(missing.Succeeded);
        Assert.Equal("client_invitation_unavailable", missing.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Invitations);
    }

    [Fact]
    public async Task Issue_refuses_a_ficha_that_already_has_a_linked_account()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com", hasLinkedAccount: true);

        var result = await _service.IssueAsync(business.Id, client.Id, "otra@example.com");

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_already_linked", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Invitations);
        Assert.Empty(_repository.Notifications);
    }

    [Theory]
    [InlineData("", "client_invitation_email_required")]
    [InlineData("   ", "client_invitation_email_required")]
    [InlineData("ana-sin-arroba", "invalid_client_invitation_email")]
    public async Task Issue_rejects_invalid_destination_email_before_opening_a_transaction(
        string destinationEmail, string expectedCode)
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        var result = await _service.IssueAsync(business.Id, client.Id, destinationEmail);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Invitations);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task Issue_rejects_an_email_too_long_for_the_invitation_column_before_opening_a_transaction()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");
        var overlongEmail = new string('a', 250) + "@example.com";
        Assert.True(overlongEmail.Length > 254);

        var result = await _service.IssueAsync(business.Id, client.Id, overlongEmail);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_client_invitation_email", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Invitations);
    }

    [Fact]
    public async Task Reissuing_invalidates_the_previous_invitation_so_one_stays_live()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");
        Assert.True((await _service.IssueAsync(business.Id, client.Id, "primera@example.com")).Succeeded);
        var firstToken = TokenFrom(_repository.Notifications[0]);
        _clock.Now = IssuedAt.AddHours(3);

        var reissued = await _service.IssueAsync(business.Id, client.Id, "segunda@example.com");

        Assert.True(reissued.Succeeded);
        Assert.Equal(2, _repository.Invitations.Count);
        Assert.Single(_repository.Invitations, invitation => invitation.IsUsable(_clock.Now));
        var previous = _repository.Invitations[0];
        Assert.Equal(_clock.Now, previous.RevokedAt);
        var secondToken = TokenFrom(_repository.Notifications[1]);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal("segunda@example.com", _repository.Invitations.Single(i => i.IsUsable(_clock.Now)).Email);
    }

    [Fact]
    public async Task Revoke_invalidates_every_live_invitation_and_is_idempotent()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");
        Assert.True((await _service.IssueAsync(business.Id, client.Id, "ana@example.com")).Succeeded);

        var first = await _service.RevokeAsync(business.Id, client.Id);
        var second = await _service.RevokeAsync(business.Id, client.Id);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.All(_repository.Invitations, invitation => Assert.False(invitation.IsUsable(_clock.Now)));
        Assert.All(_repository.Invitations, invitation => Assert.Equal(IssuedAt, invitation.RevokedAt));
    }

    [Fact]
    public async Task A_revoked_ficha_can_be_invited_again_with_a_fresh_link()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");
        Assert.True((await _service.IssueAsync(business.Id, client.Id, "ana@example.com")).Succeeded);
        var revokedToken = TokenFrom(_repository.Notifications[0]);
        Assert.True((await _service.RevokeAsync(business.Id, client.Id)).Succeeded);
        _clock.Now = IssuedAt.AddHours(4);

        var reissued = await _service.IssueAsync(business.Id, client.Id, "nueva@example.com");

        Assert.True(reissued.Succeeded);
        var replacementToken = TokenFrom(_repository.Notifications[1]);
        Assert.NotEqual(revokedToken, replacementToken);
        Assert.Single(_repository.Invitations, invitation => invitation.IsUsable(_clock.Now));
    }

    [Fact]
    public async Task Revoke_refuses_a_ficha_of_another_business_generically()
    {
        var hairdresser = SeedBusiness(HairdresserSlug);
        var yoga = SeedBusiness(YogaSlug, "Estudio de Yoga");
        var yogaClient = _repository.SeedClient(yoga.Id, "Ana", "Gómez", "ana@example.com");
        Assert.True((await _service.IssueAsync(yoga.Id, yogaClient.Id, "ana@example.com")).Succeeded);

        var result = await _service.RevokeAsync(hairdresser.Id, yogaClient.Id);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_unavailable", result.Error!.Code);
        Assert.True(Assert.Single(_repository.Invitations).IsUsable(_clock.Now));
    }

    [Fact]
    public async Task Revoke_refuses_a_missing_ficha_generically()
    {
        var business = SeedBusiness(HairdresserSlug);

        var result = await _service.RevokeAsync(business.Id, 999);

        Assert.False(result.Succeeded);
        Assert.Equal("client_invitation_unavailable", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Invitation_expires_exactly_48_hours_after_it_is_issued()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");

        var receipt = (await _service.IssueAsync(business.Id, client.Id, "ana@example.com")).Value;
        var invitation = Assert.Single(_repository.Invitations);

        Assert.Equal(IssuedAt.AddHours(48), receipt.ExpiresAt);
        Assert.Equal(receipt.ExpiresAt, invitation.ExpiresAt);
        Assert.True(invitation.IsUsable(receipt.ExpiresAt.AddTicks(-1)));
        Assert.False(invitation.IsUsable(receipt.ExpiresAt));
    }

    [Fact]
    public async Task Issue_rolls_back_when_the_notice_cannot_be_queued()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");
        _repository.BeforeNotificationInsert = _ => throw new InvalidOperationException("outbox unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IssueAsync(business.Id, client.Id, "ana@example.com"));

        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
        // Fake limitation: an in-memory store cannot undo the invitation row. Only a SQL fixture proves the rollback.
        Assert.Single(_repository.Invitations);
    }

    [Fact]
    public async Task A_retried_notice_of_the_same_invitation_is_deduplicated_without_failing_the_issue()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");
        // The retry re-enqueues the exact notice of the invitation being created, so the notice-bound key collides
        // and the duplicate must be ignored instead of treated as a failure.
        _repository.BeforeNotificationInsert = notice => _repository.SeedQueuedNotification(notice.DedupKey);

        var result = await _service.IssueAsync(business.Id, client.Id, "ana@example.com");

        Assert.True(result.Succeeded);
        Assert.Single(_repository.Invitations);
        Assert.Single(_repository.Notifications);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Two_simultaneous_issues_leave_exactly_one_live_invitation()
    {
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com");
        _repository.SynchronizeNextTwoIssues();

        var results = await Task.WhenAll(
            _service.IssueAsync(business.Id, client.Id, "primera@example.com"),
            _service.IssueAsync(business.Id, client.Id, "segunda@example.com"));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(2, _repository.Invitations.Count);
        Assert.Single(_repository.Invitations, invitation => invitation.IsUsable(_clock.Now));
        Assert.Equal(2, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Issue_precheck_race_can_leave_a_stale_invitation_for_the_acceptance_to_refuse()
    {
        // Known limitation accepted for slice 1: the linked-account check runs before the Clients row lock, so a
        // linkage committed in between cannot stop this issue. The acceptance step of RF-26 must fail closed for an
        // invitation whose ficha is already linked by the time it is consumed; this test documents that boundary.
        var business = SeedBusiness(HairdresserSlug);
        var client = _repository.SeedClient(business.Id, "Ana", "Gómez", "ana@example.com", hasLinkedAccount: false);
        _repository.BeforeInvitationInsert = () => client.HasLinkedAccount = true;

        var result = await _service.IssueAsync(business.Id, client.Id, "ana@example.com");

        Assert.True(result.Succeeded);
        Assert.Single(_repository.Invitations);
        Assert.True(client.HasLinkedAccount);
    }

    [Fact]
    public void Invitation_link_rules_build_only_from_the_configured_trusted_base_url()
    {
        Assert.Equal(
            "https://reservas.example.com/r/peluqueria/cuenta/invitacion/opaque-token",
            ClientAccountUrlRules.BuildInvitationUrl("https://reservas.example.com", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildInvitationUrl("http://reservas.example.com", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildInvitationUrl("", "peluqueria", "opaque-token"));
        Assert.Throws<ArgumentException>(() =>
            ClientAccountUrlRules.BuildInvitationUrl("https://reservas.example.com", "peluqueria", ""));
    }

    private Business SeedBusiness(string slug, string name = "Peluquería Marina")
    {
        var business = new Business { Name = name, Slug = slug, ContactPhone = "1145550000", CreatedAt = IssuedAt };
        _businesses.InsertAsync(business).GetAwaiter().GetResult();
        return business;
    }

    private static string LinkFrom(Notification notification)
    {
        var match = Regex.Match(notification.BodyHtml, "href=\"([^\"]+)\"");
        Assert.True(match.Success, "The invitation email must contain a link.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string TokenFrom(Notification notification)
    {
        var link = LinkFrom(notification);
        const string marker = "/cuenta/invitacion/";
        var index = link.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The invitation email must link to the account invitation route.");
        return link[(index + marker.Length)..];
    }

    private sealed class TrustedInvitationUrlBuilder : IClientInvitationUrlBuilder
    {
        private const string BaseUrl = "https://reservas.example.com";

        public string BuildInvitationUrl(string slug, string token) =>
            ClientAccountUrlRules.BuildInvitationUrl(BaseUrl, slug, token);
    }
}
