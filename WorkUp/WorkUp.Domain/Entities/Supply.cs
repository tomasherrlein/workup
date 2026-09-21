using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Insumo with a declared stock (RF-14). Stock only changes when the owner declares it (D-03).</summary>
public class Supply : BusinessEntity
{
    public string Name { get; set; } = "";
    public decimal CurrentStock { get; set; }
    public decimal MinimumStock { get; set; }
    public DateTime LastDeclaredAt { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsBelowMinimum => CurrentStock < MinimumStock;
}
