using System.Security.Cryptography;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class PrivateAppointmentLinkServiceTests
{
    private static readonly DateTime StartsAt = new(2026, 3, 10, 10, 0, 0);
    private static readonly DateTime ExpiresAt = StartsAt.AddHours(24);
    private readonly FakePrivateLinkRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new() { Now = StartsAt.AddHours(-1) };
    private readonly RecordingLinkProtector _protector = new();
    private readonly RecordingUrlBuilder _urlBuilder = new();
    private readonly PrivateAppointmentLinkService _service;

    public PrivateAppointmentLinkServiceTests()
    {
        _service = new PrivateAppointmentLinkService(_repository, _unitOfWork, _protector, _urlBuilder, _clock);
        _repository.AddLink(
            "valid-token",
            businessId: 7,
            bookingId: 12,
            new PrivateAppointmentLinkRecord(
                StartsAt, StartsAt.AddMinutes(30), ExpiresAt, "Corte", "Ana Pérez",
                AppointmentStatus.Scheduled, BookingStatus.Pending, null, null, null, null, null));
    }

    [Fact]
    public async Task Public_lookup_returns_only_the_exact_booking_projection_and_available_actions()
    {
        var view = await _service.GetPublicViewAsync("valid-token");

        Assert.NotNull(view);
        Assert.Equal(StartsAt, view.StartsAt);
        Assert.Equal(StartsAt.AddMinutes(30), view.EndsAt);
        Assert.Equal("Corte", view.ServiceName);
        Assert.Equal("Ana Pérez", view.WorkerName);
        Assert.True(view.CanConfirm);
        Assert.True(view.CanCancel);
        Assert.False(view.CanReportAbsence);
        Assert.DoesNotContain("ClientId", string.Join(',', typeof(PrivateAppointmentView).GetProperties().Select(property => property.Name)));
        Assert.DoesNotContain("Notes", string.Join(',', typeof(PrivateAppointmentView).GetProperties().Select(property => property.Name)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("revoked-token")]
    [InlineData("expired-token")]
    public async Task Public_lookup_returns_the_same_unavailable_shape_for_missing_revoked_or_expired_tokens(string token)
    {
        if (token == "revoked-token")
            _repository.AddLink(token, 7, 13, Lookup(ExpiresAt), revokedAt: _clock.Now);
        if (token == "expired-token")
            _repository.AddLink(token, 7, 14, Lookup(_clock.Now));

        var view = await _service.GetPublicViewAsync(token);

        Assert.Null(view);
    }

    [Fact]
    public async Task Public_lookup_stays_readable_until_the_exact_expiry_even_after_cancellation()
    {
        _repository.SetBookingStatus("valid-token", BookingStatus.Cancelled, cancelledAt: _clock.Now);
        _clock.Now = ExpiresAt.AddTicks(-1);

        var view = await _service.GetPublicViewAsync("valid-token");

        Assert.NotNull(view);
        Assert.False(view.CanConfirm);
        Assert.False(view.CanCancel);
        Assert.False(view.CanReportAbsence);
        _clock.Now = ExpiresAt;
        Assert.Null(await _service.GetPublicViewAsync("valid-token"));
    }

    [Fact]
    public async Task Confirm_before_start_transitions_pending_once_and_repeat_is_idempotent()
    {
        var first = await _service.ConfirmIntentionAsync("valid-token");
        var confirmedAt = _repository.GetBooking("valid-token").ConfirmedAt;
        _clock.Now = StartsAt.AddHours(-2);
        var repeated = await _service.ConfirmIntentionAsync("valid-token");

        Assert.True(first.Succeeded);
        Assert.True(repeated.Succeeded);
        Assert.Equal(BookingStatus.Confirmed, _repository.GetBooking("valid-token").BookingStatus);
        Assert.Equal(confirmedAt, _repository.GetBooking("valid-token").ConfirmedAt);
        Assert.Equal(2, _unitOfWork.CommitCount);
        Assert.Equal(1, _repository.TransitionCount("valid-token", PrivateAppointmentLinkAction.Confirm));
    }

    [Fact]
    public async Task Confirm_is_denied_at_start_and_does_not_change_booking_state()
    {
        _clock.Now = StartsAt;

        var result = await _service.ConfirmIntentionAsync("valid-token");

        AssertUnavailable(result);
        Assert.Equal(BookingStatus.Pending, _repository.GetBooking("valid-token").BookingStatus);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    public async Task Cancel_before_start_preserves_booking_history_and_sets_client_cancellation(BookingStatus status)
    {
        if (status == BookingStatus.Confirmed)
            _repository.SetBookingStatus("valid-token", status, confirmedAt: _clock.Now.AddMinutes(-5));

        var result = await _service.CancelFromPrivateLinkAsync("valid-token");
        var booking = _repository.GetBooking("valid-token");

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);
        Assert.Equal(CancelledBy.Client, booking.CancelledBy);
        Assert.Equal(_clock.Now, booking.CancelledAt);
        Assert.Equal(1, _repository.PaymentHistoryCount("valid-token"));
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Cancel_is_denied_at_start_or_after_expiry()
    {
        _clock.Now = StartsAt;
        AssertUnavailable(await _service.CancelFromPrivateLinkAsync("valid-token"));
        _clock.Now = ExpiresAt;
        AssertUnavailable(await _service.CancelFromPrivateLinkAsync("valid-token"));
        Assert.Equal(BookingStatus.Pending, _repository.GetBooking("valid-token").BookingStatus);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    public async Task Absence_is_allowed_from_start_through_the_instant_before_expiry(BookingStatus status)
    {
        if (status == BookingStatus.Confirmed)
            _repository.SetBookingStatus("valid-token", status, confirmedAt: StartsAt.AddHours(-1));
        _clock.Now = StartsAt;

        var result = await _service.ReportAbsenceFromPrivateLinkAsync("valid-token");
        var booking = _repository.GetBooking("valid-token");

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Absent, booking.BookingStatus);
        Assert.Equal(AbsenceSource.ClientLink, booking.AbsenceSource);
        Assert.Equal(StartsAt, booking.StartsAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Absence_is_allowed_one_tick_before_expiry()
    {
        _clock.Now = ExpiresAt.AddTicks(-1);

        var result = await _service.ReportAbsenceFromPrivateLinkAsync("valid-token");

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Absent, _repository.GetBooking("valid-token").BookingStatus);
        Assert.Equal(AbsenceSource.ClientLink, _repository.GetBooking("valid-token").AbsenceSource);
    }

    [Fact]
    public async Task Conflicting_booking_state_remains_readable_but_exposes_no_actions()
    {
        _repository.SetBookingStatus("valid-token", BookingStatus.Pending, confirmedAt: _clock.Now);

        var view = await _service.GetPublicViewAsync("valid-token");
        var result = await _service.ConfirmIntentionAsync("valid-token");

        Assert.NotNull(view);
        Assert.False(view.CanConfirm);
        Assert.False(view.CanCancel);
        Assert.False(view.CanReportAbsence);
        AssertUnavailable(result);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Absence_is_denied_at_expiry_and_does_not_overwrite_attended_cancelled_or_absent()
    {
        _clock.Now = ExpiresAt;
        AssertUnavailable(await _service.ReportAbsenceFromPrivateLinkAsync("valid-token"));

        foreach (var status in new[] { BookingStatus.Attended, BookingStatus.Cancelled, BookingStatus.Absent })
        {
            _repository.ResetBooking("valid-token");
            _repository.SetBookingStatus(
                "valid-token", status,
                cancelledAt: status == BookingStatus.Cancelled ? _clock.Now : null,
                attendanceRecordedAt: status == BookingStatus.Attended ? _clock.Now : null,
                absenceSource: status == BookingStatus.Absent ? AbsenceSource.Owner : null);
            _clock.Now = StartsAt;
            AssertUnavailable(await _service.ReportAbsenceFromPrivateLinkAsync("valid-token"));
            Assert.Equal(status, _repository.GetBooking("valid-token").BookingStatus);
        }
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Invalid_public_action_has_one_generic_response_and_never_exposes_token_or_client_data()
    {
        var result = await _service.CancelFromPrivateLinkAsync("not-a-token-with-private-data");

        AssertUnavailable(result);
        Assert.DoesNotContain("not-a-token", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("client", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("private_link_unavailable", result.Error.Code);
    }

    [Fact]
    public async Task Owner_copy_is_business_scoped_and_unprotects_only_the_active_link()
    {
        var wrongTenant = await _service.CopyCurrentLinkAsync(8, 12);
        var copy = await _service.CopyCurrentLinkAsync(7, 12);

        Assert.False(wrongTenant.Succeeded);
        Assert.True(copy.Succeeded);
        Assert.Equal("https://trusted.example/t/valid-token", copy.Value.Url);
        Assert.Equal(ExpiresAt, copy.Value.ExpiresAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Owner_replace_atomically_revokes_old_token_and_keeps_original_expiry()
    {
        var result = await _service.ReplaceCurrentLinkAsync(7, 12);

        Assert.True(result.Succeeded);
        Assert.Equal(ExpiresAt, result.Value.ExpiresAt);
        Assert.StartsWith("https://trusted.example/t/", result.Value.Url, StringComparison.Ordinal);
        var newToken = result.Value.Url[(result.Value.Url.LastIndexOf('/') + 1)..];
        Assert.NotEqual("valid-token", newToken);
        Assert.True(_repository.IsRevoked("valid-token"));
        var replacement = Assert.Single(_repository.ActiveLinks(7, 12));
        Assert.Equal(SecureToken.Hash(newToken), replacement.TokenHash);
        Assert.Equal(_protector.Protect(newToken), replacement.ProtectedToken);
        Assert.Equal(ExpiresAt, replacement.ExpiresAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Null(await _service.GetPublicViewAsync("valid-token"));
        Assert.NotNull(await _service.GetPublicViewAsync(newToken));
    }

    [Fact]
    public async Task Owner_revoke_invalidates_the_link_without_replacing_or_extending_it()
    {
        var result = await _service.RevokeCurrentLinkAsync(7, 12);

        Assert.True(result.Succeeded);
        Assert.True(_repository.IsRevoked("valid-token"));
        Assert.Empty(_repository.ActiveLinks(7, 12));
        Assert.Null(await _service.GetPublicViewAsync("valid-token"));
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Owner_revoke_is_business_scoped()
    {
        var result = await _service.RevokeCurrentLinkAsync(8, 12);

        Assert.False(result.Succeeded);
        Assert.Equal("owner_private_link_unavailable", result.Error!.Code);
        Assert.False(_repository.IsRevoked("valid-token"));
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Owner_revoke_fails_closed_for_expired_or_ambiguous_links()
    {
        _clock.Now = ExpiresAt;
        var expired = await _service.RevokeCurrentLinkAsync(7, 12);
        Assert.False(expired.Succeeded);
        Assert.False(_repository.IsRevoked("valid-token"));

        _clock.Now = StartsAt.AddHours(-1);
        _repository.AddLink("second-token", 7, 12, Lookup(ExpiresAt));
        var ambiguous = await _service.RevokeCurrentLinkAsync(7, 12);

        Assert.False(ambiguous.Succeeded);
        Assert.False(_repository.IsRevoked("valid-token"));
        Assert.False(_repository.IsRevoked("second-token"));
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Concurrent_duplicate_owner_revocations_apply_once()
    {
        _repository.SynchronizeNextTwoOwnerReads();

        var results = await Task.WhenAll(
            _service.RevokeCurrentLinkAsync(7, 12),
            _service.RevokeCurrentLinkAsync(7, 12));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => !result.Succeeded);
        Assert.True(_repository.IsRevoked("valid-token"));
        Assert.Equal(1, _repository.RevokeCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("revoke")]
    [InlineData("replace")]
    public async Task Owner_operations_recheck_link_expiry_after_booking_lock(string operation)
    {
        _repository.AfterOwnerBookingLock = () => _clock.Now = ExpiresAt;

        WorkUp.BusinessLogic.Common.Result result = operation switch
        {
            "copy" => await _service.CopyCurrentLinkAsync(7, 12),
            "revoke" => await _service.RevokeCurrentLinkAsync(7, 12),
            "replace" => await _service.ReplaceCurrentLinkAsync(7, 12),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        Assert.False(result.Succeeded);
        Assert.Equal("owner_private_link_unavailable", result.Error!.Code);
        Assert.False(_repository.IsRevoked("valid-token"));
        Assert.Single(_repository.ActiveLinks(7, 12));
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Owner_replace_rechecks_expiry_before_insert_and_does_not_commit_an_expired_replacement()
    {
        _repository.AfterOwnerLinkRevoke = () => _clock.Now = ExpiresAt;

        var result = await _service.ReplaceCurrentLinkAsync(7, 12);

        Assert.False(result.Succeeded);
        Assert.Equal("owner_private_link_unavailable", result.Error!.Code);
        Assert.Equal(0, _repository.OwnerLinkInsertAttempts);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Owner_replace_does_not_extend_an_expired_link_or_cross_business_boundary()
    {
        _clock.Now = ExpiresAt;
        var expired = await _service.ReplaceCurrentLinkAsync(7, 12);
        _clock.Now = StartsAt.AddHours(-1);
        var wrongTenant = await _service.ReplaceCurrentLinkAsync(8, 12);

        Assert.False(expired.Succeeded);
        Assert.False(wrongTenant.Succeeded);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Single(_repository.ActiveLinks(7, 12));
    }

    [Fact]
    public async Task Ambiguous_multiple_active_owner_links_fail_closed_without_returning_a_token()
    {
        _repository.AddLink("second-token", 7, 12, Lookup(ExpiresAt));

        var copy = await _service.CopyCurrentLinkAsync(7, 12);
        var replace = await _service.ReplaceCurrentLinkAsync(7, 12);

        Assert.False(copy.Succeeded);
        Assert.False(replace.Succeeded);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(2, _repository.ActiveLinks(7, 12).Count);
    }

    private PrivateAppointmentLinkRecord Lookup(DateTime expiresAt) => new(
        StartsAt, StartsAt.AddMinutes(30), expiresAt, "Corte", "Ana Pérez",
        AppointmentStatus.Scheduled, BookingStatus.Pending, null, null, null, null, null);

    private static void AssertUnavailable(WorkUp.BusinessLogic.Common.Result result)
    {
        Assert.False(result.Succeeded);
        Assert.Equal("private_link_unavailable", result.Error!.Code);
        Assert.Equal("Este enlace ya no está disponible. Contactá al negocio.", result.Error.Message);
    }

    private sealed class RecordingLinkProtector : ILinkTokenProtector
    {
        public string Protect(string token) => "protected:" + token;
        public string Unprotect(string protectedToken) => protectedToken.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedToken["protected:".Length..]
            : throw new CryptographicException("invalid protected token");
    }

    private sealed class RecordingUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://trusted.example/t/" + Uri.EscapeDataString(token);
    }
}
