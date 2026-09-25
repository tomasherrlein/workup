using Microsoft.AspNetCore.Mvc;

namespace WorkUp.Controllers;

/// <summary>
/// Landing page of the authenticated client panel (T20). It exists so a successful sign-in always has a safe local
/// destination and so the tenant guard of <see cref="ClientPanelControllerBase"/> has a protected page to send the
/// client to; T21 replaces it with the real panel of appointments and classes.
/// </summary>
[Route("r/{slug}/cuenta")]
public sealed class ClientAccountHomeController : ClientPanelControllerBase
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
