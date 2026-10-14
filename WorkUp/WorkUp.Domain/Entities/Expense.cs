using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Gasto del negocio (RF-13).</summary>
public class Expense : BusinessEntity
{
    public int ExpenseTypeId { get; set; }
    public DateOnly SpentOn { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
