using WorkUp.BusinessLogic.Payments;
using WorkUp.Domain.Enums;
using WorkUp.ViewModels.Shared;

namespace WorkUp.ViewModels.Payments;

/// <summary>
/// The Spanish label of each payment concept (RF-28), so the history list and the concept filter never drift apart.
/// The vocabulary is the one the owner already reads in the panel, not the enum name.
/// </summary>
public static class PaymentConceptLabels
{
    public static string For(PaymentConcept concept) => concept switch
    {
        PaymentConcept.IndividualAppointment => "Turno individual",
        PaymentConcept.WalkIn => "Atención sin turno",
        PaymentConcept.DropInClass => "Clase suelta",
        PaymentConcept.Membership => "Abono",
        _ => throw new ArgumentOutOfRangeException(nameof(concept), concept, null)
    };
}

/// <summary>One option of the concept filter, with its Spanish label and its selected state.</summary>
public sealed record PaymentConceptOptionViewModel(PaymentConcept? Concept, string Label, bool IsSelected);

/// <summary>One client of the business the owner may filter by. The list is tenant-scoped, so it never names another business's client.</summary>
public sealed record PaymentClientOptionViewModel(int Id, string Name, bool IsSelected);

/// <summary>
/// One row of the payment history (RF-28, D-20). The payment is the original fact: its amount and its concrete origin
/// are shown as recorded, beside its correction — an annulment with its reason, date and person responsible, or the
/// amount already returned in refunds with what is left. <see cref="CanAnnul"/> and <see cref="CanRefund"/> are the
/// courtesy gates of the screen, not the guards: the service still enforces the coexistence rule under its own lock.
/// </summary>
public sealed record PaymentLedgerRowViewModel(PaymentLedgerRow Row)
{
    public int PaymentId => Row.PaymentId;

    /// <summary>The name the owner reads; falls back so a row with an unresolved client never shows an empty label.</summary>
    public string ClientName => string.IsNullOrWhiteSpace(Row.ClientName) ? "Sin clienta" : Row.ClientName;

    public string ConceptLabel => PaymentConceptLabels.For(Row.Concept);

    /// <summary>The concrete origin of the payment: the enrollment or the booking it belongs to, when there is one.</summary>
    public string? ReferenceLabel => Row.EnrollmentId is { } enrollmentId
        ? $"Inscripción #{enrollmentId}"
        : Row.BookingId is { } bookingId
            ? $"Reserva #{bookingId}"
            : null;

    public decimal Amount => Row.Amount;

    public DateTime PaidAt => Row.PaidAt;

    public bool Annulled => Row.Annulled;

    public string? AnnulmentReason => Row.AnnulmentReason;

    public DateTime? AnnulledAt => Row.AnnulledAt;

    public string? AnnulledBy => Row.AnnulledBy;

    public decimal RefundedAmount => Row.RefundedAmount;

    /// <summary>What is still returnable: the amount charged minus every refund already linked to the payment.</summary>
    public decimal RemainingAmount => Row.Amount - Row.RefundedAmount;

    public bool HasRefund => Row.RefundedAmount > 0;

    public bool FullyRefunded => HasRefund && RemainingAmount <= 0;

    /// <summary>
    /// The state as a word (The Word-First Rule): <c>Cobrado</c> for a payment with no correction, <c>Anulado</c> for
    /// an annulled one and <c>Devuelto</c> for one with refunds; the refunded total and the remaining amount are the
    /// detail beside it, never colour alone.
    /// </summary>
    public StatusTag StateTag => Row.Annulled
        ? StatusTag.Annulled
        : HasRefund
            ? new StatusTag("Devuelto", "tag-neutral", "coin")
            : StatusTag.Paid;

    /// <summary>
    /// Annulling is offered only while no refund exists: the two corrections never coexist, and the service refuses
    /// the second one in either order.
    /// </summary>
    public bool CanAnnul => !Row.Annulled && !HasRefund;

    /// <summary>
    /// A refund is offered while the payment is not annulled and something is left to return; a fully refunded one can
    /// only be rejected, so no form is shown.
    /// </summary>
    public bool CanRefund => !Row.Annulled && RemainingAmount > 0;
}

/// <summary>
/// The payment history page (RF-28): the selected filters, the rows of the current page and the total that lets the
/// owner page it. Read-only: the page opens no transaction and writes nothing.
/// </summary>
public sealed class PaymentLedgerIndexViewModel
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public int? ClientId { get; init; }

    public PaymentConcept? Concept { get; init; }

    public IReadOnlyList<PaymentConceptOptionViewModel> ConceptOptions { get; init; } = [];

    public IReadOnlyList<PaymentClientOptionViewModel> Clients { get; init; } = [];

    public IReadOnlyList<PaymentLedgerRowViewModel> Items { get; init; } = [];

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PaymentLedgerService.DefaultPageSize;

    public int TotalCount { get; init; }

    /// <summary>Business day, used as the default (and ceiling) of the refund-date input of every row.</summary>
    public DateOnly Today { get; init; }

    public bool HasItems => Items.Count > 0;

    public bool HasFilters => From is not null || To is not null || ClientId is not null || Concept is not null;

    public int TotalPages => TotalCount <= 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;

    /// <summary>Empty-state copy that names the filters when there are any, so an empty page is never ambiguous.</summary>
    public string EmptyMessage => HasFilters
        ? "No hay cobros para los filtros elegidos."
        : "Todavía no hay cobros registrados. Los cobros que registres en el panel van a aparecer acá.";
}
