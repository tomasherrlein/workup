using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Expenses;
using WorkUp.Infrastructure;

namespace WorkUp.Areas.Owner.Controllers;

public sealed class ExpensesIndexViewModel
{
    [Display(Name = "Desde")]
    public DateOnly? From { get; init; }

    [Display(Name = "Hasta")]
    public DateOnly? To { get; init; }

    public List<ExpenseListItem> Expenses { get; init; } = [];
}

public sealed class ExpenseFormViewModel
{
    [Display(Name = "Tipo de gasto")]
    [Range(1, int.MaxValue, ErrorMessage = "Elegí un tipo de gasto.")]
    public int ExpenseTypeId { get; set; }

    [Display(Name = "Fecha")]
    [Required(ErrorMessage = "Ingresá la fecha.")]
    public DateOnly? SpentOn { get; set; }

    [Display(Name = "Monto")]
    [Required(ErrorMessage = "Ingresá el monto.")]
    public string Amount { get; set; } = "";

    [Display(Name = "Descripción")]
    [Required(ErrorMessage = "Ingresá una descripción.")]
    [StringLength(300, ErrorMessage = "La descripción no puede superar los 300 caracteres.")]
    public string Description { get; set; } = "";

    public List<WorkUp.Domain.Entities.ExpenseType> ExpenseTypes { get; set; } = [];
}

/// <summary>Owner-only expense registration and period history (RF-13).</summary>
public sealed class ExpensesController(ExpenseService expenseService, IClock clock) : OwnerControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        return View(new ExpensesIndexViewModel
        {
            From = from,
            To = to,
            Expenses = await expenseService.ListAsync(BusinessId, from, to, ct)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        ViewData["Title"] = "Registrar gasto";
        return View(await BuildFormAsync(new ExpenseFormViewModel { SpentOn = clock.Today }, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(ExpenseFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Registrar gasto";
        if (!decimal.TryParse(model.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            ModelState.AddModelError(nameof(model.Amount), "El monto debe ser mayor a cero.");
        if (!ModelState.IsValid)
            return View(await BuildFormAsync(model, ct));

        var result = await expenseService.CreateAsync(BusinessId,
            new CreateExpenseRequest(model.ExpenseTypeId, model.SpentOn!.Value, amount, model.Description), ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View(await BuildFormAsync(model, ct));
        }

        this.Flash("Gasto registrado.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<ExpenseFormViewModel> BuildFormAsync(ExpenseFormViewModel model, CancellationToken ct)
    {
        model.ExpenseTypes = await expenseService.ListAvailableTypesAsync(BusinessId, ct);
        return model;
    }
}
