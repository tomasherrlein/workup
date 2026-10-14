using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Models.PrivateAppointments;

namespace WorkUp.Controllers;

[AllowAnonymous]
[Route("t/{token}")]
public sealed class PrivateAppointmentsController(PrivateAppointmentLinkService linkService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string token, CancellationToken ct)
    {
        SetPrivateResponseHeaders();
        var appointment = await linkService.GetPublicViewAsync(token, ct);
        ViewData["Title"] = "Ver mi turno";
        return View(new PrivateAppointmentPageViewModel(appointment));
    }

    [HttpPost("confirmar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string token, CancellationToken ct)
    {
        SetPrivateResponseHeaders();
        await linkService.ConfirmIntentionAsync(token, ct);
        return RedirectToAction(nameof(Index), new { token });
    }

    [HttpPost("cancelar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(string token, CancellationToken ct)
    {
        SetPrivateResponseHeaders();
        await linkService.CancelFromPrivateLinkAsync(token, ct);
        return RedirectToAction(nameof(Index), new { token });
    }

    [HttpPost("inasistencia")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReportAbsence(string token, CancellationToken ct)
    {
        SetPrivateResponseHeaders();
        await linkService.ReportAbsenceFromPrivateLinkAsync(token, ct);
        return RedirectToAction(nameof(Index), new { token });
    }

    private void SetPrivateResponseHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
