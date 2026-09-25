using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
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
/// Grilla semanal de clases (RF-04), generación de la semana (RF-06), clases sueltas (RF-22, RF-07 cupo &gt; 1) y
/// detalle de una clase generada con su asistencia, atención sin turno, cancelación de una anotación y suspensión de
/// la fecha (RF-10, RF-11, RF-12). Reachable once the business has a class-type service (Settings shortcut and
/// navigation hide it otherwise).
/// <para>
/// The drop-in roster, the detail page and its actions need more collaborators than the grid. They are optional only so
/// a caller that never reaches those screens (the grid and week tests, for instance) can keep the shorter constructor;
/// the container always supplies them, because RF-10, RF-11 and RF-22 are part of the panel and their services are
/// registered. A missing collaborator closes the action that needs it instead of running it unenforced.
/// </para>
/// </summary>
public class ClassesController(
    ClassesService classesService,
    CatalogService catalogService,
    StaffService staffService,
    WeekGenerationService weekGenerationService,
    IClock clock,
    ClassDropInService? dropInsService = null,
    ClientsService? clientsService = null,
    ClassDetailService? classDetailService = null,
    ClassAttendanceService? attendanceService = null,
    ClassWalkInService? walkInService = null,
    ClassDateActionsService? dateActionsService = null,
    BusinessSettingsService? settingsService = null) : OwnerControllerBase
{
    /// <summary>Message of a rejected amount or observation, shared by every action that accepts them.</summary>
    private const string ChargeOrNoteMessage = "Revisá el importe y la observación (máximo 1000 caracteres).";

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
    /// Read-only detail of one generated class (RF-10, RF-11, RF-12, RF-22): the fixed, drop-in and cancelled roster
    /// with the derived membership state of every fixed student and the occupancy the read resolved with a single
    /// capacity rule. The flags the screen offers are advisory — the service revalidates every action under its own
    /// lock — and the page reads the clock exactly once, so the two date-level actions and the arrival window are
    /// judged against the same instant. A session that does not exist, belongs to another business or is not a class is
    /// the same answer.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        ViewData["Title"] = "Detalle de la clase";

        // The detail page is part of the panel, so the container always supplies its collaborators. A caller that used
        // the shorter constructor gets a closed page instead of a roster whose actions could not be enforced.
        if (classDetailService is null || clientsService is null)
            return NotFound();

        var result = await classDetailService.GetAsync(BusinessId, id, ct);
        if (!result.Succeeded)
            return NotFound();

        var detail = result.Value;

        // The single clock read of the page: the suspension window, the arrival window and every action offered
        // derive from this value, so a clock that advances while the page is built cannot split them.
        var now = clock.Now;

        // Regla 15: the business decides whether attention without a booking exists at all. A missing settings
        // collaborator simply never offers the form.
        var allowsWalkIns = settingsService is not null
            && (await settingsService.GetAsync(BusinessId, ct)).AllowsWalkIns;

        // Same name resolution as the grid, the week and the drop-in roster: the read carries ids, the owner reads
        // names.
        var servicesById = (await catalogService.ListAsync(BusinessId, ct)).ToDictionary(s => s.Id);
        var workersById = (await staffService.ListAsync(BusinessId, ct)).ToDictionary(w => w.Id);

        var service = servicesById.GetValueOrDefault(detail.Session.ServiceId);
        var session = new ClassDetailSessionViewModel(
            detail.Session.AppointmentId,
            detail.Session.Date,
            detail.Session.StartTime,
            detail.Session.EndTime,
            detail.Session.EndsAt,
            service?.Name ?? "Clase",
            workersById.TryGetValue(detail.Session.WorkerId, out var worker) ? worker.FullName : string.Empty,
            detail.Session.MaxCapacity,
            detail.PlacesTaken,
            detail.RemainingPlaces,
            detail.Suspended,
            detail.Session.SuspensionReason,
            // The class lock rule is shared with the service: Programado and strictly before the start.
            ClassCapacity.IsBookable(detail.Session.Status, detail.Session.StartsAt, now),
            CanRegisterWalkIn(detail, allowsWalkIns, now));

        var students = detail.Students
            .Select(student => new ClassDetailStudentViewModel(
                student.BookingId,
                student.ClientName,
                student.Status,
                student.Modality,
                student.FixedState,
                CanAttend(student, detail),
                CanMarkAbsent(student, detail),
                CanCancel(student, detail, now)))
            .ToList();

        // The walk-in picker only offers the business's own clients, exactly like the drop-in roster.
        var clients = (await clientsService.SearchAsync(BusinessId, null, ct))
            .Select(client => new ClassDropInClientOptionViewModel(client.Id, client.FullName))
            .ToList();

        return View(new ClassDetailViewModel(session, students, clients));
    }

    /// <summary>
    /// Registers the attendance of one student (RF-11) with the optional drop-in charge and the optional observation
    /// for the next visit, then redirects back to the class (POST/redirect/GET). The charge is only meaningful for a
    /// drop-in, so the form does not offer the amount for a fixed student; the service refuses it either way, and a
    /// fixed student's money lives in the membership. Every rejection flashes its sentence and writes nothing.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Attend(int id, int appointmentId,
        [ModelBinder(typeof(InvariantDecimalModelBinder))] decimal? amount, string? nextVisitNote,
        CancellationToken ct = default)
    {
        if (attendanceService is null)
            return NotFound();

        if (!IsValidAmount(amount) || !IsValidNote(nextVisitNote))
            return ReturnToClass(appointmentId, ChargeOrNoteMessage, success: false);

        var request = new ClassAttendanceRequest(
            amount is { } charge ? new ClassAttendanceCharge(charge) : null, nextVisitNote);
        var result = await attendanceService.MarkAttendedAsync(BusinessId, id, request, ct);

        return ReturnToClass(appointmentId, result.Succeeded
            ? amount is { } charged
                ? $"Asistencia registrada. Cobro de {Formatting.Money(charged)} registrado."
                : "Asistencia registrada."
            : AttendanceMessage(result.Error!), result.Succeeded);
    }

    /// <summary>
    /// Marks one student as absent (RF-12), then redirects back to the class (POST/redirect/GET). An absence is a fact
    /// and never depends on the membership coverage, and a suspended class did not happen, so the service is the one
    /// that refuses it.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAbsent(int id, int appointmentId, CancellationToken ct = default)
    {
        if (attendanceService is null)
            return NotFound();

        var result = await attendanceService.MarkAbsentAsync(BusinessId, id, ct);
        return ReturnToClass(appointmentId,
            result.Succeeded ? "Ausencia registrada." : AttendanceMessage(result.Error!), result.Succeeded);
    }

    /// <summary>
    /// Registers the arrival of an existing client who shows up for the class without a booking (RF-11): the walk-in
    /// annotation, the attendance and the optional charge and observation in one unit, then redirects back to the class
    /// (POST/redirect/GET). The charge and the observation are independent, and no email is queued (D-07), so neither
    /// the screen nor the flash promises one. The service revalidates the whole rule under the session lock.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterWalkIn(int appointmentId, int clientId,
        [ModelBinder(typeof(InvariantDecimalModelBinder))] decimal? amount, string? nextVisitNote,
        CancellationToken ct = default)
    {
        if (walkInService is null)
            return NotFound();

        // A form submitted without a pick is not a business rejection: say what is missing instead of asking the
        // service to answer for an empty client id.
        if (clientId <= 0)
            return ReturnToClass(appointmentId, "Elegí una alumna con ficha para registrar la llegada.", success: false);

        if (!IsValidAmount(amount) || !IsValidNote(nextVisitNote))
            return ReturnToClass(appointmentId, ChargeOrNoteMessage, success: false);

        var result = await walkInService.RegisterAsync(BusinessId, new ClassWalkInRequest(
            appointmentId,
            clientId,
            amount is { } charge ? new ClassAttendanceCharge(charge) : null,
            nextVisitNote), ct);

        return ReturnToClass(appointmentId, result.Succeeded
            ? amount is { } charged
                ? $"Llegada registrada. Cobro de {Formatting.Money(charged)} registrado."
                : "Llegada registrada."
            : WalkInMessage(result.Error!), result.Succeeded);
    }

    /// <summary>
    /// Cancels exactly one annotation of this class date (RF-22) and redirects back to the class
    /// (POST/redirect/GET). While the class is still <c>Programado</c> and has not started, any non-cancelled
    /// annotation can be cancelled, a recorded <c>Atendido</c> or <c>Ausente</c> included; the place is released and
    /// neither the enrollment nor a charge is touched, so nothing is refunded or credited here (D-24, T18).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelBooking(int id, int appointmentId, CancellationToken ct = default)
    {
        if (dateActionsService is null)
            return NotFound();

        var result = await dateActionsService.CancelBookingAsync(BusinessId, id, ct);
        return ReturnToClass(appointmentId, result.Succeeded
            ? "Anotación cancelada: el lugar quedó liberado."
            : DateActionMessage(result.Error!), result.Succeeded);
    }

    /// <summary>
    /// Suspends the whole class of this date (RF-10) with a mandatory reason and redirects back to the class
    /// (POST/redirect/GET). The suspension writes the session row only, never marks absences and never refunds or
    /// credits anything; the flash reports how many notices it queued, because the notice count is what the owner
    /// cannot see on the page.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suspend(int id, string? reason, CancellationToken ct = default)
    {
        if (dateActionsService is null)
            return NotFound();

        var result = await dateActionsService.SuspendClassAsync(BusinessId, id, reason, ct);
        return ReturnToClass(id, result.Succeeded
            ? SuspensionMessage(result.Value)
            : DateActionMessage(result.Error!), result.Succeeded);
    }

    /// <summary>
    /// Whether the arrival form is worth showing: the business admits attention without a booking (Regla 15), the class
    /// is <c>Programado</c> and not suspended, a place is free, and the clock is inside the arrival window
    /// <c>[start, end]</c>. The end is the <c>EndsAt</c> stored with the generated session — the same value the server
    /// judges — never a duration recomputed from the tariff of the moment: a tariff edited after the session was
    /// generated must not close a window the service still accepts. The service revalidates against that stored end.
    /// </summary>
    private static bool CanRegisterWalkIn(ClassDetail detail, bool allowsWalkIns, DateTime now)
    {
        if (!allowsWalkIns)
            return false;

        return detail.Session.Status == AppointmentStatus.Scheduled
            && !detail.Suspended
            && detail.RemainingPlaces > 0
            && now >= detail.Session.StartsAt
            && now <= detail.Session.EndsAt;
    }

    /// <summary>
    /// Whether the screen offers attendance: the class is still <c>Programado</c> and not suspended, the annotation is
    /// open, and the student has coverage on the class date — a drop-in pays on arrival, so it always has it, while a
    /// fixed student needs the active state (D-16, D-23). The service is the one that enforces it.
    /// </summary>
    private static bool CanAttend(ClassDetailStudent student, ClassDetail detail) =>
        detail.Session.Status == AppointmentStatus.Scheduled
        && !detail.Suspended
        && student.Status is BookingStatus.Pending or BookingStatus.Confirmed
        && (student.IsDropIn || student.FixedState == ClassFixedStudentState.Active);

    /// <summary>
    /// Whether the screen offers the absence: the same open annotation and an unsuspended class, with no coverage
    /// requirement at all, because the absence is a fact.
    /// </summary>
    private static bool CanMarkAbsent(ClassDetailStudent student, ClassDetail detail) =>
        detail.Session.Status == AppointmentStatus.Scheduled
        && !detail.Suspended
        && student.Status is BookingStatus.Pending or BookingStatus.Confirmed;

    /// <summary>
    /// Whether the screen offers the cancellation of one annotation: the class is still <c>Programado</c> and has not
    /// started, and the annotation was not cancelled already — a recorded attendance or absence can still be
    /// cancelled before the class, which is what makes the recorded fact stop counting.
    /// </summary>
    private static bool CanCancel(ClassDetailStudent student, ClassDetail detail, DateTime now) =>
        detail.Session.Status == AppointmentStatus.Scheduled
        && !detail.Suspended
        && !student.IsCancelled
        && detail.Session.StartsAt > now;

    /// <summary>Whether the optional amount of an attendance or walk-in can be stored. A missing charge is valid.</summary>
    private static bool IsValidAmount(decimal? amount) =>
        amount is null
        || (amount is > 0 and <= 9999999999.99m && decimal.Round(amount.Value, 2) == amount.Value);

    /// <summary>Whether the optional observation fits the client's record. A missing note is valid.</summary>
    private static bool IsValidNote(string? note) => note is null || note.Length <= 1000;

    /// <summary>
    /// Flash the message and redirect back to the class (POST/redirect/GET), or to the drop-in roster when the form
    /// carried no class id — that id only selects the page the owner returns to, never what the action writes.
    /// </summary>
    private IActionResult ReturnToClass(int appointmentId, string message, bool success)
    {
        this.Flash(message, success ? FlashType.Success : FlashType.Error);
        return appointmentId > 0
            ? RedirectToAction(nameof(Detail), new { id = appointmentId })
            : RedirectToAction(nameof(DropIns));
    }

    /// <summary>
    /// Turns the stable code of a rejected attendance or absence into the sentence the owner reads, with the same rule
    /// as the other mappers: a known code gets its message, an unknown one never reaches the screen as a code.
    /// </summary>
    private static string AttendanceMessage(BusinessError error) => error.Code switch
    {
        "class_attendance_not_found" => "No encontramos esa anotación de la clase.",
        "class_attendance_cancelled" => "Esa anotación está cancelada: el lugar ya fue liberado.",
        "class_attendance_already_attended" => "Esa alumna ya figura como presente.",
        "class_attendance_already_absent" => "Esa alumna ya figura como ausente.",
        "class_attendance_without_coverage" =>
            "La inscripción no está vigente en la fecha de la clase: renová el abono primero.",
        "class_attendance_suspended" => "Esa clase está suspendida: no se registra asistencia, ausencia ni cobro.",
        "class_attendance_charge_not_allowed" => "Sólo se cobra la clase suelta; el abono se registra aparte.",
        "class_attendance_already_charged" => "Esa clase suelta ya tiene un cobro vigente.",
        "class_attendance_invalid_charge" => "Revisá el importe y el motivo del precio.",
        "class_attendance_invalid_note" => ChargeOrNoteMessage,
        "class_attendance_note_failed" => "No pudimos guardar la observación. Volvé a intentar.",
        "class_attendance_unavailable" => "Esa anotación no admite esta acción.",
        _ => "No pudimos registrar la acción. Volvé a intentar."
    };

    /// <summary>
    /// Turns the stable code of a rejected walk-in into the sentence the owner reads. No rejection promises an email:
    /// the walk-in path never queues one (D-07).
    /// </summary>
    private static string WalkInMessage(BusinessError error) => error.Code switch
    {
        "class_walk_in_invalid_request" => "No pudimos registrar la atención. Volvé a intentar.",
        "class_walk_in_not_found" => "No encontramos esa clase.",
        "class_walk_in_client_not_found" => "No encontramos a esa alumna en tu negocio.",
        "class_walk_in_disabled" => "Este negocio no admite atención sin turno.",
        "class_walk_in_not_available" =>
            "Esa clase está suspendida: no se puede registrar una atención sin turno.",
        "class_walk_in_outside_window" =>
            "La atención sin turno sólo se puede registrar entre el inicio y el fin de la clase.",
        "class_walk_in_full" => "La clase está completa. Probá con otro día u horario.",
        "class_walk_in_already_booked" => "Esa alumna ya está anotada en esa clase.",
        "class_walk_in_invalid_charge" => "Revisá el importe y el motivo del precio.",
        "class_walk_in_invalid_note" => ChargeOrNoteMessage,
        "class_walk_in_note_failed" => "No pudimos guardar la observación. Volvé a intentar.",
        "class_walk_in_unavailable" => "Esa anotación no admite esta acción.",
        _ => "No pudimos registrar la atención. Volvé a intentar."
    };

    /// <summary>
    /// Turns the stable code of a rejected cancellation or suspension into the sentence the owner reads.
    /// </summary>
    private static string DateActionMessage(BusinessError error) => error.Code switch
    {
        "class_date_booking_not_found" => "No encontramos esa anotación de la clase.",
        "class_date_class_not_found" => "No encontramos esa clase.",
        "class_date_not_available" =>
            "La clase ya empezó o está suspendida: esa acción ya no está disponible.",
        "class_date_booking_cancelled" => "Esa anotación ya estaba cancelada: el lugar ya fue liberado.",
        "class_date_invalid_reason" => "Ingresá un motivo de hasta 300 caracteres.",
        _ => "No pudimos registrar la acción. Volvé a intentar."
    };

    /// <summary>
    /// The suspension outcome as one sentence. It reports the notices that actually landed in the outbox, and it says
    /// nothing about the students when none landed: a repeated request is refused long before a notice is composed, and
    /// an outbox that already held the same key is a deduplication hit, not an empty class.
    /// </summary>
    private static string SuspensionMessage(ClassSuspensionOutcome outcome) => outcome.NoticesQueued switch
    {
        0 => "Clase suspendida.",
        1 => "Clase suspendida. Quedó 1 aviso para la alumna anotada.",
        _ => $"Clase suspendida. Quedaron {outcome.NoticesQueued} avisos para las alumnas anotadas."
    };

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
