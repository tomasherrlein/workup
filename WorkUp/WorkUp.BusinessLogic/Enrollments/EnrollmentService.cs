using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Enrollments;

/// <summary>
/// Fixed-class enrollment and monthly membership (RF-05, RF-20, RF-29, D-16, D-20, D-21, D-23, D-24, Reglas 16-19 and
/// 23). The pending enrollment is registered before any payment and retains its permanent seats from that moment
/// (Regla 23): the first payment activates the membership with the first period of <see cref="MembershipCalendar"/>, a
/// later one renews it — early from the previous expiry, late from the payment day, never retroactively — and every
/// activation or renewal backfills only the missing fixed annotations of generated sessions that have not started.
/// Annulling and terminating release places by closing the validity of the fixed assignments and cancelling the
/// annotations from a date on, never by deleting a row, and the membership list derives every state on read, because
/// "Pendiente de renovación" is never persisted (D-23).
/// <para>
/// Each write is one transaction that commits exactly once: every rejection returns before the commit, so the
/// transaction is disposed and rolls back and a rejected operation never leaves a partial enrollment, payment or
/// annotation. Every operation reads <see cref="IClock.Now"/> exactly once and derives every date from that value, so
/// a clock that advances mid-call cannot split one decision across two instants.
/// </para>
/// </summary>
public sealed class EnrollmentService(
    IEnrollmentRepository repository,
    IMembershipPriceRepository membershipPrices,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const string InvalidRequestCode = "enrollment_invalid_request";
    private const string InvalidFrequencyCode = "enrollment_invalid_frequency";
    private const string InvalidEffectiveDateCode = "enrollment_invalid_effective_date";
    private const string InvalidGridClassesCode = "enrollment_invalid_grid_classes";
    private const string InvalidPriceCode = "enrollment_invalid_price";
    private const string PriceReasonRequiredCode = "enrollment_price_reason_required";
    private const string PriceNotConfiguredCode = "enrollment_price_not_configured";
    private const string ClientNotFoundCode = "enrollment_client_not_found";
    private const string GridClassNotFoundCode = "enrollment_grid_class_not_found";
    private const string GridClassInactiveCode = "enrollment_grid_class_inactive";
    private const string NoSeatCode = "enrollment_no_seat";
    private const string EnrollmentNotFoundCode = "enrollment_not_found";
    private const string EnrollmentNotPendingCode = "enrollment_not_pending";
    private const string EnrollmentNotTerminableCode = "enrollment_not_terminable";
    private const string MembershipNotFoundCode = "membership_not_found";
    private const string MembershipNotChargeableCode = "membership_not_chargeable";
    private const string InvalidAmountCode = "membership_invalid_amount";
    private const string ChargeReasonRequiredCode = "membership_reason_required";

    /// <summary>
    /// Registers the pending enrollment of a student of the business (RF-05 steps 1-3). The agreed price defaults to
    /// the monthly price configured for the weekly frequency and any other amount needs a reason (D-20); the chosen
    /// grid classes must belong to the business, be active and still hold a free permanent seat on the effective date,
    /// judged under the lock the guarded insert also uses.
    /// </summary>
    public async Task<Result<PendingEnrollmentOutcome>> CreatePendingAsync(
        int businessId, CreateEnrollmentRequest request, CancellationToken ct = default)
    {
        if (businessId <= 0 || request is null || request.ClientId <= 0)
            return Result<PendingEnrollmentOutcome>.Failure(
                InvalidRequestCode, "No pudimos identificar la inscripción. Volvé a intentar.");

        if (request.WeeklyFrequency is < 1 or > 7)
            return Result<PendingEnrollmentOutcome>.Failure(
                InvalidFrequencyCode,
                "La frecuencia semanal debe estar entre 1 y 7.",
                nameof(request.WeeklyFrequency));

        if (request.EffectiveFrom == default)
            return Result<PendingEnrollmentOutcome>.Failure(
                InvalidEffectiveDateCode,
                "Elegí una fecha de vigencia válida.",
                nameof(request.EffectiveFrom));

        if (request.GridClassIds is null)
            return Result<PendingEnrollmentOutcome>.Failure(
                InvalidGridClassesCode,
                "Elegí las clases fijas, hasta la frecuencia semanal.",
                nameof(request.GridClassIds));

        // Repeating a grid class would reserve the same seat twice and make the count of chosen classes lie, so the
        // set is normalized before it is validated.
        var gridClassIds = request.GridClassIds.Distinct().ToArray();
        if (gridClassIds.Length == 0
            || gridClassIds.Length > request.WeeklyFrequency
            || gridClassIds.Any(gridClassId => gridClassId <= 0))
            return Result<PendingEnrollmentOutcome>.Failure(
                InvalidGridClassesCode,
                "Elegí al menos una clase fija, hasta la frecuencia semanal.",
                nameof(request.GridClassIds));

        // The single clock read of the operation: the registered instant.
        var now = clock.Now;

        await using var tx = await unitOfWork.BeginAsync(ct: ct);

        var client = await repository.FindClientAsync(businessId, request.ClientId, ct);
        if (client is null)
            return Result<PendingEnrollmentOutcome>.Failure(
                ClientNotFoundCode, "No encontramos a esa alumna en tu negocio.");

        // A frequency without a configured monthly price is not offered when enrolling (plan assumption 4), so it is
        // rejected before any grid class is locked.
        var configured = await membershipPrices.GetByFrequencyAsync(businessId, request.WeeklyFrequency, ct);
        if (configured is null)
            return Result<PendingEnrollmentOutcome>.Failure(
                PriceNotConfiguredCode,
                "Ese plan de abono todavía no tiene precio. Configuralo antes de inscribir.");

        var agreedPrice = request.AgreedPrice ?? configured.Price;
        if (agreedPrice <= 0)
            return Result<PendingEnrollmentOutcome>.Failure(
                InvalidPriceCode, "El precio acordado debe ser mayor a cero.", nameof(request.AgreedPrice));

        var priceReason = string.IsNullOrWhiteSpace(request.PriceReason) ? null : request.PriceReason.Trim();
        if (request.AgreedPrice is { } requestedPrice && requestedPrice != configured.Price && priceReason is null)
            return Result<PendingEnrollmentOutcome>.Failure(
                PriceReasonRequiredCode,
                "Indicá el motivo del monto acordado.",
                nameof(request.PriceReason));

        // The grid class rows are the lock: two enrollments competing for the last seat serialize here, and the seat
        // read below plus the guarded insert happen with those rows held.
        var lockedGridClasses = await repository.LockGridClassesAsync(businessId, gridClassIds, ct);
        if (lockedGridClasses.Count != gridClassIds.Length)
            return Result<PendingEnrollmentOutcome>.Failure(
                GridClassNotFoundCode, "No encontramos alguna de las clases fijas elegidas.");

        if (lockedGridClasses.Any(gridClass => !gridClass.IsActive))
            return Result<PendingEnrollmentOutcome>.Failure(
                GridClassInactiveCode, "Alguna de las clases fijas elegidas está archivada.");

        foreach (var gridClass in lockedGridClasses)
        {
            var seats = await repository.ListFixedSeatsAsync(
                businessId, gridClass.GridClassId, request.EffectiveFrom, ct);
            if (!FixedSeatCapacity.HasSeat(gridClass.MaxCapacity, seats, request.EffectiveFrom))
                return Result<PendingEnrollmentOutcome>.Failure(
                    NoSeatCode, "No queda lugar fijo en alguna de las clases elegidas.");
        }

        var enrollment = new Enrollment
        {
            BusinessId = businessId,
            ClientId = request.ClientId,
            WeeklyFrequency = request.WeeklyFrequency,
            Status = EnrollmentStatus.PendingPayment,
            AgreedPrice = agreedPrice,
            PriceReason = priceReason,
            CreatedAt = now
        };
        var fixedClasses = gridClassIds
            .Select(gridClassId => new EnrollmentFixedClass
            {
                GridClassId = gridClassId,
                ValidFrom = request.EffectiveFrom
            })
            .ToArray();

        var insert = await repository.TryInsertEnrollmentAsync(new EnrollmentInsert(enrollment, fixedClasses), ct);
        switch (insert.Result)
        {
            case EnrollmentInsertResultType.Inserted:
                break;
            case EnrollmentInsertResultType.NoSeatLeft:
                // The guarded insert repeats the seat rule, so losing the race is reported as a state, not as an
                // unhandled exception, and the grid class never gets two students beyond its capacity.
                return Result<PendingEnrollmentOutcome>.Failure(
                    NoSeatCode, "No queda lugar fijo en alguna de las clases elegidas.");
            default:
                throw new InvalidOperationException(
                    $"El insert de la inscripción devolvió un resultado desconocido: {(int)insert.Result}.");
        }

        await tx.CommitAsync(ct);

        return Result<PendingEnrollmentOutcome>.Success(new PendingEnrollmentOutcome(
            insert.EnrollmentId,
            request.ClientId,
            request.WeeklyFrequency,
            agreedPrice,
            request.EffectiveFrom,
            gridClassIds));
    }

    /// <summary>
    /// Charges one month of the membership (RF-05 steps 4-6, RF-29 steps 3-4). The first charge activates the
    /// enrollment with <see cref="MembershipCalendar.FirstPeriod"/>; a later one renews it, continuing from the
    /// previous expiry when it is paid on time and starting on the payment day when it is late, always writing one
    /// payment and exactly one period (<c>UQ_MembershipPeriods_Payment</c>), and then syncs the fixed annotations of
    /// the generated sessions that have not started.
    /// <para>
    /// The enrollment row is locked for the whole operation, so a second concurrent charge waits, reads the period
    /// the first one wrote and renews from that expiry: two real charges extend the coverage month by month instead of
    /// writing two periods that overlap, and the loser never re-reads a stale reference day.
    /// </para>
    /// </summary>
    public async Task<Result<MembershipChargeOutcome>> ChargeAsync(
        int businessId, ChargeMembershipRequest request, CancellationToken ct = default)
    {
        if (businessId <= 0 || request is null || request.EnrollmentId <= 0)
            return Result<MembershipChargeOutcome>.Failure(MembershipNotFoundCode, "No encontramos ese abono.");

        // The single clock read of the payment: the coverage dates, the paid and recorded instants and the sync bound
        // all derive from it.
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);

        await using var tx = await unitOfWork.BeginAsync(ct: ct);

        var state = await repository.LockEnrollmentAsync(businessId, request.EnrollmentId, ct);
        if (state is null)
            return Result<MembershipChargeOutcome>.Failure(MembershipNotFoundCode, "No encontramos ese abono.");

        if (state.Enrollment.Status is not (EnrollmentStatus.PendingPayment or EnrollmentStatus.Active))
            return Result<MembershipChargeOutcome>.Failure(
                MembershipNotChargeableCode,
                "Esa inscripción está anulada o dada de baja: no se puede cobrar.");

        var amount = request.Amount ?? state.Enrollment.AgreedPrice;
        if (amount <= 0)
            return Result<MembershipChargeOutcome>.Failure(
                InvalidAmountCode, "El monto debe ser mayor a cero.", nameof(request.Amount));

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (request.Amount is { } requestedAmount
            && requestedAmount != state.Enrollment.AgreedPrice
            && reason is null)
            return Result<MembershipChargeOutcome>.Failure(
                ChargeReasonRequiredCode,
                "Indicá el motivo del monto cobrado.",
                nameof(request.Reason));

        var activated = state.Enrollment.Status == EnrollmentStatus.PendingPayment;
        MembershipPeriodDates dates;
        if (activated || state.Periods.Count == 0)
        {
            // The first payment activates: the coverage starts on the payment day and its reference day is that day.
            dates = MembershipCalendar.FirstPeriod(today);
        }
        else
        {
            // Renewal: on time it continues from the previous expiry, late it restarts on the payment day. The latest
            // expiry is the one the next month follows, and it is read under the enrollment lock.
            var previousExpiresOn = state.Periods.Max(period => period.ExpiresOn);
            dates = MembershipCalendar.Renewal(
                previousExpiresOn, state.Enrollment.ReferenceDay ?? today.Day, today);
        }

        // The tariff price is recorded next to the amount so the screen can show when the agreed amount differs from
        // the list price (D-20). A frequency without a configured price is not an error here: the enrollment already
        // carries the agreed amount and the list price is simply unknown.
        var configured = await membershipPrices.GetByFrequencyAsync(businessId, state.Enrollment.WeeklyFrequency, ct);

        var payment = new Payment
        {
            BusinessId = businessId,
            ClientId = state.Enrollment.ClientId,
            Concept = PaymentConcept.Membership,
            EnrollmentId = state.Enrollment.Id,
            Amount = amount,
            ListPrice = configured?.Price,
            PriceReason = reason ?? state.Enrollment.PriceReason,
            PaidAt = now,
            RecordedAt = now
        };
        payment.Id = await repository.InsertPaymentAsync(payment, ct);

        var period = new MembershipPeriod
        {
            EnrollmentId = state.Enrollment.Id,
            CoverageStart = dates.CoverageStart,
            ExpiresOn = dates.ExpiresOn,
            PaymentId = payment.Id,
            CreatedAt = now
        };
        period.Id = await repository.InsertMembershipPeriodAsync(period, ct);

        state.Enrollment.Status = EnrollmentStatus.Active;
        if (activated)
            state.Enrollment.ActivatedAt = now;
        state.Enrollment.ReferenceDay = dates.ReferenceDay;
        await repository.UpdateEnrollmentAsync(state.Enrollment, ct);

        // An enrollment without fixed classes has nothing to annotate, so the sync read is skipped instead of asking
        // storage for the sessions of an empty set of grid classes.
        var sync = state.FixedClasses.Count == 0
            ? new EnrollmentSyncOutcome(0, 0, 0, [])
            : await SyncFixedAnnotationsAsync(businessId, state, now, ct);

        await tx.CommitAsync(ct);

        return Result<MembershipChargeOutcome>.Success(new MembershipChargeOutcome(
            state.Enrollment.Id,
            state.Enrollment.ClientId,
            payment.Id,
            period.Id,
            dates.CoverageStart,
            dates.ExpiresOn,
            dates.ReferenceDay,
            amount,
            activated,
            sync));
    }

    /// <summary>
    /// Annuls a pending enrollment (RF-20). Only the owner, only while the enrollment is pending, and without any
    /// automatic expiry: the joined places are released by closing the validity of the fixed assignments and
    /// cancelling the annotations from now on, never by deleting a row and never touching a session that already ran.
    /// </summary>
    public async Task<Result<EnrollmentAnnulmentOutcome>> AnnulPendingAsync(
        int businessId, AnnulEnrollmentRequest request, CancellationToken ct = default)
    {
        if (businessId <= 0 || request is null || request.EnrollmentId <= 0)
            return Result<EnrollmentAnnulmentOutcome>.Failure(
                EnrollmentNotFoundCode, "No encontramos esa inscripción.");

        // The single clock read: the annulment instant and the day the seats are released from.
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);

        await using var tx = await unitOfWork.BeginAsync(ct: ct);

        var state = await repository.LockEnrollmentAsync(businessId, request.EnrollmentId, ct);
        if (state is null)
            return Result<EnrollmentAnnulmentOutcome>.Failure(
                EnrollmentNotFoundCode, "No encontramos esa inscripción.");

        if (state.Enrollment.Status != EnrollmentStatus.PendingPayment)
            return Result<EnrollmentAnnulmentOutcome>.Failure(
                EnrollmentNotPendingCode, "Sólo se puede anular una inscripción pendiente de pago.");

        state.Enrollment.Status = EnrollmentStatus.Annulled;
        state.Enrollment.AnnulledAt = now;
        await repository.UpdateEnrollmentAsync(state.Enrollment, ct);

        // The seat is released from today on, and the annotations of the sessions that have not started are cancelled:
        // the rows stay, so the history of what was held remains readable.
        await repository.CloseFixedClassesAsync(businessId, state.Enrollment.Id, today, ct);
        var cancelled = await repository.CancelFixedAnnotationsAsync(businessId, state.Enrollment.Id, now, ct);

        await tx.CommitAsync(ct);

        return Result<EnrollmentAnnulmentOutcome>.Success(
            new EnrollmentAnnulmentOutcome(state.Enrollment.Id, cancelled));
    }

    /// <summary>
    /// Ends the membership from a chosen effective date (RF-29 step 6, D-19). A pending enrollment can be ended this
    /// way too, because an enrollment that was never paid is one of the ways the owner resolves it. The status becomes
    /// <c>Terminated</c>, the fixed assignments are closed from the effective date on and their annotations are
    /// cancelled from that date on: the earlier dates keep their history untouched.
    /// </summary>
    public async Task<Result<EnrollmentTerminationOutcome>> TerminateAsync(
        int businessId, TerminateEnrollmentRequest request, CancellationToken ct = default)
    {
        if (businessId <= 0 || request is null || request.EnrollmentId <= 0)
            return Result<EnrollmentTerminationOutcome>.Failure(
                EnrollmentNotFoundCode, "No encontramos esa inscripción.");

        if (request.EffectiveFrom == default)
            return Result<EnrollmentTerminationOutcome>.Failure(
                InvalidEffectiveDateCode,
                "Elegí una fecha de vigencia válida.",
                nameof(request.EffectiveFrom));

        // The single clock read: the termination instant.
        var now = clock.Now;

        await using var tx = await unitOfWork.BeginAsync(ct: ct);

        var state = await repository.LockEnrollmentAsync(businessId, request.EnrollmentId, ct);
        if (state is null)
            return Result<EnrollmentTerminationOutcome>.Failure(
                EnrollmentNotFoundCode, "No encontramos esa inscripción.");

        if (state.Enrollment.Status is not (EnrollmentStatus.PendingPayment or EnrollmentStatus.Active))
            return Result<EnrollmentTerminationOutcome>.Failure(
                EnrollmentNotTerminableCode, "Esa inscripción ya está anulada o dada de baja.");

        state.Enrollment.Status = EnrollmentStatus.Terminated;
        state.Enrollment.TerminatedAt = now;
        await repository.UpdateEnrollmentAsync(state.Enrollment, ct);

        await repository.CloseFixedClassesAsync(businessId, state.Enrollment.Id, request.EffectiveFrom, ct);

        // "From that date on": the sessions of the effective day and later are cancelled, and the earlier ones are
        // never touched, so ending a membership cannot rewrite the attendance history of the past.
        var cancelled = await repository.CancelFixedAnnotationsAsync(
            businessId,
            state.Enrollment.Id,
            request.EffectiveFrom.ToDateTime(TimeOnly.MinValue),
            ct);

        await tx.CommitAsync(ct);

        return Result<EnrollmentTerminationOutcome>.Success(
            new EnrollmentTerminationOutcome(state.Enrollment.Id, request.EffectiveFrom, cancelled));
    }

    /// <summary>
    /// The membership list (RF-29). Every state is derived from the paid periods on the reference date and never
    /// persisted (D-23), the "expires soon" flag is the three days before the expiry that RF-30 warns about, and the
    /// annulled and terminated enrollments are listed only by <see cref="EnrollmentListFilter.All"/>, each one with
    /// its own status. Read-only: it opens no transaction and writes nothing.
    /// </summary>
    public async Task<Result<IReadOnlyList<EnrollmentListItem>>> ListAsync(
        int businessId, EnrollmentListFilter filter = EnrollmentListFilter.All, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Result<IReadOnlyList<EnrollmentListItem>>.Failure(
                InvalidRequestCode, "No pudimos identificar el negocio.");

        // The single clock read of the listing: every derived state is judged against the same day.
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);

        var rows = await repository.ListEnrollmentsAsync(businessId, ct);

        var items = rows
            .Select(row => ProjectListItem(row, today))
            .Where(item => MatchesFilter(item, filter))
            .OrderBy(item => item.ClientName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.EnrollmentId)
            .ToArray();

        return Result<IReadOnlyList<EnrollmentListItem>>.Success(items);
    }

    /// <summary>
    /// Creates the fixed annotations the membership is missing in the sessions that were already generated and have
    /// not started (RF-05 step 6, RF-06 step 3). It never modifies an existing annotation, never resurrects a
    /// cancelled one, never annotates a session that already started — the query bound is a convenience, the rule is
    /// judged here — and never overbooks a session, because the capacity is re-read per session and the guarded insert
    /// repeats the seat rule. Future weeks are left to the week generation (T13) and the past is never backfilled.
    /// </summary>
    private async Task<EnrollmentSyncOutcome> SyncFixedAnnotationsAsync(
        int businessId, EnrollmentState state, DateTime now, CancellationToken ct)
    {
        var enrollmentId = state.Enrollment.Id;
        var clientId = state.Enrollment.ClientId;

        var sessions = await repository.ListFixedSyncSessionsAsync(businessId, enrollmentId, now, ct);

        var sessionIds = new List<int>(sessions.Count);
        var toCreate = new List<FixedAnnotationInsert>();
        var omitted = 0;
        var refused = 0;

        foreach (var session in sessions)
        {
            if (session.StartsAt <= now)
                continue;

            sessionIds.Add(session.AppointmentId);

            // The enrollment already has an annotation in this session, cancelled or not: it is never modified, and a
            // cancelled one is never resurrected (T13's idempotency precedent).
            if (session.Bookings.Any(booking => booking.EnrollmentId == enrollmentId))
            {
                omitted++;
                continue;
            }

            var statuses = session.Bookings.Select(booking => booking.Status).ToArray();
            if (!ClassCapacity.HasPlace(session.MaxCapacity, statuses))
            {
                refused++;
                continue;
            }

            toCreate.Add(new FixedAnnotationInsert(session.AppointmentId, enrollmentId, clientId, now));
        }

        var created = 0;
        if (toCreate.Count > 0)
        {
            var results = await repository.InsertFixedAnnotationsAsync(businessId, toCreate, ct);
            if (results.Count != toCreate.Count)
                throw new InvalidOperationException(
                    "La sincronización de anotaciones fijas no devolvió un resultado por anotación.");

            foreach (var result in results)
            {
                switch (result.Result)
                {
                    case FixedAnnotationInsertResultType.Inserted:
                        created++;
                        break;
                    case FixedAnnotationInsertResultType.AlreadyPresent:
                        // Written by a concurrent activation between the read and the insert: it counts as omitted,
                        // because this call wrote nothing.
                        omitted++;
                        break;
                    case FixedAnnotationInsertResultType.NoSeatLeft:
                        // The last place was taken in between: the fixed annotation is refused instead of overbooking
                        // the session.
                        refused++;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"La inserción de una anotación fija devolvió un resultado desconocido: {(int)result.Result}.");
                }
            }
        }

        return new EnrollmentSyncOutcome(created, omitted, refused, sessionIds.OrderBy(id => id).ToArray());
    }

    private static EnrollmentListItem ProjectListItem(EnrollmentListRow row, DateOnly today)
    {
        var state = MembershipCalendar.StateOn(row.Periods, today);
        var currentPeriodEnd = ResolveCurrentPeriodEnd(row.Periods, today);

        // `Vencen pronto` is the three days before the expiry that RF-30 warns about. A current membership always
        // expires after today, so the difference is positive and the comparison cannot underflow.
        var expiresSoon = state == MembershipState.Current
            && currentPeriodEnd is { } expiry
            && expiry.DayNumber - today.DayNumber <= MembershipListRules.ExpiresSoonThresholdDays;

        return new EnrollmentListItem(
            row.EnrollmentId,
            row.ClientId,
            row.ClientName,
            row.WeeklyFrequency,
            row.Status,
            currentPeriodEnd,
            state,
            PendingPayment: row.Status == EnrollmentStatus.PendingPayment,
            ExpiresSoon: expiresSoon);
    }

    /// <summary>
    /// The expiry the owner has to resolve: the one in force when a period covers the date, and the day the membership
    /// lapsed when none does. <c>null</c> means no month was ever paid. The state itself is never persisted (D-23).
    /// </summary>
    private static DateOnly? ResolveCurrentPeriodEnd(IReadOnlyList<MembershipPeriod> periods, DateOnly today)
    {
        DateOnly? inForce = null;
        DateOnly? lapsed = null;
        foreach (var period in periods)
        {
            if (period.Covers(today))
            {
                if (inForce is null || period.ExpiresOn > inForce.Value)
                    inForce = period.ExpiresOn;
            }
            else if (period.ExpiresOn <= today && (lapsed is null || period.ExpiresOn > lapsed.Value))
            {
                lapsed = period.ExpiresOn;
            }
        }

        return inForce ?? lapsed;
    }

    /// <summary>
    /// Whether the derived item belongs to the requested view. Only <see cref="EnrollmentListFilter.All"/> shows
    /// annulled and terminated enrollments, each one with its own status label; every other view is a calendar state
    /// of an enrollment that still holds places. An <c>Active</c> enrollment whose periods do not cover the reference
    /// date — including one with no period at all, which only corrupt data produces — belongs to
    /// <see cref="EnrollmentListFilter.Expired"/>: it keeps its seat pending the owner's renewal, exactly like the
    /// week generation derives <c>Pendiente de renovación</c> (D-23).
    /// </summary>
    private static bool MatchesFilter(EnrollmentListItem item, EnrollmentListFilter filter) => filter switch
    {
        EnrollmentListFilter.All => true,
        EnrollmentListFilter.Current =>
            item.Status == EnrollmentStatus.Active && item.MembershipState == MembershipState.Current,
        EnrollmentListFilter.ExpiringSoon => item.Status == EnrollmentStatus.Active && item.ExpiresSoon,
        EnrollmentListFilter.Expired =>
            item.Status == EnrollmentStatus.Active && item.MembershipState != MembershipState.Current,
        EnrollmentListFilter.PendingPayment => item.PendingPayment,
        _ => throw new InvalidOperationException(
            $"La lista de abonos recibió un filtro desconocido: {(int)filter}.")
    };
}
