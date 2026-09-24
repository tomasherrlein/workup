using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Rules;
using WorkUp.Infrastructure;
using WorkUp.ViewModels.Classes;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>Grilla semanal de clases (RF-04) y generación de la semana (RF-06). Reachable once the business has a class-type service (Settings shortcut and navigation hide it otherwise).</summary>
public class ClassesController(ClassesService classesService, CatalogService catalogService, StaffService staffService, WeekGenerationService weekGenerationService, IClock clock) : OwnerControllerBase
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

    /// <summary>
    /// Read-only preview of the Monday-to-Sunday week that contains <paramref name="date"/>, or the next week when it
    /// is omitted (RF-06). GET never writes: it only shows what a confirm would generate.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Week(DateOnly? date, CancellationToken ct)
    {
        ViewData["Title"] = "Generar semana";

        // A malformed date binds to null, which would silently confirm the default week. Reject it before the
        // service is reached so the owner corrects the value instead of writing a week they never picked.
        if (!ModelState.IsValid)
        {
            this.Flash("Elegí una fecha válida.", FlashType.Error);
            return RedirectToAction(nameof(Week));
        }

        var result = await weekGenerationService.PreviewAsync(BusinessId, date, ct);
        if (!result.Succeeded)
        {
            this.Flash(result.Error!.Message, FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        var preview = result.Value;
        // Same name resolution as Index: the preview carries ids, the owner reads names.
        var servicesById = (await catalogService.ListAsync(BusinessId, ct)).ToDictionary(s => s.Id);
        var workersById = (await staffService.ListAsync(BusinessId, ct)).ToDictionary(w => w.Id);

        var sessions = preview.Sessions
            .Select(session => new WeekPreviewSessionViewModel(
                session.Date,
                session.StartTime,
                servicesById.TryGetValue(session.ServiceId, out var service) ? service.Name : string.Empty,
                workersById.TryGetValue(session.WorkerId, out var worker) ? worker.FullName : string.Empty,
                session.IsNew,
                session.Suspended,
                session.FixedStudents
                    .Select(student => new WeekPreviewFixedStudentViewModel(
                        student.EnrollmentId, student.ClientName, student.State, student.AlreadyBooked, student.Cancelled))
                    .ToList()))
            .ToList();

        var today = clock.Today;
        return View(new WeekPreviewViewModel
        {
            SelectedDate = date,
            WeekStart = preview.WeekStart,
            WeekEnd = preview.WeekEnd,
            Sessions = sessions,
            CurrentWeekStart = WeekCalendar.TryGetWeek(today, out var currentWeekStart, out _) ? currentWeekStart : null,
            NextWeekStart = WeekCalendar.NextMonday(today),
            // Same single clock read as the quick links: the service rejects a week that already ended, so the page
            // must not offer a confirm that can only fail.
            IsPastWeek = preview.WeekEnd < today
        });
    }

    /// <summary>
    /// Confirms the week that contains <paramref name="date"/>, then redirects back to its preview (POST/redirect/GET)
    /// so the owner immediately sees the regenerated week instead of a stale page.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ConfirmWeek(DateOnly? date, CancellationToken ct)
    {
        // The POST must never write on a malformed date: without this, `?date=abc` binds to null and the default
        // next week is confirmed behind a success flash the owner never asked for.
        if (!ModelState.IsValid)
        {
            this.Flash("Elegí una fecha válida.", FlashType.Error);
            return RedirectToAction(nameof(Week));
        }

        var result = await weekGenerationService.ConfirmAsync(BusinessId, date, ct);
        this.Flash(result.Succeeded ? ConfirmationMessage(result.Value) : result.Error!.Message,
            result.Succeeded ? FlashType.Success : FlashType.Error);
        // A raw DateOnly is not IConvertible: it would stringify under the ambient culture, unlike the invariant
        // links the view already emits. Format it the same way so the redirect always echoes the picked date.
        return RedirectToAction(nameof(Week), new { date = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
    }

    private static string ConfirmationMessage(WeekGenerationConfirmation confirmation)
    {
        var createdSessions = confirmation.CreatedSessions == 1
            ? "1 clase nueva"
            : $"{confirmation.CreatedSessions} clases nuevas";
        var createdAnnotations = confirmation.CreatedFixedBookings == 1
            ? "1 anotación fija nueva"
            : $"{confirmation.CreatedFixedBookings} anotaciones fijas nuevas";
        var existingSessions = confirmation.ExistingSessions switch
        {
            0 => "No había clases ya generadas.",
            1 => "1 clase ya existía.",
            _ => $"{confirmation.ExistingSessions} clases ya existían."
        };

        return $"Semana generada: {createdSessions} y {createdAnnotations}. {existingSessions}";
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
