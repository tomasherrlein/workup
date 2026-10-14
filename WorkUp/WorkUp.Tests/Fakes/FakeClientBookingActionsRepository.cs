using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory guarded storage for the client panel confirm/cancel actions (T21.2). Rows are keyed by the trusted
/// (businessId, clientId, bookingId) triple taken from the client session cookie, so a lookup for another ficha or
/// another tenant misses exactly like the scoped <c>WHERE BusinessId = @businessId AND ClientId = @clientId</c> of
/// <c>SqlClientBookingActionsRepository</c>.
/// <para>
/// Every write re-evaluates the guard against the live row, not against the snapshot returned by the lock read,
/// mirroring the guarded <c>UPDATE ... WHERE</c> of the SQL adapter. <see cref="OnLocked"/> runs between those two
/// moments so a test can mutate the row and prove that a lost action writes nothing.
/// </para>
/// </summary>
public sealed class FakeClientBookingActionsRepository : IClientBookingActionsRepository
{
    private readonly Dictionary<(int BusinessId, int ClientId, int BookingId), ClientBookingActionState> _rows = [];
    private readonly HashSet<(int BusinessId, int WorkerId)> _workers = [];

    /// <summary>Runs right after a lock read snapshotted the row and before the guarded write re-reads it.</summary>
    public Action? OnLocked { get; set; }

    /// <summary>Ordered repository calls, so a test can prove the worker lock precedes the booking lock.</summary>
    public List<string> Calls { get; } = [];

    public int ConfirmWriteCount { get; private set; }

    public int CancelWriteCount { get; private set; }

    public void Add(int businessId, int clientId, ClientBookingActionState state)
    {
        _rows[(businessId, clientId, state.BookingId)] = state;
        _workers.Add((businessId, state.WorkerId));
    }

    public void Mutate(
        int businessId, int clientId, int bookingId, Func<ClientBookingActionState, ClientBookingActionState> change)
    {
        var key = (businessId, clientId, bookingId);
        _rows[key] = change(_rows[key]);
    }

    public ClientBookingActionState? Get(int businessId, int clientId, int bookingId) =>
        _rows.GetValueOrDefault((businessId, clientId, bookingId));

    public Task<int?> GetWorkerIdAsync(int businessId, int clientId, int bookingId, CancellationToken ct = default)
    {
        Calls.Add("worker-id");
        var state = Get(businessId, clientId, bookingId);
        return Task.FromResult(state is null ? (int?)null : state.WorkerId);
    }

    public Task<bool> LockWorkerAsync(int businessId, int workerId, CancellationToken ct = default)
    {
        Calls.Add("lock-worker");
        return Task.FromResult(_workers.Contains((businessId, workerId)));
    }

    public Task<ClientBookingActionState?> LockClientBookingAsync(
        int businessId, int clientId, int bookingId, CancellationToken ct = default)
    {
        Calls.Add("lock-booking");
        var snapshot = Get(businessId, clientId, bookingId);
        OnLocked?.Invoke();
        return Task.FromResult(snapshot);
    }

    public Task<bool> ConfirmClientBookingAsync(
        int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default)
    {
        Calls.Add("confirm");
        var state = Get(businessId, clientId, bookingId);
        if (state is null || !ConfirmGuard(state, now))
            return Task.FromResult(false);

        Mutate(businessId, clientId, bookingId, current => current with
        {
            BookingStatus = BookingStatus.Confirmed,
            ConfirmedAt = now
        });
        ConfirmWriteCount++;
        return Task.FromResult(true);
    }

    public Task<bool> CancelClientBookingAsync(
        int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default)
    {
        Calls.Add("cancel");
        var state = Get(businessId, clientId, bookingId);
        if (state is null || !CancelGuard(state, now))
            return Task.FromResult(false);

        Mutate(businessId, clientId, bookingId, current => current with
        {
            BookingStatus = BookingStatus.Cancelled,
            CancelledBy = CancelledBy.Client,
            CancelledAt = now
        });
        CancelWriteCount++;
        return Task.FromResult(true);
    }

    // Mirrors the guarded WHERE of SqlClientBookingActionsRepository on purpose; it stays independent from the
    // service rules so a service that accepts an invalid state is still caught by the refusing guard.
    private static bool IsOwnIndividualTurnBeforeStart(ClientBookingActionState state, DateTime now) =>
        state.AppointmentKind == AppointmentKind.Individual &&
        state.Origin == AppointmentOrigin.Reserved &&
        state.MaxCapacity == 1 &&
        state.Modality is null &&
        state.EnrollmentId is null &&
        state.AppointmentStatus == AppointmentStatus.Scheduled &&
        state.StartsAt > now &&
        state.CancelledAt is null && state.CancelledBy is null &&
        state.AttendanceRecordedAt is null && state.AbsenceSource is null;

    private static bool ConfirmGuard(ClientBookingActionState state, DateTime now) =>
        IsOwnIndividualTurnBeforeStart(state, now) &&
        state.BookingStatus == BookingStatus.Pending && state.ConfirmedAt is null;

    private static bool CancelGuard(ClientBookingActionState state, DateTime now) =>
        IsOwnIndividualTurnBeforeStart(state, now) &&
        ((state.BookingStatus == BookingStatus.Pending && state.ConfirmedAt is null) ||
         (state.BookingStatus == BookingStatus.Confirmed && state.ConfirmedAt is not null));
}
