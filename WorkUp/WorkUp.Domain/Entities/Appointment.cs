using WorkUp.Domain.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Turno: an individual appointment, a walk-in attention, or a dated weekly class (clase de la semana).
/// Clients are attached through <see cref="Booking"/>; the state of each client lives there (Regla 3).
/// </summary>
public class Appointment : BusinessEntity
{
    public AppointmentKind Kind { get; set; }
    public int ServiceId { get; set; }
    public int WorkerId { get; set; }

    /// <summary>Set only for class sessions generated from the weekly grid (RF-06).</summary>
    public int? GridClassId { get; set; }

    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }

    /// <summary>Capacity copied from the service when the appointment was created.</summary>
    public int MaxCapacity { get; set; }

    /// <summary>Only individual appointments have an origin.</summary>
    public AppointmentOrigin? Origin { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public string? SuspensionReason { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool HasStarted(DateTime now) => now >= StartsAt;
}
