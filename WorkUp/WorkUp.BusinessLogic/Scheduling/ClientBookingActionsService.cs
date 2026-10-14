using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>
/// Confirm-intention and cancellation of an individual turn from the authenticated client panel (T21.2, RF-08,
/// RF-22). The caller supplies <c>businessId</c> and <c>clientId</c> straight from the client session cookie; this
/// service never receives an email or a private-link token, never looks a ficha up by email, and never links a
/// booking to an account. It does not reuse the private-link authorization: the private-link path proves ownership
/// with a token, this one proves it with the scoped <c>ClientId</c> filter of every read and write.
/// <para>
/// Both actions run inside one transaction and take the same deterministic locks as owner rescheduling and public
/// booking: the per-worker row first (the padlock the README fixes for an individual turn) and then the booking row,
/// and the write re-checks its predicates against the row it just locked. That keeps a competing reschedule, a
/// private-link action, or a second tap from interleaving and losing an action.
/// </para>
/// <para>
/// A booking of another ficha or another tenant is indistinguishable from one that does not exist, and a booking
/// whose state or start no longer allows the action fails with a stable code and writes nothing. Cancelling never
/// touches payments: RF-22 and D-24 forbid automatic refunds, credits or penalties.
/// </para>
/// </summary>
public sealed class ClientBookingActionsService(
    IClientBookingActionsRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const string NotFoundCode = "client_booking_not_found";
    private const string NotFoundMessage = "No encontramos ese turno.";
    private const string UnavailableCode = "client_booking_unavailable";
    private const string ConfirmUnavailableMessage = "No se puede confirmar este turno.";
    private const string CancelUnavailableMessage = "No se puede cancelar este turno.";

    /// <summary>
    /// Records the client's intention for their own individual turn before its start (RF-08). Repeating a
    /// confirmation is a no-op that keeps the original <c>ConfirmedAt</c>.
    /// </summary>
    public Task<Result> ConfirmIntentionAsync(int businessId, int clientId, int bookingId, CancellationToken ct = default) =>
        ApplyAsync(businessId, clientId, bookingId, ClientBookingAction.Confirm, ct);

    /// <summary>
    /// Cancels the client's own pending or confirmed individual turn before its start, attributed to the client
    /// (RF-22), without mutating payments.
    /// </summary>
    public Task<Result> CancelAsync(int businessId, int clientId, int bookingId, CancellationToken ct = default) =>
        ApplyAsync(businessId, clientId, bookingId, ClientBookingAction.Cancel, ct);

    /// <summary>
    /// True when the booking is the client's own individual reserved turn, still ahead of the clock, on a scheduled
    /// appointment, and with no cancellation, attendance or absence already recorded. Both RF-08 and RF-22 require
    /// strictly before the start: the exact start already counts as started, so it belongs to history.
    /// </summary>
    public static bool IsOwnIndividualTurnBeforeStart(ClientBookingActionState booking, DateTime now) =>
        booking.AppointmentKind == AppointmentKind.Individual &&
        booking.Origin == AppointmentOrigin.Reserved &&
        booking.MaxCapacity == 1 &&
        booking.Modality is null &&
        booking.EnrollmentId is null &&
        booking.AppointmentStatus == AppointmentStatus.Scheduled &&
        booking.StartsAt > now &&
        booking.CancelledAt is null &&
        booking.CancelledBy is null &&
        booking.AttendanceRecordedAt is null &&
        booking.AbsenceSource is null;

    /// <summary>A confirmation already recorded and still consistent (RF-08 step 2).</summary>
    public static bool IsAlreadyConfirmed(ClientBookingActionState booking) =>
        booking.BookingStatus == BookingStatus.Confirmed && booking.ConfirmedAt is not null;

    /// <summary>Confirm is allowed on a pending turn, or is a no-op on an already confirmed one.</summary>
    public static bool CanConfirm(ClientBookingActionState booking, DateTime now) =>
        IsOwnIndividualTurnBeforeStart(booking, now) &&
        ((booking.BookingStatus == BookingStatus.Pending && booking.ConfirmedAt is null) ||
         IsAlreadyConfirmed(booking));

    /// <summary>
    /// Cancel is allowed on a pending or confirmed turn, only when no cancellation, attendance or absence was
    /// recorded already. A booking already paid (`Cobrado`) stays cancellable because cancelling only touches the
    /// booking row and never mutates payments, and `Atendido`, `Ausente` and `Cancelado` are refused.
    /// </summary>
    public static bool CanCancel(ClientBookingActionState booking, DateTime now) =>
        IsOwnIndividualTurnBeforeStart(booking, now) &&
        ((booking.BookingStatus == BookingStatus.Pending && booking.ConfirmedAt is null) ||
         (booking.BookingStatus == BookingStatus.Confirmed && booking.ConfirmedAt is not null));

    private async Task<Result> ApplyAsync(
        int businessId, int clientId, int bookingId, ClientBookingAction action, CancellationToken ct)
    {
        if (businessId <= 0 || clientId <= 0 || bookingId <= 0)
            return NotFound();

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // Worker first, then booking: exactly the order owner rescheduling and public booking use, so this action
        // serializes with a competing reschedule of the same worker instead of interleaving state changes.
        var workerId = await repository.GetWorkerIdAsync(businessId, clientId, bookingId, ct);
        if (workerId is null)
            return NotFound();
        if (!await repository.LockWorkerAsync(businessId, workerId.Value, ct))
            return Unavailable(action);

        var booking = await repository.LockClientBookingAsync(businessId, clientId, bookingId, ct);
        if (booking is null || booking.BusinessId != businessId || booking.BookingId != bookingId)
            return NotFound();
        if (booking.WorkerId != workerId.Value)
            return Unavailable(action);

        var now = clock.Now;
        if (action == ClientBookingAction.Confirm)
        {
            if (!CanConfirm(booking, now))
                return Unavailable(action);

            if (IsAlreadyConfirmed(booking))
            {
                // RF-08 step 2: a new confirmation of an already confirmed turn changes nothing, not even
                // ConfirmedAt. The lock read already proved the state, so there is no guarded write to re-check.
                await transaction.CommitAsync(ct);
                return Result.Success();
            }

            if (!await repository.ConfirmClientBookingAsync(businessId, clientId, bookingId, now, ct))
                return Unavailable(action);
        }
        else
        {
            if (!CanCancel(booking, now))
                return Unavailable(action);

            if (!await repository.CancelClientBookingAsync(businessId, clientId, bookingId, now, ct))
                return Unavailable(action);
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    private static Result NotFound() => Result.Failure(NotFoundCode, NotFoundMessage);

    private static Result Unavailable(ClientBookingAction action) => action == ClientBookingAction.Confirm
        ? Result.Failure(UnavailableCode, ConfirmUnavailableMessage)
        : Result.Failure(UnavailableCode, CancelUnavailableMessage);

    private enum ClientBookingAction
    {
        Confirm,
        Cancel
    }
}
