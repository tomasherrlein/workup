using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Infrastructure;

namespace WorkUp.Areas.Owner.Controllers;

public sealed class ClientsIndexViewModel
{
    public string? Query { get; init; }
    public List<WorkUp.Domain.Entities.Client> Clients { get; init; } = [];
}

public sealed class ClientFormViewModel
{
    [Display(Name = "Nombre")]
    [Required(ErrorMessage = "Ingresá el nombre.")]
    public string FirstName { get; set; } = "";

    [Display(Name = "Apellido")]
    [Required(ErrorMessage = "Ingresá el apellido.")]
    public string LastName { get; set; } = "";

    [Display(Name = "Email")]
    [Required(ErrorMessage = "Ingresá el email.")]
    public string Email { get; set; } = "";

    [Display(Name = "Teléfono")]
    [Required(ErrorMessage = "Ingresá el teléfono.")]
    public string Phone { get; set; } = "";

    [Display(Name = "Observaciones de salud")]
    [StringLength(1000, ErrorMessage = "Las observaciones de salud no pueden superar los 1000 caracteres.")]
    public string? HealthNotes { get; set; }
}

/// <summary>
/// Destination of an owner-assisted invitation (RF-26, T22). The email is prefilled with the one stored on the ficha
/// but stays editable, because the owner is the one who confirms it with the person; <see cref="EmailConfirmed"/> is
/// the explicit confirmation the form cannot skip.
/// </summary>
public sealed class ClientInvitationFormViewModel
{
    [Display(Name = "Email de destino")]
    [Required(ErrorMessage = "Ingresá el email de destino.")]
    public string Email { get; set; } = "";

    [Display(Name = "Confirmé este email con la persona")]
    public bool EmailConfirmed { get; set; }
}

