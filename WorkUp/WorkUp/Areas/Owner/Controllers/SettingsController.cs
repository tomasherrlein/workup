using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.ViewModels.Businesses;
using WorkUp.ViewModels.Catalog;
using WorkUp.ViewModels.Staff;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>Negocio y avisos, tipos de gasto, tarifario, trabajadoras (RF-01, RF-02, RF-03, RF-13). More sub-sections join this index as later tasks add them.</summary>
public class SettingsController(
    BusinessSettingsService businessSettingsService,
    ExpenseTypeService expenseTypeService,
    CatalogService catalogService,
    MembershipPriceService membershipPriceService,
    StaffService staffService) : OwnerControllerBase
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

    [HttpGet]
    public async Task<IActionResult> Services(CancellationToken ct)
    {
        var services = await catalogService.ListAsync(BusinessId, ct);
        var rows = services
            .Select(s => new ServiceRowViewModel(s.Id, s.Name, s.Price, s.DurationMinutes, s.MaxCapacity, s.IsActive))
            .ToList();

        var hasGroupServices = await catalogService.HasGroupServicesAsync(BusinessId, ct);
        var membershipPrices = hasGroupServices
            ? (await membershipPriceService.ListAsync(BusinessId, ct))
                .Select(p => new MembershipPriceRowViewModel(p.WeeklyFrequency, p.Price))
                .ToList()
            : [];

        var model = new ServicesPageViewModel
        {
            Individual = rows.Where(r => r.MaxCapacity == 1).OrderByDescending(r => r.IsActive).ThenBy(r => r.Name, StringComparer.Ordinal).ToList(),
            Classes = rows.Where(r => r.MaxCapacity > 1).OrderByDescending(r => r.IsActive).ThenBy(r => r.Name, StringComparer.Ordinal).ToList(),
            HasGroupServices = hasGroupServices,
            MembershipPrices = membershipPrices,
            AvailableFrequencies = Enumerable.Range(1, 7).Except(membershipPrices.Select(p => p.WeeklyFrequency)).ToList()
        };
        return View(model);
    }

    [HttpGet]
    public IActionResult CreateService()
    {
        ViewData["Title"] = "Nuevo servicio";
        return View("ServiceForm", new ServiceFormViewModel());
    }

    [HttpPost]
    public async Task<IActionResult> CreateService(ServiceFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Nuevo servicio";
        if (!ModelState.IsValid)
            return View("ServiceForm", model);

        var request = new CreateServiceRequest(model.Name, model.Price, model.DurationMinutes, model.MaxCapacity);
        var result = await catalogService.CreateAsync(BusinessId, request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("ServiceForm", model);
        }

        this.Flash("Servicio agregado.");
        return RedirectToAction(nameof(Services));
    }

    [HttpGet]
    public async Task<IActionResult> EditService(int id, CancellationToken ct)
    {
        var service = await catalogService.GetServiceAsync(BusinessId, id, ct);
        if (service is null)
            return NotFound();

        ViewData["Title"] = "Editar servicio";
        ViewData["ServiceId"] = service.Id;
        return View("ServiceForm", new ServiceFormViewModel
        {
            Name = service.Name,
            Price = service.Price,
            DurationMinutes = service.DurationMinutes,
            MaxCapacity = service.MaxCapacity
        });
    }

    [HttpPost]
    public async Task<IActionResult> EditService(int id, ServiceFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Editar servicio";
        ViewData["ServiceId"] = id;
        if (!ModelState.IsValid)
            return View("ServiceForm", model);

        var request = new UpdateServiceRequest(model.Name, model.Price, model.DurationMinutes, model.MaxCapacity);
        var result = await catalogService.UpdateAsync(BusinessId, id, request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("ServiceForm", model);
        }

        this.Flash("Cambios guardados.");
        return RedirectToAction(nameof(Services));
    }

    [HttpPost]
    public async Task<IActionResult> ArchiveService(int id, CancellationToken ct)
    {
        var result = await catalogService.SetActiveAsync(BusinessId, id, isActive: false, ct);
        this.Flash(result.Succeeded ? "Servicio archivado." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Services));
    }

    [HttpPost]
    public async Task<IActionResult> ReactivateService(int id, CancellationToken ct)
    {
        var result = await catalogService.SetActiveAsync(BusinessId, id, isActive: true, ct);
        this.Flash(result.Succeeded ? "Servicio reactivado." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Services));
    }

    [HttpPost]
    public async Task<IActionResult> SetMembershipPrice(int weeklyFrequency, decimal price, CancellationToken ct)
    {
        var result = await membershipPriceService.SetPriceAsync(BusinessId, weeklyFrequency, price, ct);
        this.Flash(result.Succeeded ? "Precio de abono guardado." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Services));
    }

    [HttpPost]
    public async Task<IActionResult> RemoveMembershipPrice(int weeklyFrequency, CancellationToken ct)
    {
        var result = await membershipPriceService.RemoveAsync(BusinessId, weeklyFrequency, ct);
        this.Flash(result.Succeeded ? "Precio de abono quitado." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Services));
    }

    [HttpGet]
    public async Task<IActionResult> Workers(CancellationToken ct)
    {
        var workerList = await staffService.ListAsync(BusinessId, ct);
        var rows = workerList
            .OrderByDescending(w => w.IsActive)
            .ThenBy(w => w.LastName, StringComparer.Ordinal)
            .ThenBy(w => w.FirstName, StringComparer.Ordinal)
            .Select(w => new WorkerRowViewModel(w.Id, w.FullName, Formatting.MaskPhone(w.Phone), BuildScheduleSummary(w.Schedules), w.IsActive))
            .ToList();
        return View(rows);
    }

    [HttpGet]
    public IActionResult CreateWorker()
    {
        ViewData["Title"] = "Nueva trabajadora";
        return View("WorkerForm", new WorkerFormViewModel());
    }

    [HttpPost]
    public async Task<IActionResult> CreateWorker(WorkerFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Nueva trabajadora";
        if (!ModelState.IsValid)
            return View("WorkerForm", model);

        var request = new CreateWorkerRequest(model.FirstName, model.LastName, model.Phone, model.Cuil, ToRequestDays(model.Days));
        var result = await staffService.CreateAsync(BusinessId, request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("WorkerForm", model);
        }

        this.Flash("Trabajadora agregada.");
        return RedirectToAction(nameof(Workers));
    }

    [HttpGet]
    public async Task<IActionResult> EditWorker(int id, CancellationToken ct)
    {
        var worker = await staffService.GetWorkerAsync(BusinessId, id, ct);
        if (worker is null)
            return NotFound();

        ViewData["Title"] = "Editar trabajadora";
        ViewData["WorkerId"] = worker.Id;
        return View("WorkerForm", ToFormViewModel(worker));
    }

    [HttpPost]
    public async Task<IActionResult> EditWorker(int id, WorkerFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Editar trabajadora";
        ViewData["WorkerId"] = id;
        if (!ModelState.IsValid)
            return View("WorkerForm", model);

        var request = new UpdateWorkerRequest(model.FirstName, model.LastName, model.Phone, model.Cuil, ToRequestDays(model.Days));
        var result = await staffService.UpdateAsync(BusinessId, id, request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("WorkerForm", model);
        }

        this.Flash("Cambios guardados.");
        return RedirectToAction(nameof(Workers));
    }

    [HttpPost]
    public async Task<IActionResult> ArchiveWorker(int id, CancellationToken ct)
    {
        var result = await staffService.SetActiveAsync(BusinessId, id, isActive: false, ct);
        this.Flash(result.Succeeded ? "Trabajadora archivada." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Workers));
    }

    [HttpPost]
    public async Task<IActionResult> ReactivateWorker(int id, CancellationToken ct)
    {
        var result = await staffService.SetActiveAsync(BusinessId, id, isActive: true, ct);
        this.Flash(result.Succeeded ? "Trabajadora reactivada." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Workers));
    }

    private static string BuildScheduleSummary(List<WorkerSchedule> schedules)
    {
        if (schedules.Count == 0)
            return "Sin horario cargado";

        return string.Join(" · ", schedules
            .OrderBy(s => ((int)s.DayOfWeek + 6) % 7) // Monday-first order; DayOfWeek itself starts on Sunday.
            .Select(s => Formatting.ShortDayName(s.DayOfWeek)));
    }

    private static List<DayScheduleInput> ToRequestDays(List<DayScheduleFormViewModel> days) =>
        days.Select(d => new DayScheduleInput(
                d.DayOfWeek,
                d.WorksThisDay,
                d.StartTime,
                d.EndTime,
                d.Breaks.Select(b => new BreakInput(b.StartTime, b.EndTime)).ToList()))
            .ToList();

    private static WorkerFormViewModel ToFormViewModel(Worker worker)
    {
        var schedulesByDay = worker.Schedules.ToDictionary(s => s.DayOfWeek);
        var breaksByDay = worker.Breaks.ToLookup(b => b.DayOfWeek);

        var model = new WorkerFormViewModel
        {
            FirstName = worker.FirstName,
            LastName = worker.LastName,
            Phone = worker.Phone,
            Cuil = worker.Cuil
        };

        foreach (var day in model.Days)
        {
            if (schedulesByDay.TryGetValue(day.DayOfWeek, out var schedule))
            {
                day.WorksThisDay = true;
                day.StartTime = schedule.StartTime;
                day.EndTime = schedule.EndTime;
            }

            var dayBreaks = breaksByDay[day.DayOfWeek].OrderBy(b => b.StartTime).ToList();
            for (var i = 0; i < day.Breaks.Count && i < dayBreaks.Count; i++)
            {
                day.Breaks[i].StartTime = dayBreaks[i].StartTime;
                day.Breaks[i].EndTime = dayBreaks[i].EndTime;
            }
        }

        return model;
    }
}
