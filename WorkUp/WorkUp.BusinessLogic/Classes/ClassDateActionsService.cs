using System.Globalization;
using System.Net;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Owner actions over one date of a generated class (RF-10, RF-22, D-15, D-24): cancelling exactly one annotation of
/// that date and suspending the whole class session with a mandatory reason and one notice per student.
/// <para>
/// <b>One unit, one clock read, one lock order.</b> Each action is one transaction that commits exactly once and every
/// rejection returns before that commit, so the transaction is disposed and rolls back with nothing written (when the
/// service is composed inside an outer transaction, whose opener decides whether the unit lands, no path leaves a
/// persistent write visible while answering a failure: every rejection happens before the first write of that path).
/// The clock is read exactly once per call, <b>after</b> the class lock is held, and that single instant decides both
/// the rule and every written timestamp: waiting for the lock can never be judged with a stale time, so a class that
/// starts while the operation waits is refused instead of cancelled. Both actions take the <c>Appointments</c> row of
/// the class first and only then touch the annotations — the class lock of the README, the same order the per-student
/// attendance uses — so a cancellation and a suspension of the same class serialize on that row instead of
/// interleaving, and the guarded writes repeat the state, the time and the tenant they judged.
/// </para>
/// <para>
/// <b>Cancelling a date</b> writes only that annotation: <c>Cancelado</c>, <c>CancelledBy = Owner</c> and the instant.
/// The enrollment, the fixed grid, the membership periods and every payment stay exactly as they were, and nothing is
/// refunded or credited (RF-22, D-24). Any annotation that is not cancelled can be cancelled while the class is
/// <c>Programado</c> and has not started — a fixed or a drop-in one, and even one already recorded as attended or
/// absent, because the recorded status is what stops counting once the annotation is cancelled. From the start of the
/// class on, the absence is the only record left (RF-22 error handling), and a suspended class has nothing to cancel
/// (RF-10). The place is released by the status itself: the occupancy rule of <see cref="ClassCapacity"/> counts every
/// non-cancelled annotation, and this service adds no count of its own.
/// </para>
/// <para>
/// <b>Suspending a class</b> writes the session row only: <c>Suspendido</c>, <c>SuspendedAt</c> and the trimmed reason.
/// Neither the grid nor the annotations are touched, nobody is marked absent, the class is never resumed, and no
/// refund, credit or recovery is generated (RF-10, Regla 17, D-15, D-24). Then one <c>ClassSuspended</c> email is
/// queued per non-cancelled annotation, fixed and drop-in alike, with the stable key
/// <c>suspension:appointment:{appointmentId}:booking:{bookingId}</c> and with no management link, because a class has
/// no private link; the notice carries the reason and the class date and time, HTML-encoded. A class without
/// annotations is suspended without notices, and a repeated request is refused by the guarded write before any notice
/// is composed, so a double submission or a technical retry never sends the same notice twice.
/// </para>
/// </summary>
public sealed class ClassDateActionsService(
    IClassDateActionsRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>Stable codes of the cancellation and the suspension (RF-10, RF-22). They are the caller's contract.</summary>
    private const string BookingNotFoundCode = "class_date_booking_not_found";

    private const string ClassNotFoundCode = "class_date_class_not_found";

    private const string NotAvailableCode = "class_date_not_available";

    private const string AlreadyCancelledCode = "class_date_booking_cancelled";

    private const string InvalidReasonCode = "class_date_invalid_reason";

    private const string BookingNotFoundMessage = "No encontramos esa anotación de la clase.";

    private const string ClassNotFoundMessage = "No encontramos esa clase.";

    private const string NotAvailableMessage =
        "La clase ya empezó o está suspendida: esa acción ya no está disponible.";

    private const string AlreadyCancelledMessage = "Esa anotación ya estaba cancelada: el lugar ya fue liberado.";

    private const string InvalidReasonMessage = "Ingresá un motivo de hasta 300 caracteres.";

    /// <summary>
    /// Cancels exactly one annotation of a generated class date before its class starts (RF-22). A missing identifier is
    /// answered like a missing annotation, before any storage call and before the transaction exists; a foreign,
    /// missing or non-class annotation is not found; an annotation of a started or suspended class, and an annotation
    /// that was already cancelled, are each refused with their own stable code and never rewritten.
    /// </summary>
    public async Task<Result> CancelBookingAsync(int businessId, int bookingId, CancellationToken ct = default)
    {
        // A missing identifier is answered like a missing annotation: there is no annotation of the business to
        // cancel, and the caller must not be able to tell "you did not name it" from "it is not yours".
        if (businessId <= 0 || bookingId <= 0)
            return Failure(BookingNotFoundCode, BookingNotFoundMessage);

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // The class session row is locked first and the annotation row second, inside the caller's transaction. A
        // booking of another business is simply not found, so nothing about another tenant's rows leaks through the
        // answer.
        var state = await repository.LockBookingAsync(businessId, bookingId, ct);
        if (state is null
            || state.BusinessId != businessId
            || state.BookingId != bookingId
            || state.Kind != AppointmentKind.ClassSession
            || state.Modality is not (BookingModality.Fixed or BookingModality.DropIn))
            return Failure(BookingNotFoundCode, BookingNotFoundMessage);

        // The single clock read of the operation, taken once the class lock is held: the availability rule and the
        // instant that gets persisted derive from this value, so waiting for the lock cannot cancel a class that
        // already started, and a clock that advances mid-call cannot split the judgement from the write.
        var now = clock.Now;

        // The availability of the class is judged before the state of the annotation, so the caller hears what it can
        // do with this annotation (nothing, the class is gone) before the report of what already happened to it. The
        // rule is the shared one: `Programado` and strictly before its start.
        if (!ClassCapacity.IsBookable(state.AppointmentStatus, state.StartsAt, now))
            return Failure(NotAvailableCode, NotAvailableMessage);

        if (state.BookingStatus == BookingStatus.Cancelled)
            return Failure(AlreadyCancelledCode, AlreadyCancelledMessage);

        // The guarded statement repeats every rule inside one atomic update. A row that stopped matching between the
        // lock and the update is left untouched and reported as "no row changed", and that is a state that no longer
        // admits the cancellation.
        if (!await repository.CancelBookingAsync(businessId, bookingId, now, ct))
            return Failure(NotAvailableCode, NotAvailableMessage);

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Suspends the whole class of one date, before its class starts and while it is still <c>Programado</c>, and
    /// queues one <c>ClassSuspended</c> notice per non-cancelled annotation (RF-10). The reason is mandatory, trimmed
    /// and at most 300 characters; it is validated before the transaction exists, so an invalid reason never touches
    /// storage. The suspension is written before any notice is composed, so a submission that the guarded write
    /// refuses — a class already suspended, for instance — emits no notice at all.
    /// </summary>
    public async Task<Result<ClassSuspensionOutcome>> SuspendClassAsync(
        int businessId, int appointmentId, string? reason, CancellationToken ct = default)
    {
        // A missing identifier is answered like a missing class: there is no class of the business to suspend.
        if (businessId <= 0 || appointmentId <= 0)
            return Failure<ClassSuspensionOutcome>(ClassNotFoundCode, ClassNotFoundMessage);

        // The reason rule is shared with the screen that will collect it: the trimmed text must be present and fit the
        // stored column. Validating before the transaction keeps an invalid request away from storage entirely.
        var normalizedReason = ClassSuspensionInput.NormalizeReason(reason);
        if (!ClassSuspensionInput.IsValid(normalizedReason))
            return Failure<ClassSuspensionOutcome>(InvalidReasonCode, InvalidReasonMessage);

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // The class lock of the README: the Appointments row is taken first and held until the unit ends, and the
        // annotations are read under it, so a cancellation of one of them cannot land between this read and the
        // suspension write. A session of another business is simply not found.
        var session = await repository.LockSessionAsync(businessId, appointmentId, ct);
        if (session is null
            || session.BusinessId != businessId
            || session.AppointmentId != appointmentId
            || session.Kind != AppointmentKind.ClassSession)
            return Failure<ClassSuspensionOutcome>(ClassNotFoundCode, ClassNotFoundMessage);

        // The single clock read of the operation, taken once the class lock is held: the availability rule, the
        // suspension instant and every notice instant derive from this value.
        var now = clock.Now;

        // A suspended class is not suspended twice and a class that already started is not suspended at all: the
        // shared rule is `Programado` and strictly before the start (RF-10, RF-22). A repeated request is refused here,
        // long before a notice could be composed, so a double submission never emits an extra email.
        if (!ClassCapacity.IsBookable(session.Status, session.StartsAt, now))
            return Failure<ClassSuspensionOutcome>(NotAvailableCode, NotAvailableMessage);

        // The guarded write is the fallible step, so it goes before the notices: a row that stopped matching between
        // the lock and the write is reported as "no row changed" with nothing queued and nothing written.
        if (!await repository.SuspendAsync(businessId, appointmentId, normalizedReason, now, ct))
            return Failure<ClassSuspensionOutcome>(NotAvailableCode, NotAvailableMessage);

        // One notice per annotation that still holds its place. The outbox deduplicates by key inside the same
        // transaction, so the count reports what actually landed and a notice that was already stored for the same
        // student and the same class is never sent twice.
        var queued = 0;
        foreach (var booking in session.Bookings)
        {
            if (booking.IsCancelled)
                continue;

            if (await repository.EnqueueNotificationAsync(
                ComposeSuspensionNotice(businessId, booking, session, normalizedReason, now), ct))
                queued++;
        }

        await transaction.CommitAsync(ct);

        return Result<ClassSuspensionOutcome>.Success(new ClassSuspensionOutcome(appointmentId, queued));
    }

    /// <summary>
    /// Composes the suspension notice of one student (RF-10). It carries the reason and the class date and time in
    /// the same shape as the other class emails, and every value is HTML-encoded. It has no management link: a class
    /// has no private link (only an individual turn does), so the notice contains no address of its own.
    /// </summary>
    private static Notification ComposeSuspensionNotice(
        int businessId,
        ClassSuspensionBooking booking,
        ClassSuspensionSession session,
        string reason,
        DateTime now)
    {
        var safeName = WebUtility.HtmlEncode(booking.ClientName);
        var safeReason = WebUtility.HtmlEncode(reason);
        var when = WebUtility.HtmlEncode(session.StartsAt.ToString(
            "dddd d 'de' MMMM 'a las' HH:mm", CultureInfo.GetCultureInfo("es-AR")));

        return new Notification
        {
            BusinessId = businessId,
            Type = NotificationType.ClassSuspended,
            Recipient = booking.ClientEmail,
            Subject = "Clase suspendida",
            BodyHtml = $"<p>Hola {safeName},</p>" +
                       $"<p>La clase del {when} quedó suspendida.</p>" +
                       $"<p>Motivo: {safeReason}</p>",
            // The stable key of the SRS error handling: the same student and the same class never get a second notice,
            // whatever the retry is.
            DedupKey = $"suspension:appointment:{session.AppointmentId}:booking:{booking.BookingId}",
            CreatedAt = now
        };
    }

    private static Result Failure(string code, string message) => Result.Failure(code, message);

    private static Result<T> Failure<T>(string code, string message) => Result<T>.Failure(code, message);
}
