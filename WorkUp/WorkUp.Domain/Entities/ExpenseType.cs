using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Tipo de gasto defined by the business (RF-01, RF-13). Archived instead of deleted.</summary>
public class ExpenseType : BusinessEntity
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}
