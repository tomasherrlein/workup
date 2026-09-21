using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Horario de atención of a worker for one day of the week (RF-03).</summary>
public class WorkerSchedule : Entity
{
    public int WorkerId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}
