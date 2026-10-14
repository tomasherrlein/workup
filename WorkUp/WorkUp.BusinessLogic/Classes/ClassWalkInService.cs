using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// At-arrival registration of a client who shows up for an already generated class session without a booking (RF-11,
/// RF-12, RF-22, Regla 15, D-07, D-20, D-25). The session must belong to the business, be a <c>ClassSession</c>, be
/// <c>Programado</c> — never suspended — and be inside its arrival window <c>[StartsAt, EndsAt]</c>, because the client
/// who walks in usually arrives once the class has started, whereas a booking is only accepted before the start. The
/// business must admit attention without a booking (Regla 15) and the client must already belong to it: the desk path
/// never creates a client by mistake.
/// <para>
/// The whole operation is one transaction that commits exactly once, under one clock read taken while the session
/// lock is held — never before it, so a wait for the row lock cannot be judged with a stale arrival time. The free place is not
/// recomputed here: the occupancy comes from <see cref="ClassCapacity"/> over the booking statuses of the session the
/// lock returned, and the last place is protected by the same guarded insert and row lock of the public drop-in
/// booking (T14) — the <c>UPDLOCK</c> on <c>Appointments</c> plus <c>TryInsertDropInAsync</c> — so no parallel
/// counting rule and no parallel SQL guard exist. The annotation is written as <c>Suelta</c> in the same shape as the
/// public path, then marked <c>Atendido</c> and charged with <see cref="PaymentConcept.DropInClass"/> through the
/// shared attendance write sequence (<see cref="ClassAttendanceInput"/>), from the single instant this operation read.
/// No email is queued: D-07 keeps the attention without a receipt, and the only class emails belong to the scheduled
/// reminder (T24) and to the suspension notice (T16 unit 4).
/// </para>
/// <para>
/// Every rejection returns before the commit, so the transaction is disposed and rolls back, and the annotation the
/// guarded insert may already have written disappears with it: a refused observation, a lost last place or a guarded
/// status write that changes no row never leaves a partial booking, payment or note. Like the public booking path, the
/// operation owns its transaction; if it is composed inside an outer one, that outer transaction decides whether an
/// earlier write survives a later refusal.
/// </para>
/// </summary>
public sealed class ClassWalkInService(
    IClassDropInRepository dropIns,
    IClassAttendanceRepository attendance,
    IBusinessRepository businesses,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>Stable codes of the walk-in rules (RF-11, RF-12, RF-22, Regla 15). They are the caller's contract.</summary>
    private const string InvalidRequestCode = "class_walk_in_invalid_request";

    private const string NotFoundCode = "class_walk_in_not_found";

    private const string ClientNotFoundCode = "class_walk_in_client_not_found";

    private const string DisabledCode = "class_walk_in_disabled";

    private const string NotAvailableCode = "class_walk_in_not_available";

    private const string OutsideWindowCode = "class_walk_in_outside_window";

    private const string FullCode = "class_walk_in_full";

    private const string AlreadyBookedCode = "class_walk_in_already_booked";

    private const string InvalidChargeCode = "class_walk_in_invalid_charge";

    private const string InvalidNoteCode = "class_walk_in_invalid_note";

    private const string NoteFailedCode = "class_walk_in_note_failed";

    private const string UnavailableCode = "class_walk_in_unavailable";

    private const string NotFoundMessage = "No encontramos esa clase.";

    private const string ClientNotFoundMessage = "No encontramos a esa alumna en tu negocio.";

    private const string DisabledMessage = "Este negocio no admite atención sin turno.";

    private const string NotAvailableMessage = "Esa clase está suspendida: no se puede registrar una atención sin turno.";

    private const string OutsideWindowMessage =
        "La atención sin turno sólo se puede registrar entre el inicio y el fin de la clase.";

    private const string FullMessage = "La clase está completa. Probá con otro día u horario.";

    private const string AlreadyBookedMessage = "Esa alumna ya está anotada en esa clase.";

    private const string UnavailableMessage = "Esa anotación no admite esta acción.";

    /// <summary>
    /// Registers the arrival of an existing client into an existing class session (RF-11): the <c>Suelta</c>
    /// annotation, the <c>Atendido</c> mark and the optional charge and observation are one unit. The identifiers and
    /// the optional charge and note are validated before the transaction exists, so a malformed request never touches
    /// storage; every rejection after that returns before the commit and rolls the unit back.
    /// </summary>
    public async Task<Result<ClassWalkInOutcome>> RegisterAsync(
        int businessId, ClassWalkInRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A missing identifier is answered like a missing record: the caller must not be able to tell "you did not
        // name it" from "it is not yours".
        if (businessId <= 0 || request.AppointmentId <= 0 || request.ClientId <= 0)
            return Failure(InvalidRequestCode, "No pudimos registrar la atención. Volvé a intentar.");

        // The amount and the reason follow exactly the class attendance rule, from the same shared input limits, and
        // the observation the same stored length. Validating here keeps an invalid request away from storage and from
        // the transaction.
        if (!ClassAttendanceInput.IsChargeValid(request.Charge))
            return Failure(InvalidChargeCode, "Revisá el importe y el motivo del precio.");

        if (!ClassAttendanceInput.IsNoteValid(request.Note))
            return Failure(InvalidNoteCode, "La observación no puede superar 1000 caracteres.");

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // Regla 15: the business decides whether attention without a booking exists at all, so it is judged before the
        // client, the class and the seat. A business that vanished is indistinguishable from a foreign one.
        var business = await businesses.GetByIdAsync(businessId, ct);
        if (business is null)
            return Failure(NotFoundCode, NotFoundMessage);

        if (!business.AllowsWalkIns)
            return Failure(DisabledCode, DisabledMessage);

        // The desk path only accepts its own clients (RF-11): resolving the client before the lock keeps the "not
        // found" answer about the client the owner named, not about the class.
        var client = await dropIns.FindClientAsync(businessId, request.ClientId, ct);
        if (client is null)
            return Failure(ClientNotFoundCode, ClientNotFoundMessage);

        // The session row is the lock: the repository takes the update lock and returns the live occupancy, and null
        // when the session is not this business's. Holding it is what makes the count below and the guarded insert of
        // the last place a single serialized step — the same primitive the public drop-in booking uses.
        var session = await dropIns.LockSessionAsync(businessId, request.AppointmentId, ct);
        if (session is null || session.Kind != AppointmentKind.ClassSession)
            return Failure(NotFoundCode, NotFoundMessage);

        // The discriminant is re-judged after the lock, never only in the query: an individual turn is capacity 1 and
        // can also be `Programado`, so a repository that forgot its kind filter must not let it take a class walk-in.
        if (session.Status != AppointmentStatus.Scheduled)
            return Failure(NotAvailableCode, NotAvailableMessage);

        // The single clock read of the operation, taken once the session lock is held: the arrival window, the
        // booked-at instant and the attendance and charge instants all derive from this value, so a clock that
        // advances mid-call cannot split them, and a wait for the lock cannot be judged with a stale arrival time.
        var now = clock.Now;

        // The arrival window includes both ends: the client who arrives as the class starts and the one who arrives
        // as it ends are both late, not early. A session without an end — a hand-built row, never a storage read —
        // has no window to accept.
        if (now < session.StartsAt || now > session.EndsAt)
            return Failure(OutsideWindowCode, OutsideWindowMessage);

        // The client's own annotation is decided before the occupancy, exactly like the public path: she must hear
        // that she is already booked even when her own place is what makes the class look full.
        if (session.Bookings.Any(booking =>
            booking.ClientId == client.ClientId && ClassCapacity.OccupiesPlace(booking.Status)))
            return Failure(AlreadyBookedCode, AlreadyBookedMessage);

        // The single capacity rule of the domain: every non-cancelled annotation, fixed and drop-in alike, occupies a
        // place. The walk-in adds no counting of its own.
        var statuses = session.Bookings.Select(booking => booking.Status).ToArray();
        if (!ClassCapacity.HasPlace(session.MaxCapacity, statuses))
            return Failure(FullCode, FullMessage);

        // The guarded insert is the real protection of the last place: it reports the losing outcome without writing,
        // so the loser rolls back instead of overbooking the class. The annotation it writes is the same `Suelta` of
        // the public flow, and it is created at the instant this operation read.
        var insert = await dropIns.TryInsertDropInAsync(
            new DropInInsert(businessId, session.AppointmentId, client.ClientId, now), ct);
        switch (insert.Result)
        {
            case DropInInsertResultType.Inserted:
                break;
            case DropInInsertResultType.NoPlaceLeft:
                return Failure(FullCode, FullMessage);
            case DropInInsertResultType.AlreadyBooked:
                return Failure(AlreadyBookedCode, AlreadyBookedMessage);
            default:
                // The enum knows exactly three outcomes and has no zero member: any other value is a broken
                // implementation. Failing loud beats reading it as a successful insert with a zero booking id, which
                // would attend an annotation that was never written.
                throw new InvalidOperationException(
                    $"El insert de la atención sin turno devolvió un resultado desconocido: {(int)insert.Result}.");
        }

        // The annotation row is read under the attendance lock of the same unit: it carries the class tariff the
        // charge records beside the agreed amount and the state the write sequence judges. The booking was written by
        // this very transaction, so a lock that cannot resolve it is a broken state, not a race.
        var locked = await attendance.LockAsync(businessId, insert.BookingId, ct);
        if (locked is null || !IsFreshDropIn(businessId, insert.BookingId, locked))
            return Failure(UnavailableCode, UnavailableMessage);

        // No duplicate-charge guard is needed here, unlike the per-student flow: the annotation was written by this
        // very transaction's guarded insert, and no payment can already reference it, so the same client showing up
        // twice is refused earlier as `already_booked` and never as a duplicate charge.

        // The observation, the guarded status and the optional charge are written in the shared order, from the single
        // instant this operation read: every refusal it answers is rolled back by this unit, so a refused note cannot
        // leave the annotation, a status or a payment behind.
        var write = await ClassAttendanceInput.WriteAsync(
            attendance, businessId, locked.Annotation, BookingStatus.Attended, request.Charge, request.Note, now, ct);
        switch (write)
        {
            case ClassAttendanceWriteOutcome.Applied:
                break;
            case ClassAttendanceWriteOutcome.NoteRefused:
                return Failure(NoteFailedCode, "No pudimos guardar la observación. Volvé a intentar.");
            case ClassAttendanceWriteOutcome.StatusRefused:
                return Failure(UnavailableCode, UnavailableMessage);
            default:
                throw new InvalidOperationException(
                    $"La escritura de la atención sin turno devolvió un resultado desconocido: {(int)write}.");
        }

        await transaction.CommitAsync(ct);

        return Result<ClassWalkInOutcome>.Success(new ClassWalkInOutcome(
            session.AppointmentId,
            insert.BookingId,
            client.ClientId,
            session.StartsAt,
            request.Charge?.Amount));
    }

    /// <summary>
    /// Whether the annotation the attendance lock returned is the fresh drop-in this unit just wrote: the same booking
    /// of the same business, a class session, a <c>Suelta</c> annotation still open, and a class that is not suspended.
    /// Everything here is already guaranteed by the lock and the guarded insert; the check exists so a broken or raced
    /// state fails with a stable code instead of attending an annotation the rules never allowed.
    /// </summary>
    private static bool IsFreshDropIn(int businessId, int bookingId, ClassAttendanceLock locked) =>
        locked.SessionStatus != AppointmentStatus.Suspended
        && locked.Annotation.BusinessId == businessId
        && locked.Annotation.BookingId == bookingId
        && locked.Annotation.Kind == AppointmentKind.ClassSession
        && locked.Annotation.Modality == BookingModality.DropIn
        && locked.Annotation.BookingStatus is BookingStatus.Pending or BookingStatus.Confirmed;

    private static Result<ClassWalkInOutcome> Failure(string code, string message) =>
        Result<ClassWalkInOutcome>.Failure(code, message);
}
