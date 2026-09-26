using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure.Auth;
using WorkUp.ViewModels.ClientPanel;

namespace WorkUp.Controllers;

/// <summary>
/// Read-only "Mis turnos y clases" of the authenticated client panel (T21.1, RF-08, RF-22). The agenda is read for
/// the tenant and the ficha taken exclusively from the client session cookie through the inherited properties of
/// <see cref="ClientPanelControllerBase"/>, so the action accepts no business or client id from the route, the query
/// or a form and one session can never see another ficha's bookings. The base guard already refuses a session in
/// another business's route before this action runs.
/// <para>
/// This work unit only reads: it renders upcoming and history rows with their safe public data and the eligibility
/// label of each one. Confirm, cancel and reschedule are separate work units, so no action link or form is rendered
/// here yet.
/// </para>
/// </summary>
[Route("r/{slug}/mis-turnos")]
public sealed class ClientAppointmentsController(ClientPanelService service) : ClientPanelControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var agenda = await service.GetAgendaAsync(BusinessId, ClientId, ct);
        return View(ClientAppointmentsViewModel.From(User.GetBusinessSlug(), agenda));
    }
}
