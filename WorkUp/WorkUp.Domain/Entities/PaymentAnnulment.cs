using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Anulación de cobro: reason, date and person responsible, linked to the original payment (RF-28).</summary>
public class PaymentAnnulment : Entity
{
    public int PaymentId { get; set; }
    public string Reason { get; set; } = "";
    public DateTime AnnulledAt { get; set; }
    public string AnnulledBy { get; set; } = "";
}
