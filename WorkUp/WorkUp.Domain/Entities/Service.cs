using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Servicio del tarifario o tipo de clase (RF-02). Max capacity 1 means an individual turn; greater than 1 means a
/// group class published in the weekly grid. The service occupies its whole duration (D-25).
/// </summary>
public class Service : BusinessEntity
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int DurationMinutes { get; set; }
    public int MaxCapacity { get; set; } = 1;
    public bool IsActive { get; set; } = true;

    public bool IsIndividual => MaxCapacity == 1;
}
