using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Observación para la próxima atención, kept as history (RF-11). Owner-only.</summary>
public class ClientNote : Entity
{
    public int ClientId { get; set; }
    public int? AppointmentId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
