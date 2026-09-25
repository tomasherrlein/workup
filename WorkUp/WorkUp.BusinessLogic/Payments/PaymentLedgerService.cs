using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Payments;

/// <summary>
/// The payment ledger (RF-11, RF-28, D-20): the owner annuls a payment registered to correct it and registers a
/// manual refund linked to a payment. A payment is a fact and is never edited nor deleted; every correction is a new
/// row that hangs from it, and the original stays in the history with its amount, its concept and its concrete origin.
/// <para>
/// <b>One unit, one lock, one clock read.</b> Each write opens one transaction, locks the payment row, judges every
/// rule from that stable read and commits exactly once; a rejection returns without committing, which rolls the unit
/// back. The current time is read exactly once per operation, after the lock, so the annulment instant and the
/// refund's recording instant cannot be split by a clock that advances mid-call. The guarded inserts repeat the rules
/// storage can express, so a write that lost a race changes no row and is reported instead of silently duplicated.
/// </para>
/// <para>
/// <b>Annulment and refund never coexist.</b> In either order the second correction is refused: a payment with any
/// refund cannot be annulled and an annulled payment cannot be refunded. The accumulated partial refunds can never
/// pass the amount charged, because the cap is cumulative over the refunds already linked to the payment.
/// </para>
/// <para>
/// <b>An annulment of a membership payment is financial only.</b> It is a correction of the money, not of the
/// service: the paid coverage, the fixed seats and the attendance are untouched, and ending a membership is the
/// termination flow of RF-29 (T15), never this one. This service writes an annulment and nothing else, so there is no
/// duplicate of the coverage or the seat rules anywhere in it, and no automatic reversal of a service or a bank
/// transfer is promised.
/// </para>
/// </summary>
public sealed class PaymentLedgerService(
    IPaymentLedgerRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>Stable codes of the annulment and refund rules (RF-28, D-20). They are the caller's contract.</summary>
    public const string NotFoundCode = "payment_not_found";

    public const string AlreadyAnnulledCode = "payment_already_annulled";

    public const string AlreadyRefundedCode = "payment_already_refunded";

    public const string InvalidReasonCode = "payment_invalid_reason";

    public const string InvalidActorCode = "payment_invalid_actor";

    public const string InvalidAmountCode = "payment_invalid_amount";

    public const string InvalidDateCode = "payment_invalid_date";

    public const string RefundExceedsAmountCode = "payment_refund_exceeds_amount";

    public const string InvalidRangeCode = "payment_invalid_range";

    /// <summary>
    /// A refund dated after the business's today. It is its own code because it is a different fact from a refund
    /// dated before the payment, and the owner fixes it differently: one is a date already returned, the other is a
    /// date that has not happened yet.
    /// </summary>
    public const string InvalidFutureDateCode = "payment_refund_in_future";

    /// <summary>Same limits as the stored columns: <c>Reason</c> 300, <c>AnnulledBy</c> 60 (001_schema.sql).</summary>
    public const int MaxReasonLength = 300;

    public const int MaxActorLength = 60;

    /// <summary>Same amount ceiling as the stored <c>DECIMAL(12, 2)</c> columns and the charge paths (D-20).</summary>
    public const decimal MaxAmount = 9999999999.99m;

    /// <summary>The page contract of the list: a sane default and a ceiling, so a request cannot ask for everything.</summary>
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    private const string NotFoundMessage = "No encontramos ese cobro.";

    /// <summary>
    /// Annuls one payment (RF-28): it writes the annulment with its reason, the clock instant and the person
    /// responsible, linked to the payment, and leaves the payment row untouched. It is refused for a payment of
    /// another business or a missing one, for a payment already annulled and for one that already has a refund,
    /// because the two corrections never coexist.
    /// </summary>
    public async Task<Result<PaymentAnnulmentOutcome>> AnnulAsync(
        int businessId, AnnulPaymentRequest request, CancellationToken ct = default)
    {
        if (businessId <= 0 || request is null || request.PaymentId <= 0)
            return Result<PaymentAnnulmentOutcome>.Failure(NotFoundCode, NotFoundMessage);

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
            return Result<PaymentAnnulmentOutcome>.Failure(
                InvalidReasonCode, "Indicá el motivo de la anulación.", nameof(request.Reason));

        var actor = request.AnnulledBy?.Trim();
        if (string.IsNullOrEmpty(actor) || actor.Length > MaxActorLength)
            return Result<PaymentAnnulmentOutcome>.Failure(
                InvalidActorCode, "Indicá quién anula el cobro.", nameof(request.AnnulledBy));

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var state = await repository.LockPaymentAsync(businessId, request.PaymentId, ct);
        if (state is null)
            return Result<PaymentAnnulmentOutcome>.Failure(NotFoundCode, NotFoundMessage);

        if (state.IsAnnulled)
            return Result<PaymentAnnulmentOutcome>.Failure(
                AlreadyAnnulledCode, "Ese cobro ya está anulado.");

        if (state.Refunds.Count > 0)
            return Result<PaymentAnnulmentOutcome>.Failure(
                AlreadyRefundedCode, "Ese cobro tiene devoluciones: no se puede anular.");

        // The single clock read of the annulment: the instant the correction was recorded.
        var now = clock.Now;

        var annulment = new PaymentAnnulment
        {
            PaymentId = state.Payment.Id,
            Reason = reason,
            AnnulledAt = now,
            AnnulledBy = actor
        };

        var insert = await repository.TryInsertAnnulmentAsync(businessId, annulment, ct);
        switch (insert.Result)
        {
            case PaymentAnnulmentInsertResultType.Inserted:
                break;
            case PaymentAnnulmentInsertResultType.AlreadyAnnulled:
                return Result<PaymentAnnulmentOutcome>.Failure(
                    AlreadyAnnulledCode, "Ese cobro ya está anulado.");
            case PaymentAnnulmentInsertResultType.Refunded:
                return Result<PaymentAnnulmentOutcome>.Failure(
                    AlreadyRefundedCode, "Ese cobro tiene devoluciones: no se puede anular.");
            default:
                throw new InvalidOperationException(
                    $"La inserción de la anulación devolvió un resultado desconocido: {(int)insert.Result}.");
        }

        await transaction.CommitAsync(ct);

        return Result<PaymentAnnulmentOutcome>.Success(new PaymentAnnulmentOutcome(
            state.Payment.Id, insert.AnnulmentId, reason, now, actor));
    }

    /// <summary>
    /// Registers a manual refund linked to one payment (D-20). The amount may be partial and the date is the day the
    /// money was returned as the owner typed it; the recording instant comes from the clock. It is refused for a
    /// payment of another business or a missing one, for an annulled payment, for an amount that does not fit or is
    /// not positive, for a date after the business's today — a completed refund cannot be dated in the future — for a
    /// date before the payment and for a refund that would push the accumulated refunds past the amount charged.
    /// </summary>
    public async Task<Result<PaymentRefundOutcome>> RefundAsync(
        int businessId, RefundPaymentRequest request, CancellationToken ct = default)
    {
        if (businessId <= 0 || request is null || request.PaymentId <= 0)
            return Result<PaymentRefundOutcome>.Failure(NotFoundCode, NotFoundMessage);

        if (!IsValidAmount(request.Amount))
            return Result<PaymentRefundOutcome>.Failure(
                InvalidAmountCode, "El importe de la devolución debe ser mayor a cero.", nameof(request.Amount));

        if (request.RefundedOn == default)
            return Result<PaymentRefundOutcome>.Failure(
                InvalidDateCode, "Indicá la fecha de la devolución.", nameof(request.RefundedOn));

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
            return Result<PaymentRefundOutcome>.Failure(
                InvalidReasonCode, "Indicá el motivo de la devolución.", nameof(request.Reason));

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        var state = await repository.LockPaymentAsync(businessId, request.PaymentId, ct);
        if (state is null)
            return Result<PaymentRefundOutcome>.Failure(NotFoundCode, NotFoundMessage);

        if (state.IsAnnulled)
            return Result<PaymentRefundOutcome>.Failure(
                AlreadyAnnulledCode, "Ese cobro está anulado: no se puede devolver.");

        // The single clock read of the refund: the instant it was recorded. The chosen day is the owner's input.
        var now = clock.Now;

        // The refund is money that already went back, so its day cannot be in the future: a date after the business's
        // today would record a completed correction in a period that has not happened yet. Every earlier day keeps
        // the accepted policy below, and the only source of "today" is the clock read above.
        if (request.RefundedOn > DateOnly.FromDateTime(now))
            return Result<PaymentRefundOutcome>.Failure(
                InvalidFutureDateCode,
                "La devolución no puede tener una fecha posterior a hoy.",
                nameof(request.RefundedOn));

        if (request.RefundedOn < DateOnly.FromDateTime(state.Payment.PaidAt))
            return Result<PaymentRefundOutcome>.Failure(
                InvalidDateCode, "La devolución no puede ser anterior al cobro.", nameof(request.RefundedOn));

        var total = state.RefundedAmount + request.Amount;
        if (total > state.Payment.Amount)
            return Result<PaymentRefundOutcome>.Failure(
                RefundExceedsAmountCode,
                "Las devoluciones no pueden superar el importe cobrado.",
                nameof(request.Amount));

        var refund = new Refund
        {
            PaymentId = state.Payment.Id,
            Amount = request.Amount,
            RefundedOn = request.RefundedOn,
            Reason = reason,
            RecordedAt = now
        };

        var insert = await repository.TryInsertRefundAsync(businessId, refund, ct);
        switch (insert.Result)
        {
            case PaymentRefundInsertResultType.Inserted:
                break;
            case PaymentRefundInsertResultType.AlreadyAnnulled:
                return Result<PaymentRefundOutcome>.Failure(
                    AlreadyAnnulledCode, "Ese cobro está anulado: no se puede devolver.");
            case PaymentRefundInsertResultType.ExceedsAmount:
                return Result<PaymentRefundOutcome>.Failure(
                    RefundExceedsAmountCode,
                    "Las devoluciones no pueden superar el importe cobrado.",
                    nameof(request.Amount));
            default:
                throw new InvalidOperationException(
                    $"La inserción de la devolución devolvió un resultado desconocido: {(int)insert.Result}.");
        }

        await transaction.CommitAsync(ct);

        return Result<PaymentRefundOutcome>.Success(new PaymentRefundOutcome(
            state.Payment.Id, insert.RefundId, request.Amount, request.RefundedOn, total, state.Payment.Amount - total));
    }

    /// <summary>
    /// The payment history of the business (RF-28), tenant-scoped and filtered by period, client and concept, in
    /// recent-first order and paged by the service. The page size is clamped to the contract, so a caller cannot ask
    /// for an unbounded page. An inverted period (<c>From</c> later than <c>To</c>) is refused before the read
    /// instead of silently returning an empty history. Reading the history writes nothing.
    /// </summary>
    public async Task<Result<PaymentLedgerPage>> ListAsync(
        int businessId, PaymentLedgerQuery query, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Result<PaymentLedgerPage>.Failure(NotFoundCode, NotFoundMessage);

        query ??= new PaymentLedgerQuery();

        // An inverted period would filter every row out and quietly show an empty history instead of telling the
        // owner the range is impossible. It is rejected before the read, so storage is never asked to run it.
        if (query.From is { } from && query.To is { } to && from > to)
            return Result<PaymentLedgerPage>.Failure(
                InvalidRangeCode,
                "La fecha desde no puede ser posterior a la fecha hasta.",
                nameof(query.From));

        var rows = await repository.ListAsync(businessId, query, ct);

        var ordered = rows
            .OrderByDescending(row => row.PaidAt)
            .ThenByDescending(row => row.PaymentId)
            .ToArray();

        var pageSize = Math.Clamp(query.PageSize <= 0 ? DefaultPageSize : query.PageSize, 1, MaxPageSize);
        var page = Math.Max(1, query.Page);
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        return Result<PaymentLedgerPage>.Success(new PaymentLedgerPage(items, ordered.Length, page, pageSize));
    }

    /// <summary>Whether a refund amount can be stored: positive, at most two decimals and within the column ceiling.</summary>
    private static bool IsValidAmount(decimal amount) =>
        amount > 0 && amount <= MaxAmount && decimal.Round(amount, 2) == amount;
}