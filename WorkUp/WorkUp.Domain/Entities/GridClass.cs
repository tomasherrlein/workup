using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Clase de la grilla: a class type fixed on a weekday, time and teacher that repeats every week (RF-04, D-05).
/// Duration and capacity come from the service.
/// </summary>
public class GridClass : BusinessEntity
{
    public int ServiceId { get; set; }
    public int WorkerId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public bool IsActive { get; set; } = true;
}
