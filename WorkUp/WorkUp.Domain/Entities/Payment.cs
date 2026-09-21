using WorkUp.Domain.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Cobro. Always identifies its concept and concrete origin (a booking or an enrollment). Never edited or deleted:
/// corrections go through <see cref="PaymentAnnulment"/> and refunds through <see cref="Refund"/> (D-20).
/// </summary>
public class Payment : BusinessEntity
{
    public int? ClientId { get; set; }
    public PaymentConcept Concept { get; set; }
    public int? BookingId { get; set; }
    public int? EnrollmentId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>Tariff price when the payment was recorded, to show when the agreed amount differs.</summary>
    public decimal? ListPrice { get; set; }

    public string? PriceReason { get; set; }
    public DateTime PaidAt { get; set; }
    public DateTime RecordedAt { get; set; }
}
