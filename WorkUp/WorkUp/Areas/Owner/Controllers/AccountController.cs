using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.Business.Businesses;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.ViewModels.Businesses;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>Registration, login and logout (RF-01, RNF Seguridad). Anonymous, except Logout.</summary>
[Area("Owner")]
public class AccountController(RegisterBusinessService registerBusinessService, OwnerLoginService ownerLoginService) : Controller
{
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewBag.ReturnUrl = returnUrl;
        return View(new LoginViewModel());
    }

    [HttpPost]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl, CancellationToken ct)
    {
        ViewBag.ReturnUrl = returnUrl;
        if (!ModelState.IsValid)
            return View(model);

        var result = await ownerLoginService.LoginAsync(model.Username, model.Password, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View(model);
        }

        await OwnerSignIn.SignInAsync(HttpContext, result.Value);

        if (returnUrl is not null && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction("Index", "Day");
    }

    [HttpGet]
    public IActionResult Register() => View(new RegisterBusinessViewModel());

    [HttpPost]
    public async Task<IActionResult> Register(RegisterBusinessViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(model);

        var request = new RegisterBusinessRequest(
            model.Name, model.Slug, model.ContactPhone, model.NotificationEmail,
            model.Username, model.Password, model.ConfirmPassword);

        var result = await registerBusinessService.RegisterAsync(request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View(model);
        }

        await OwnerSignIn.SignInAsync(HttpContext, result.Value);
        this.Flash("Cuenta creada. ¡Bienvenida a WorkUp!");
        return RedirectToAction("Index", "Day");
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = AuthSchemes.Owner)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Owner);
        return RedirectToAction("Index", "Home");
    }
}
