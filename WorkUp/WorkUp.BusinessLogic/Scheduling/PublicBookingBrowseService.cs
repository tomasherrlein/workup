using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Scheduling;

public sealed record PublicBookingBusiness(int Id, string Name, string Slug);
public sealed record PublicBookingServiceOption(int Id, string Name, decimal Price, int DurationMinutes);
public sealed record PublicBookingWorkerOption(int Id, string FullName);
public sealed record PublicBookingCatalog(PublicBookingBusiness Business, IReadOnlyList<PublicBookingServiceOption> Services);
public sealed record PublicBookingWorkerSelection(
    PublicBookingBusiness Business,
    PublicBookingServiceOption Service,
    IReadOnlyList<PublicBookingWorkerOption> Workers);
public sealed record PublicBookingAvailability(
    PublicBookingBusiness Business,
    PublicBookingServiceOption Service,
    PublicBookingWorkerOption Worker,
    DateOnly Date,
    IReadOnlyList<TimeOnly> AvailableStartTimes,
    DateTime? NearestFutureAlternative,
    bool IsProvisional = true);

/// <summary>Minimal public worker projection; contact and tax identifiers never leave persistence for public browsing.</summary>
public interface IPublicBookingBrowseRepository
{
    Task<IReadOnlyList<Service>> ListActiveIndividualServicesAsync(int businessId, CancellationToken ct = default);
    Task<IReadOnlyList<PublicBookingWorkerOption>> ListActiveWorkersAsync(int businessId, CancellationToken ct = default);
    Task<PublicBookingWorkerOption?> GetActiveWorkerAsync(int businessId, int workerId, CancellationToken ct = default);
}

