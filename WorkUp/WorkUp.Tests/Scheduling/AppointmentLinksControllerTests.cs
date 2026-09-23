using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class AppointmentLinksControllerTests
{
    [Fact]
    public async Task Copy_current_selects_the_existing_copy_view_and_keeps_private_response_headers()
    {
        var now = new DateTime(2026, 9, 22, 9, 0, 0);
        var clock = new FakeClock { Now = now };
        var repository = new FakePrivateLinkRepository();
        repository.AddLink(
            "copy-current-test-token",
            businessId: 7,
            bookingId: 12,
            new PrivateAppointmentLinkRecord(
                now.AddHours(2), now.AddHours(2).AddMinutes(30), now.AddDays(1), "Service", "Worker",
                AppointmentStatus.Scheduled, BookingStatus.Pending, null, null, null, null, null));

        var response = new DefaultHttpContext();
        response.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(WorkUpClaims.BusinessId, "7")], "test"));
        var controller = new AppointmentLinksController(
            new OwnerAppointmentLinksService(new EmptyOwnerPrivateAppointmentRepository(), clock),
            new PrivateAppointmentLinkService(
                repository,
                new FakeUnitOfWork(),
                new TestLinkTokenProtector(),
                new TestAppointmentUrlBuilder(),
                clock))
        {
            ControllerContext = new ControllerContext { HttpContext = response }
        };

        var result = await controller.CopyCurrent(12, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Copy", view.ViewName);
        Assert.Equal("no-store", response.Response.Headers.CacheControl);
        Assert.Equal("no-cache", response.Response.Headers.Pragma);
        Assert.Equal("no-referrer", response.Response.Headers["Referrer-Policy"]);
    }

    private sealed class EmptyOwnerPrivateAppointmentRepository : IOwnerPrivateAppointmentRepository
    {
        public Task<IReadOnlyList<OwnerPrivateAppointmentSummary>> ListRecentIndividualBookingsAsync(
            int businessId, DateTime now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OwnerPrivateAppointmentSummary>>([]);
    }

    private sealed class TestLinkTokenProtector : ILinkTokenProtector
    {
        public string Protect(string token) => "protected:" + token;

        public string Unprotect(string protectedToken) =>
            protectedToken.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedToken["protected:".Length..]
                : throw new System.Security.Cryptography.CryptographicException("Invalid test token.");
    }

    private sealed class TestAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://example.invalid/" + token;
    }
}
