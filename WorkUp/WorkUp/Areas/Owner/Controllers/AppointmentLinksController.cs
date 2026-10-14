using Microsoft.AspNetCore.Mvc;
using WorkUp.Areas.Owner.Models.AppointmentLinks;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>Owner-only, tenant-scoped management of private links for individual bookings.</summary>
public sealed class AppointmentLinksController(
    OwnerAppointmentLinksService listingService,
    PrivateAppointmentLinkService linkService) : OwnerControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        SetPrivateResponseHeaders();
        ViewData["Title"] = "Enlaces privados";
        var bookings = await listingService.ListRecentAsync(BusinessId, ct);
        return View(new AppointmentLinksIndexViewModel(bookings));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Copy(int id, CancellationToken ct)
    {
        var result = await linkService.CopyCurrentLinkAsync(BusinessId, id, ct);
        if (!result.Succeeded)
        {
            this.Flash("No se pudo completar la acción sobre el enlace.", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(CopyCurrent), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> CopyCurrent(int id, CancellationToken ct)
    {
        SetPrivateResponseHeaders();
        var result = await linkService.CopyCurrentLinkAsync(BusinessId, id, ct);
        if (!result.Succeeded)
            return NotFound();

        ViewData["Title"] = "Copiar enlace privado";
        return View("Copy", new AppointmentLinkCopyViewModel(result.Value.Url, result.Value.ExpiresAt));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Replace(int id, CancellationToken ct)
    {
        var result = await linkService.ReplaceCurrentLinkAsync(BusinessId, id, ct);
        if (!result.Succeeded)
        {
            this.Flash("No se pudo completar la acción sobre el enlace.", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        this.Flash("Enlace reemplazado.");
        return RedirectToAction(nameof(CopyCurrent), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int id, CancellationToken ct)
    {
        var result = await linkService.RevokeCurrentLinkAsync(BusinessId, id, ct);
        this.Flash(
            result.Succeeded ? "Enlace revocado." : "No se pudo completar la acción sobre el enlace.",
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    private void SetPrivateResponseHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
