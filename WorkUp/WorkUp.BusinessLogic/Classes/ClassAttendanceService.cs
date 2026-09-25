using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Per-student attendance and absence of a generated class session, with the optional drop-in charge (RF-11, RF-12,
/// D-16, D-20, D-23). One operation is one transaction: it locks the annotation with its session, judges the rules
/// from that single read, writes the status — and the charge and the observation when they were asked for — and
/// commits exactly once. Every rejection returns before the commit, so the transaction rolls back and leaves nothing
/// behind. The method shape mirrors the individual path (<c>OwnerDayActionsService</c>), because attendance plus an
/// optional independent charge is the same problem there and here.
/// <para>
/// <b>Attendance</b> is only enabled for a student with current coverage on the class date: a fixed annotation must
/// belong to an enrollment that still holds its places and whose paid periods cover the class date
/// (<see cref="MembershipCalendar.StateOn"/> is <c>Current</c>), so a <c>Pendiente de renovación</c> or a
/// <c>Pendiente de pago</c> student is refused with a stable code (D-16, D-23, Regla 19). A <c>Suelta</c> annotation
/// is always allowed: a drop-in pays on arrival and has no membership to be current. The class date is the session's
/// own date, never the moment of the call, so the same annotation is judged the same way whenever the owner opens
/// the screen.
/// </para>
/// <para>
/// <b>Absence</b> is allowed for any annotation that is not cancelled, including a pending renewal or a pending
/// payment fixed student, because the absence is a fact and never depends on coverage. It is written with
/// <see cref="AbsenceSource.Owner"/> and never overwrites an <c>Atendido</c> annotation. A cancelled annotation is
/// never overwritten and never revived, and a second attendance or a second absence is refused with a stable code
/// instead of silently succeeding, so a double submission is always explicit and can never write the optional charge
/// twice.
/// </para>
/// <para>
/// <b>Charge</b> is only written for a <c>Suelta</c> annotation and only when the owner asked for it, in the same
/// transaction as the attendance but as an independent row (<see cref="PaymentConcept.DropInClass"/>, D-20). An
/// effective payment of that booking is never duplicated; an annulled one is not effective. <b>Observation</b> is
/// mandatory to land: a note that cannot be written fails the whole operation with a stable code and rolls back,
/// exactly like the individual path, so the client's record never loses it silently.
/// </para>
/// </summary>
public sealed class ClassAttendanceService(
    IClassAttendanceRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>Stable codes of the attendance and absence rules (RF-11, RF-12). They are the caller's contract.</summary>
    private const string NotFoundCode = "class_attendance_not_found";

    private const string InvalidChargeCode = "class_attendance_invalid_charge";

    private const string InvalidNoteCode = "class_attendance_invalid_note";

    private const string CancelledCode = "class_attendance_cancelled";

    private const string AlreadyAttendedCode = "class_attendance_already_attended";

    private const string AlreadyAbsentCode = "class_attendance_already_absent";

    private const string WithoutCoverageCode = "class_attendance_without_coverage";

    private const string ChargeNotAllowedCode = "class_attendance_charge_not_allowed";

    private const string AlreadyChargedCode = "class_attendance_already_charged";

    private const string UnavailableCode = "class_attendance_unavailable";

    private const string NoteFailedCode = "class_attendance_note_failed";

    /// <summary>Same limits as the individual charge path and the stored columns (D-20).</summary>
    private const decimal MaxAmount = 9999999999.99m;

    private const int MaxPriceReasonLength = 200;

    private const int MaxNoteLength = 1000;

    /// <summary>
    /// Marks one student as attended (RF-11). The optional charge and the optional observation travel with it and
    /// are written in the same transaction. A missing identifier answers like a missing annotation, before any
    /// storage call and before the transaction exists.
    /// </summary>
    public Task<Result> MarkAttendedAsync(
        int businessId, int bookingId, ClassAttendanceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(businessId, bookingId, attend: true, request, ct);
    }

    /// <summary>
    /// Marks one student as absent (RF-12). It never charges and never notes: the absence is a fact, the observation
    /// belongs to the attendance flow and the money is independent. It is allowed for any annotation that is not
    /// cancelled, including a fixed student pending renewal or pending payment.
    /// </summary>
    public Task<Result> MarkAbsentAsync(int businessId, int bookingId, CancellationToken ct = default) =>
        ExecuteAsync(businessId, bookingId, attend: false, new ClassAttendanceRequest(), ct);

    /// <summary>
    /// The core of both operations. Validation happens before the transaction, so an invalid identifier, an invalid
    /// amount or a note that is too long never opens one. The clock is read exactly once, after every rule passed and
    /// just before the write: the status instant, the charge instant and the note instant derive from that single
    /// value, so a clock that advances mid-call cannot split them.
    /// </summary>
    private async Task<Result> ExecuteAsync(
        int businessId, int bookingId, bool attend, ClassAttendanceRequest request, CancellationToken ct)
    {
        // A missing identifier is answered like a missing annotation: there is no annotation of the business to act
        // on, and the caller must not be able to tell "you did not name it" from "it is not yours".
        if (businessId <= 0 || bookingId <= 0)
            return Failure(NotFoundCode, NotFoundMessage);

        var charge = request.Charge;
        if (charge is not null
            && (charge.Amount <= 0 || charge.Amount > MaxAmount
                || decimal.Round(charge.Amount, 2) != charge.Amount
                || charge.PriceReason?.Length > MaxPriceReasonLength))
            return Failure(InvalidChargeCode, "Revisá el importe y el motivo del precio.");

        if (request.Note?.Length > MaxNoteLength)
            return Failure(InvalidNoteCode, "La observación no puede superar 1000 caracteres.");

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // The annotation row is the lock. It carries its session, and a booking of another business is simply not
        // found, so nothing about another tenant's rows leaks through the answer.
        var state = await repository.LockAsync(businessId, bookingId, ct);
        if (state is null
            || state.BusinessId != businessId
            || state.BookingId != bookingId
            || state.Kind != AppointmentKind.ClassSession
            || state.Modality is not (BookingModality.Fixed or BookingModality.DropIn))
            return Failure(NotFoundCode, NotFoundMessage);

        if (state.BookingStatus == BookingStatus.Cancelled)
            return Failure(CancelledCode, "Esa anotación está cancelada: el lugar ya fue liberado.");

        // Never overwrite a recorded attendance or absence, and never revive one: the same annotation answers the
        // same code whether the caller asks for attendance or absence afterwards.
        if (state.BookingStatus == BookingStatus.Attended)
            return Failure(AlreadyAttendedCode, "Esa alumna ya figura como presente.");

        if (state.BookingStatus == BookingStatus.Absent)
            return Failure(AlreadyAbsentCode, "Esa alumna ya figura como ausente.");

        // Only an open annotation can transition; any other value is a state the rules do not know.
        if (state.BookingStatus is not (BookingStatus.Pending or BookingStatus.Confirmed))
            return Failure(UnavailableCode, "Esa anotación no admite esta acción.");

        if (attend && !HasCurrentCoverage(state))
            return Failure(
                WithoutCoverageCode, "La inscripción no está vigente en la fecha de la clase: renová el abono primero.");

        if (charge is not null)
        {
            // Only a drop-in pays on arrival: a fixed student's money lives in the membership.
            if (state.Modality != BookingModality.DropIn)
                return Failure(ChargeNotAllowedCode, "Sólo se cobra la clase suelta; el abono se registra aparte.");

            // An effective payment of the annotation is never duplicated. An annulled one is not a charge anymore.
            if (await repository.HasEffectivePaymentAsync(businessId, bookingId, ct))
                return Failure(AlreadyChargedCode, "Esa clase suelta ya tiene un cobro vigente.");
        }

        var now = clock.Now;

        var status = attend ? BookingStatus.Attended : BookingStatus.Absent;
        if (!await repository.SetStatusAsync(businessId, bookingId, status, now, ct))
            return Failure(UnavailableCode, "Esa anotación no admite esta acción.");

        if (charge is not null)
            await repository.InsertPaymentAsync(
                new ClassAttendancePayment(
                    businessId,
                    state.ClientId,
                    bookingId,
                    PaymentConcept.DropInClass,
                    charge.Amount,
                    state.ListPrice,
                    charge.PriceReason,
                    now),
                ct);

        if (!string.IsNullOrWhiteSpace(request.Note)
            && !await repository.InsertNoteAsync(
                businessId, state.ClientId, state.AppointmentId, request.Note.Trim(), now, ct))
            return Failure(NoteFailedCode, "No pudimos guardar la observación. Volvé a intentar.");

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Whether the annotation has current coverage on its own class date. A drop-in always does: it pays on arrival
    /// and has no membership. A fixed annotation needs both halves of the rule (D-16, D-21, D-23): the enrollment must
    /// still hold its places — annulled and terminated released them — and its paid periods must cover the class
    /// date. A <c>Pendiente de pago</c> enrollment retains its place but never paid its first period, so it is refused
    /// even if a stored period somehow covered the date; an active enrollment whose periods do not cover it keeps the
    /// place pending the owner's renewal and is refused too (Regla 19).
    /// </summary>
    private static bool HasCurrentCoverage(ClassAttendanceBookingState state)
    {
        if (state.Modality == BookingModality.DropIn)
            return true;

        if (state.EnrollmentStatus is not { } enrollmentStatus
            || !FixedSeatCapacity.OccupiesSeat(enrollmentStatus))
            return false;

        if (enrollmentStatus == EnrollmentStatus.PendingPayment)
            return false;

        return MembershipCalendar.StateOn(state.Periods, state.ClassDate) == MembershipState.Current;
    }

    private static Result Failure(string code, string message) => Result.Failure(code, message);

    private const string NotFoundMessage = "No encontramos esa anotación de la clase.";
}
