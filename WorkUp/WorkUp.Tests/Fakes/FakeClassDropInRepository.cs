using WorkUp.BusinessLogic.Classes;

namespace WorkUp.Tests.Fakes;

/// <summary>One session of the fake, owned by a business so a foreign tenant can be simulated.</summary>
public sealed record FakeClassDropInSession(int BusinessId, ClassSessionRow Row);

/// <summary>One client of the fake, owned by a business, so a cross-tenant client can be simulated.</summary>
public sealed record FakeClassDropInClient(int BusinessId, DropInClient Client);

/// <summary>
/// In-memory stand-in for the drop-in persistence port. Every call is recorded and the tenant scope is emulated: a
/// session or client registered under another business is invisible to the requested one, exactly like the SQL
/// filters by <c>BusinessId</c>. <see cref="TryInsertReturnsNull"/> emulates the guarded insert losing the race for
/// the last place. A successful insert does not mutate the occupancy of the session, so a test that needs a full
/// class declares its bookings up front.
/// </summary>
public sealed class FakeClassDropInRepository : IClassDropInRepository
{
    public List<FakeClassDropInSession> Sessions { get; } = [];
    public List<FakeClassDropInClient> Clients { get; } = [];

    /// <summary>Drop-ins the guarded insert actually wrote.</summary>
    public List<DropInInsert> Inserts { get; } = [];

    public List<DropInNotification> Notifications { get; } = [];

    public int NextClientId { get; set; } = 1000;
    public int NextBookingId { get; set; } = 500;

    /// <summary>When true the guarded insert reports that no place remained, like a lost race.</summary>
    public bool TryInsertReturnsNull { get; set; }

    public int ListCallCount { get; private set; }
    public int? ListBusinessId { get; private set; }
    public DateTime? RequestedFrom { get; private set; }
    public DateTime? RequestedTo { get; private set; }
    public int? RequestedServiceId { get; private set; }

    public int LockCallCount { get; private set; }
    public int? LockBusinessId { get; private set; }
    public int? LockedAppointmentId { get; private set; }

    public int IdentifyCallCount { get; private set; }
    public string? IdentifiedEmail { get; private set; }
    public string? IdentifiedPhone { get; private set; }

    public int ClientExistsCallCount { get; private set; }

    public int InsertCallCount { get; private set; }

    public int EnqueueCallCount { get; private set; }

    public Task<IReadOnlyList<ClassSessionRow>> ListSessionsAsync(
        int businessId, DateTime from, DateTime to, int? serviceId, CancellationToken ct = default)
    {
        ListCallCount++;
        ListBusinessId = businessId;
        RequestedFrom = from;
        RequestedTo = to;
        RequestedServiceId = serviceId;

        IReadOnlyList<ClassSessionRow> rows = Sessions
            .Where(session => session.BusinessId == businessId)
            .Where(session => session.Row.StartsAt >= from && session.Row.StartsAt <= to)
            .Where(session => serviceId is null || session.Row.ServiceId == serviceId)
            .Select(session => session.Row)
            .ToList();

        return Task.FromResult(rows);
    }

    public Task<ClassSessionRow?> LockSessionAsync(int businessId, int appointmentId, CancellationToken ct = default)
    {
        LockCallCount++;
        LockBusinessId = businessId;
        LockedAppointmentId = appointmentId;

        var session = Sessions.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId && candidate.Row.AppointmentId == appointmentId);

        return Task.FromResult(session?.Row);
    }

    public Task<DropInClient> IdentifyClientAsync(
        int businessId, string email, string firstName, string lastName, string? phone, CancellationToken ct = default)
    {
        IdentifyCallCount++;
        IdentifiedEmail = email;
        IdentifiedPhone = phone;

        var existing = Clients.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId &&
            string.Equals(candidate.Client.Email, email, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return Task.FromResult(existing.Client);

        var created = new DropInClient(NextClientId++, email, firstName, lastName);
        Clients.Add(new FakeClassDropInClient(businessId, created));
        return Task.FromResult(created);
    }

    public Task<bool> ClientExistsAsync(int businessId, int clientId, CancellationToken ct = default)
    {
        ClientExistsCallCount++;
        return Task.FromResult(Clients.Any(candidate =>
            candidate.BusinessId == businessId && candidate.Client.ClientId == clientId));
    }

    public Task<int?> TryInsertDropInAsync(DropInInsert insert, CancellationToken ct = default)
    {
        InsertCallCount++;

        if (TryInsertReturnsNull)
            return Task.FromResult<int?>(null);

        Inserts.Add(insert);
        return Task.FromResult<int?>(NextBookingId++);
    }

    public Task EnqueueNotificationAsync(DropInNotification notification, CancellationToken ct = default)
    {
        EnqueueCallCount++;
        Notifications.Add(notification);
        return Task.CompletedTask;
    }
}
