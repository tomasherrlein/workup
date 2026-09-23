using Microsoft.AspNetCore.Mvc;
using WorkUp.Areas.Owner.Models.Day;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Areas.Owner.Controllers;

public sealed class DayController(OwnerDayService dayService, IClock clock) : OwnerControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(DateOnly? date = null, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var day = await dayService.GetDayAsync(BusinessId, date ?? clock.Today, ct);
        return View(new DayIndexViewModel(day));
    }
}
