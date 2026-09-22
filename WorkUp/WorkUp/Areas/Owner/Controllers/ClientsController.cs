using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Clients;
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

/// <summary>Owner-only client fichas. Health observations and notes never leave this controller's owner area.</summary>
public sealed class ClientsController(ClientsService clientsService) : OwnerControllerBase
{
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
}
