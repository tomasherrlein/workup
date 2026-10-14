namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>
/// Persistence boundary for the client panel confirm/cancel actions (T21.2). Every method receives the trusted
/// <c>businessId</c> and <c>clientId</c> of the client session cookie and filters by both, so a booking of another
/// ficha or another tenant looks exactly like a booking that does not exist. The lock reads and the guarded writes
/// run inside the caller's transaction; each write re-evaluates its predicates against the current row.
/// </summary>
public interface IClientBookingActionsRepository
{
    /// <summary>
    /// Worker of the client's own booking, read before any lock so the transaction can take the per-worker padlock
    /// first. <c>null</c> means the booking is not this ficha's inside this tenant, or does not exist.
    /// </summary>
    Task<int?> GetWorkerIdAsync(int businessId, int clientId, int bookingId, CancellationToken ct = default);

    /// <summary>Locks the worker row (<c>UPDLOCK, ROWLOCK</c>) that the README fixes for an individual turn.</summary>
    Task<bool> LockWorkerAsync(int businessId, int workerId, CancellationToken ct = default);

    /// <summary>Locks the client's own booking row and returns its state, or <c>null</c> when it is not theirs.</summary>
    Task<ClientBookingActionState?> LockClientBookingAsync(
        int businessId, int clientId, int bookingId, CancellationToken ct = default);

    /// <summary>Guarded write of a pending confirmation; returns false when the re-checked row no longer matches.</summary>
    Task<bool> ConfirmClientBookingAsync(
        int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default);

    /// <summary>Cancels a pending or confirmed own booking; returns false when the re-checked row no longer matches.</summary>
    Task<bool> CancelClientBookingAsync(
        int businessId, int clientId, int bookingId, DateTime now, CancellationToken ct = default);
}
