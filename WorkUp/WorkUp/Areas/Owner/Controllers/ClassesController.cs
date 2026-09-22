using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Infrastructure;
using WorkUp.ViewModels.Classes;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>Grilla semanal de clases (RF-04). Reachable once the business has a class-type service (Settings shortcut and navigation hide it otherwise).</summary>
public class ClassesController(ClassesService classesService, CatalogService catalogService, StaffService staffService, IClock clock) : OwnerControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(DayOfWeek? day, CancellationToken ct)
    {
        var selectedDay = day ?? clock.Today.DayOfWeek;

        var gridClasses = await classesService.ListAsync(BusinessId, ct);
        var servicesById = (await catalogService.ListAsync(BusinessId, ct)).ToDictionary(s => s.Id);
        var workersById = (await staffService.ListAsync(BusinessId, ct)).ToDictionary(w => w.Id);

        // Defensive: a grid class always references an existing service and worker of the same business, but skip
        // silently instead of throwing if that ever drifts, rather than breaking the whole grid for one bad row.
        var rows = gridClasses
            .Where(g => servicesById.ContainsKey(g.ServiceId) && workersById.ContainsKey(g.WorkerId))
            .Select(g =>
            {
                var service = servicesById[g.ServiceId];
                var worker = workersById[g.WorkerId];
                return new GridClassRowViewModel(
                    g.Id, g.DayOfWeek, g.StartTime, g.StartTime.AddMinutes(service.DurationMinutes),
                    service.Name, worker.FullName, service.MaxCapacity, g.IsActive);
            })
            .ToList();

        var activeByDay = ClassesIndexViewModel.MondayFirst.ToDictionary(
            d => d,
            d => rows.Where(r => r.IsActive && r.DayOfWeek == d).OrderBy(r => r.StartTime).ToList());

        var archived = rows.Where(r => !r.IsActive).OrderBy(r => r.DayOfWeek).ThenBy(r => r.StartTime).ToList();

        return View(new ClassesIndexViewModel { SelectedDay = selectedDay, ActiveByDay = activeByDay, Archived = archived });
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        ViewData["Title"] = "Nueva clase";
        return View("GridClassForm", await BuildFormAsync(new GridClassFormViewModel(), ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(GridClassFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Nueva clase";
        if (!ModelState.IsValid)
            return View("GridClassForm", await BuildFormAsync(model, ct));

        var request = new CreateGridClassRequest(model.ServiceId, model.WorkerId, model.DayOfWeek!.Value, model.StartTime!.Value);
        var result = await classesService.CreateAsync(BusinessId, request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("GridClassForm", await BuildFormAsync(model, ct));
        }

        this.Flash("Clase agregada a la grilla.");
        return RedirectToAction(nameof(Index), new { day = result.Value.DayOfWeek });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var gridClass = await classesService.GetAsync(BusinessId, id, ct);
        if (gridClass is null)
            return NotFound();

        ViewData["Title"] = "Editar clase";
        ViewData["GridClassId"] = gridClass.Id;
        ViewData["HasActiveFixedAssignment"] = await classesService.HasActiveFixedAssignmentsAsync(BusinessId, id, ct);

        var model = new GridClassFormViewModel
        {
            ServiceId = gridClass.ServiceId,
            WorkerId = gridClass.WorkerId,
            DayOfWeek = gridClass.DayOfWeek,
            StartTime = gridClass.StartTime
        };
        return View("GridClassForm", await BuildFormAsync(model, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, GridClassFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Editar clase";
        ViewData["GridClassId"] = id;
        ViewData["HasActiveFixedAssignment"] = await classesService.HasActiveFixedAssignmentsAsync(BusinessId, id, ct);
        if (!ModelState.IsValid)
            return View("GridClassForm", await BuildFormAsync(model, ct));

        var request = new UpdateGridClassRequest(model.ServiceId, model.WorkerId, model.DayOfWeek!.Value, model.StartTime!.Value);
        var result = await classesService.UpdateAsync(BusinessId, id, request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddBusinessError(result.Error!);
            return View("GridClassForm", await BuildFormAsync(model, ct));
        }

        this.Flash("Cambios guardados.");
        return RedirectToAction(nameof(Index), new { day = model.DayOfWeek });
    }

    [HttpPost]
    public async Task<IActionResult> Archive(int id, CancellationToken ct)
    {
        var result = await classesService.SetActiveAsync(BusinessId, id, isActive: false, ct);
        this.Flash(result.Succeeded ? "Clase archivada." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Reactivate(int id, CancellationToken ct)
    {
        var result = await classesService.SetActiveAsync(BusinessId, id, isActive: true, ct);
        this.Flash(result.Succeeded ? "Clase reactivada." : result.Error!.Message, result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    private async Task<GridClassFormViewModel> BuildFormAsync(GridClassFormViewModel model, CancellationToken ct)
    {
        model.ServiceOptions = (await classesService.ListEligibleServicesAsync(BusinessId, ct))
            .Select(s => new ClassTypeOptionViewModel(s.Id, s.Name))
            .ToList();
        model.WorkerOptions = (await classesService.ListEligibleWorkersAsync(BusinessId, ct))
            .Select(w => new TeacherOptionViewModel(w.Id, w.FullName))
            .ToList();
        return model;
    }
}
