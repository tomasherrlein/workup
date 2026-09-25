using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.Domain.Entities;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.ViewModels.ClientAccounts;

namespace WorkUp.Controllers;

/// <summary>
/// Public client account pages under <c>/r/{slug}/cuenta/...</c> (RF-25, RF-15, RF-27): self-registration with a
/// confirmed email, sign-in, logout, and password recovery. The business is always resolved from the public slug of
/// the route and never from a form field, so no request can aim at another tenant's account. A link is spent only by
/// the POST of its form: the GET that renders the form leaves the token untouched, because mail scanners and
/// prefetchers follow every emailed URL. No page here opens a session except a successful sign-in, and the only
/// action that clears the client cookie is the POST of <c>salir</c>.
/// </summary>
[AllowAnonymous]
[Route("r/{slug}/cuenta")]
public sealed class ClientAccountController(
    ClientRegistrationService registrationService,
    ClientAccessService accessService,
    IBusinessRepository businesses) : Controller
{
    private const string BusinessNotFoundCode = "business_not_found";
    private const string OwnerAssistanceCode = "client_record_needs_owner_assistance";
    private const string PasswordTooShortCode = "password_too_short";

    // --- Registration and email confirmation (RF-25) -----------------------------------------------------------

    [HttpGet("registrarse")]
    public async Task<IActionResult> Register(string slug, CancellationToken ct)
    {
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        return View("Register", new ClientRegisterViewModel());
    }

    /// <summary>
    /// Creates a pending account with the password hash and the temporary names of the future ficha, and queues the
    /// single confirmation email. No session is started here: the account only becomes usable when the emailed link
    /// is consumed, so registering never signs anyone in (D-18).
    /// </summary>
    [HttpPost("registrarse")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(string slug, ClientRegisterViewModel model, CancellationToken ct)
    {
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        if (!ModelState.IsValid)
            return View("Register", model);

        // Backstop of the confirmation field: the business service only receives the password, so unlike the owner
        // registration (where the rule lives in the service) this check belongs to the web layer. When the binder
        // already rejected the model, the form comes back with the binder's message instead of a duplicate.
        if (model.Password != model.ConfirmPassword)
        {
            ModelState.AddModelError(nameof(model.ConfirmPassword), "Las contraseñas no coinciden.");
            return View("Register", model);
        }

        var attempt = await registrationService.RegisterAsync(
            new RegisterClientAccountRequest(slug, model.FirstName, model.LastName, model.Email, model.Password), ct);
        if (!attempt.Succeeded)
        {
            if (attempt.Error!.Code == BusinessNotFoundCode)
                return NotFound();

            ModelState.AddBusinessError(attempt.Error);
            return View("Register", model);
        }

        this.Flash(
            "Te enviamos un email para confirmar tu cuenta. El enlace vence en 48 horas y sirve una sola vez.",
            FlashType.Info);
        return Redirect(AccountPath(slug, "reenviar"));
    }

    /// <summary>
    /// Confirmation screen of the emailed link. It is a pure read: opening the URL spends nothing, so a scanner that
    /// follows the email cannot burn the single use. The token travels back in a hidden field.
    /// </summary>
    [HttpGet("verificar/{token}")]
    public async Task<IActionResult> Verify(string slug, string token, CancellationToken ct)
    {
        SetTokenPageHeaders();
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        return View("Verify", new ClientVerifyEmailViewModel { Token = token });
    }

    /// <summary>
    /// Spends the single-use verification link: it creates the ficha with the pending names and activates the
    /// account. A link that no longer works answers one generic page, and the case where the business already has a
    /// ficha with that email fails closed towards the owner-assisted invitation (T22, RF-26) instead of taking over
    /// someone else's history. Confirming the email never signs in: the session starts with the password.
    /// </summary>
    [HttpPost("verificar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(string slug, ClientVerifyEmailViewModel model, CancellationToken ct)
    {
        SetTokenPageHeaders();
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        if (!ModelState.IsValid)
            return View("Verify", model);

        var attempt = await registrationService.VerifyEmailAsync(slug, model.Token, ct);
        if (!attempt.Succeeded)
        {
            return attempt.Error!.Code switch
            {
                BusinessNotFoundCode => NotFound(),
                OwnerAssistanceCode => View(
                    "Status", ClientAccountStatusViewModel.OwnerAssistanceRequired(business.Name)),
                _ => View("Status", ClientAccountStatusViewModel.VerificationLinkUnavailable(business.Slug))
            };
        }

        this.Flash("Confirmamos tu email. Ya podés ingresar con tu contraseña.");
        return Redirect(AccountPath(slug, "ingresar"));
    }

    [HttpGet("reenviar")]
    public async Task<IActionResult> Resend(string slug, CancellationToken ct)
    {
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        return View("Resend", new ClientResendVerificationViewModel());
    }

    /// <summary>
    /// Sends a new 48 h confirmation link to a pending account, so an expired link is never a dead end. The answer is
    /// the same for an unknown email and for an already confirmed account: the request enumerates nothing.
    /// </summary>
    [HttpPost("reenviar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(string slug, ClientResendVerificationViewModel model, CancellationToken ct)
    {
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        if (!ModelState.IsValid)
            return View("Resend", model);

        var attempt = await registrationService.ResendVerificationAsync(
            new ResendClientVerificationRequest(slug, model.Email), ct);
        if (!attempt.Succeeded)
        {
            if (attempt.Error!.Code == BusinessNotFoundCode)
                return NotFound();

            ModelState.AddBusinessError(attempt.Error);
            return View("Resend", model);
        }

        this.Flash("Si esa cuenta está pendiente de confirmar, te enviamos un enlace nuevo.", FlashType.Info);
        return Redirect(AccountPath(slug, "reenviar"));
    }

    // --- Sign-in and logout (RF-15) ----------------------------------------------------------------------------

    [HttpGet("ingresar")]
    public async Task<IActionResult> Login(string slug, string? returnUrl, CancellationToken ct)
    {
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        // The form only carries a destination that already passed the local-and-same-business check, and the value the
        // binder read from the query string is rewritten to match before the view renders it.
        var model = new ClientLoginViewModel { ReturnUrl = returnUrl };
        SanitizeReturnUrlForRendering(slug, model);
        return View("Login", model);
    }

    /// <summary>
    /// Starts the session of an active account: verified email and linked ficha. A missing account, a wrong password,
    /// a pending account and an unlinked one all get one generic error and no session, so the form never reveals who
    /// has an account and sign-in never links a ficha by email. The destination is the offered local URL only when it
    /// belongs to this same business; otherwise the authenticated placeholder of the business.
    /// </summary>
    [HttpPost("ingresar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string slug, ClientLoginViewModel model, CancellationToken ct)
    {
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        if (!ModelState.IsValid)
        {
            SanitizeReturnUrlForRendering(slug, model);
            return View("Login", model);
        }

        var attempt = await accessService.LoginAsync(
            new ClientLoginRequest(slug, model.Email, model.Password), ct);
        if (!attempt.Succeeded)
        {
            if (attempt.Error!.Code == BusinessNotFoundCode)
                return NotFound();

            ModelState.AddBusinessError(attempt.Error);
            SanitizeReturnUrlForRendering(slug, model);
            return View("Login", model);
        }

        await ClientSignIn.SignInAsync(HttpContext, attempt.Value);
        return Redirect(ResolveReturnUrl(slug, model.ReturnUrl));
    }

    /// <summary>
    /// Clears the client session. It is a POST on purpose: no link, prefetch or image can sign a client out.
    /// </summary>
    [HttpPost("salir")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(string slug, CancellationToken ct)
    {
        await HttpContext.SignOutAsync(AuthSchemes.Client);
        this.Flash("Cerraste tu sesión.", FlashType.Info);
        return Redirect($"/r/{Uri.EscapeDataString(slug)}");
    }

    // --- Password recovery (RF-27) -----------------------------------------------------------------------------

    [HttpGet("recuperar")]
    public async Task<IActionResult> Recover(string slug, CancellationToken ct)
    {
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        return View("Recover", new ClientRecoverPasswordViewModel());
    }

    /// <summary>
    /// Queues a single-use 30 minute recovery link. The answer never depends on whether the email has an active
    /// account, so the request reveals nothing; the message is the same for an absent, a pending and an active one.
    /// </summary>
    [HttpPost("recuperar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Recover(string slug, ClientRecoverPasswordViewModel model, CancellationToken ct)
    {
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        if (!ModelState.IsValid)
            return View("Recover", model);

        var attempt = await accessService.RequestPasswordResetAsync(
            new ClientPasswordResetRequest(slug, model.Email), ct);
        if (!attempt.Succeeded)
        {
            if (attempt.Error!.Code == BusinessNotFoundCode)
                return NotFound();

            ModelState.AddBusinessError(attempt.Error);
            return View("Recover", model);
        }

        this.Flash(
            "Si ese email tiene una cuenta, te enviamos un enlace para restablecer tu contraseña. " +
            "Vence en 30 minutos y sirve una sola vez.",
            FlashType.Info);
        return Redirect(AccountPath(slug, "recuperar"));
    }

    /// <summary>
    /// New-password screen of the recovery link. Like the confirmation screen, it only renders: the token is spent by
    /// the POST, so a scanner that opens the email cannot invalidate the link before the person uses it.
    /// </summary>
    [HttpGet("restablecer/{token}")]
    public async Task<IActionResult> Reset(string slug, string token, CancellationToken ct)
    {
        SetTokenPageHeaders();
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        return View("Reset", new ClientResetPasswordViewModel { Token = token });
    }

    /// <summary>
    /// Spends the recovery link and replaces the password hash in one transaction. It never sends the password
    /// anywhere, never starts a session, and answers one generic page for a replayed, expired or foreign link. A
    /// rejected password keeps the link usable, so a short password is not a one-shot loss of the link.
    /// </summary>
    [HttpPost("restablecer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(string slug, ClientResetPasswordViewModel model, CancellationToken ct)
    {
        SetTokenPageHeaders();
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        if (!ModelState.IsValid)
            return View("Reset", model);

        // Backstop of the confirmation field, exactly as in registration: the recovery service replaces the password
        // without seeing the confirmation, so the match is enforced here before the single-use link is spent.
        if (model.NewPassword != model.ConfirmPassword)
        {
            ModelState.AddModelError(nameof(model.ConfirmPassword), "Las contraseñas no coinciden.");
            return View("Reset", model);
        }

        var attempt = await accessService.CompletePasswordResetAsync(
            new CompleteClientPasswordResetRequest(slug, model.Token, model.NewPassword), ct);
        if (!attempt.Succeeded)
        {
            if (attempt.Error!.Code == BusinessNotFoundCode)
                return NotFound();

            if (attempt.Error.Code == PasswordTooShortCode)
            {
                ModelState.AddBusinessError(attempt.Error);
                return View("Reset", model);
            }

            return View("Status", ClientAccountStatusViewModel.PasswordResetLinkUnavailable(business.Slug));
        }

        this.Flash("Tu contraseña cambió. Ingresá con la nueva.");
        return Redirect(AccountPath(slug, "ingresar"));
    }

    // --- Shared plumbing ---------------------------------------------------------------------------------------

    /// <summary>Authenticated placeholder of the client panel; T21 replaces it with the real panel.</summary>
    private static string AccountPath(string slug) => $"/r/{Uri.EscapeDataString(slug)}/cuenta";

    private static string AccountPath(string slug, string action) => $"{AccountPath(slug)}/{action}";

    private Task<Business?> FindBusinessAsync(string slug, CancellationToken ct) =>
        businesses.GetBySlugAsync(slug.Trim(), ct);

    private void SetPublicLayout(Business business)
    {
        ViewData["BusinessName"] = business.Name;
        ViewData["HomeUrl"] = $"/r/{Uri.EscapeDataString(business.Slug)}";
    }

    /// <summary>A page that shows or receives a token is never stored nor allowed to leak it through a referrer.</summary>
    private void SetTokenPageHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>
    /// Destination of a successful sign-in: the offered URL only when it is a local path of this same business, and
    /// the panel placeholder otherwise. Both conditions matter: a protocol-relative or absolute URL would turn the
    /// sign-in into an open redirect, and a path of another <c>/r/{slug}</c> would carry the client to a tenant that
    /// is not hers. Dot segments and escape sequences are refused so <c>/r/this/../other</c> cannot smuggle a path
    /// out of the checked prefix.
    /// </summary>
    private string ResolveReturnUrl(string slug, string? returnUrl) =>
        IsReturnUrlOfferedByThisBusiness(returnUrl, slug)
            ? returnUrl!
            : AccountPath(slug);

    /// <summary>
    /// Keeps the sign-in form from rendering a destination the controller already refused. The binder records what the
    /// request carried in <see cref="ModelState"/> -- the query string on the GET, the hidden field on the POST -- and
    /// the <c>asp-for</c> input tag helper resolves the field from that entry before falling back to the model, matching
    /// the key case-insensitively. Sanitizing only the model is therefore not enough: a hostile
    /// <c>?returnUrl=https://evil.example</c> would come back in the hidden field of a form whose model no longer has
    /// it. Rewriting the entry makes ModelState agree with the model. This is only the echo, never the check:
    /// <see cref="ResolveReturnUrl"/> still validates the offer on its own before any redirect, so a form that smuggled
    /// a destination in by hand changes nothing about where a successful sign-in lands.
    /// </summary>
    private void SanitizeReturnUrlForRendering(string slug, ClientLoginViewModel model)
    {
        model.ReturnUrl = IsReturnUrlOfferedByThisBusiness(model.ReturnUrl, slug) ? model.ReturnUrl : null;
        ModelState.Remove(nameof(ClientLoginViewModel.ReturnUrl));
        if (model.ReturnUrl is not null)
            ModelState.SetModelValue(nameof(ClientLoginViewModel.ReturnUrl), model.ReturnUrl, model.ReturnUrl);
    }

    private bool IsReturnUrlOfferedByThisBusiness(string? returnUrl, string slug)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || Url?.IsLocalUrl(returnUrl) != true)
            return false;

        var separator = returnUrl.IndexOfAny(['?', '#']);
        var path = separator >= 0 ? returnUrl[..separator] : returnUrl;
        if (path.Contains('\\') || path.Contains('%') || path.Contains("..", StringComparison.Ordinal))
            return false;

        return path.StartsWith($"/r/{slug}/", StringComparison.OrdinalIgnoreCase);
    }
}