/// <summary>Owner-only client fichas. Health observations and notes never leave this controller's owner area.</summary>
public sealed class ClientsController(
    ClientsService clientsService,
    ClientInvitationService? invitationService = null,
    IOptions<WorkUpOptions>? options = null) : OwnerControllerBase
{
    /// <summary>
    /// The configured email mode (<c>WorkUp:Email:Mode</c>), parsed once with the same fail-fast parser the startup
    /// uses. The invitation flash depends on it: in <see cref="OutboxMode.Retain"/> the notice stays <c>Pending</c>
    /// and no email is sent, and in <see cref="OutboxMode.Deliver"/> it is only handed to the scheduled dispatch. A
    /// missing options dependency is read as the retained default, and an unusable value throws at construction.
    /// </summary>
    private readonly OutboxMode _emailMode = OutboxModeParser.Parse(options?.Value?.Email?.Mode);

    /// <summary>
    /// What the panel may state about an invitation it just queued. In Outbox nothing is sent and the notice stays
    /// <c>Pending</c>; in Smtp it is only handed to the scheduled dispatch, so the message never promises the inbox
    /// of the destination. It carries neither the raw token, which exists only inside the queued email, nor the
    /// stored hash.
    /// </summary>
    private string QueuedInvitationMessage(string email, DateTime expiresAt) =>
        _emailMode == OutboxMode.Deliver
            ? $"Invitación encolada para {email}. El correo quedó en cola para su envío automático; no podemos " +
              $"garantizar que llegue a esa casilla. El enlace vence el {Formatting.ShortDate(expiresAt)} a las " +
              $"{Formatting.Time(expiresAt)}."
            : $"Invitación encolada para {email}. El correo todavía no se envía: el envío automático de emails no " +
              $"está activo en esta versión. El enlace vence el {Formatting.ShortDate(expiresAt)} a las " +
              $"{Formatting.Time(expiresAt)}.";

    [HttpGet]
    public async Task<IActionResult> Index(string? query, CancellationToken ct)
    {
        return View(new ClientsIndexViewModel { Query = query, Clients = await clientsService.SearchAsync(BusinessId, query, ct) });
    }

    [HttpGet]
    public IActionResult Create()
    {
        ViewData["Title"] = "Nueva ficha";
        return View("Form", new ClientFormViewModel());
    }

    [HttpPost]
    public async Task<IActionResult> Create(ClientFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Nueva ficha";
        if (!ModelState.IsValid)
            return View("Form", model);

        var result = await clientsService.CreateAsync(BusinessId, new CreateClientRequest(model.FirstName, model.LastName, model.Email, model.Phone, model.HealthNotes), ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("Form", model);
        }

        this.Flash("Ficha creada.");
        return RedirectToAction(nameof(Detail), new { id = result.Value.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var client = await clientsService.GetAsync(BusinessId, id, ct);
        if (client is null)
            return NotFound();

        ViewData["Title"] = "Editar ficha";
        ViewData["ClientId"] = id;
        return View("Form", new ClientFormViewModel
        {
            FirstName = client.FirstName,
            LastName = client.LastName,
            Email = client.Email,
            Phone = client.Phone ?? "",
            HealthNotes = client.HealthNotes
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, ClientFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Editar ficha";
        ViewData["ClientId"] = id;
        if (!ModelState.IsValid)
            return View("Form", model);

        var result = await clientsService.UpdateAsync(BusinessId, id, new UpdateClientRequest(model.FirstName, model.LastName, model.Email, model.Phone, model.HealthNotes), ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("Form", model);
        }

        this.Flash("Cambios guardados.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var detail = await clientsService.GetDetailAsync(BusinessId, id, ct);
        return detail is null ? NotFound() : View(detail);
    }

    [HttpPost]
    public async Task<IActionResult> AddNote(int id, string text, CancellationToken ct)
    {
        var result = await clientsService.AddNoteAsync(BusinessId, id, text, ct: ct);
        this.Flash(result.Succeeded ? "Observación agregada." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>
    /// States that a missing or foreign ficha is never disclosed: the invitation service answers both with the same
    /// code, and the panel turns that code into the same generic 404 instead of a message that would confirm existence.
    /// </summary>
    private const string UnavailableCode = "client_invitation_unavailable";

    /// <summary>
    /// The message of an unconfirmed or malformed destination. It is judged before the service starts a transaction, so
    /// an unconfirmed email never reaches storage.
    /// </summary>
    private const string InvitationFormMessage = "Revisá el email de destino y confirmá que lo verificaste con la persona.";

    private const string InvitationRevokedMessage = "Invitación revocada.";

    /// <summary>
    /// Queues the 48 h assisted invitation of a real ficha of this business (RF-26) and redirects to the ficha
    /// (POST/redirect/GET). The owner confirms the destination email with the person, so that editable value — not the
    /// one stored on the ficha — is the email the invitation proves and the account will use. The confirmation checkbox
    /// is mandatory and judged here, before the service opens its transaction; the tenant always comes from the owner
    /// cookie, never from the form. The flash reports the notice as queued and says in words what the configured email
    /// mode really does with it: Outbox sends nothing yet, and Smtp only hands it to the automatic dispatch without
    /// promising the inbox. It carries neither the raw token, which exists only inside the queued email, nor the stored
    /// hash.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(int id, ClientInvitationFormViewModel input, CancellationToken ct = default)
    {
        if (invitationService is null)
            return NotFound();

        if (!input.EmailConfirmed)
            ModelState.AddModelError(nameof(input.EmailConfirmed), InvitationFormMessage);

        if (!ModelState.IsValid)
        {
            this.Flash(InvitationFormMessage, FlashType.Error);
            return RedirectToAction(nameof(Detail), new { id });
        }

        var result = await invitationService.IssueAsync(BusinessId, id, input.Email, ct);
        if (result.Error?.Code == UnavailableCode)
            return NotFound();

        var message = result.Succeeded
            ? QueuedInvitationMessage(result.Value.Email, result.Value.ExpiresAt)
            : result.Error!.Message;
        this.Flash(message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>
    /// Revokes every live invitation of a ficha of this business (RF-26) and redirects to the ficha
    /// (POST/redirect/GET). It is idempotent: repeating it reports success and changes nothing new, which is what the
    /// panel needs because it offers the action while the ficha has no account, even with no outstanding invitation.
    /// The action is owner-only and tenant-scoped: a missing or foreign ficha answers the same generic 404.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeInvitation(int id, CancellationToken ct = default)
    {
        if (invitationService is null)
            return NotFound();

        var result = await invitationService.RevokeAsync(BusinessId, id, ct);
        if (result.Error?.Code == UnavailableCode)
            return NotFound();

        this.Flash(result.Succeeded ? InvitationRevokedMessage : result.Error!.Message,
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Detail), new { id });
    }
}
