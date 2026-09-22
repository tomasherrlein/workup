using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Infrastructure;
using WorkUp.Models.Reservations;

namespace WorkUp.Controllers;

[AllowAnonymous]
[Route("r/{slug}")]
public sealed class ReservationsController(
    PublicBookingBrowseService browseService,
    BookingService bookingService,
    IClock clock) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, CancellationToken ct)
    {
        var catalog = await browseService.GetCatalogAsync(slug, ct);
        if (catalog is null)
            return NotFound();

        SetPublicLayout(catalog.Business);
        return View("Services", new PublicBookingServicesViewModel(catalog.Business, catalog.Services));
    }

    [HttpGet("servicio/{serviceId:int}")]
    public async Task<IActionResult> Workers(string slug, int serviceId, CancellationToken ct)
    {
        var selection = await browseService.GetWorkerSelectionAsync(slug, serviceId, ct);
        if (selection is null)
            return NotFound();

        SetPublicLayout(selection.Business);
        return View("Workers", new PublicBookingWorkersViewModel(
            selection.Business, selection.Service, selection.Workers));
    }

    [HttpGet("servicio/{serviceId:int}/trabajadora/{workerId:int}")]
    public Task<IActionResult> Availability(
        string slug,
        int serviceId,
        int workerId,
        DateOnly? date,
        CancellationToken ct) =>
        RenderAvailabilityAsync(slug, serviceId, workerId, date ?? DateOnly.FromDateTime(clock.Now), null, ct);

    [HttpGet("servicio/{serviceId:int}/trabajadora/{workerId:int}/datos")]
    public async Task<IActionResult> Contact(
        string slug,
        int serviceId,
        int workerId,
        DateOnly date,
        TimeOnly startTime,
        CancellationToken ct)
    {
        var availability = await browseService.GetAvailabilityAsync(slug, serviceId, workerId, date, ct);
        if (availability is null)
            return NotFound();

        if (!availability.AvailableStartTimes.Contains(startTime))
            return await RenderAvailabilityAsync(
                slug, serviceId, workerId, date, "Ese horario ya no está disponible. Elegí otro.", ct);

        SetPublicLayout(availability.Business);
        return View("Contact", CreateContactModel(availability, startTime));
    }

    [HttpPost("reservar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Book(string slug, PublicBookingContactViewModel model, CancellationToken ct)
    {
        var availability = await browseService.GetAvailabilityAsync(
            slug, model.ServiceId, model.WorkerId, model.Date, ct);
        if (availability is null)
            return NotFound();

        SetPublicLayout(availability.Business);
        AttachSelection(model, availability);
        if (!ModelState.IsValid)
            return View("Contact", model);

        if (!availability.AvailableStartTimes.Contains(model.StartTime))
        {
            ModelState.AddModelError(nameof(model.StartTime), "Ese horario ya no está disponible. Elegí otro.");
            model.AlternativeStart = availability.NearestFutureAlternative;
            return View("Contact", model);
        }

        var attempt = await bookingService.BookIndividualAsync(
            availability.Business.Id,
            new BookIndividualAppointmentRequest(
                model.ServiceId,
                model.WorkerId,
                model.Date,
                model.StartTime,
                model.Email,
                model.FirstName,
                model.LastName),
            ct);

        if (attempt.Succeeded)
            return View("Confirmation", new PublicBookingConfirmationViewModel(availability.Business, attempt.Confirmation));

        if (attempt.Error!.Code is "business_unavailable" or "service_unavailable" or "worker_unavailable")
            return NotFound();

        if (attempt.Error.Code == "slot_unavailable")
        {
            ModelState.AddModelError(nameof(model.StartTime), "Ese horario se ocupó recién. Elegí otro.");
            model.AlternativeStart = attempt.AlternativeStart;
            return View("Contact", model);
        }

        ModelState.AddBusinessError(attempt.Error);
        model.AlternativeStart = attempt.AlternativeStart ?? availability.NearestFutureAlternative;
        return View("Contact", model);
    }

    private async Task<IActionResult> RenderAvailabilityAsync(
        string slug,
        int serviceId,
        int workerId,
        DateOnly date,
        string? notice,
        CancellationToken ct)
    {
        var availability = await browseService.GetAvailabilityAsync(slug, serviceId, workerId, date, ct);
        if (availability is null)
            return NotFound();

        SetPublicLayout(availability.Business);
        return View("Availability", new PublicBookingAvailabilityViewModel(
            availability.Business,
            availability.Service,
            availability.Worker,
            availability.Date,
            DateOnly.FromDateTime(clock.Now),
            availability.AvailableStartTimes,
            availability.NearestFutureAlternative,
            notice));
    }

    private static PublicBookingContactViewModel CreateContactModel(
        PublicBookingAvailability availability,
        TimeOnly startTime)
    {
        var model = new PublicBookingContactViewModel
        {
            ServiceId = availability.Service.Id,
            WorkerId = availability.Worker.Id,
            Date = availability.Date,
            StartTime = startTime
        };
        AttachSelection(model, availability);
        return model;
    }

    private static void AttachSelection(
        PublicBookingContactViewModel model,
        PublicBookingAvailability availability)
    {
        model.Business = availability.Business;
        model.Service = availability.Service;
        model.Worker = availability.Worker;
    }

    private void SetPublicLayout(PublicBookingBusiness business)
    {
        ViewData["Title"] = "Reservar turno";
        ViewData["BusinessName"] = business.Name;
        ViewData["HomeUrl"] = Url.Action(nameof(Index), "Reservations", new { slug = business.Slug }) ?? $"/r/{business.Slug}";
    }
}
