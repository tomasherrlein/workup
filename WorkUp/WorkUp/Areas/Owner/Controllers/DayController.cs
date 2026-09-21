using Microsoft.AspNetCore.Mvc;
using WorkUp.Business.Abstractions;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>
/// Jornada del día. Minimal placeholder for T01 (RF-01 needs somewhere to land after login); T12 replaces its
/// content with the real Ahora/Próximo/Más tarde/Finalizado sections.
/// </summary>
public class DayController(IClock clock) : OwnerControllerBase
{
    public IActionResult Index() => View(clock.Today);
}
