using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Cancellation of the client's own drop-in class annotation from the authenticated client panel (T21.3, RF-22).
/// The caller supplies <c>businessId</c> and <c>clientId</c> straight from the client session cookie; this service
/// never receives an email, a form <c>clientId</c> or a private-link token, never looks a ficha up by email, and never
/// links a booking to an account. Every read and every write is scoped by both identifiers, so a booking of another
/// ficha or another tenant is indistinguishable from one that does not exist.
/// <para>
/// <b>One unit, one clock read, one lock order.</b> The whole operation runs inside one transaction that commits
/// exactly once; every rejection returns before that commit, so the transaction is disposed and rolls back with
/// nothing written. A lock-free, ficha-scoped probe first discovers which class the client's own annotation belongs
/// to, and then the locks are taken in the order the owner's class date actions use — the session <c>Appointments</c>
/// row first and the annotation <c>Bookings</c> row second — so a competing owner action over the same class
/// serializes on the session row instead of interleaving. The clock is read exactly once, <b>after</b> both locks are
/// held: the same instant decides the availability rule and every written timestamp, so a class that starts while the
/// operation waits is refused instead of cancelled.
/// </para>
/// <para>
/// <b>Nothing but the annotation.</b> The cancellation writes <c>Cancelado</c>, <c>CancelledBy = Client</c> and the
/// instant, and nothing else: no payment, no enrollment and no fixed schedule is touched (RF-22, D-24), and no
/// refund, credit or penalty is generated. The freed place is released by the status itself, which is what
/// <see cref="ClassCapacity"/> and <c>UX_Bookings_ActiveClient</c> read, so the same client can book that class
/// again.
/// </para>
/// </summary>
public sealed class ClientClassCancellationService(
    IClassDropInRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>
    /// Stable code of the generic "not found" (T21.3, RF-22): it answers a booking that does not exist, one of
    /// another ficha, one of another tenant, a fixed annotation and an individual turn, all identically, so the panel
    /// never learns whether a booking it does not own exists.
    /// </summary>
    private const string NotFoundCode = "client_class_booking_not_found";

    private const string NotFoundMessage = "No encontramos esa reserva.";

    /// <summary>
    /// Stable code of a booking that is the client's own drop-in but whose state no longer admits the cancellation:
    /// a class that already started (the exact start included) or is suspended, and an annotation that is attended,
    /// absent or already cancelled. The already-cancelled case shares this code on purpose: the client owns the
    /// booking, so nothing has to be hidden from them, and one consistent answer covers every state the cancellation
    /// cannot accept instead of a different code per state.
    /// </summary>
    private const string UnavailableCode = "client_class_booking_unavailable";

    private const string UnavailableMessage = "No se puede cancelar esa reserva ahora.";

    /// <summary>
    /// Cancels the client's own drop-in annotation of a class before its class starts (RF-22). A missing identifier is
    /// answered like a missing booking, before any storage call and before the transaction exists; a foreign ficha,
    /// another tenant, a fixed annotation and an individual turn are not found; a suspended or already started class,
    /// and an annotation that is attended, absent or already cancelled, are refused without writing.
    /// </summary>
    public async Task<Result> CancelOwnDropInAsync(
        int businessId, int clientId, int bookingId, CancellationToken ct = default)
    {
        // A missing identifier is answered like a missing booking: there is no booking of this client inside this
        // business to cancel, and the caller must not be able to tell "you did not name it" from "it is not yours".
        if (businessId <= 0 || clientId <= 0 || bookingId <= 0)
            return Result.Failure(NotFoundCode, NotFoundMessage);

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // Lock-free probe: it only discovers which class the client's own annotation belongs to, so the session row
        // can be locked first. It takes no lock and filters by business and ficha, so a booking of another client or
        // another tenant is simply not found and is indistinguishable from a missing one.
        var appointmentId = await repository.FindOwnBookingSessionIdAsync(businessId, clientId, bookingId, ct);
        if (appointmentId is null)
            return Result.Failure(NotFoundCode, NotFoundMessage);

        // The session row is locked first, exactly the class lock of the README and the order the owner class date
        // actions use: a competing owner cancellation or suspension of the same class waits here instead of
        // interleaving with this one.
        var session = await repository.LockSessionAsync(businessId, appointmentId.Value, ct);
        if (session is null
            || session.AppointmentId != appointmentId.Value
            || session.Kind != AppointmentKind.ClassSession)
            return Result.Failure(NotFoundCode, NotFoundMessage);

        // Then the client's own annotation row, scoped by business and ficha. A fixed annotation of the same client is
        // found here and refused below with the same answer as a missing booking.
        var booking = await repository.LockClientDropInAsync(businessId, clientId, bookingId, ct);
        if (booking is null
            || booking.BookingId != bookingId
            || booking.AppointmentId != appointmentId.Value
            || booking.Kind != AppointmentKind.ClassSession
            || booking.Modality != BookingModality.DropIn)
            return Result.Failure(NotFoundCode, NotFoundMessage);

        // The single clock read of the operation, taken once both locks are held: the availability rule and the
        // instant that gets persisted derive from this value, so waiting for the lock cannot cancel a class that
        // already started, and a clock that advances mid-call cannot split the judgement from the write.
        var now = clock.Now;

        // The class must still be `Programado` and strictly before its start (RF-22). The exact start already counts
        // as started, so from that instant on the absence flow applies and there is nothing to cancel.
        if (!ClassCapacity.IsBookable(session.Status, session.StartsAt, now))
            return Result.Failure(UnavailableCode, UnavailableMessage);

        // Only an annotation that still holds its place is cancellable: pending or confirmed. Attended, absent and
        // already cancelled are all refused here, so a double cancel never reaches the write and never overwrites the
        // original instant or author.
        if (booking.Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
            return Result.Failure(UnavailableCode, UnavailableMessage);

        // The guarded statement repeats every rule inside one atomic update, scoped by business, ficha and booking. A
        // row that stopped matching between the lock and the update is left untouched and reported as "no row
        // changed", and that is a state that no longer admits the cancellation.
        if (!await repository.CancelClientDropInAsync(businessId, clientId, bookingId, now, ct))
            return Result.Failure(UnavailableCode, UnavailableMessage);

        await transaction.CommitAsync(ct);
        return Result.Success();
    }
}
