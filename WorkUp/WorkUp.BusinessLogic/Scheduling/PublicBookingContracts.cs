using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Scheduling;

public sealed record BookIndividualAppointmentRequest(
    int ServiceId,
    int WorkerId,
    DateOnly Date,
    TimeOnly StartTime,
    string Email,
    string FirstName,
    string LastName);

/// <summary>Safe public confirmation data. The private token is intentionally never returned by the booking service.</summary>
public sealed record PublicBookingConfirmation(
    int AppointmentId,
    int BookingId,
    DateTime StartsAt,
    DateTime EndsAt,
    string ServiceName,
    string WorkerName,
    decimal AgreedPrice);

public sealed record PublicBookingAttempt(Result<PublicBookingConfirmation> Result, DateTime? AlternativeStart = null)
{
    public bool Succeeded => Result.Succeeded;
    public BusinessError? Error => Result.Error;
    public PublicBookingConfirmation Confirmation => Result.Value;
}

public sealed record PublicBookingBusinessSettings(string Name, int ReminderLeadHours);

public sealed record BookingAvailabilitySnapshot(
    IReadOnlyList<WorkerSchedule> Schedules,
    IReadOnlyList<WorkerBreak> Breaks,
    IReadOnlyList<Appointment> Appointments,
    IReadOnlyList<Booking> Bookings);

/// <summary>Persistence boundary for the public individual-booking transaction.</summary>
public interface IPublicBookingRepository
{
    /// <summary>Acquires the per-worker SQL update lock and returns the active worker in this business.</summary>
    Task<Worker?> LockActiveWorkerAsync(int businessId, int workerId, CancellationToken ct = default);
    Task<Service?> GetActiveIndividualServiceAsync(int businessId, int serviceId, CancellationToken ct = default);
    Task<PublicBookingBusinessSettings?> GetBusinessSettingsAsync(int businessId, CancellationToken ct = default);

    /// <summary>Reloads schedules, breaks, and all blocking appointments/bookings after the worker lock is held.</summary>
    Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly startDate, DateOnly endDate, CancellationToken ct = default);

    Task<Client?> GetClientByEmailAsync(int businessId, string normalizedEmail, CancellationToken ct = default);

    /// <summary>
    /// Reads the persisted ficha by tenant and id for the authenticated client panel. It never creates or mutates a
    /// client, and returns null when that id does not belong to the business.
    /// </summary>
    Task<Client?> GetClientByIdAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>Returns null only when the tenant/email unique key lost a concurrent insert race.</summary>
    Task<int?> TryInsertClientAsync(Client client, CancellationToken ct = default);

    Task<int> InsertAppointmentAsync(Appointment appointment, CancellationToken ct = default);
    Task<int> InsertBookingAsync(Booking booking, CancellationToken ct = default);
    Task<int> InsertAppointmentLinkAsync(AppointmentLink link, CancellationToken ct = default);
    Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default);
}

/// <summary>Builds the externally configured absolute URL for an appointment-link token.</summary>
public interface IPublicAppointmentUrlBuilder
{
    string Build(string token);
}
