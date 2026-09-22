using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeBookingRepository : IPublicBookingRepository
{
    private int _nextClientId = 1;
    private int _nextAppointmentId = 1;
    private int _nextBookingId = 1;
    private int _nextLinkId = 1;
    private int _nextNotificationId = 1;

    public Worker? Worker { get; set; }
    public Service? Service { get; set; }
    public PublicBookingBusinessSettings? BusinessSettings { get; set; } = new("Test business", 24);
    public BookingAvailabilitySnapshot Snapshot { get; set; } = new([], [], [], []);
    public bool LoseNextClientInsertRace { get; set; }
    public List<Client> Clients { get; } = [];
    public List<Appointment> Appointments { get; } = [];
    public List<Booking> Bookings { get; } = [];
    public List<AppointmentLink> Links { get; } = [];
    public List<Notification> Notifications { get; } = [];
    public List<string> Calls { get; } = [];
    public DateOnly? SnapshotStartDate { get; private set; }
    public DateOnly? SnapshotEndDate { get; private set; }

    public Task<Worker?> LockActiveWorkerAsync(int businessId, int workerId, CancellationToken ct = default)
    {
        Calls.Add("lock-worker");
        return Task.FromResult(Worker is { IsActive: true } worker && worker.BusinessId == businessId && worker.Id == workerId
            ? worker
            : null);
    }

    public Task<Service?> GetActiveIndividualServiceAsync(int businessId, int serviceId, CancellationToken ct = default)
    {
        Calls.Add("service");
        return Task.FromResult(Service is { IsActive: true, MaxCapacity: 1 } service && service.BusinessId == businessId && service.Id == serviceId
            ? service
            : null);
    }

    public Task<PublicBookingBusinessSettings?> GetBusinessSettingsAsync(int businessId, CancellationToken ct = default)
    {
        Calls.Add("business");
        return Task.FromResult(BusinessSettings);
    }

    public Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly startDate, DateOnly endDate, CancellationToken ct = default)
    {
        Calls.Add("availability");
        SnapshotStartDate = startDate;
        SnapshotEndDate = endDate;
        return Task.FromResult(Snapshot);
    }

    public Task<Client?> GetClientByEmailAsync(int businessId, string normalizedEmail, CancellationToken ct = default)
    {
        Calls.Add("find-client");
        return Task.FromResult(Clients.SingleOrDefault(client => client.BusinessId == businessId && client.Email == normalizedEmail));
    }

    public Task<int?> TryInsertClientAsync(Client client, CancellationToken ct = default)
    {
        Calls.Add("insert-client");
        if (LoseNextClientInsertRace || Clients.Any(existing => existing.BusinessId == client.BusinessId && existing.Email == client.Email))
        {
            LoseNextClientInsertRace = false;
            return Task.FromResult<int?>(null);
        }

        client.Id = _nextClientId++;
        Clients.Add(client);
        return Task.FromResult<int?>(client.Id);
    }

    public Task<int> InsertAppointmentAsync(Appointment appointment, CancellationToken ct = default)
    {
        Calls.Add("insert-appointment");
        appointment.Id = _nextAppointmentId++;
        Appointments.Add(appointment);
        return Task.FromResult(appointment.Id);
    }

    public Task<int> InsertBookingAsync(Booking booking, CancellationToken ct = default)
    {
        Calls.Add("insert-booking");
        booking.Id = _nextBookingId++;
        Bookings.Add(booking);
        return Task.FromResult(booking.Id);
    }

    public Task<int> InsertAppointmentLinkAsync(AppointmentLink link, CancellationToken ct = default)
    {
        Calls.Add("insert-link");
        link.Id = _nextLinkId++;
        Links.Add(link);
        return Task.FromResult(link.Id);
    }

    public Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        Calls.Add("insert-notification");
        notification.Id = _nextNotificationId++;
        Notifications.Add(notification);
        return Task.FromResult(notification.Id);
    }
}
