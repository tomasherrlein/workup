using Microsoft.AspNetCore.Mvc;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Infrastructure;
using WorkUp.Infrastructure.Auth;
using WorkUp.ViewModels.ClientPanel;

namespace WorkUp.Controllers;

/// <summary>
/// Reservation screens of the authenticated client panel (T21.5, RF-07, RF-22): an individual turn and a drop-in
/// class. Every read and every write is scoped to the tenant and the ficha taken exclusively from the client session
/// cookie through the inherited properties of <see cref="ClientPanelControllerBase"/>, so no action accepts a
/// business or client id from the route, the query or a form and one session can never reserve for another ficha or
/// tenant. The catalogue is read by <c>User.GetBusinessSlug()</c>, never by the request slug. The base guard already
/// refuses a session in another business's route before an action runs.
/// <para>
/// The individual screen offers the active individual services, the active workers of that service and the free
/// fifteen-minute starts of the chosen day through <see cref="PublicBookingBrowseService"/>. Its save is a POST-only
/// antiforgery route that calls <see cref="BookingService.BookIndividualForClientAsync"/> only: the request carries
/// just the service, the worker, the day and the time, and the persisted ficha supplies the rest. The drop-in screen
/// lists the future sessions with a free place through <see cref="ClassDropInService.ListOptionsAsync"/> and books
/// with the existing <see cref="ClassDropInService.BookForClientAsync"/>.
/// </para>
/// <para>
/// A catalog this tenant cannot see, a missing or foreign ficha and a session of another tenant all answer the same
/// generic 404; a stale slot, a full class and a duplicate are shown as safe feedback on the same screen, rebuilt
/// with the fresh options, so a refused save never leaks existence and never writes.
/// </para>
/// </summary>
[Route("r/{slug}/mis-turnos/reservar")]
public sealed class ClientReservationsController(
    PublicBookingBrowseService browseService,
    BookingService bookingService,
    ClassDropInService dropInService,
    CatalogService catalogService,
    StaffService staffService,
    IClock clock) : ClientPanelControllerBase
{
    /// <summary>Forward window of the panel drop-in offer (RF-22): today plus the next 30 days.</summary>
    private const int DropInWindowDays = 30;

    /// <summary>
    /// Codes that hide the cause behind the same generic 404: a ficha that is not this session's own inside this
    /// tenant, a catalog this tenant cannot see and a business that is not bookable. None of them writes anything.
    /// </summary>
    private static readonly HashSet<string> HiddenErrorCodes = new(StringComparer.Ordinal)
    {
        "client_booking_not_found",
        "business_unavailable",
        "service_unavailable",
        "worker_unavailable",
        "drop_in_client_not_found",
        "drop_in_not_found"
    };

    /// <summary>Stable code of a chosen start that was taken between the read and the save.</summary>
    private const string SlotUnavailableCode = "slot_unavailable";

    /// <summary>
    /// Hidden selectors of this form, paired with the Spanish message the summary shows when their own error would
    /// otherwise stay invisible. Individual.cshtml renders Input.ServiceId, Input.WorkerId and Input.Date as hidden
    /// inputs and gives a span only to Input.StartTime, while the summary is <c>ModelOnly</c>: it renders only the
    /// entries keyed by <see cref="string.Empty"/>. An error on the day therefore answered HTTP 200 with the rebuilt
    /// form and no message at all (T27 D1). The hidden service and worker are non-nullable ids, so an omitted one
    /// never reaches ModelState and is deliberately not listed here.
    /// </summary>
    private static readonly (string Field, string Message)[] HiddenSelectorFields =
    [
        ("Input.Date", "Elegí un día.")
    ];

    /// <summary>
    /// Copies the error of a hidden selector into a model-level entry so the <c>ModelOnly</c> summary renders and
    /// announces it. Only the hidden key is lifted, so the visible Input.StartTime span keeps its own message and
    /// nothing is mirrored field by field into the summary. The field entry is preserved for the binder and the
    /// message is a fixed Spanish string, never the framework's own non-localised binder text.
    /// </summary>
    private void LiftHiddenSelectorErrors()
    {
        foreach (var (field, message) in HiddenSelectorFields)
            if (ModelState.TryGetValue(field, out var entry) && entry.Errors.Count > 0)
                ModelState.AddModelError(string.Empty, message);
    }

    // --- Individual turn ----------------------------------------------------------------------------------------

    /// <summary>
    /// Individual reservation screen of the cookie's business (RF-07). The optional query narrows the shown service,
    /// worker and day; the controller resolves the active ones and previews the free fifteen-minute starts. It is a
    /// pure read: it opens no transaction and writes nothing.
    /// </summary>
    [HttpGet("turno")]
    public async Task<IActionResult> Turno(int? serviceId, int? workerId, DateOnly? date, CancellationToken ct)
    {
        if (serviceId is <= 0 || workerId is <= 0)
            return NotFound();

        var catalog = await browseService.GetCatalogAsync(User.GetBusinessSlug(), ct);
        if (catalog is null)
            return NotFound();

        var input = new ClientIndividualReservationInput
        {
            ServiceId = serviceId ?? 0,
            WorkerId = workerId ?? 0,
            Date = date
        };

        var model = await BuildIndividualFormAsync(catalog, input, ct);
        if (model is null)
            return NotFound();

        return View("Individual", model);
    }

    /// <summary>
    /// Creates the pending individual turn of the session's own ficha (RF-07). The request carries only the chosen
    /// service, worker, day and time; the tenant and the ficha come from the cookie, so there is no email, name or
    /// identity field and the persisted ficha is what supplies the notice recipient. A hidden catalog or a foreign
    /// ficha is the same generic 404; a taken slot flashes a safe message with the nearest alternative and the fresh
    /// options; success redirects to the own agenda.
    /// </summary>
    [HttpPost("turno")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookTurno(
        [Bind(Prefix = "Input")] ClientIndividualReservationInput input, CancellationToken ct)
    {
        var catalog = await browseService.GetCatalogAsync(User.GetBusinessSlug(), ct);
        if (catalog is null)
            return NotFound();

        if (input.Date is null || input.StartTime is null)
        {
            if (input.Date is null)
                ModelState.AddModelError("Input.Date", "Elegí un día.");
            if (input.StartTime is null)
                ModelState.AddModelError("Input.StartTime", "Elegí un horario.");
            LiftHiddenSelectorErrors();
            return await RerenderIndividualAsync(catalog, input, ct);
        }

        var attempt = await bookingService.BookIndividualForClientAsync(
            BusinessId, ClientId, input.ServiceId, input.WorkerId, input.Date.Value, input.StartTime.Value, ct);

        if (attempt.Succeeded)
        {
            this.Flash("Reservaste tu turno.");
            return RedirectToOwnAgenda();
        }

        if (HiddenErrorCodes.Contains(attempt.Error!.Code))
            return NotFound();

        if (attempt.Error.Code == SlotUnavailableCode)
            ModelState.AddModelError("Input.StartTime", "Ese horario se ocupó recién. Elegí otro.");
        else
            ModelState.AddBusinessError(attempt.Error);

        return await RerenderIndividualAsync(catalog, input, ct);
    }

    /// <summary>
    /// Rebuilds the individual screen after a refused save. The options are the fresh ones of the selected day, so
    /// the person chooses again from what is really free; a catalog that stopped being visible answers the same
    /// generic 404 as before.
    /// </summary>
    private async Task<IActionResult> RerenderIndividualAsync(
        PublicBookingCatalog catalog, ClientIndividualReservationInput input, CancellationToken ct)
    {
        var model = await BuildIndividualFormAsync(catalog, input, ct);
        if (model is null)
            return NotFound();

        return View("Individual", model);
    }

    /// <summary>
    /// Resolves the resume of the individual screen: the active services of the catalog, the active workers of the
    /// selected service, the selected day (today or later) and the free fifteen-minute starts. It returns <c>null</c>
    /// only when the selected service is no longer visible for this tenant, which the caller answers as a generic 404.
    /// </summary>
    private async Task<ClientIndividualReservationViewModel?> BuildIndividualFormAsync(
        PublicBookingCatalog catalog, ClientIndividualReservationInput input, CancellationToken ct)
    {
        var slug = User.GetBusinessSlug();
        var services = catalog.Services
            .Select(service => new ClientReservationServiceOption(
                service.Id, service.Name, service.Price, service.DurationMinutes))
            .ToArray();

        var selectedService = input.ServiceId > 0
            ? services.FirstOrDefault(service => service.Id == input.ServiceId)
            : services.FirstOrDefault();
        if (input.ServiceId > 0 && selectedService is null)
            return null;

        var today = DateOnly.FromDateTime(clock.Now);
        if (selectedService is null)
            return new ClientIndividualReservationViewModel(slug, services, [], today, [], input, null);

        var selection = await browseService.GetWorkerSelectionAsync(slug, selectedService.Id, ct);
        if (selection is null)
            return null;

        var workers = selection.Workers
            .Select(worker => new ClientReservationWorkerOption(worker.Id, worker.FullName))
            .ToArray();

        var selectedWorker = input.WorkerId > 0
            ? workers.FirstOrDefault(worker => worker.Id == input.WorkerId)
            : workers.FirstOrDefault();
        if (input.WorkerId > 0 && selectedWorker is null)
            return null;

        if (selectedWorker is null)
            return new ClientIndividualReservationViewModel(slug, services, workers, today, [], input, null);

        var date = input.Date is { } requested && requested >= today ? requested : today;
        input.ServiceId = selectedService.Id;
        input.WorkerId = selectedWorker.Id;
        input.Date = date;

        var availability = await browseService.GetAvailabilityAsync(
            slug, selectedService.Id, selectedWorker.Id, date, ct);
        if (availability is null)
            return new ClientIndividualReservationViewModel(slug, services, workers, date, [], input, null);

        return new ClientIndividualReservationViewModel(
            slug, services, workers, date, availability.AvailableStartTimes, input, availability.NearestFutureAlternative);
    }

    // --- Drop-in class ------------------------------------------------------------------------------------------

    /// <summary>
    /// Drop-in class screen of the cookie's business (RF-22). It lists the future class sessions with a free place of
    /// the next 30 days, with their class and teacher names. It is a pure read: it opens no transaction and writes
    /// nothing.
    /// </summary>
    [HttpGet("clase")]
    public async Task<IActionResult> Classes(CancellationToken ct)
    {
        var model = await BuildDropInListAsync(ct);
        return View("Classes", model);
    }

    /// <summary>
    /// Books the session's own ficha into the chosen drop-in class (RF-22). The appointment comes from the route and
    /// the tenant and the ficha come from the cookie, so there is no identity field in the request. A session of
    /// another tenant, a foreign ficha or a session that does not exist is the same generic 404; a full or duplicate
    /// class flashes safe feedback on the rebuilt screen; success redirects to the own agenda.
    /// </summary>
    [HttpPost("clase/{appointmentId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BookClass(int appointmentId, CancellationToken ct)
    {
        var result = await dropInService.BookForClientAsync(BusinessId, appointmentId, ClientId, ct);
        if (result.Succeeded)
        {
            this.Flash("Te anotaste en la clase suelta.");
            return RedirectToOwnAgenda();
        }

        if (HiddenErrorCodes.Contains(result.Error!.Code))
            return NotFound();

        ModelState.AddBusinessError(result.Error);
        return View("Classes", await BuildDropInListAsync(ct));
    }

    /// <summary>
    /// Builds the panel offer: the bookable sessions of the forward window, with the class and teacher names of the
    /// ids the offer carries, exactly like the public screen. A failed listing degrades to an empty offer instead of
    /// breaking the screen.
    /// </summary>
    private async Task<ClientClassReservationViewModel> BuildDropInListAsync(CancellationToken ct)
    {
        var slug = User.GetBusinessSlug();
        var today = DateOnly.FromDateTime(clock.Now);
        var options = await dropInService.ListOptionsAsync(
            BusinessId, today, today.AddDays(DropInWindowDays), serviceId: null, ct);
        if (!options.Succeeded)
            return new ClientClassReservationViewModel(slug, []);

        var services = await catalogService.ListAsync(BusinessId, ct);
        var workers = await staffService.ListAsync(BusinessId, ct);
        var serviceNames = services.ToDictionary(service => service.Id, service => service.Name);
        var teacherNames = workers.ToDictionary(worker => worker.Id, worker => worker.FullName);

        var sessions = options.Value
            .Select(option => new ClientReservationClassOption(
                option.AppointmentId,
                option.Date,
                option.StartTime,
                serviceNames.GetValueOrDefault(option.ServiceId, ""),
                teacherNames.GetValueOrDefault(option.WorkerId, ""),
                option.RemainingPlaces))
            .ToArray();

        return new ClientClassReservationViewModel(slug, sessions);
    }

    /// <summary>
    /// The target is built from the authenticated slug, never from the route value, so it can never retarget another
    /// tenant.
    /// </summary>
    private IActionResult RedirectToOwnAgenda() =>
        Redirect($"/r/{Uri.EscapeDataString(User.GetBusinessSlug())}/mis-turnos");
}
