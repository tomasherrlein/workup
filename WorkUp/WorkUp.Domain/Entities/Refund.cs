using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Devolución manual made outside the system, linked to the original payment (D-20).</summary>
public class Refund : Entity
{
    public int PaymentId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly RefundedOn { get; set; }
    public string Reason { get; set; } = "";
    public DateTime RecordedAt { get; set; }
}
