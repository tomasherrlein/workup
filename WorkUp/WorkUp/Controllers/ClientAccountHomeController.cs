using Microsoft.AspNetCore.Mvc;
using WorkUp.Infrastructure.Auth;

namespace WorkUp.Controllers;

/// <summary>
/// Legacy landing page of the authenticated client panel (T20). T21 replaces its placeholder with the real panel of
/// appointments and classes, so <c>/r/{slug}/cuenta</c> now redirects to <c>/r/{slug}/mis-turnos</c>. The target is
/// built from the authenticated business slug of the cookie, never from the route value, so a caller can never
/// retarget another tenant; the route slug still passes through the inherited guard of
/// <see cref="ClientPanelControllerBase"/> before this action runs.
/// </summary>
[Route("r/{slug}/cuenta")]
public sealed class ClientAccountHomeController : ClientPanelControllerBase
{
    [HttpGet("")]
    public IActionResult Index() =>
        Redirect($"/r/{Uri.EscapeDataString(User.GetBusinessSlug())}/mis-turnos");
}