/// <summary>Builds tenant-scoped public booking choices and unlocked, provisional availability previews.</summary>
public sealed class PublicBookingBrowseService(
    IBusinessRepository businesses,
    IPublicBookingBrowseRepository browseRepository,
    IPublicBookingRepository bookingRepository,
    IClock clock)
{
    private const int AlternativeSearchDays = 30;

    public async Task<PublicBookingCatalog?> GetCatalogAsync(string slug, CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(slug, ct);
        if (business is null)
            return null;

        var services = await browseRepository.ListActiveIndividualServicesAsync(business.Id, ct);
        return new PublicBookingCatalog(
            ToPublicBusiness(business),
            services
                .Where(service => service.BusinessId == business.Id && service.IsActive && service.IsIndividual)
                .OrderBy(service => service.Name, StringComparer.Ordinal)
                .Select(ToPublicService)
                .ToArray());
    }

    public async Task<PublicBookingWorkerSelection?> GetWorkerSelectionAsync(
        string slug,
        int serviceId,
        CancellationToken ct = default)
    {
        var business = await businesses.GetBySlugAsync(slug, ct);
        if (business is null || serviceId <= 0)
            return null;

        var service = await bookingRepository.GetActiveIndividualServiceAsync(business.Id, serviceId, ct);
        if (!IsUsable(service, business.Id, serviceId))
            return null;

        var workers = await browseRepository.ListActiveWorkersAsync(business.Id, ct);
        return new PublicBookingWorkerSelection(
            ToPublicBusiness(business),
            ToPublicService(service!),
            workers
                .Where(worker => worker.Id > 0)
                .OrderBy(worker => worker.FullName, StringComparer.Ordinal)
                .ToArray());
    }

    public async Task<PublicBookingAvailability?> GetAvailabilityAsync(
        string slug,
        int serviceId,
        int workerId,
        DateOnly date,
        CancellationToken ct = default)
    {
        if (serviceId <= 0 || workerId <= 0 || date < DateOnly.FromDateTime(clock.Now) ||
            date.DayNumber >= DateOnly.MaxValue.DayNumber - AlternativeSearchDays)
            return null;

        var business = await businesses.GetBySlugAsync(slug, ct);
        if (business is null)
            return null;

        var service = await bookingRepository.GetActiveIndividualServiceAsync(business.Id, serviceId, ct);
        if (!IsUsable(service, business.Id, serviceId))
            return null;

        var worker = await browseRepository.GetActiveWorkerAsync(business.Id, workerId, ct);
        if (worker is null || worker.Id != workerId)
            return null;

        var searchEndDate = date.AddDays(AlternativeSearchDays);
        // This intentionally runs outside a transaction and without a worker lock. BookingService rechecks every
        // selection after taking the lock when the customer submits their contact form.
        var snapshot = await bookingRepository.GetAvailabilitySnapshotAsync(business.Id, workerId, date, searchEndDate, ct);
        var now = clock.Now;
        var slots = AvailabilityCalculator.GetAvailableSlots(
                workerId, date, service!, snapshot.Schedules, snapshot.Breaks, snapshot.Appointments, snapshot.Bookings)
            .Where(slot => slot > now)
            .Select(TimeOnly.FromDateTime)
            .ToArray();
        var nearest = FindNearestFutureSlot(workerId, date, searchEndDate, service!, now, snapshot);

        return new PublicBookingAvailability(
            ToPublicBusiness(business),
            ToPublicService(service!),
            worker,
            date,
            slots,
            nearest);
    }

    private static DateTime? FindNearestFutureSlot(
        int workerId,
        DateOnly startDate,
        DateOnly endDate,
        Service service,
        DateTime now,
        BookingAvailabilitySnapshot snapshot)
    {
        var requestedStart = startDate.ToDateTime(new TimeOnly(12, 0));
        DateTime? nearest = null;
        var nearestDistance = long.MaxValue;

        for (var date = startDate; ; date = date.AddDays(1))
        {
            foreach (var slot in AvailabilityCalculator.GetAvailableSlots(
                         workerId, date, service, snapshot.Schedules, snapshot.Breaks, snapshot.Appointments, snapshot.Bookings))
            {
                if (slot <= now)
                    continue;

                var distance = Math.Abs(slot.Ticks - requestedStart.Ticks);
                if (distance < nearestDistance || (distance == nearestDistance && (nearest is null || slot < nearest.Value)))
                {
                    nearest = slot;
                    nearestDistance = distance;
                }
            }

            if (date == endDate)
                return nearest;
        }
    }

    private static bool IsUsable(Service? service, int businessId, int serviceId) =>
        service is { IsActive: true, IsIndividual: true } && service.BusinessId == businessId && service.Id == serviceId;

    private static PublicBookingBusiness ToPublicBusiness(Business business) => new(business.Id, business.Name, business.Slug);

    private static PublicBookingServiceOption ToPublicService(Service service) =>
        new(service.Id, service.Name, service.Price, service.DurationMinutes);
}

/// <summary>Validates a trusted configured public base URL and builds an appointment link without request data.</summary>
public static class PublicAppointmentUrlRules
{
    public static Uri ValidateBaseUrl(string configuredBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredBaseUrl) || configuredBaseUrl != configuredBaseUrl.Trim() ||
            !Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment) || configuredBaseUrl.Contains('?') || configuredBaseUrl.Contains('#') ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)))
            throw new ArgumentException("WorkUp:PublicBaseUrl must be an absolute HTTPS URL (HTTP is allowed only for loopback development).", nameof(configuredBaseUrl));

        return new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/"
        }.Uri;
    }

    public static Uri ValidateBaseUrl(string configuredBaseUrl, bool isDevelopment)
    {
        var baseUri = ValidateBaseUrl(configuredBaseUrl);
        if (!isDevelopment && (baseUri.Scheme != Uri.UriSchemeHttps || baseUri.IsLoopback))
            throw new ArgumentException(
                "WorkUp:PublicBaseUrl must use HTTPS with a non-loopback host outside Development.",
                nameof(configuredBaseUrl));

        return baseUri;
    }

    public static string Build(string configuredBaseUrl, string token)
    {
        var validatedBaseUrl = ValidateBaseUrl(configuredBaseUrl);
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("An appointment token is required.", nameof(token));

        return new Uri(validatedBaseUrl, "t/" + Uri.EscapeDataString(token)).AbsoluteUri;
    }
}
