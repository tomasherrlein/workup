using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;
using WorkUp.Infrastructure;
using WorkUp.ViewModels.Enrollments;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>
/// Abonos (RF-05, RF-20, RF-29): the membership list with its filters, the pending enrollment with its fixed classes,
/// the charge that activates or renews it, the annulment of a pending one and the termination with a free effective
/// date. Every rule, every date and every seat belongs to <see cref="EnrollmentService"/>: this controller routes,
/// resolves the names the owner reads and maps the stable error codes to Spanish. It never decides whether an
/// operation is possible, and it never writes outside the service's transaction.
/// <para>
/// The collaborators the create form needs are optional so the shorter constructor keeps the list flows (and their
/// tests) independent; the container always supplies all of them, because the Abonos section is part of the panel.
/// </para>
/// </summary>
public sealed class EnrollmentsController(
    EnrollmentService enrollments,
    IClock clock,
    IEnrollmentRepository? repository = null,
    ClassesService? classes = null,
    CatalogService? catalog = null,
    StaffService? staff = null,
    ClientsService? clients = null,
    IMembershipPriceRepository? membershipPrices = null,
    FixedScheduleChangeService? scheduleChanges = null) : OwnerControllerBase
{
    /// <summary>Upper bound the HTML amount inputs use; the service only requires a positive amount.</summary>
    private const decimal MaxAmount = 9999999999.99m;

    /// <summary>
    /// Membership list (RF-29). The filter is a request value; an unknown one falls back to the default view instead
    /// of failing, so a stale link never turns into an error page. The service derives every state on read.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(string? filter, CancellationToken ct)
    {
        ViewData["Title"] = "Abonos";
        SetPrivateHeaders();

        var selected = ParseFilter(filter);
        var result = await enrollments.ListAsync(BusinessId, selected, ct);
        if (!result.Succeeded)
        {
            this.Flash(result.Error!.Message, FlashType.Error);
            return View(BuildIndex(EnrollmentListFilter.All, []));
        }

        return View(BuildIndex(selected, result.Value.Select(item => new EnrollmentRowViewModel(item)).ToList()));
    }

    /// <summary>Create-enrollment form (RF-05 steps 1-3): clients, weekly frequency with its tariff, the grid classes with a free seat and the optional agreed price.</summary>
    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        ViewData["Title"] = "Nueva inscripción";
        return View("Form", await BuildFormAsync(new EnrollmentFormViewModel(), applyDefaults: true, ct));
    }

    /// <summary>
    /// Registers the pending enrollment and redirects back to the list (POST/redirect/GET). The business comes from
    /// the owner claim, never from the form, and the effective date is the registration day the clock owns. A rejected
    /// create re-renders the form with the mapped message and keeps everything the owner typed.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(EnrollmentFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Nueva inscripción";

        if (model.AgreedPrice is { } price && (price <= 0 || price > MaxAmount || decimal.Round(price, 2) != price))
            ModelState.AddModelError(nameof(model.AgreedPrice), "Ingresá un monto con hasta dos decimales.");
        if (model.GridClassIds.Count == 0)
            ModelState.AddModelError(nameof(model.GridClassIds), "Elegí al menos una clase fija.");

        if (!ModelState.IsValid)
            return View("Form", await BuildFormAsync(model, applyDefaults: false, ct));

        // The fixed classes are valid from the registration day; the effective date is never a form field, so a
        // request cannot ask for a future start to slip a seat into a full grid class.
        var request = new CreateEnrollmentRequest(
            model.ClientId,
            model.WeeklyFrequency,
            model.AgreedPrice,
            model.PriceReason,
            model.GridClassIds,
            clock.Today);

        var result = await enrollments.CreatePendingAsync(BusinessId, request, ct);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(result.Error!.Field ?? string.Empty, CreateErrorMessage(result.Error!));
            return View("Form", await BuildFormAsync(model, applyDefaults: false, ct));
        }

        this.Flash("Inscripción registrada. Cobrá el primer abono para activarla.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Charges the chosen membership (RF-05 step 6, RF-29): the first payment activates the pending enrollment and a
    /// later one renews an active membership. The amount is optional — without it the agreed price is charged — and
    /// any other amount needs a reason (D-20). Redirects back to the list with the new expiry.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Charge(int enrollmentId,
        [ModelBinder(typeof(InvariantDecimalModelBinder))] decimal? amount, string? reason, CancellationToken ct)
    {
        SetPrivateHeaders();

        if (amount is { } value && (value <= 0 || value > MaxAmount || decimal.Round(value, 2) != value))
        {
            this.Flash("Ingresá un monto válido para cobrar.", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        if (reason?.Length > 200)
        {
            this.Flash("El motivo no puede superar 200 caracteres.", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        var result = await enrollments.ChargeAsync(
            BusinessId, new ChargeMembershipRequest(enrollmentId, amount, reason), ct);
        this.Flash(result.Succeeded ? ChargeMessage(result.Value) : ChargeErrorMessage(result.Error!),
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Annuls a pending enrollment (RF-20), releasing its fixed places, and redirects back to the list. The screen
    /// only offers the action for a pending one, but the service remains the guard.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Annul(int enrollmentId, CancellationToken ct)
    {
        SetPrivateHeaders();

        var result = await enrollments.AnnulPendingAsync(BusinessId, new AnnulEnrollmentRequest(enrollmentId), ct);
        this.Flash(result.Succeeded ? "Inscripción anulada. Sus lugares fijos quedaron libres." : AnnulErrorMessage(result.Error!),
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Ends the membership from the date the owner chose (RF-29 step 6), releasing the places from that date on, and
    /// redirects back to the list. Deliberately not named <c>Terminate</c> on the wire: the panel posts to this action.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Terminate(int enrollmentId, DateOnly? effectiveFrom, CancellationToken ct)
    {
        SetPrivateHeaders();

        // A date is required to end a membership: a malformed or absent one is reported instead of ending it today.
        if (effectiveFrom is not { } from)
        {
            this.Flash("Elegí la fecha desde la que dar de baja el abono.", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        var result = await enrollments.TerminateAsync(
            BusinessId, new TerminateEnrollmentRequest(enrollmentId, from), ct);
        this.Flash(result.Succeeded ? TerminateMessage(result.Value) : TerminateErrorMessage(result.Error!),
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Guided schedule change form (RF-21, D-19): the enrollment's current open fixed classes and the active grid
    /// classes that still have a free permanent seat. The read is scoped by the business from the claim, so a foreign
    /// or missing id resolves to <c>null</c> and the caller redirects without ever exposing a name or an option.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ChangeSchedule(int enrollmentId, CancellationToken ct)
    {
        ViewData["Title"] = "Cambiar horario";
        SetPrivateHeaders();

        var model = await BuildChangeScheduleFormAsync(
            new ChangeScheduleFormViewModel { EnrollmentId = enrollmentId }, applyDefaults: true, ct);
        if (model is null)
            return ChangeScheduleNotFound();

        return View("ChangeSchedule", model);
    }

    /// <summary>
    /// Registers the guided schedule change from the effective date the owner chose (RF-21) and redirects to the list
    /// (POST/redirect/GET). The business comes from the owner claim, never from the form, and the service owns every
    /// rule: a rejected change re-renders the form with the mapped Spanish message and keeps what the owner typed.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeSchedule(ChangeScheduleFormViewModel model, CancellationToken ct)
    {
        ViewData["Title"] = "Cambiar horario";
        SetPrivateHeaders();

        if (!ModelState.IsValid)
            return await ReRenderChangeScheduleAsync(model, ct);

        var result = await scheduleChanges!.ChangeAsync(
            BusinessId,
            new ChangeFixedScheduleRequest(
                model.EnrollmentId, model.CurrentGridClassId, model.NewGridClassId, model.EffectiveFrom!.Value),
            ct);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(result.Error!.Field ?? string.Empty, ChangeScheduleErrorMessage(result.Error!));
            return await ReRenderChangeScheduleAsync(model, ct);
        }

        this.Flash(
            $"Horario cambiado desde el {Formatting.ShortDate(result.Value.EffectiveFrom)}. " +
            $"El horario anterior queda vigente hasta el {Formatting.ShortDate(result.Value.PreviousValidTo)}.",
            FlashType.Success);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>The same answer for a missing, foreign or ended enrollment: back to the list, naming nothing.</summary>
    private IActionResult ChangeScheduleNotFound()
    {
        this.Flash("No encontramos una inscripción con horario fijo para cambiar.", FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> ReRenderChangeScheduleAsync(ChangeScheduleFormViewModel model, CancellationToken ct)
    {
        // The options are re-read on every render, so a rejected change keeps the typed values and a current picture.
        // If the enrollment stopped being changeable meanwhile, nothing is exposed and the owner goes back to the list.
        var rebuilt = await BuildChangeScheduleFormAsync(model, applyDefaults: false, ct);
        return rebuilt is null ? ChangeScheduleNotFound() : View("ChangeSchedule", rebuilt);
    }

    /// <summary>
    /// Resolves the form options from the enrollment of this business. It returns <c>null</c> when the enrollment is
    /// not this business's, is not pending payment or active, has no open fixed class or no other slot to offer, so a
    /// stale, missing or foreign id never exposes the name or the options: only then are the client name and the grid
    /// classes resolved.
    /// </summary>
    private async Task<ChangeScheduleFormViewModel?> BuildChangeScheduleFormAsync(
        ChangeScheduleFormViewModel model, bool applyDefaults, CancellationToken ct)
    {
        var state = await repository!.LockEnrollmentAsync(BusinessId, model.EnrollmentId, ct);
        if (state is null || state.Enrollment.Status is not (EnrollmentStatus.PendingPayment or EnrollmentStatus.Active))
            return null;

        var openFixedClasses = state.FixedClasses
            .Where(fixedClass => fixedClass.ValidTo is null)
            .OrderBy(fixedClass => fixedClass.ValidFrom)
            .ThenBy(fixedClass => fixedClass.Id)
            .ToList();
        if (openFixedClasses.Count == 0)
            return null;

        var gridClasses = (await classes!.ListAsync(BusinessId, ct)).ToDictionary(gridClass => gridClass.Id);
        var services = (await catalog!.ListAsync(BusinessId, ct)).ToDictionary(service => service.Id);
        var workers = (await staff!.ListAsync(BusinessId, ct)).ToDictionary(worker => worker.Id);

        model.CurrentClasses = [];
        foreach (var fixedClass in openFixedClasses)
        {
            if (!gridClasses.TryGetValue(fixedClass.GridClassId, out var gridClass))
                continue;
            if (BuildCurrentFixedClassLabel(gridClass, services, workers, fixedClass) is not { } label)
                continue;

            model.CurrentClasses.Add(new ChangeScheduleCurrentOptionViewModel(fixedClass.GridClassId, label));
        }

        if (model.CurrentClasses.Count == 0)
            return null;

        model.ClientName = await ResolveClientNameAsync(state.Enrollment.ClientId, ct);
        model.GridClasses = await BuildGridClassOptionsAsync(
            ct, model.CurrentClasses.Select(option => option.GridClassId).ToArray());
        model.Today = clock.Today;
        if (applyDefaults)
            model.EffectiveFrom ??= clock.Today;

        return model;
    }

    private async Task<string> ResolveClientNameAsync(int clientId, CancellationToken ct)
    {
        var client = await clients!.GetAsync(BusinessId, clientId, ct);
        return client is null ? "Alumna" : client.FullName;
    }

    /// <summary>
    /// The current fixed class as the owner reads it: day, time, class type and teacher, plus the day it started. A
    /// grid class or a name that cannot be resolved is skipped instead of being shown half-labelled.
    /// </summary>
    private static string? BuildCurrentFixedClassLabel(
        GridClass gridClass,
        IReadOnlyDictionary<int, Service> services,
        IReadOnlyDictionary<int, Worker> workers,
        EnrollmentFixedClass fixedClass)
    {
        if (!services.TryGetValue(gridClass.ServiceId, out var service)
            || !workers.TryGetValue(gridClass.WorkerId, out var worker))
            return null;

        return $"{GridClassLabel(gridClass, service, worker)} · desde el {Formatting.ShortDate(fixedClass.ValidFrom)}";
    }

    /// <summary>
    /// Parses the filter of the request. An unknown value falls back to the default view instead of propagating an
    /// undefined enum to the service, which would fail the whole page.
    /// </summary>
    private static EnrollmentListFilter ParseFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return EnrollmentListFilter.All;

        return Enum.TryParse<EnrollmentListFilter>(value.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed
                : EnrollmentListFilter.All;
    }

    private EnrollmentIndexViewModel BuildIndex(EnrollmentListFilter filter, IReadOnlyList<EnrollmentRowViewModel> items) =>
        new()
        {
            Filter = filter,
            Filters = BuildFilters(filter),
            Items = items,
            // One clock read for the page: the terminate forms default to the business day the list was judged on.
            Today = clock.Today
        };

    private static IReadOnlyList<EnrollmentFilterOptionViewModel> BuildFilters(EnrollmentListFilter selected) =>
    [
        new(EnrollmentListFilter.All, "Todos", selected == EnrollmentListFilter.All),
        new(EnrollmentListFilter.Current, "Vigentes", selected == EnrollmentListFilter.Current),
        new(EnrollmentListFilter.ExpiringSoon, "Vencen pronto", selected == EnrollmentListFilter.ExpiringSoon),
        new(EnrollmentListFilter.Expired, "Vencidos", selected == EnrollmentListFilter.Expired),
        new(EnrollmentListFilter.PendingPayment, "Pendientes de pago", selected == EnrollmentListFilter.PendingPayment)
    ];

    /// <summary>
    /// Fills the form options. On the first render only it preselects the lowest configured frequency and seeds its
    /// tariff as the model's agreed price: the amount input itself starts empty and that tariff is only its
    /// placeholder, so leaving the amount untouched lets the service apply the tariff of the frequency the owner
    /// actually chose (D-20). The registration day is not seeded here: the clock reads it at POST time. On a re-render
    /// the bound values are kept untouched, so a rejected create never overwrites what the owner typed or cleared.
    /// </summary>
    private async Task<EnrollmentFormViewModel> BuildFormAsync(
        EnrollmentFormViewModel model, bool applyDefaults, CancellationToken ct)
    {
        model.Clients = (await clients!.SearchAsync(BusinessId, null, ct))
            .Select(client => new EnrollmentClientOptionViewModel(client.Id, client.FullName))
            .ToList();

        var prices = await membershipPrices!.ListByBusinessAsync(BusinessId, ct);
        model.FrequencyOptions = prices
            .OrderBy(price => price.WeeklyFrequency)
            .Select(price => new EnrollmentFrequencyOptionViewModel(
                price.WeeklyFrequency, FrequencyLabel(price.WeeklyFrequency), price.Price))
            .ToList();

        model.GridClasses = await BuildGridClassOptionsAsync(ct);

        if (applyDefaults)
        {
            if (model.WeeklyFrequency == 0 && model.FrequencyOptions.Count > 0)
                model.WeeklyFrequency = model.FrequencyOptions[0].Frequency;

            model.AgreedPrice ??= model.FrequencyOptions
                .FirstOrDefault(option => option.Frequency == model.WeeklyFrequency)?.Price;
        }

        // The amount input starts empty on the first render: changing the frequency without touching the amount must
        // post no amount so the service uses the tariff of the chosen frequency (D-20), never the tariff preseeded for
        // another one. The tariff of the preselected frequency is therefore only the placeholder. A re-render keeps
        // the typed value, so the placeholder is informational there.
        ViewData["AgreedPriceStartsEmpty"] = applyDefaults;
        ViewData["AgreedPricePlaceholder"] = model.FrequencyOptions
            .FirstOrDefault(option => option.Frequency == model.WeeklyFrequency) is { } preselected
                ? Formatting.Money(preselected.Price)
                : null;

        return model;
    }

    /// <summary>
    /// The grid classes the owner may choose: the active ones whose service is an active class-type service (max
    /// capacity greater than 1) and that still have a free permanent seat on the business day. The seat count is the
    /// same <see cref="FixedSeatCapacity"/> rule the service enforces under its lock; this read only decides what the
    /// screen offers, never whether a seat exists at write time. <paramref name="excludingGridClassIds"/> drops the
    /// slots the form cannot offer as a new one (the enrollment's own current classes, which the change would reject
    /// as a duplicate).
    /// </summary>
    private async Task<List<EnrollmentGridClassOptionViewModel>> BuildGridClassOptionsAsync(
        CancellationToken ct, IReadOnlyCollection<int>? excludingGridClassIds = null)
    {
        var services = (await catalog!.ListAsync(BusinessId, ct)).ToDictionary(service => service.Id);
        var workers = (await staff!.ListAsync(BusinessId, ct)).ToDictionary(worker => worker.Id);
        var today = clock.Today;

        var options = new List<EnrollmentGridClassOptionViewModel>();
        foreach (var gridClass in await classes!.ListAsync(BusinessId, ct))
        {
            if (!gridClass.IsActive)
                continue;
            if (excludingGridClassIds is not null && excludingGridClassIds.Contains(gridClass.Id))
                continue;

            // Defensive: a grid class always references an existing active class-type service and a worker of the same
            // business, but skip silently instead of breaking the whole picker if that ever drifts.
            if (!services.TryGetValue(gridClass.ServiceId, out var service) || !service.IsActive || service.MaxCapacity <= 1)
                continue;
            if (!workers.TryGetValue(gridClass.WorkerId, out var worker))
                continue;

            var seats = await repository!.ListFixedSeatsAsync(BusinessId, gridClass.Id, today, ct);
            var freeSeats = FixedSeatCapacity.RemainingSeats(service.MaxCapacity, seats, today);
            // A full grid class can only be rejected, so it is not offered; the remaining seats of the others are shown.
            if (freeSeats <= 0)
                continue;

            options.Add(new EnrollmentGridClassOptionViewModel(
                gridClass.Id, GridClassLabel(gridClass, service, worker), freeSeats));
        }

        return options
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.GridClassId)
            .ToList();
    }

    private static string FrequencyLabel(int weeklyFrequency) =>
        weeklyFrequency == 1 ? "1 vez por semana" : $"{weeklyFrequency} veces por semana";

    private static string GridClassLabel(GridClass gridClass, Service service, Worker worker) =>
        $"{Formatting.DayName(gridClass.DayOfWeek)} {Formatting.Time(gridClass.StartTime)} · {service.Name} · {worker.FullName}";

    private static string ChargeMessage(MembershipChargeOutcome outcome)
    {
        var message = outcome.Activated
            ? $"Inscripción activada. El abono vence el {Formatting.ShortDate(outcome.ExpiresOn)}."
            : $"Abono renovado. Vence el {Formatting.ShortDate(outcome.ExpiresOn)}.";

        return outcome.Sync.RefusedWithoutSeat switch
        {
            0 => message,
            1 => message + " Quedó 1 clase ya generada sin lugar libre.",
            var refused => message + $" Quedaron {refused} clases ya generadas sin lugar libre."
        };
    }

    private static string TerminateMessage(EnrollmentTerminationOutcome outcome) =>
        $"Abono dado de baja desde el {Formatting.ShortDate(outcome.EffectiveFrom)}. Sus lugares quedaron libres.";

    /// <summary>
    /// Turns the stable code of a rejected create into the sentence the owner reads. The codes are the service's
    /// contract and are never shown as text: an unrecognized one falls back to the service's own message, which is
    /// already Spanish, instead of leaking the code.
    /// </summary>
    private static string CreateErrorMessage(BusinessError error) => error.Code switch
    {
        "enrollment_invalid_request" => "No pudimos identificar la inscripción. Volvé a intentar.",
        "enrollment_invalid_frequency" => "La frecuencia semanal debe estar entre 1 y 7.",
        "enrollment_invalid_effective_date" => "Elegí una fecha de vigencia válida.",
        "enrollment_invalid_grid_classes" => "Elegí al menos una clase fija, hasta la frecuencia semanal.",
        "enrollment_invalid_price" => "El precio acordado debe ser mayor a cero.",
        "enrollment_price_reason_required" => "Indicá el motivo del monto acordado.",
        "enrollment_price_not_configured" => "Ese plan de abono todavía no tiene precio. Configuralo antes de inscribir.",
        "enrollment_client_not_found" => "No encontramos a esa alumna en tu negocio.",
        "enrollment_grid_class_not_found" => "No encontramos alguna de las clases fijas elegidas.",
        "enrollment_grid_class_inactive" => "Alguna de las clases fijas elegidas está archivada.",
        "enrollment_no_seat" => "No queda lugar fijo en alguna de las clases elegidas.",
        _ => error.Message
    };

    /// <summary>Turns the stable code of a rejected charge into the sentence the owner reads, never the raw code.</summary>
    private static string ChargeErrorMessage(BusinessError error) => error.Code switch
    {
        "membership_not_found" => "No encontramos ese abono.",
        "membership_not_chargeable" => "Esa inscripción está anulada o dada de baja: no se puede cobrar.",
        "membership_invalid_amount" => "El monto debe ser mayor a cero.",
        "membership_reason_required" => "Indicá el motivo del monto cobrado.",
        _ => error.Message
    };

    /// <summary>Turns the stable code of a rejected annulment into the sentence the owner reads, never the raw code.</summary>
    private static string AnnulErrorMessage(BusinessError error) => error.Code switch
    {
        "enrollment_not_found" => "No encontramos esa inscripción.",
        "enrollment_not_pending" => "Sólo se puede anular una inscripción pendiente de pago.",
        _ => error.Message
    };

    /// <summary>Turns the stable code of a rejected termination into the sentence the owner reads, never the raw code.</summary>
    private static string TerminateErrorMessage(BusinessError error) => error.Code switch
    {
        "enrollment_not_found" => "No encontramos esa inscripción.",
        "enrollment_invalid_effective_date" => "Elegí una fecha de vigencia válida.",
        "enrollment_not_terminable" => "Esa inscripción ya está anulada o dada de baja.",
        _ => error.Message
    };

    /// <summary>
    /// Turns the stable code of a rejected schedule change into the sentence the owner reads, never the raw code. The
    /// date-bound refusals (<c>effective_date_too_early</c>, <c>sessions_generated</c>) keep the service message: it
    /// already names the earliest usable date, which is the actionable part the owner needs to retry.
    /// </summary>
    private static string ChangeScheduleErrorMessage(BusinessError error) => error.Code switch
    {
        "fixed_schedule_change_invalid_request" => "No pudimos identificar el cambio. Volvé a intentar.",
        "fixed_schedule_change_invalid_effective_date" => "Elegí una fecha de vigencia válida.",
        "fixed_schedule_change_enrollment_not_found" => "No encontramos esa inscripción.",
        "fixed_schedule_change_not_changeable" =>
            "Esa inscripción está anulada o dada de baja: ya no tiene un horario fijo que cambiar.",
        "fixed_schedule_change_assignment_not_found" =>
            "Ese horario fijo ya no está vigente en la inscripción. Volvé a cargar el formulario.",
        "fixed_schedule_change_duplicate_slot" => "Elegí un horario distinto del actual.",
        "fixed_schedule_change_grid_class_not_found" => "No encontramos el nuevo horario en tu negocio.",
        "fixed_schedule_change_grid_class_inactive" => "El nuevo horario está archivado: elegí otro.",
        "fixed_schedule_change_no_seat" => "No queda lugar fijo en el nuevo horario. Elegí otro.",
        "fixed_schedule_change_previous_assignment_changed" =>
            "Ese horario fijo cambió recién. Volvé a revisar la inscripción.",
        _ => error.Message
    };

    private void SetPrivateHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
