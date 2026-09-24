using Microsoft.AspNetCore.Mvc;
using WorkUp.Areas.Owner.Models.Day;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Staff;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure;

namespace WorkUp.Areas.Owner.Controllers;

public sealed class DayController(OwnerDayService dayService, OwnerDayActionsService actions, IClock clock,
    OwnerWalkInService? walkIns = null, BusinessSettingsService? settings = null,
    CatalogService? catalog = null, StaffService? staff = null, ClientsService? clients = null) : OwnerControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(DateOnly? date = null, CancellationToken ct = default)
    {
        SetPrivateHeaders();
        var day = await dayService.GetDayAsync(BusinessId, date ?? clock.Today, ct);
        var allowsWalkIns = settings is not null && (await settings.GetAsync(BusinessId, ct)).AllowsWalkIns;
        // RF-15: RF-11 is invocable from the Jornada itself; the form never receives BusinessId or a start time.
        var walkIn = allowsWalkIns ? await PopulateWalkIn(new WalkInViewModel(), ct) : null;
        return View(new DayIndexViewModel(day, clock.Now, allowsWalkIns, walkIn));
    }

    [HttpGet]
    public async Task<IActionResult> WalkIn(CancellationToken ct = default)
    {
        SetPrivateHeaders();
        if (!(await settings!.GetAsync(BusinessId, ct)).AllowsWalkIns) return NotFound();
        return View(await PopulateWalkIn(new WalkInViewModel(), ct));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WalkIn(WalkInViewModel model, CancellationToken ct = default)
    {
        SetPrivateHeaders();
        if (!(await settings!.GetAsync(BusinessId, ct)).AllowsWalkIns) return NotFound();
        if (model.AgreedPrice is { } price && decimal.Round(price, 2) != price)
            ModelState.AddModelError(nameof(model.AgreedPrice), "Ingresá un precio con hasta dos decimales.");
        if (ModelState.IsValid)
        {
            var result = await walkIns!.RegisterAsync(BusinessId,
                new OwnerWalkInRequest(model.ClientId, model.ServiceId, model.WorkerId,
                    model.AgreedPrice, model.PriceReason, model.CollectPayment, model.NextVisitNote), ct);
            if (result.Succeeded)
            {
                this.Flash(result.Value.HasOverlap
                    ? "Atención registrada. Se superpone con otra atención de la trabajadora; revisá la jornada."
                    : "Atención registrada.");
                // PRG to the saved start date: a second clock read could already be past midnight.
                return RedirectToAction(nameof(Index), new { date = DateOnly.FromDateTime(result.Value.StartsAt) });
            }
            ModelState.AddBusinessError(result.Error!);
        }
        // Stay on the Jornada so the invalid input and its errors remain visible in place.
        var day = await dayService.GetDayAsync(BusinessId, clock.Today, ct);
        return View(nameof(Index), new DayIndexViewModel(day, clock.Now, true, await PopulateWalkIn(model, ct)));
    }

    private async Task<WalkInViewModel> PopulateWalkIn(WalkInViewModel model, CancellationToken ct)
    {
        model.Services = (await catalog!.ListAsync(BusinessId, ct))
            .Where(s => s.IsActive && s.MaxCapacity == 1).Select(s => new WalkInOption(s.Id, s.Name)).ToList();
        model.Workers = (await staff!.ListAsync(BusinessId, ct))
            .Where(w => w.IsActive).Select(w => new WalkInOption(w.Id, w.FullName)).ToList();
        model.Clients = (await clients!.SearchAsync(BusinessId, null, ct))
            .Select(c => new WalkInOption(c.Id, c.FullName)).ToList();
        return model;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Finish(int id, DateOnly date, decimal? amount, string? nextVisitNote,
        CancellationToken ct = default)
    {
        SetPrivateHeaders();
        if (nextVisitNote?.Length > 1000 || amount is <= 0 || amount is > 9999999999.99m ||
            amount is { } value && decimal.Round(value, 2) != value)
            return ReturnToDay(date, "Revisá el importe y la observación (máximo 1000 caracteres).", false);
        var result = await actions.FinishIndividualAsync(BusinessId, id,
            amount is { } charge ? new OwnerDayCharge(charge) : null, nextVisitNote, ct);
        return ReturnToDay(date, result.Succeeded ? "Atención registrada." : SafeError(result), result.Succeeded);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAbsent(int id, DateOnly date, CancellationToken ct = default)
    {
        SetPrivateHeaders();
        var result = await actions.MarkAbsentAsync(BusinessId, id, ct);
        return ReturnToDay(date, result.Succeeded ? "Ausencia registrada." : SafeError(result), result.Succeeded);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Charge(int id, DateOnly date, decimal? amount, CancellationToken ct = default)
    {
        SetPrivateHeaders();
        if (amount is not { } value || value <= 0 || value > 9999999999.99m || decimal.Round(value, 2) != value)
            return ReturnToDay(date, "Ingresá un importe válido para cobrar.", false);
        var result = await actions.ChargeIndividualAsync(BusinessId, id, new OwnerDayCharge(value), ct);
        return ReturnToDay(date, result.Succeeded ? "Cobro registrado." : SafeError(result), result.Succeeded);
    }

    private IActionResult ReturnToDay(DateOnly date, string message, bool success)
    {
        this.Flash(message, success ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index), new { date = date == default ? clock.Today : date });
    }

    private static string SafeError(Result result) => result.Error?.Code switch
    {
        "owner_day_invalid_charge" or "owner_day_price_mismatch" or "owner_day_invalid_note" => result.Error.Message,
        _ => "No se pudo registrar la acción. Revisá el estado del turno e intentá de nuevo."
    };

    private void SetPrivateHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
