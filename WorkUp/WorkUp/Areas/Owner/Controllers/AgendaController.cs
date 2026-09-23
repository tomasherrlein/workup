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
        return View(new AgendaBookingDetailViewModel(booking, CanReschedule(booking, clock.Now)));
    }

    [HttpGet]
    public async Task<IActionResult> Reschedule(int id, CancellationToken ct = default)
    {
        SetPrivateResponseHeaders();
        var booking = await agendaService.GetIndividualBookingAsync(BusinessId, id, ct);
        if (booking is null || !CanReschedule(booking, clock.Now))
            return NotFound();

        return BuildRescheduleForm(booking, new OwnerRescheduleInput());
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

        if (!CanReschedule(booking, clock.Now))
        {
            ModelState.AddModelError(string.Empty, RescheduleErrorMessage);
            return BuildRescheduleForm(booking, input);
        }

        if (!ModelState.IsValid)
            return BuildRescheduleForm(booking, input);

        var date = input.Date!.Value;
        var startsAt = date.ToDateTime(input.StartTime!.Value);
        var agenda = await agendaService.GetAgendaAsync(BusinessId, new OwnerAgendaFilter(DateOnly.FromDateTime(booking.StartsAt)), ct);
        var originalEntry = agenda.Entries.SingleOrDefault(entry =>
            entry.BookingId == id && entry.Kind == AppointmentKind.Individual && entry.Origin == AppointmentOrigin.Reserved);
        if (originalEntry is null)
        {
            ModelState.AddModelError(string.Empty, RescheduleErrorMessage);
            return BuildRescheduleForm(booking, input);
        }

        var attempt = await rescheduleService.RescheduleIndividualAsync(
            BusinessId, id, new OwnerRescheduleRequest(originalEntry.WorkerId, startsAt), ct);
        if (!attempt.Succeeded)
        {
            ModelState.AddModelError(string.Empty, RescheduleErrorMessage);
            return BuildRescheduleForm(booking, input);
        }

        this.Flash("Turno reprogramado.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet]
    public Task<IActionResult> NewBooking(
        int? serviceId = null,
        int? workerId = null,
        DateOnly? date = null,
        CancellationToken ct = default)
    {
        SetPrivateResponseHeaders();
        var input = new OwnerBookingCreateInput
        {
            ServiceId = serviceId,
            WorkerId = workerId,
            Date = date ?? DateOnly.FromDateTime(clock.Now)
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
            return await BuildBookingFormAsync(input, ct);

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
            return await BuildBookingFormAsync(input, ct);
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

    private IActionResult BuildRescheduleForm(OwnerAgendaBookingDetail booking, OwnerRescheduleInput input)
    {
        ViewData["Title"] = "Reprogramar turno";
        return View("Reschedule", new AgendaBookingRescheduleViewModel(booking, input));
    }

    private static bool CanReschedule(OwnerAgendaBookingDetail booking, DateTime now) =>
        booking.AppointmentKind == AppointmentKind.Individual &&
        booking.Origin == AppointmentOrigin.Reserved &&
        booking.MaxCapacity == 1 &&
        booking.AppointmentStatus == AppointmentStatus.Scheduled &&
        booking.StartsAt > now &&
        booking.CancelledAt is null &&
        booking.CancelledBy is null &&
        booking.AttendanceRecordedAt is null &&
        booking.AbsenceSource is null &&
        ((booking.BookingStatus == BookingStatus.Pending && booking.ConfirmedAt is null) ||
         (booking.BookingStatus == BookingStatus.Confirmed && booking.ConfirmedAt is not null));

    private const string RescheduleErrorMessage =
        "No se pudo reprogramar el turno. El horario puede haberse ocupado o el turno ya no admite cambios. Revisá los datos e intentá de nuevo.";

    private async Task<IActionResult> BuildBookingFormAsync(OwnerBookingCreateInput input, CancellationToken ct)
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
        return View("NewBooking", new AgendaBookingFormViewModel(form, input));
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
