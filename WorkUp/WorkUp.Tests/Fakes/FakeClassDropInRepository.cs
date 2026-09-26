using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>One session of the fake, owned by a business so a foreign tenant can be simulated.</summary>
public sealed record FakeClassDropInSession(int BusinessId, ClassSessionRow Row);

/// <summary>One client of the fake, owned by a business, so a cross-tenant client can be simulated.</summary>
public sealed record FakeClassDropInClient(int BusinessId, DropInClient Client);

/// <summary>
/// One cancellable drop-in annotation of the fake, owned by a business so a foreign tenant can be simulated. The
/// cancellation facts are kept beside the locked state so a test can observe what the cancellation wrote, exactly
/// like the <c>CancelledAt</c>, <c>CancelledBy</c> and <c>Status</c> columns of the real row.
/// </summary>
public sealed class FakeClassDropInBooking(int businessId, DropInCancellationState state)
{
    public int BusinessId { get; } = businessId;

    public DropInCancellationState State { get; set; } = state;

    public CancelledBy? CancelledBy { get; set; }

    public DateTime? CancelledAt { get; set; }
}

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

    /// <summary>The cancellable drop-in annotations of the fake, each owned by a business.</summary>
    public List<FakeClassDropInBooking> DropInBookings { get; } = [];

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

    /// <summary>
    /// Optional side effect run while the session lock is being acquired. It emulates wall time passing while the
    /// caller waits for the row lock, so a test can prove that the class is judged at the instant the lock is held
    /// instead of at the instant the call began. The real statement takes the <c>UPDLOCK</c> here; the fake only
    /// replays whatever the test asks for at that point.
    /// </summary>
    public Action? WhileWaitingForLock { get; set; }

    public int RosterCallCount { get; private set; }
    public int? RosterBusinessId { get; private set; }
    public DateTime? RosterFromExclusive { get; private set; }

    public int LockDropInCallCount { get; private set; }
    public int? LockDropInBusinessId { get; private set; }
    public int? LockedDropInBookingId { get; private set; }

    public int CancelCallCount { get; private set; }

    /// <summary>Every cancellation the service asked for, so a test can pin the instant it was given.</summary>
    public List<(int BookingId, DateTime Now)> CancelRequests { get; } = [];

    /// <summary>Calls to the lock-free ficha-scoped probe of the client panel cancellation (T21.3).</summary>
    public int FindOwnProbeCallCount { get; private set; }

    public int? FindOwnProbeBusinessId { get; private set; }

    public int? FindOwnProbeClientId { get; private set; }

    public int? FindOwnProbeBookingId { get; private set; }

    /// <summary>Calls to the ficha-scoped lock read of the client's own annotation.</summary>
    public int LockClientDropInCallCount { get; private set; }

    /// <summary>Calls to the guarded client-side cancellation.</summary>
    public int CancelClientCallCount { get; private set; }

    /// <summary>Every client-side cancellation the service asked for, with the instant it was given.</summary>
    public List<(int BookingId, DateTime Now)> CancelClientRequests { get; } = [];

    /// <summary>
    /// Ordered repository calls of the client panel cancellation, so a test can prove the lock-free probe runs first,
    /// then the session lock and only then the annotation lock. The existing paths append to it too, which does not
    /// change any of their assertions.
    /// </summary>
    public List<string> Calls { get; } = [];

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
        Calls.Add("lock-session");
        LockCallCount++;
        LockBusinessId = businessId;
        LockedAppointmentId = appointmentId;

        // The wait for the row lock happens here, before the locked state is resolved.
        WhileWaitingForLock?.Invoke();

        var session = Sessions.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId && candidate.Row.AppointmentId == appointmentId);

        return Task.FromResult(session?.Row);
    }

    /// <summary>
    /// Emulates the tenant scope and the strictly-future start of the roster query. It deliberately does not filter
    /// the kind nor the modality: those filters are the caller's own guards, and emulating them here would hide a
    /// service that forgot them — exactly like <see cref="ListSessionsAsync"/> does not filter the kind. The SQL
    /// statements do carry both filters, and the opt-in fixture proves them against a real database.
    /// </summary>
    public Task<IReadOnlyList<ClassSessionRow>> ListUpcomingRosterAsync(
        int businessId, DateTime fromExclusive, CancellationToken ct = default)
    {
        RosterCallCount++;
        RosterBusinessId = businessId;
        RosterFromExclusive = fromExclusive;

        IReadOnlyList<ClassSessionRow> rows = Sessions
            .Where(session => session.BusinessId == businessId)
            .Where(session => session.Row.StartsAt > fromExclusive)
            .Select(session => session.Row)
            .ToList();

        return Task.FromResult(rows);
    }

    public Task<DropInCancellationState?> LockDropInForCancellationAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        LockDropInCallCount++;
        LockDropInBusinessId = businessId;
        LockedDropInBookingId = bookingId;

        var booking = DropInBookings.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId && candidate.State.BookingId == bookingId);

        return Task.FromResult(booking?.State);
    }

    /// <summary>
    /// Emulates the guarded cancellation statement, guard included: only a drop-in annotation of a scheduled class
    /// that starts after <paramref name="now"/> and is not cancelled yet is written, and anything else reports that no
    /// row changed. <c>CancelledBy = Owner</c> mirrors the fixed value of the statement; a fake test can pin it, but
    /// only the opt-in fixture proves the stored column.
    /// </summary>
    public Task<bool> CancelDropInAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default)
    {
        CancelCallCount++;
        CancelRequests.Add((bookingId, now));

        var booking = DropInBookings.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId
            && candidate.State.BookingId == bookingId
            && candidate.State.Modality == BookingModality.DropIn
            && candidate.State.Kind == AppointmentKind.ClassSession
            && candidate.State.AppointmentStatus == AppointmentStatus.Scheduled
            && candidate.State.StartsAt > now
            && candidate.State.Status != BookingStatus.Cancelled);
        if (booking is null)
            return Task.FromResult(false);

        booking.State = booking.State with { Status = BookingStatus.Cancelled };
        booking.CancelledAt = now;
        booking.CancelledBy = CancelledBy.Owner;
        ReleasePlace(booking.State.AppointmentId, bookingId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Emulates the lock-free, ficha-scoped probe of the client panel cancellation (T21.3): the session of the
    /// client's own annotation, resolved from the registered session rows, which are the only place the fake keeps the
    /// owning client of a booking. A booking whose session is not registered, or whose annotation belongs to another
    /// ficha, is invisible exactly like the scoped <c>WHERE BusinessId = ... AND ClientId = ...</c> of the real read.
    /// It filters by tenant and ficha but not by modality or kind: those are the service's own guards, and emulating
    /// them here would hide a service that forgot them.
    /// </summary>
    public Task<int?> FindOwnBookingSessionIdAsync(
        int businessId, int clientId, int bookingId, CancellationToken ct = default)
    {
        Calls.Add("probe");
        FindOwnProbeCallCount++;
        FindOwnProbeBusinessId = businessId;
        FindOwnProbeClientId = clientId;
        FindOwnProbeBookingId = bookingId;

        var session = FindOwnSession(businessId, clientId, bookingId);
        return Task.FromResult(session is null ? (int?)null : session.Row.AppointmentId);
    }

    /// <summary>
    /// Emulates the ficha-scoped lock read of the client's own annotation, including the guard that its session is
    /// registered under the same business. It deliberately does not filter the modality nor the kind, so a service
    /// that forgot those guards is caught by its own rules instead of being hidden by the fake.
    /// </summary>
    public Task<DropInCancellationState?> LockClientDropInAsync(
        int businessId, int clientId, int bookingId, CancellationToken ct = default)
    {
        Calls.Add("lock-client-drop-in");
        LockClientDropInCallCount++;

        var session = FindOwnSession(businessId, clientId, bookingId);
        if (session is null)
            return Task.FromResult<DropInCancellationState?>(null);

        var booking = DropInBookings.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId && candidate.State.BookingId == bookingId);
        return Task.FromResult(booking?.State);
    }

    /// <summary>
    /// Emulates the guarded client-side cancellation: only the client's own drop-in annotation, still pending or
    /// confirmed, of a scheduled class that starts after <paramref name="now"/>, is written — and anything else
    /// reports that no row changed. <c>CancelledBy = Client</c> mirrors the fixed value of the statement; a fake test
    /// can pin it, but only the opt-in fixture proves the stored column.
    /// </summary>
    public Task<bool> CancelClientDropInAsync(
        int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default)
    {
        Calls.Add("cancel-client-drop-in");
        CancelClientCallCount++;
        CancelClientRequests.Add((bookingId, now));

        var session = FindOwnSession(businessId, clientId, bookingId);
        if (session is null)
            return Task.FromResult(false);

        var booking = DropInBookings.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId
            && candidate.State.BookingId == bookingId
            && candidate.State.Modality == BookingModality.DropIn
            && candidate.State.Kind == AppointmentKind.ClassSession
            && candidate.State.AppointmentStatus == AppointmentStatus.Scheduled
            && candidate.State.StartsAt > now
            && candidate.State.Status is BookingStatus.Pending or BookingStatus.Confirmed);
        if (booking is null)
            return Task.FromResult(false);

        booking.State = booking.State with { Status = BookingStatus.Cancelled };
        booking.CancelledAt = now;
        booking.CancelledBy = CancelledBy.Client;
        ReleasePlace(booking.State.AppointmentId, bookingId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// The registered session that holds <paramref name="bookingId"/> as an annotation of
    /// <paramref name="clientId"/> inside the business, or <c>null</c>. It is the fake's whole tenant and ficha scope:
    /// the owning client of a booking is only known through the session row it was registered with.
    /// </summary>
    private FakeClassDropInSession? FindOwnSession(int businessId, int clientId, int bookingId) =>
        Sessions.FirstOrDefault(session =>
            session.BusinessId == businessId
            && session.Row.Bookings.Any(booking =>
                booking.BookingId == bookingId && booking.ClientId == clientId));

    /// <summary>
    /// Mirrors the cancelled row into the occupancy of its session, so a later booking sees the released place, just
    /// like a real reader of <c>Bookings</c> would.
    /// </summary>
    private void ReleasePlace(int appointmentId, int bookingId)
    {
        var index = Sessions.FindIndex(session =>
            session.Row.AppointmentId == appointmentId
            && session.Row.Bookings.Any(booking => booking.BookingId == bookingId));
        if (index < 0)
            return;

        var session = Sessions[index];
        var bookings = session.Row.Bookings
            .Select(booking => booking.BookingId == bookingId
                ? booking with { Status = BookingStatus.Cancelled }
                : booking)
            .ToArray();
        Sessions[index] = session with { Row = session.Row with { Bookings = bookings } };
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
