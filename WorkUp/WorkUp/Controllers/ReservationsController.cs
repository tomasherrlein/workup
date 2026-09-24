using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Infrastructure;
using WorkUp.Models.Reservations;
using WorkUp.ViewModels.Reservations;

namespace WorkUp.Controllers;

[AllowAnonymous]
[Route("r/{slug}")]
public sealed class ReservationsController(
    PublicBookingBrowseService browseService,
    BookingService bookingService,
    ClassDropInService dropInService,
    IBusinessRepository businesses,
    CatalogService catalogService,
    StaffService staffService,
    IClock clock) : Controller
{
    /// <summary>
    /// Forward window of the public drop-in offer (RF-22): today plus the next 30 days. The business service keeps
    /// only the sessions that are <c>Programado</c>, still in the future and with a free place, so the screen never
    /// shows a suspended, started or full class.
    /// </summary>
    private const int DropInWindowDays = 30;

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

    [HttpGet("clases")]
    public async Task<IActionResult> Classes(string slug, int? session, CancellationToken ct)
    {
        var business = await businesses.GetBySlugAsync(slug, ct);
        if (business is null)
            return NotFound();

        var model = await BuildDropInListAsync(business, ct);

        // The form only opens for a session that is actually offered: an id from the query string never becomes a
        // form for a class the business does not publish.
        if (session is { } selected && model.Sessions.Any(option => option.AppointmentId == selected))
            model.OpenAppointmentId = selected;

        SetPublicLayout(ToPublicBusiness(business));
        return View("Classes", model);
    }

    /// <summary>
    /// Books the public drop-in (RF-07 cupo &gt; 1). The offer is read before booking so the confirmation can name the
    /// class and its teacher, and so a rejected booking re-renders the same screen with the typed values. The list is
    /// a read: a rejected request never reaches the guarded insert, so it never writes.
    /// </summary>
    [HttpPost("clases/reservar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookDropIn(string slug, PublicDropInListViewModel model, CancellationToken ct)
    {
        var business = await businesses.GetBySlugAsync(slug, ct);
        if (business is null)
            return NotFound();

        var publicBusiness = ToPublicBusiness(business);
        SetPublicLayout(publicBusiness);

        var list = await BuildDropInListAsync(business, ct);
        var selected = list.Sessions.FirstOrDefault(option => option.AppointmentId == model.AppointmentId);

        var attempt = await dropInService.BookPublicAsync(
            business.Id,
            new DropInBookingRequest(model.AppointmentId, model.Email, model.FirstName, model.LastName, model.Phone),
            ct);

        if (attempt.Succeeded)
        {
            return View("DropIn", new PublicDropInConfirmationViewModel(
                publicBusiness,
                attempt.Value.Date,
                attempt.Value.StartTime,
                selected?.ServiceName ?? "",
                selected?.TeacherName ?? ""));
        }

        // The service owns the stable codes; mapping them onto the same field names the form posted keeps the
        // message next to the right control and, because the whole model comes back, keeps what the person typed.
        ModelState.AddBusinessError(attempt.Error!);
        list.OpenAppointmentId = model.AppointmentId;
        list.AppointmentId = model.AppointmentId;
        list.Email = model.Email;
        list.FirstName = model.FirstName;
        list.LastName = model.LastName;
        list.Phone = model.Phone;
        return View("Classes", list);
    }

    /// <summary>
    /// Builds the public offer: the bookable sessions of the forward window, with the class and teacher names of the
    /// ids the offer carries. A failed listing (an impossible range or a business without id) degrades to an empty
    /// offer instead of breaking the public screen.
    /// </summary>
    private async Task<PublicDropInListViewModel> BuildDropInListAsync(Business business, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.Now);
        var options = await dropInService.ListOptionsAsync(
            business.Id, today, today.AddDays(DropInWindowDays), serviceId: null, ct);

        if (!options.Succeeded)
            return new PublicDropInListViewModel { Business = ToPublicBusiness(business) };

        var services = await catalogService.ListAsync(business.Id, ct);
        var workers = await staffService.ListAsync(business.Id, ct);
        var serviceNames = services.ToDictionary(service => service.Id, service => service.Name);
        var teacherNames = workers.ToDictionary(worker => worker.Id, worker => worker.FullName);

        return new PublicDropInListViewModel
        {
            Business = ToPublicBusiness(business),
            Sessions = options.Value
                .Select(option => new PublicDropInSession(
                    option.AppointmentId,
                    option.Date,
                    option.StartTime,
                    serviceNames.GetValueOrDefault(option.ServiceId, ""),
                    teacherNames.GetValueOrDefault(option.WorkerId, ""),
                    option.RemainingPlaces))
                .ToArray()
        };
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

    private static PublicBookingBusiness ToPublicBusiness(Business business) =>
        new(business.Id, business.Name, business.Slug);

    private void SetPublicLayout(PublicBookingBusiness business)
    {
        ViewData["Title"] = "Reservar turno";
        ViewData["BusinessName"] = business.Name;
        ViewData["HomeUrl"] = Url.Action(nameof(Index), "Reservations", new { slug = business.Slug }) ?? $"/r/{business.Slug}";
    }
}
