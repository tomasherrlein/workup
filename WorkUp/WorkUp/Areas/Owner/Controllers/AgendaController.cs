using Microsoft.AspNetCore.Mvc;
using WorkUp.Areas.Owner.Models.Agenda;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>Tenant-scoped owner agenda and individual booking actions.</summary>
public sealed class AgendaController(
    OwnerAgendaService agendaService,
    OwnerBookingActionsService bookingActionsService,
    OwnerRescheduleService rescheduleService,
    IClock clock) : OwnerControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index(
        DateOnly? date = null,
        string? period = null,
        int? workerId = null,
        string? status = null,
        string? origin = null,
        CancellationToken ct = default)
    {
        SetPrivateResponseHeaders();
        var selectedPeriod = ParseEnum<OwnerAgendaPeriod>(period) ?? OwnerAgendaPeriod.Day;
        var bookingStatus = ParseEnum<BookingStatus>(status);
        var appointmentOrigin = ParseEnum<AppointmentOrigin>(origin);
        var filter = new OwnerAgendaFilter(
            date ?? DateOnly.FromDateTime(clock.Now),
            selectedPeriod,
            workerId is > 0 ? workerId : null,
            bookingStatus,
            appointmentOrigin);
        var agenda = await agendaService.GetAgendaAsync(BusinessId, filter, ct);

        ViewData["Title"] = "Agenda";
        return View(new AgendaIndexViewModel(agenda, selectedPeriod, filter.WorkerId, bookingStatus, appointmentOrigin));
    }

    [HttpGet]
    public async Task<IActionResult> Detail(int id, CancellationToken ct = default)
    {
        SetPrivateResponseHeaders();
        var booking = await agendaService.GetIndividualBookingAsync(BusinessId, id, ct);
        if (booking is null)
            return NotFound();

        ViewData["Title"] = "Detalle del turno";
        return View(new AgendaBookingDetailViewModel(booking));
    }

    [HttpGet]
    public async Task<IActionResult> Reschedule(int id, DateOnly? date = null, CancellationToken ct = default)
    {
        SetPrivateResponseHeaders();
        var booking = await agendaService.GetIndividualBookingAsync(BusinessId, id, ct);
        if (booking is null || !booking.CanReschedule)
            return NotFound();

        var selectedDate = date ?? DateOnly.FromDateTime(booking.StartsAt);
        var options = await rescheduleService.GetOptionsAsync(BusinessId, id, selectedDate, ct);
        return BuildRescheduleForm(booking, new OwnerRescheduleInput { Date = selectedDate }, options);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reschedule(
        int id,
        [Bind(Prefix = "Input")] OwnerRescheduleInput input,
        CancellationToken ct = default)
    {
        SetPrivateResponseHeaders();
        var booking = await agendaService.GetIndividualBookingAsync(BusinessId, id, ct);
        if (booking is null)
            return NotFound();

        if (!booking.CanReschedule)
        {
            ModelState.AddModelError(string.Empty, RescheduleErrorMessage);
            return await BuildRescheduleFormWithOptionsAsync(booking, input, ct);
        }

        if (!ModelState.IsValid)
        {
            LiftHiddenSelectorErrors();
            return await BuildRescheduleFormWithOptionsAsync(booking, input, ct);
        }

        var startsAt = input.Date!.Value.ToDateTime(input.StartTime!.Value);
        var attempt = await rescheduleService.RescheduleIndividualAsync(
            BusinessId, id, new OwnerRescheduleRequest(booking.WorkerId, startsAt), ct);
        if (!attempt.Succeeded)
        {
            if (RevealableRescheduleErrorCodes.Contains(attempt.Error!.Code))
                ModelState.AddBusinessError(attempt.Error);
            else
                ModelState.AddModelError(string.Empty, RescheduleErrorMessage);
            return await BuildRescheduleFormWithOptionsAsync(booking, input, ct);
        }

        this.Flash("Turno reprogramado.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet]
    public Task<IActionResult> NewBooking(
        int? serviceId = null,
        int? workerId = null,
        DateOnly? date = null,
        TimeOnly? startTime = null,
        string? email = null,
        string? firstName = null,
        string? lastName = null,
        CancellationToken ct = default)
    {
        SetPrivateResponseHeaders();
        // startTime/email/firstName/lastName let the "conflict alternative" and "horario más cercano" links (which
        // navigate here with GET) carry the data the owner already typed and preselect the suggested time, instead
        // of discarding it as if the notice "Tus datos se conservaron" were not true.
        var input = new OwnerBookingCreateInput
        {
            ServiceId = serviceId,
            WorkerId = workerId,
            Date = date ?? DateOnly.FromDateTime(clock.Now),
            StartTime = startTime,
            Email = email ?? "",
            FirstName = firstName ?? "",
            LastName = lastName ?? ""
        };
        return BuildBookingFormAsync(input, ct);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBooking(
        [Bind(Prefix = "Input")] OwnerBookingCreateInput input,
        CancellationToken ct = default)
    {
        SetPrivateResponseHeaders();
        if (!ModelState.IsValid)
        {
            LiftHiddenSelectorErrors();
            return await BuildBookingFormAsync(input, ct);
        }

        var attempt = await bookingActionsService.BookIndividualAsync(BusinessId, new BookIndividualAppointmentRequest(
            input.ServiceId!.Value,
            input.WorkerId!.Value,
            input.Date!.Value,
            input.StartTime!.Value,
            input.Email,
            input.FirstName,
            input.LastName), ct);
        if (!attempt.Succeeded)
        {
            ModelState.AddModelError(string.Empty, attempt.Error!.Message);
            return await BuildBookingFormAsync(input, ct, attempt.AlternativeStart);
        }

        this.Flash("Turno reservado.");
        return RedirectToAction(nameof(Detail), new { id = attempt.Confirmation.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct = default)
    {
        var result = await bookingActionsService.CancelIndividualBookingAsync(BusinessId, id, ct);
        if (result.Error?.Code == "owner_booking_not_found")
            return NotFound();

        this.Flash(
            result.Succeeded ? "Turno cancelado." : "No se pudo cancelar este turno. Revisá su estado e intentá de nuevo.",
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Detail), new { id });
    }

    private async Task<IActionResult> BuildRescheduleFormWithOptionsAsync(
        OwnerAgendaBookingDetail booking, OwnerRescheduleInput input, CancellationToken ct)
    {
        var date = input.Date ?? DateOnly.FromDateTime(booking.StartsAt);
        input.Date = date;
        var options = await rescheduleService.GetOptionsAsync(BusinessId, booking.BookingId, date, ct);
        return BuildRescheduleForm(booking, input, options);
    }

    private IActionResult BuildRescheduleForm(
        OwnerAgendaBookingDetail booking, OwnerRescheduleInput input, OwnerRescheduleOptions? options = null)
    {
        ViewData["Title"] = "Reprogramar turno";
        return View("Reschedule", new AgendaBookingRescheduleViewModel(booking, input, options));
    }

    private const string RescheduleErrorMessage =
        "No se pudo reprogramar el turno. El horario puede haberse ocupado o el turno ya no admite cambios. Revisá los datos e intentá de nuevo.";

    /// <summary>
    /// Hidden selectors of the owner forms, paired with the Spanish message the summary shows when their own error
    /// would otherwise stay invisible. NewBooking.cshtml renders Input.ServiceId, Input.WorkerId and Input.Date as
    /// hidden inputs with no <c>asp-validation-for</c> span, and Reschedule.cshtml does the same with Input.Date;
    /// both summaries are <c>ModelOnly</c>, which renders only the entries keyed by <see cref="string.Empty"/>.
    /// A binder or validator failure on one of them therefore answered HTTP 200 with a rebuilt form, no write and no
    /// message at all (T27 D1). Input.StartTime is deliberately absent: it is a visible select with its own span, so
    /// its message is already on screen and must not be duplicated.
    /// </summary>
    private static readonly (string Field, string Message)[] HiddenSelectorFields =
    [
        ("Input.ServiceId", "Elegí un servicio."),
        ("Input.WorkerId", "Elegí una trabajadora."),
        ("Input.Date", "Elegí un día.")
    ];

    /// <summary>
    /// Copies the error of a hidden selector into a model-level entry so the <c>ModelOnly</c> summary renders and
    /// announces it. Only these keys are lifted, so a message that already has a visible span is never mirrored into
    /// the summary and the summary carries no tenant, booking or client identity. The field entry itself is kept for
    /// the binder and for any future span. The lifted text is a fixed Spanish message, never the framework's own
    /// binder text, which is not localised.
    /// </summary>
    private void LiftHiddenSelectorErrors()
    {
        foreach (var (field, message) in HiddenSelectorFields)
            if (ModelState.TryGetValue(field, out var entry) && entry.Errors.Count > 0)
                ModelState.AddModelError(string.Empty, message);
    }

    /// <summary>
    /// Business-rule codes safe to show verbatim: they describe an invalid choice on the owner's own booking, not
    /// whether it exists. Not-found/unauthorized codes (<c>owner_booking_not_found</c>, <c>owner_booking_unavailable</c>)
    /// stay behind <see cref="RescheduleErrorMessage"/> so a stale or cross-tenant request never leaks existence.
    /// </summary>
    private static readonly HashSet<string> RevealableRescheduleErrorCodes = new(StringComparer.Ordinal)
    {
        "owner_reschedule_invalid_time",
        "owner_reschedule_same_slot",
        "owner_reschedule_slot_unavailable"
    };

    private async Task<IActionResult> BuildBookingFormAsync(
        OwnerBookingCreateInput input, CancellationToken ct, DateTime? conflictAlternative = null)
    {
        var form = await bookingActionsService.GetBookingFormAsync(
            BusinessId,
            User.GetBusinessSlug(),
            input.ServiceId,
            input.WorkerId,
            input.Date,
            ct);
        if (form is null)
            return NotFound();

        ViewData["Title"] = "Anotar turno";
        return View("NewBooking", new AgendaBookingFormViewModel(form, input, conflictAlternative));
    }

    private void SetPrivateResponseHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static TEnum? ParseEnum<TEnum>(string? value) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;
}
