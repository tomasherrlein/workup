using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Supplies;
using WorkUp.Infrastructure;

namespace WorkUp.Areas.Owner.Controllers;

public sealed class SuppliesIndexViewModel
{
    public List<WorkUp.Domain.Entities.Supply> Supplies { get; init; } = [];
}

public sealed class SupplyFormViewModel
{
    [Display(Name = "Insumo")]
    [Required(ErrorMessage = "Ingresá un nombre para el insumo.")]
    [StringLength(100, ErrorMessage = "El nombre no puede superar los 100 caracteres.")]
    public string Name { get; set; } = "";

    [Display(Name = "Existencia declarada")]
    [Required(ErrorMessage = "Ingresá la existencia declarada.")]
    public string CurrentStock { get; set; } = "";

    [Display(Name = "Existencia mínima")]
    [Required(ErrorMessage = "Ingresá la existencia mínima.")]
    public string MinimumStock { get; set; } = "";
}

/// <summary>Owner-only declared supply stock (RF-14). Editing is the only stock-changing workflow.</summary>
public sealed class SuppliesController(SupplyService supplyService) : OwnerControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new SuppliesIndexViewModel { Supplies = await supplyService.ListAsync(BusinessId, ct) });

    [HttpGet]
    public IActionResult Create()
    {
        ViewData["Title"] = "Nuevo insumo";
        return View("Form", new SupplyFormViewModel());
    }

    [HttpPost]
    public async Task<IActionResult> Create(SupplyFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Nuevo insumo";
        if (!ModelState.IsValid)
            return View("Form", model);

        if (!TryReadStock(model, out var currentStock, out var minimumStock))
            return View("Form", model);

        var result = await supplyService.CreateAsync(BusinessId, new DeclareSupplyRequest(model.Name, currentStock, minimumStock), ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("Form", model);
        }

        this.Flash("Insumo declarado.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var supply = await supplyService.GetAsync(BusinessId, id, ct);
        if (supply is null)
            return NotFound();

        ViewData["Title"] = "Declarar existencia";
        ViewData["SupplyId"] = id;
        return View("Form", new SupplyFormViewModel
        {
            Name = supply.Name,
            CurrentStock = supply.CurrentStock.ToString(CultureInfo.InvariantCulture),
            MinimumStock = supply.MinimumStock.ToString(CultureInfo.InvariantCulture)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, SupplyFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Declarar existencia";
        ViewData["SupplyId"] = id;
        if (!ModelState.IsValid)
            return View("Form", model);

        if (!TryReadStock(model, out var currentStock, out var minimumStock))
            return View("Form", model);

        var result = await supplyService.UpdateAsync(BusinessId, id, new DeclareSupplyRequest(model.Name, currentStock, minimumStock), ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("Form", model);
        }

        this.Flash("Existencia declarada.");
        return RedirectToAction(nameof(Index));
    }

    private bool TryReadStock(SupplyFormViewModel model, out decimal currentStock, out decimal minimumStock)
    {
        var isCurrentStockValid = decimal.TryParse(model.CurrentStock, NumberStyles.Number, CultureInfo.InvariantCulture, out currentStock) && currentStock >= 0;
        var isMinimumStockValid = decimal.TryParse(model.MinimumStock, NumberStyles.Number, CultureInfo.InvariantCulture, out minimumStock) && minimumStock >= 0;
        if (!isCurrentStockValid)
            ModelState.AddModelError(nameof(model.CurrentStock), "La existencia no puede ser negativa.");
        if (!isMinimumStockValid)
            ModelState.AddModelError(nameof(model.MinimumStock), "El mínimo no puede ser negativo.");
        return isCurrentStockValid && isMinimumStockValid;
    }
}
