using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.ViewModels.ClientPanel;

namespace WorkUp.Controllers;

/// <summary>
/// "Mis turnos y clases" of the authenticated client panel (T21.1/T21.2/T21.3, RF-08, RF-22). The agenda and every
/// mutation are scoped to the tenant and the ficha taken exclusively from the client session cookie through the
/// inherited properties of <see cref="ClientPanelControllerBase"/>, so no action accepts a business or client id from
/// the route, the query or a form and one session can never read or mutate another ficha's bookings. The base guard
/// already refuses a session in another business's route before an action runs.
/// <para>
/// The read is a GET and never changes state. Confirm and the two cancels (individual turn and drop-in class) are
/// POST-only antiforgery routes that receive just the booking id from the route and prove ownership with the
/// cookie-scoped <c>ClientId</c>; they never reuse the private-link authorization. A booking of another ficha or
/// another tenant answers the same generic 404 as one that does not exist; a booking whose state or start no longer
/// allows the action flashes a safe message and returns to the own agenda without leaking existence.
/// </para>
/// </summary>
[Route("r/{slug}/mis-turnos")]
public sealed class ClientAppointmentsController(
    ClientPanelService service,
    ClientBookingActionsService bookingActionsService,
    ClientClassCancellationService classCancellationService) : ClientPanelControllerBase
{
    /// <summary>Failure code of a booking that is not this session's own, inside this tenant, or does not exist.</summary>
    private const string BookingNotFoundCode = "client_booking_not_found";

    /// <summary>Failure code of a class booking that is not this session's own drop-in, inside this tenant, or does not exist.</summary>
    private const string ClassBookingNotFoundCode = "client_class_booking_not_found";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var agenda = await service.GetAgendaAsync(BusinessId, ClientId, ct);
        return View(ClientAppointmentsViewModel.From(User.GetBusinessSlug(), agenda));
    }

    /// <summary>
    /// Records the intention for the session's own individual turn before its start (RF-08). Repeating it is a no-op.
    /// </summary>
    [HttpPost("{bookingId:int}/confirmar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int bookingId, CancellationToken ct)
    {
        var result = await bookingActionsService.ConfirmIntentionAsync(BusinessId, ClientId, bookingId, ct);
        return RedirectAfterAction(result, "Confirmaste que vas a asistir.", "No se pudo confirmar este turno.");
    }

    /// <summary>
    /// Cancels the session's own pending or confirmed individual turn before its start (RF-22), without touching
    /// payments.
    /// </summary>
    [HttpPost("{bookingId:int}/cancelar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int bookingId, CancellationToken ct)
    {
        var result = await bookingActionsService.CancelAsync(BusinessId, ClientId, bookingId, ct);
        return RedirectAfterAction(result, "Cancelaste tu turno.", "No se pudo cancelar este turno.");
    }

    /// <summary>
    /// Cancels the session's own drop-in annotation of a class before its start (RF-22), without touching payments,
    /// the fixed schedule or any enrollment. The identity is the ficha of the cookie; the fixed class date and any
    /// annotation of another ficha or tenant answer the same generic 404 as a missing booking.
    /// </summary>
    [HttpPost("{bookingId:int}/cancelar-clase")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelClass(int bookingId, CancellationToken ct)
    {
        var result = await classCancellationService.CancelOwnDropInAsync(BusinessId, ClientId, bookingId, ct);
        return RedirectAfterAction(result, "Cancelaste tu clase suelta.", "No se pudo cancelar esta clase.");
    }

    /// <summary>
    /// A missing or foreign booking is a generic 404; any other refusal flashes a safe message and returns to the own
    /// agenda. The target is built from the authenticated slug, never from the route value, so it can never retarget
    /// another tenant.
    /// </summary>
    private IActionResult RedirectAfterAction(Result result, string successMessage, string fallbackMessage)
    {
        if (result.Succeeded)
        {
            this.Flash(successMessage);
            return RedirectToOwnAgenda();
        }

        // Both cancel routes share the same generic 404: an individual turn and a drop-in class each use their own
        // not-found code, and in both cases a foreign or missing booking is indistinguishable from a nonexistent one.
        if (result.Error?.Code is BookingNotFoundCode or ClassBookingNotFoundCode)
            return NotFound();

        this.Flash(result.Error?.Message ?? fallbackMessage, FlashType.Error);
        return RedirectToOwnAgenda();
    }

    private IActionResult RedirectToOwnAgenda() =>
        Redirect($"/r/{Uri.EscapeDataString(User.GetBusinessSlug())}/mis-turnos");
}