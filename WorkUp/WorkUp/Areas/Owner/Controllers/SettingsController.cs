using Microsoft.AspNetCore.Mvc;
using WorkUp.Business.Businesses;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.ViewModels.Businesses;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>Negocio y avisos, tipos de gasto (RF-01, RF-13). More sub-sections join this index as later tasks add them.</summary>
public class SettingsController(BusinessSettingsService businessSettingsService, ExpenseTypeService expenseTypeService) : OwnerControllerBase
{
    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> Business(CancellationToken ct)
    {
        var business = await businessSettingsService.GetAsync(BusinessId, ct);
        return View(new BusinessSettingsViewModel
        {
            Name = business.Name,
            Slug = business.Slug,
            ContactPhone = business.ContactPhone,
            NotificationEmail = business.NotificationEmail,
            AllowsWalkIns = business.AllowsWalkIns,
            ReminderLeadHours = business.ReminderLeadHours,
            DailySummaryTime = business.DailySummaryTime,
            StockReviewEveryDays = business.StockReviewEveryDays
        });
    }

    [HttpPost]
    public async Task<IActionResult> Business(BusinessSettingsViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(model);

        var request = new UpdateBusinessSettingsRequest(
            model.Name, model.Slug, model.ContactPhone, model.NotificationEmail,
            model.AllowsWalkIns, model.ReminderLeadHours, model.DailySummaryTime, model.StockReviewEveryDays);

        var result = await businessSettingsService.UpdateAsync(BusinessId, request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View(model);
        }

        // The Owner cookie caches the business name and slug (for the layout); refresh it after they change.
        var session = new OwnerSession(result.Value.Id, result.Value.Name, result.Value.Slug, User.GetOwnerAccountId(), OwnerUsername);
        await OwnerSignIn.SignInAsync(HttpContext, session);

        this.Flash("Cambios guardados.");
        return RedirectToAction(nameof(Business));
    }

    [HttpGet]
    public async Task<IActionResult> ExpenseTypes(CancellationToken ct)
    {
        var items = await expenseTypeService.ListAsync(BusinessId, ct);
        return View(items.Select(e => new ExpenseTypeRowViewModel(e.Id, e.Name, e.IsActive)).ToList());
    }

    [HttpPost]
    public async Task<IActionResult> CreateExpenseType(string name, CancellationToken ct)
    {
        var result = await expenseTypeService.CreateAsync(BusinessId, name, ct);
        this.Flash(result.Succeeded ? "Tipo de gasto agregado." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(ExpenseTypes));
    }

    [HttpPost]
    public async Task<IActionResult> RenameExpenseType(int id, string name, CancellationToken ct)
    {
        var result = await expenseTypeService.RenameAsync(BusinessId, id, name, ct);
        this.Flash(result.Succeeded ? "Nombre actualizado." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(ExpenseTypes));
    }

    [HttpPost]
    public async Task<IActionResult> ArchiveExpenseType(int id, CancellationToken ct)
    {
        var result = await expenseTypeService.SetActiveAsync(BusinessId, id, isActive: false, ct);
        this.Flash(result.Succeeded ? "Tipo de gasto archivado." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(ExpenseTypes));
    }

    [HttpPost]
    public async Task<IActionResult> ReactivateExpenseType(int id, CancellationToken ct)
    {
        var result = await expenseTypeService.SetActiveAsync(BusinessId, id, isActive: true, ct);
        this.Flash(result.Succeeded ? "Tipo de gasto reactivado." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(ExpenseTypes));
    }
}
