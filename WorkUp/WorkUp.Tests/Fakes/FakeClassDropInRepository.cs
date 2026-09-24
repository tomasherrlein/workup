using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

/// <summary>One session of the fake, owned by a business so a foreign tenant can be simulated.</summary>
public sealed record FakeClassDropInSession(int BusinessId, ClassSessionRow Row);

/// <summary>One client of the fake, owned by a business, so a cross-tenant client can be simulated.</summary>
public sealed record FakeClassDropInClient(int BusinessId, DropInClient Client);

/// <summary>
/// In-memory stand-in for the drop-in persistence port. Every call is recorded and the tenant scope is emulated: a
/// session or client registered under another business is invisible to the requested one, exactly like the SQL
/// filters by <c>BusinessId</c>. <see cref="TryInsertReturnsNoPlaceLeft"/>,
/// <see cref="TryInsertReturnsAlreadyBooked"/> and <see cref="TryInsertResultOverride"/> emulate the outcomes of the
/// guarded insert, including the undefined one the real implementation must never produce. A successful insert does
/// not mutate the occupancy of the session, so a test that needs a full class declares its bookings up front.
/// <para>
/// The emulation covers the tenant and date filtering and the transaction outcomes, not the SQL arithmetic: it
/// filters by calendar day instead of building the half-open <c>StartsAt &lt; (toInclusive + 1 day) 00:00</c> bound,
/// so a test that passes here proves the service contract, never that bound.
/// </para>
/// </summary>
public sealed class FakeClassDropInRepository : IClassDropInRepository
{
    public List<FakeClassDropInSession> Sessions { get; } = [];
    public List<FakeClassDropInClient> Clients { get; } = [];

    /// <summary>Drop-ins the guarded insert actually wrote.</summary>
    public List<DropInInsert> Inserts { get; } = [];

    public List<Notification> Notifications { get; } = [];

    public int NextClientId { get; set; } = 1000;
    public int NextBookingId { get; set; } = 500;

    /// <summary>When true the guarded insert reports that no place remained, like a lost race for the last place.</summary>
    public bool TryInsertReturnsNoPlaceLeft { get; set; }

    /// <summary>When true the guarded insert reports the active-client unique key, like a lost double-submit race.</summary>
    public bool TryInsertReturnsAlreadyBooked { get; set; }

    /// <summary>
    /// When set, the guarded insert reports exactly this result instead of inserting. It exists to hand the service a
    /// value that is not one of the three defined outcomes, which the real implementation must never produce.
    /// </summary>
    public DropInInsertResult? TryInsertResultOverride { get; set; }

    public int ListCallCount { get; private set; }
    public int? ListBusinessId { get; private set; }
    public DateOnly? RequestedFrom { get; private set; }
    public DateOnly? RequestedTo { get; private set; }
    public int? RequestedServiceId { get; private set; }

    public int LockCallCount { get; private set; }
    public int? LockBusinessId { get; private set; }
    public int? LockedAppointmentId { get; private set; }

    public int IdentifyCallCount { get; private set; }
    public string? IdentifiedEmail { get; private set; }
    public string? IdentifiedPhone { get; private set; }

    public int FindClientCallCount { get; private set; }

    public int InsertCallCount { get; private set; }

    public int EnqueueCallCount { get; private set; }

    public Task<IReadOnlyList<ClassSessionRow>> ListSessionsAsync(
        int businessId, DateOnly fromInclusive, DateOnly toInclusive, int? serviceId, CancellationToken ct = default)
    {
        ListCallCount++;
        ListBusinessId = businessId;
        RequestedFrom = fromInclusive;
        RequestedTo = toInclusive;
        RequestedServiceId = serviceId;

        // The fake filters by calendar day, which is the shape the SQL query must produce for the inclusive date
        // range, and it never computes the exclusive bound `(toInclusive + 1 day) 00:00`. That is deliberate: it is an
        // emulation of the query shape, not proof of the bound. The service rejects a range ending on 9999-12-31
        // before calling this, so the unrepresentable bound never reaches any implementation.
        IReadOnlyList<ClassSessionRow> rows = Sessions
            .Where(session => session.BusinessId == businessId)
            .Where(session => DateOnly.FromDateTime(session.Row.StartsAt) >= fromInclusive
                && DateOnly.FromDateTime(session.Row.StartsAt) <= toInclusive)
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

    public Task<DropInClient?> FindClientAsync(int businessId, int clientId, CancellationToken ct = default)
    {
        FindClientCallCount++;
        var client = Clients.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId && candidate.Client.ClientId == clientId);

        return Task.FromResult(client?.Client);
    }

    public Task<DropInInsertResult> TryInsertDropInAsync(DropInInsert insert, CancellationToken ct = default)
    {
        InsertCallCount++;

        if (TryInsertResultOverride is { } overridden)
            return Task.FromResult(overridden);

        if (TryInsertReturnsNoPlaceLeft)
            return Task.FromResult(DropInInsertResult.NoPlaceLeft());

        if (TryInsertReturnsAlreadyBooked)
            return Task.FromResult(DropInInsertResult.AlreadyBooked());

        Inserts.Add(insert);
        return Task.FromResult(DropInInsertResult.Inserted(NextBookingId++));
    }

    public Task EnqueueNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        EnqueueCallCount++;
        Notifications.Add(notification);
        return Task.CompletedTask;
    }
}
