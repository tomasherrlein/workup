using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Trabajadora o profesora. Data only: workers never sign in (D-06).</summary>
public class Worker : BusinessEntity
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Cuil { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public List<WorkerSchedule> Schedules { get; set; } = [];
    public List<WorkerBreak> Breaks { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}";
}
