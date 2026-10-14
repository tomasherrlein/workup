using WorkUp.Domain.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Anotación de un cliente en un turno. Holds the client state (<c>Pendiente</c>, <c>Confirmado</c>,
/// <c>Atendido</c>, <c>Ausente</c>, <c>Cancelado</c>) and the agreed price (D-20).
/// </summary>
public class Booking : BusinessEntity
{
    public int AppointmentId { get; set; }
    public int ClientId { get; set; }

    /// <summary>Set for fixed bookings created from an enrollment (RF-06).</summary>
    public int? EnrollmentId { get; set; }

    /// <summary>Null for individual appointments.</summary>
    public BookingModality? Modality { get; set; }

    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public AbsenceSource? AbsenceSource { get; set; }
    public CancelledBy? CancelledBy { get; set; }

    /// <summary>Precio acordado: captured from the tariff when booking; later tariff changes never alter it.</summary>
    public decimal? AgreedPrice { get; set; }

    /// <summary>Motivo when the agreed price differs from the tariff (optional).</summary>
    public string? PriceReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? AttendanceRecordedAt { get; set; }

    public bool IsActive => Status != BookingStatus.Cancelled;
}
