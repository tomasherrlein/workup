using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// The charge the owner forgot when marking the attendance of a drop-in (RF-11, D-20). T16 declared the limit: a
/// second attendance of the same annotation is refused, and <see cref="PaymentConcept.DropInClass"/> is written
/// nowhere else, so an annotation already <c>Atendido</c> without a charge had no path to its money. This operation is
/// that path — and only that: it adds the payment and inserts no other row, so the attendance status, the absence, the
/// observation and every notification stay exactly as they were.
/// <para>
/// <b>One unit, one lock, one clock read.</b> The operation opens one transaction, takes the lock the attendance path
/// already takes through <see cref="IClassAttendanceRepository.LockAsync"/> — the class session row first and the
/// annotation row second, in that fixed order — judges every rule from that stable read, writes the payment and
/// commits exactly once. A rejection returns before the commit, so the unit is disposed and rolled back and nothing is
/// written. The current time is read exactly once, <b>after</b> the lock is held: the charge instant of the payment and
/// the instant of the answer are that single value, and a wait for the locked row is never recorded as the charge time.
/// </para>
/// <para>
/// <b>What may be charged.</b> The annotation must belong to the business — a foreign or a missing booking is the same
/// <c>not found</c> answer — and be a class session (<see cref="AppointmentKind.ClassSession"/>); its class must be
/// <c>Programado</c>, because a suspended class did not happen and nobody pays for it (RF-10), and a session status the
/// rules do not know is refused by the same check; the annotation must be a <c>Suelta</c>, because a fixed student's
/// money lives in the membership and is never charged through this flow; it must be <c>Atendido</c>, because this charge
/// follows an attendance and never replaces it — a pending, a confirmed or an absent annotation is refused with its own
/// code, and a cancelled one is never revived. Finally the booking must have no effective charge: an annulled payment
/// is not effective, so the owner can annul a wrong charge in the ledger and register the corrected one here.
/// </para>
/// <para>
/// <b>What it writes.</b> One <c>Payments</c> row with <see cref="PaymentConcept.DropInClass"/>, the booking as its
/// concrete origin (<c>CK_Payments_Reference</c>), the client of the annotation, the class list price read under the
/// lock and the agreed amount and optional reason the owner typed — the same amount and reason limits as the
/// at-arrival charge, judged by <see cref="ClassAttendanceInput"/>. No email is queued and none can be: this service
/// has no notification port, so a charge never sends anything (D-07 keeps the attention without a receipt).
/// </para>
/// </summary>
public sealed class ClassDropInChargeService(
    IClassAttendanceRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>Stable codes of the charge-only rules (RF-11, D-20). They are the caller's contract.</summary>
    public const string NotFoundCode = "class_charge_not_found";

    public const string InvalidChargeCode = "class_charge_invalid_charge";

    public const string NotAvailableCode = "class_charge_not_available";

    public const string NotDropInCode = "class_charge_not_drop_in";

    public const string CancelledCode = "class_charge_cancelled";

    public const string NotAttendedCode = "class_charge_not_attended";

    public const string AlreadyChargedCode = "class_charge_already_charged";

    private const string NotFoundMessage = "No encontramos esa anotación de la clase.";

    /// <summary>
    /// Registers the charge of one drop-in annotation that is already marked as present (RF-11, D-20). The identifiers
    /// and the amount and the reason are validated before the transaction exists, so a malformed request never touches
    /// storage; every rejection after that returns before the commit and rolls the unit back.
    /// </summary>
    public async Task<Result<ClassDropInChargeOutcome>> ChargeAsync(
        int businessId, int bookingId, ClassAttendanceCharge charge, CancellationToken ct = default)
    {
        // A missing identifier is answered like a missing annotation: the caller must not be able to tell "you did not
        // name it" from "it is not yours".
        if (businessId <= 0 || bookingId <= 0)
            return Failure(NotFoundCode, NotFoundMessage);

        // Same limits as the at-arrival charge and the stored columns: a positive amount, at most two decimals, within
        // DECIMAL(12, 2), and a reason that fits PriceReason. A null charge is not a valid request for this flow,
        // because the whole operation is the charge; it is answered as an invalid amount instead of throwing.
        if (charge is null || !ClassAttendanceInput.IsChargeValid(charge))
            return Failure(InvalidChargeCode, "Revisá el importe y el motivo del precio.");

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // The annotation row is the lock, taken by the same session-then-booking order the attendance path uses. It
        // carries its session with its status, so the session state judged below cannot move until the unit ends, and
        // a booking of another business is simply not found, so nothing about another tenant's rows leaks.
        var locked = await repository.LockAsync(businessId, bookingId, ct);
        if (locked is null)
            return Failure(NotFoundCode, NotFoundMessage);

        var state = locked.Annotation;
        if (state.BusinessId != businessId
            || state.BookingId != bookingId
            || state.Kind != AppointmentKind.ClassSession)
            return Failure(NotFoundCode, NotFoundMessage);

        // A suspended class did not happen (RF-10): a drop-in does not pay for it, even though its annotation still
        // says `Atendido`, because suspending a class never alters its annotations. Only `Programado` can be charged,
        // so a status the rules do not know is refused here too.
        if (locked.SessionStatus != AppointmentStatus.Scheduled)
            return Failure(NotAvailableCode, "Esa clase está suspendida: no se puede registrar el cobro.");

        // Only the drop-in pays on arrival: a fixed student's money lives in the membership, and an annotation without
        // a modality is not a class annotation the rule can judge.
        if (state.Modality != BookingModality.DropIn)
            return Failure(NotDropInCode, "Sólo se cobra la clase suelta; el abono se registra aparte.");

        if (state.BookingStatus == BookingStatus.Cancelled)
            return Failure(CancelledCode, "Esa anotación está cancelada: el lugar ya fue liberado.");

        // This charge follows an attendance, it never replaces one: a pending, a confirmed or an absent annotation must
        // be marked as present by the attendance path first, and the status is never written from here.
        if (state.BookingStatus != BookingStatus.Attended)
            return Failure(NotAttendedCode, "Sólo se registra el cobro de una clase marcada como presente.");

        // An effective payment of the annotation is never duplicated. An annulled one is not effective, so the owner
        // can correct a wrong charge: annul it in the ledger and register the corrected one here. The read is taken
        // under the lock, so a concurrent charge of the same booking cannot slip in.
        if (await repository.HasEffectivePaymentAsync(businessId, bookingId, ct))
            return Failure(AlreadyChargedCode, "Esa clase suelta ya tiene un cobro vigente.");

        // The single clock read of the operation, taken with the lock held: the charge instant and the instant the
        // answer reports are this same value.
        var now = clock.Now;

        await repository.InsertPaymentAsync(
            new ClassAttendancePayment(
                businessId,
                state.ClientId,
                state.BookingId,
                PaymentConcept.DropInClass,
                charge.Amount,
                state.ListPrice,
                charge.PriceReason,
                now),
            ct);

        await transaction.CommitAsync(ct);

        return Result<ClassDropInChargeOutcome>.Success(new ClassDropInChargeOutcome(
            state.AppointmentId, state.BookingId, state.ClientId, charge.Amount, state.ListPrice, now));
    }

    private static Result<ClassDropInChargeOutcome> Failure(string code, string message) =>
        Result<ClassDropInChargeOutcome>.Failure(code, message);
}

/// <summary>
/// What the charge-only operation reports back (RF-11, D-20): the class session and the annotation it charged, the
/// client, the amount and the list price recorded beside it, and the single instant the charge carries. The session id
/// is what lets the owner return to the class detail of the annotation she just corrected.
/// </summary>
public sealed record ClassDropInChargeOutcome(
    int AppointmentId, int BookingId, int ClientId, decimal Amount, decimal ListPrice, DateTime PaidAt);
