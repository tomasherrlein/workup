using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Payments;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure;
using WorkUp.ViewModels.Payments;

namespace WorkUp.Areas.Owner.Controllers;

/// <summary>
/// Cobros (RF-11, RF-28, D-20): the payment history with its filters and the two manual corrections. A payment is a
/// fact and is never edited nor deleted: the owner annuls it with a reason — date and person responsible come from
/// the server, never from the form — or registers a refund actually made outside the system, linked to the payment.
/// Every rule, every clock read and every write belongs to <see cref="PaymentLedgerService"/>: this controller routes,
/// resolves the names the owner reads and maps the stable error codes to Spanish. An annulment is a financial-only
/// correction, so the page never promises that a service comes back or that money is transferred automatically.
/// </summary>
public sealed class PaymentsController(
    PaymentLedgerService ledger,
    ClientsService clients,
    IClock clock) : OwnerControllerBase
{
    /// <summary>
    /// Payment history (RF-28): tenant-scoped and filtered by paid period, client and concept, paged by the service.
    /// An unknown filter value falls back to "all" instead of failing the page, and an impossible period is reported
    /// instead of silently showing an empty history. Reading the history writes nothing.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(
        DateOnly? from,
        DateOnly? to,
        int? clientId,
        string? concept,
        int? page,
        CancellationToken ct)
    {
        ViewData["Title"] = "Cobros";
        SetPrivateHeaders();

        var selectedConcept = ParseConcept(concept);
        var query = new PaymentLedgerQuery(clientId, selectedConcept, from, to, Math.Max(1, page ?? 1));
        var result = await ledger.ListAsync(BusinessId, query, ct);

        if (!result.Succeeded)
            this.Flash(ListErrorMessage(result.Error!), FlashType.Error);

        // A rejected range still renders the page with its filters so the owner can correct it; the service rejected
        // it before storage, so no partial list is shown as if it were the truth.
        var resultPage = result.Succeeded
            ? result.Value
            : new PaymentLedgerPage([], 0, query.Page, PaymentLedgerService.DefaultPageSize);

        var visibleClients = await clients.SearchAsync(BusinessId, null, ct);

        return View(new PaymentLedgerIndexViewModel
        {
            From = from,
            To = to,
            ClientId = clientId,
            Concept = selectedConcept,
            ConceptOptions = BuildConceptOptions(selectedConcept),
            Clients = visibleClients
                .Select(client => new PaymentClientOptionViewModel(client.Id, client.FullName, client.Id == clientId))
                .ToList(),
            Items = resultPage.Items.Select(row => new PaymentLedgerRowViewModel(row)).ToList(),
            Page = resultPage.Page,
            PageSize = resultPage.PageSize,
            TotalCount = resultPage.TotalCount,
            Today = clock.Today
        });
    }

    /// <summary>
    /// Annuls one payment (RF-28) and redirects back to the history (POST/redirect/GET). The business is the owner's
    /// and the person responsible is the authenticated username, never a form field; the instant comes from the
    /// service's clock. It is a financial-only correction: no service, coverage or seat is touched or promised.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Annul(int paymentId, string? reason, CancellationToken ct)
    {
        SetPrivateHeaders();

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrEmpty(trimmedReason) || trimmedReason.Length > PaymentLedgerService.MaxReasonLength)
        {
            this.Flash("Indicá el motivo de la anulación (hasta 300 caracteres).", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        var result = await ledger.AnnulAsync(
            BusinessId, new AnnulPaymentRequest(paymentId, trimmedReason, OwnerUsername), ct);
        this.Flash(result.Succeeded ? "Cobro anulado." : AnnulErrorMessage(result.Error!),
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Registers a manual refund linked to a payment (D-20) and redirects back to the history (POST/redirect/GET).
    /// The amount is bound with the invariant decimal binder, the date is the real day the money was returned and the
    /// reason is required; the recording instant comes from the service's clock. Nothing here promises a transfer.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(
        int paymentId,
        [ModelBinder(typeof(InvariantDecimalModelBinder))] decimal? amount,
        DateOnly? refundedOn,
        string? reason,
        CancellationToken ct)
    {
        SetPrivateHeaders();

        if (amount is not { } value || value <= 0)
        {
            this.Flash("Ingresá un importe válido para la devolución.", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        if (refundedOn is not { } refundedDate)
        {
            this.Flash("Indicá la fecha de la devolución.", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrEmpty(trimmedReason) || trimmedReason.Length > PaymentLedgerService.MaxReasonLength)
        {
            this.Flash("Indicá el motivo de la devolución (hasta 300 caracteres).", FlashType.Error);
            return RedirectToAction(nameof(Index));
        }

        var result = await ledger.RefundAsync(
            BusinessId, new RefundPaymentRequest(paymentId, value, refundedDate, trimmedReason), ct);
        this.Flash(result.Succeeded ? "Devolución registrada." : RefundErrorMessage(result.Error!),
            result.Succeeded ? FlashType.Success : FlashType.Error);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Parses the concept filter. An unknown value falls back to "all concepts" instead of propagating an undefined
    /// enum to the service, which would fail the whole page.
    /// </summary>
    private static PaymentConcept? ParseConcept(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Enum.TryParse<PaymentConcept>(value.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed
                : null;
    }

    private static IReadOnlyList<PaymentConceptOptionViewModel> BuildConceptOptions(PaymentConcept? selected) =>
    [
        new(null, "Todos los conceptos", selected is null),
        new(PaymentConcept.Membership, PaymentConceptLabels.For(PaymentConcept.Membership), selected == PaymentConcept.Membership),
        new(PaymentConcept.IndividualAppointment, PaymentConceptLabels.For(PaymentConcept.IndividualAppointment), selected == PaymentConcept.IndividualAppointment),
        new(PaymentConcept.WalkIn, PaymentConceptLabels.For(PaymentConcept.WalkIn), selected == PaymentConcept.WalkIn),
        new(PaymentConcept.DropInClass, PaymentConceptLabels.For(PaymentConcept.DropInClass), selected == PaymentConcept.DropInClass)
    ];

    /// <summary>
    /// Turns the stable code of a rejected correction into the sentence the owner reads. The codes are the service's
    /// contract and are never shown as text: an unrecognized one falls back to the service's own message, which is
    /// already Spanish, instead of leaking the code.
    /// </summary>
    private static string AnnulErrorMessage(BusinessError error) => error.Code switch
    {
        PaymentLedgerService.NotFoundCode => "No encontramos ese cobro.",
        PaymentLedgerService.AlreadyAnnulledCode => "Ese cobro ya está anulado.",
        PaymentLedgerService.AlreadyRefundedCode => "Ese cobro tiene devoluciones: no se puede anular.",
        PaymentLedgerService.InvalidReasonCode => "Indicá el motivo (hasta 300 caracteres).",
        PaymentLedgerService.InvalidActorCode => "No pudimos identificar a la dueña que anula el cobro.",
        _ => error.Message
    };

    /// <summary>
    /// The refund path reads the shared <c>payment_already_annulled</c> code with its own wording, because the same
    /// refusal reaches the owner through a different action: an annulment is already there, so no refund is possible.
    /// </summary>
    private static string RefundErrorMessage(BusinessError error) => error.Code switch
    {
        PaymentLedgerService.NotFoundCode => "No encontramos ese cobro.",
        PaymentLedgerService.AlreadyAnnulledCode => "Ese cobro está anulado: no se puede devolver.",
        PaymentLedgerService.InvalidReasonCode => "Indicá el motivo (hasta 300 caracteres).",
        PaymentLedgerService.InvalidAmountCode => "El importe de la devolución debe ser mayor a cero.",
        PaymentLedgerService.InvalidDateCode => "La devolución no puede ser anterior al cobro.",
        // A refund dated after the business's today is its own fact, not an earlier-than-the-payment one: it gets an
        // explicit sentence instead of relying on the service message, so the contract is pinned at this layer.
        PaymentLedgerService.InvalidFutureDateCode => "La devolución no puede tener una fecha posterior a hoy.",
        PaymentLedgerService.RefundExceedsAmountCode => "Las devoluciones no pueden superar el importe cobrado.",
        _ => error.Message
    };

    /// <summary>The list path: an impossible period is the only refusal the owner can fix on the same screen.</summary>
    private static string ListErrorMessage(BusinessError error) => error.Code switch
    {
        PaymentLedgerService.NotFoundCode => "No encontramos ese cobro.",
        PaymentLedgerService.InvalidRangeCode => "La fecha desde no puede ser posterior a la fecha hasta.",
        _ => error.Message
    };

    private void SetPrivateHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
