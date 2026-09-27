using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Notifications;
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
    IBusinessRepository businesses,
    ClientInvitationAcceptanceService? invitationAcceptanceService = null,
    IOptions<WorkUpOptions>? options = null) : Controller
{
    private const string BusinessNotFoundCode = "business_not_found";
    private const string OwnerAssistanceCode = "client_record_needs_owner_assistance";
    private const string PasswordTooShortCode = "password_too_short";
    private const string InvitationLinkUnavailableCode = "client_invitation_link_unavailable";
    private const string InvitationOwnerAssistanceCode = "client_invitation_owner_assistance";

    /// <summary>
    /// The configured email mode (<c>WorkUp:Email:Mode</c>), parsed once with the same fail-fast parser the startup
    /// uses. The flash of every action that queues an email depends on it, because the two modes really do different
    /// things: <see cref="OutboxMode.Retain"/> keeps the notice <c>Pending</c> and nothing is sent, while
    /// <see cref="OutboxMode.Deliver"/> only hands it to the scheduled dispatch. A missing options dependency is read
    /// as the retained default, and an unusable value throws here, before the action can write anything.
    /// </summary>
    private readonly OutboxMode _emailMode = OutboxModeParser.Parse(options?.Value?.Email?.Mode);

    /// <summary>
    /// Chooses the flash that matches what the configured mode really does with a queued notice:
    /// <see cref="OutboxMode.Deliver"/> only hands it to the scheduled dispatch, and <see cref="OutboxMode.Retain"/>
    /// leaves it <c>Pending</c> without sending anything. Neither branch claims an inbox delivery, because the
    /// dispatcher cannot promise one even in Smtp.
    /// </summary>
    private string ModeCopy(string deliverCopy, string retainCopy) =>
        _emailMode == OutboxMode.Deliver ? deliverCopy : retainCopy;

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
    /// is consumed, so registering never signs anyone in (D-18). The flash describes what the configured mode really
    /// does with that notice: in Outbox nothing is sent yet, and in Smtp it is only queued for the automatic dispatch.
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
            ModeCopy(
                "Tu cuenta quedó pendiente de confirmación y el email para confirmarla quedó en cola para su envío " +
                "automático, aunque no podemos garantizar que llegue a tu casilla. El enlace vence en 48 horas y " +
                "sirve una sola vez.",
                "Tu cuenta quedó pendiente de confirmación y el email para confirmarla todavía no se envía: el " +
                "envío automático de emails no está activo en esta versión. El enlace vence en 48 horas y sirve una " +
                "sola vez."),
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
    /// Queues a new 48 h confirmation link for a pending account, so an expired link is never a dead end. The answer
    /// is the same for an unknown email and for an already confirmed account: the request enumerates nothing. The
    /// conditional wording is kept identical in both modes, and only says what the queued notice will really do --
    /// nothing is sent in Outbox, and Smtp hands it to the automatic dispatch without promising the inbox.
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

        this.Flash(
            ModeCopy(
                "Si esa cuenta está pendiente de confirmar, el enlace nuevo quedó en cola para su envío automático; " +
                "no podemos garantizar que llegue a esa casilla.",
                "Si esa cuenta está pendiente de confirmar, el enlace nuevo queda en cola y todavía no se envía: el " +
                "envío automático de emails no está activo en esta versión."),
            FlashType.Info);
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
    /// account, so the request reveals nothing; the message is the same for an absent, a pending and an active one,
    /// and stays conditional in both email modes, which only change what the queued notice will really do.
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
            ModeCopy(
                "Si ese email tiene una cuenta, el enlace para restablecer tu contraseña quedó en cola para su envío " +
                "automático; no podemos garantizar que llegue a esa casilla. Vence en 30 minutos y sirve una sola vez.",
                "Si ese email tiene una cuenta, el enlace para restablecer tu contraseña queda en cola y todavía no " +
                "se envía: el envío automático de emails no está activo en esta versión. Vence en 30 minutos y sirve " +
                "una sola vez."),
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

    // --- Owner-assisted invitation acceptance (RF-26, T22) ------------------------------------------------------

    /// <summary>
    /// Acceptance screen of an invitation link. It is a pure read: opening the URL spends nothing, so a mail scanner
    /// that follows the link cannot burn the single use. The page shows only the destination email the link proves and
    /// the deadline -- never a ficha, a note or a history -- and the token travels back in a hidden field. Every
    /// response, including the one for an unusable link, is uncached and referrer-free.
    /// </summary>
    [HttpGet("invitacion/{token}")]
    public async Task<IActionResult> Invitation(string slug, string token, CancellationToken ct)
    {
        SetTokenPageHeaders();
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        if (invitationAcceptanceService is null)
            return NotFound();

        var inspection = await invitationAcceptanceService.InspectAsync(slug, token, ct);
        if (!inspection.Succeeded)
            return View("Status", ClientInvitationStatus.LinkUnavailable());

        return View("Invitation", new ClientInvitationViewModel
        {
            Token = token,
            Email = inspection.Value.Email,
            ExpiresAt = inspection.Value.ExpiresAt
        });
    }

    /// <summary>
    /// Spends the single-use invitation and either creates a new verified account bound to the invited ficha or
    /// verifies and binds a pre-existing pending account with its own password. The mode is taken from a server-side
    /// whitelist and the confirmation is judged here, before the link is read, so a malformed form or a wrong password
    /// leaves the link and every account exactly as they were. Success never starts a session: the person signs in
    /// afterwards with the password of the flow. Rejections are generic: an unusable link answers one page, and an
    /// account already linked to a ficha is sent to the business without revealing that ficha or its history.
    /// </summary>
    [HttpPost("invitacion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invitation(string slug, ClientInvitationViewModel model, CancellationToken ct)
    {
        SetTokenPageHeaders();
        if (await FindBusinessAsync(slug, ct) is not { } business)
            return NotFound();

        SetPublicLayout(business);
        if (invitationAcceptanceService is null)
            return NotFound();

        ValidateInvitationForm(model);
        if (!ModelState.IsValid)
            return await RedisplayInvitationAsync(slug, model, ct);

        var attempt = model.Mode == ClientInvitationModes.NewAccount
            ? await invitationAcceptanceService.AcceptNewAsync(slug, model.Token, model.Password, ct)
            : await invitationAcceptanceService.AcceptExistingAsync(slug, model.Token, model.Password, ct);

        if (!attempt.Succeeded)
        {
            if (attempt.Error!.Code == BusinessNotFoundCode)
                return NotFound();

            if (attempt.Error.Code == InvitationOwnerAssistanceCode)
                return View("Status", ClientInvitationStatus.OwnerAssistanceRequired());

            if (attempt.Error.Code == InvitationLinkUnavailableCode)
                return View("Status", ClientInvitationStatus.LinkUnavailable());

            // A wrong password and an email that already has an account go back to the form: the link and the
            // accounts are untouched, so a failed attempt spends nothing.
            ModelState.AddBusinessError(attempt.Error);
            return await RedisplayInvitationAsync(slug, model, ct);
        }

        this.Flash("Listo: vinculamos tu cuenta. Ingresá con tu email y contraseña.");
        return Redirect(AccountPath(slug, "ingresar"));
    }

    /// <summary>
    /// Judges the invitation form before the link is read. The mode is a whitelist, the password is always required,
    /// and the repetition is compared only in the new-account mode, because a pre-existing account proves its own
    /// password without a confirmation field and without the new-password minimum.
    /// </summary>
    private void ValidateInvitationForm(ClientInvitationViewModel model)
    {
        if (!ClientInvitationModes.IsKnown(model.Mode))
            ModelState.AddModelError(nameof(model.Mode), "Elegí cómo querés continuar.");

        if (string.IsNullOrEmpty(model.Password))
            ModelState.AddModelError(nameof(model.Password), "Ingresá tu contraseña.");
        else if (model.Mode == ClientInvitationModes.NewAccount)
        {
            if (model.Password.Length < ClientInvitationAcceptanceService.MinimumPasswordLength)
                ModelState.AddModelError(nameof(model.Password), "La contraseña debe tener al menos 8 caracteres.");
            else if (model.Password != model.ConfirmPassword)
                ModelState.AddModelError(nameof(model.ConfirmPassword), "Las contraseñas no coinciden.");
        }
    }

    /// <summary>
    /// Re-renders the acceptance form for a rejected form or a rule violation that did not consume the link. The
    /// destination and the deadline are refreshed from a read-only inspection, never from the request, so the page can
    /// only ever echo the email the token proves; when even that inspection fails, the generic page is shown.
    /// </summary>
    private async Task<IActionResult> RedisplayInvitationAsync(
        string slug, ClientInvitationViewModel model, CancellationToken ct)
    {
        var inspection = await invitationAcceptanceService!.InspectAsync(slug, model.Token, ct);
        if (!inspection.Succeeded)
            return View("Status", ClientInvitationStatus.LinkUnavailable());

        model.Email = inspection.Value.Email;
        model.ExpiresAt = inspection.Value.ExpiresAt;
        DiscardSubmittedPasswords(model);
        return View("Invitation", model);
    }

    /// <summary>
    /// A rejected attempt keeps its error messages, but the raw password never travels back into the re-rendered form
    /// or the validation state: the model fields are cleared and only the attempted values are dropped, so
    /// <c>asp-validation-for</c> still shows the rejection while no <c>&lt;input&gt;</c> can echo the secret.
    /// </summary>
    private void DiscardSubmittedPasswords(ClientInvitationViewModel model)
    {
        model.Password = "";
        model.ConfirmPassword = null;
        DiscardAttemptedValue(nameof(ClientInvitationViewModel.Password));
        DiscardAttemptedValue(nameof(ClientInvitationViewModel.ConfirmPassword));
    }

    /// <summary>Drops the raw value the request carried while keeping the entry's error messages untouched.</summary>
    private void DiscardAttemptedValue(string key)
    {
        if (ModelState.TryGetValue(key, out var entry))
        {
            entry.RawValue = null;
            entry.AttemptedValue = null;
        }
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
