using Microsoft.AspNetCore.Mvc;
using WorkUp.Areas.Owner.Models.Agenda;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>Read-only owner agenda. Tenant identity comes exclusively from OwnerControllerBase.</summary>
public sealed class AgendaController(OwnerAgendaService agendaService, IClock clock) : OwnerControllerBase
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
