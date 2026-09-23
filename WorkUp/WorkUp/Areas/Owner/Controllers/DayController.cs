using Microsoft.AspNetCore.Mvc;
using WorkUp.Areas.Owner.Models.Day;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure;

namespace WorkUp.Areas.Owner.Controllers;

public sealed class DayController(OwnerDayService dayService, OwnerDayActionsService actions, IClock clock) : OwnerControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(DateOnly? date = null, CancellationToken ct = default)
    {
        SetPrivateHeaders();
        var day = await dayService.GetDayAsync(BusinessId, date ?? clock.Today, ct);
        return View(new DayIndexViewModel(day, clock.Now));
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
