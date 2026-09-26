using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.ViewModels.ClientPanel;

namespace WorkUp.Controllers;

/// <summary>
/// "Mis turnos y clases" of the authenticated client panel (T21.1/T21.2/T21.3/T21.4, RF-08, RF-09, RF-22). The
/// agenda and every mutation are scoped to the tenant and the ficha taken exclusively from the client session cookie
/// through the inherited properties of <see cref="ClientPanelControllerBase"/>, so no action accepts a business or
/// client id from the route, the query or a form and one session can never read or mutate another ficha's bookings.
/// The base guard already refuses a session in another business's route before an action runs.
/// <para>
/// The read is a GET and never changes state. Confirm, the two cancels (individual turn and drop-in class) and the
/// reprogramming save are POST-only antiforgery routes that receive just the booking id from the route and prove
/// ownership with the cookie-scoped <c>ClientId</c>; they never reuse the private-link authorization. A booking of
/// another ficha or another tenant answers the same generic 404 as one that does not exist; a booking whose state or
/// start no longer allows the action flashes a safe message and returns to the own agenda without leaking existence.
/// </para>
/// </summary>
[Route("r/{slug}/mis-turnos")]
public sealed class ClientAppointmentsController(
    ClientPanelService service,
    ClientBookingActionsService bookingActionsService,
    ClientClassCancellationService classCancellationService,
    OwnerRescheduleService rescheduleService) : ClientPanelControllerBase
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
    /// Reprogramming screen of the session's own future individual turn (T21.4, RF-09). It shows the same worker's
    /// free starts for the selected day and, when the day has none, the nearest alternative. The identity is the
    /// ficha of the cookie, so a booking of another ficha or tenant, a missing one, and an own one whose shape no
    /// longer allows the move (walk-in, class, cancelled, suspended, started) all answer the same generic 404.
    /// </summary>
    [HttpGet("{bookingId:int}/reprogramar")]
    public async Task<IActionResult> Reschedule(int bookingId, DateOnly? date, CancellationToken ct)
    {
        var booking = await FindOwnRowAsync(bookingId, ct);
        if (booking is null || !booking.CanReschedule)
            return NotFound();

        var input = new ClientRescheduleInput { Date = date ?? DateOnly.FromDateTime(booking.StartsAt) };
        return await BuildRescheduleFormAsync(booking, input, ct);
    }

    /// <summary>
    /// Moves the session's own future individual turn with the same worker (T21.4, RF-09). The request carries only
    /// the chosen day and time: the worker, the ficha and the tenant always come from the reservation and the cookie,
    /// never from a form field. A foreign or missing booking is the same generic 404; a stale or invalid choice
    /// flashes a safe message and returns to the form without leaking existence or the original reservation.
    /// </summary>
    [HttpPost("{bookingId:int}/reprogramar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reschedule(
        int bookingId, [Bind(Prefix = "Input")] ClientRescheduleInput input, CancellationToken ct)
    {
        if (input.Date is null || input.StartTime is null)
        {
            if (input.Date is null)
                ModelState.AddModelError("Input.Date", "Elegí un día.");
            if (input.StartTime is null)
                ModelState.AddModelError("Input.StartTime", "Elegí un horario.");
            return await RerenderRescheduleAsync(bookingId, input, ct);
        }

        var startsAt = input.Date.Value.ToDateTime(input.StartTime.Value);
        var attempt = await rescheduleService.RescheduleOwnIndividualAsync(BusinessId, ClientId, bookingId, startsAt, ct);
        if (attempt.Succeeded)
        {
            this.Flash("Reprogramaste tu turno.");
            return RedirectToOwnAgenda();
        }

        if (attempt.Error!.Code == BookingNotFoundCode)
            return NotFound();

        // Only a business-rule outcome on the own reservation is shown verbatim; a shape or state the core refuses
        // stays behind the generic message, so a stale request never learns anything it did not already know.
        if (RevealableRescheduleErrorCodes.Contains(attempt.Error.Code))
            ModelState.AddBusinessError(attempt.Error);
        else
            ModelState.AddModelError(string.Empty, RescheduleErrorMessage);
        return await RerenderRescheduleAsync(bookingId, input, ct);
    }

    /// <summary>
    /// Rebuilds the reprogramming screen after a refused save. The own row is re-read with the cookie identity, so a
    /// booking that disappeared meanwhile is the same generic 404 as a foreign one; the options are the fresh ones of
    /// the selected day, so the person chooses again from what is really free.
    /// </summary>
    private async Task<IActionResult> RerenderRescheduleAsync(
        int bookingId, ClientRescheduleInput input, CancellationToken ct)
    {
        var booking = await FindOwnRowAsync(bookingId, ct);
        if (booking is null)
            return NotFound();

        return await BuildRescheduleFormAsync(booking, input, ct);
    }

    private async Task<IActionResult> BuildRescheduleFormAsync(
        ClientPanelBooking booking, ClientRescheduleInput input, CancellationToken ct)
    {
        var date = input.Date ?? DateOnly.FromDateTime(booking.StartsAt);
        input.Date = date;
        var options = await rescheduleService.GetOwnOptionsAsync(BusinessId, ClientId, booking.BookingId, date, ct);
        return View("Reschedule", ClientRescheduleViewModel.From(User.GetBusinessSlug(), booking, input, options));
    }

    /// <summary>One own row of the cookie-scoped agenda, or <c>null</c> when the ficha of the session does not own it.</summary>
    private async Task<ClientPanelBooking?> FindOwnRowAsync(int bookingId, CancellationToken ct)
    {
        var agenda = await service.GetAgendaAsync(BusinessId, ClientId, ct);
        return agenda.Upcoming.Concat(agenda.History).FirstOrDefault(row => row.BookingId == bookingId);
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

    private const string RescheduleErrorMessage =
        "No se pudo reprogramar el turno. El horario puede haberse ocupado o el turno ya no admite cambios. " +
        "Revisá los datos e intentá de nuevo.";

    /// <summary>
    /// Business-rule codes safe to show verbatim: they describe an invalid choice on the session's own reservation,
    /// not whether it exists. The not-found and unavailable codes (<c>client_booking_not_found</c>,
    /// <c>client_booking_unavailable</c>) stay behind <see cref="RescheduleErrorMessage"/> so a stale request never
    /// leaks existence or the shape of the row.
    /// </summary>
    private static readonly HashSet<string> RevealableRescheduleErrorCodes = new(StringComparer.Ordinal)
    {
        "client_reschedule_invalid_time",
        "client_reschedule_same_slot",
        "client_reschedule_slot_unavailable"
    };
}