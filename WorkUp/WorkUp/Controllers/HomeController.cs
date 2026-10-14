using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using WorkUp.ViewModels.Shared;

namespace WorkUp.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();

    /// <summary>Friendly page for error status codes, re-executed as <c>/Home/Status/{code}</c>.</summary>
    public IActionResult Status(int id)
    {
        Response.StatusCode = id is >= 400 and <= 599 ? id : 500;
        return View(Response.StatusCode);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
