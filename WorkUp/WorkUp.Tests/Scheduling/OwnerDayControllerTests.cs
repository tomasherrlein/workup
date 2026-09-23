using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerDayControllerTests
{
    private static readonly DateOnly Date = new(2026, 3, 10);

    [Fact]
    public async Task Finish_uses_claim_tenant_and_redirects_to_selected_date_with_note_and_charge()
    {
        var (controller, repository) = Create();
        repository.Booking = FakeOwnerDayActionsRepository.Default() with { AgreedPrice = 90m };
        var result = await controller.Finish(12, Date, 90m, "Only for owner");
        Assert.Equal(Date, Assert.IsType<RedirectToActionResult>(result).RouteValues!["date"]);
        Assert.Equal(7, Assert.Single(repository.Payments).BusinessId);
        Assert.Equal(90m, Assert.Single(repository.Payments).Amount);
        Assert.Equal("Only for owner", Assert.Single(repository.Notes));
    }

    [Fact]
    public async Task Wrong_price_does_not_charge_and_returns_to_day_with_error()
    {
        var (controller, repository) = Create();
        repository.Booking = FakeOwnerDayActionsRepository.Default() with { AgreedPrice = 90m };
        var result = await controller.Charge(12, Date, 100m);
        Assert.Equal(Date, Assert.IsType<RedirectToActionResult>(result).RouteValues!["date"]);
        Assert.Empty(repository.Payments);
        Assert.Contains("precio acordado", string.Join(" ", controller.TempData.Values.OfType<string>()));
    }

    [Fact]
    public async Task Invalid_amount_does_not_call_repository()
    {
        var (controller, repository) = Create();
        await controller.Charge(12, Date, null);
        Assert.Empty(repository.Payments);
    }

    [Fact]
    public async Task Absence_uses_claim_tenant_and_returns_to_day()
    {
        var (controller, repository) = Create();
        var result = await controller.MarkAbsent(12, Date);
        Assert.Equal(Date, Assert.IsType<RedirectToActionResult>(result).RouteValues!["date"]);
        Assert.Equal(WorkUp.Domain.Enums.BookingStatus.Absent, repository.Booking.BookingStatus);
    }

    private static (DayController Controller, FakeOwnerDayActionsRepository Repository) Create()
    {
        var clock = new FakeClock { Now = Date.ToDateTime(new TimeOnly(10, 0)) };
        var repository = new FakeOwnerDayActionsRepository();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkUpClaims.BusinessId, "7")], "test"))
        };
        var controller = new DayController(new OwnerDayService(new FakeOwnerDayRepository(), clock),
            new OwnerDayActionsService(repository, new FakeUnitOfWork(), clock), clock)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new DictionaryTempDataProvider())
        };
        return (controller, repository);
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
