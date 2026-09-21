using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Pausa no laborable of a worker for one day of the week (RF-03). Must lie inside that day schedule.</summary>
public class WorkerBreak : Entity
{
    public int WorkerId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}
