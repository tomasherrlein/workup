using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;
using WorkUp.Infrastructure;
using WorkUp.ViewModels.Classes;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>
/// Grilla semanal de clases (RF-04), generación de la semana (RF-06) y clases sueltas (RF-22, RF-07 cupo &gt; 1).
/// Reachable once the business has a class-type service (Settings shortcut and navigation hide it otherwise).
/// <para>
/// The drop-in page needs two more collaborators than the grid. They are optional only so a caller that never renders
/// that page (the grid and week tests, for instance) can keep the shorter constructor; the container always supplies
/// both, because RF-22 is part of the panel and its services are registered.
/// </para>
/// </summary>
public class ClassesController(
    ClassesService classesService,
    CatalogService catalogService,
    StaffService staffService,
    WeekGenerationService weekGenerationService,
    IClock clock,
    ClassDropInService? dropInsService = null,
    ClientsService? clientsService = null) : OwnerControllerBase
{
    /// <summary>
    /// Forward window of the owner roster (RF-22): today plus the next 30 days, the same window the public offer
    /// uses. The business service already dropped the sessions that started; this bound only keeps the screen finite.
    /// </summary>
    private const int DropInWindowDays = 30;

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

    /// <summary>
    /// Owner roster of the sueltas (RF-22, RF-07 cupo &gt; 1): the classes of the forward window with their drop-in
    /// annotations, grouped by day in the view. The business service owns the rules — it drops the sessions that
    /// already started, keeps the suspended ones because suspending a class cancels nobody, and projects only the
    /// <c>Suelta</c> annotations — so this action only bounds the window, resolves names and decides which courtesy
    /// actions the screen offers. It never computes capacity. <paramref name="session"/> opens that session's booking
    /// form and is ignored unless it names a session the screen is actually offering.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DropIns(int? session, CancellationToken ct)
    {
        ViewData["Title"] = "Clases sueltas";

        var result = await dropInsService!.ListRosterAsync(BusinessId, ct);
        if (!result.Succeeded)
        {
            this.Flash(result.Error!.Message, FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        // One clock read for the whole page: the window and the "already started" judgement of every session derive
        // from the same instant.
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);
        var windowEnd = today.AddDays(DropInWindowDays);

        // Same name resolution as Index and Week: the roster carries ids, the owner reads names.
        var servicesById = (await catalogService.ListAsync(BusinessId, ct)).ToDictionary(s => s.Id);
        var workersById = (await staffService.ListAsync(BusinessId, ct)).ToDictionary(w => w.Id);

        var sessions = result.Value
            .Where(row => row.Date >= today && row.Date <= windowEnd)
            .Select(row =>
            {
                var suspended = row.Status == AppointmentStatus.Suspended;
                // A suspended class is not bookable (the service rejects it) and neither is one that already started.
                // The service already dropped the started ones; re-judging the start here keeps the screen honest
                // about the action it offers even if the roster ever widens.
                var cancellable = !suspended && row.StartsAt > now;
                return new ClassDropInSessionViewModel(
                    row.AppointmentId,
                    row.Date,
                    row.StartTime,
                    servicesById.TryGetValue(row.ServiceId, out var service) ? service.Name : string.Empty,
                    workersById.TryGetValue(row.WorkerId, out var worker) ? worker.FullName : string.Empty,
                    suspended,
                    !suspended,
                    row.DropIns
                        .Select(entry => new ClassDropInAnnotationViewModel(
                            entry.BookingId,
                            entry.ClientName,
                            entry.Status,
                            cancellable && entry.Status != BookingStatus.Cancelled))
                        .ToList());
            })
            .ToList();

        var clients = (await clientsService!.SearchAsync(BusinessId, null, ct))
            .Select(client => new ClassDropInClientOptionViewModel(client.Id, client.FullName))
            .ToList();

        return View(new DropInOwnerIndexViewModel
        {
            Today = today,
            WindowEnd = windowEnd,
            Sessions = sessions,
            Clients = clients,
            // The form opens only for a session that is offered and bookable: an id from the query string never
            // becomes a form for a class the owner is not seeing.
            OpenAppointmentId = session is { } selected
                && sessions.Any(candidate => candidate.AppointmentId == selected && candidate.CanBook)
                    ? selected
                    : null
        });
    }

    /// <summary>
    /// Books a client the owner picked into the chosen session (RF-11), then redirects back to the roster
    /// (POST/redirect/GET) so the owner sees the new annotation instead of a stale page. The business and the session
    /// come from the owner claim and the form, and the service revalidates every rule under its own lock: the screen
    /// never decides whether the booking is possible.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookDropIn(int appointmentId, int clientId, CancellationToken ct)
    {
        // A form submitted without a pick is not a business rejection: say what is missing instead of asking the
        // service to answer for an empty client id.
        if (clientId <= 0)
        {
            this.Flash("Elegí una alumna con ficha para anotar en la clase.", FlashType.Error);
            return RedirectToAction(nameof(DropIns));
        }

        var result = await dropInsService!.BookForClientAsync(BusinessId, appointmentId, clientId, ct);
        this.Flash(result.Succeeded ? "Alumna anotada en la clase suelta." : BookingMessage(result.Error!),
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(DropIns));
    }

    /// <summary>
    /// Cancels a drop-in annotation of the business before its class starts (RF-22), then redirects back to the
    /// roster (POST/redirect/GET). The business comes from the owner claim, never from the form, and the service
    /// answers a booking of another business exactly like a missing one.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDropIn(int bookingId, CancellationToken ct)
    {
        var result = await dropInsService!.CancelDropInAsync(BusinessId, bookingId, ct);
        this.Flash(result.Succeeded ? "Reserva suelta cancelada." : CancellationMessage(result.Error!),
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(DropIns));
    }

    /// <summary>
    /// Turns the stable code of a rejected booking into the sentence the owner reads. The codes are the caller's
    /// contract and are never shown as text: an unrecognized one falls back to a generic message instead of leaking it.
    /// </summary>
    private static string BookingMessage(BusinessError error) => error.Code switch
    {
        "drop_in_client_not_found" => "No encontramos a esa alumna en tu negocio.",
        "drop_in_not_found" => "No encontramos esa clase.",
        "drop_in_not_available" => "Esa clase ya empezó o está suspendida: no se puede anotar una suelta.",
        "drop_in_already_booked" => "Esa alumna ya está anotada en esa clase.",
        "drop_in_full" => "La clase está completa. Probá con otro día u horario.",
        _ => "No pudimos registrar la reserva. Volvé a intentar."
    };

    /// <summary>
    /// Turns the stable code of a rejected cancellation into the sentence the owner reads, with the same rule as
    /// <see cref="BookingMessage"/>: a known code gets its message, an unknown one never reaches the screen as a code.
    /// </summary>
    private static string CancellationMessage(BusinessError error) => error.Code switch
    {
        "drop_in_not_found" => "No encontramos esa reserva suelta.",
        "drop_in_not_available" => "La clase ya empezó o está suspendida: esa reserva suelta ya no se puede cancelar.",
        "drop_in_already_cancelled" => "Esa reserva suelta ya estaba cancelada.",
        _ => "No pudimos cancelar la reserva suelta. Volvé a intentar."
    };

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
