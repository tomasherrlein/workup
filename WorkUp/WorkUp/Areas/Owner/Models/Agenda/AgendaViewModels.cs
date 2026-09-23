using System.ComponentModel.DataAnnotations;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Areas.Owner.Models.Agenda;

public sealed record AgendaIndexViewModel(
    OwnerAgendaResult Agenda,
    OwnerAgendaPeriod Period,
    int? WorkerId,
    BookingStatus? BookingStatus,
    AppointmentOrigin? Origin);

public sealed record AgendaBookingDetailViewModel(OwnerAgendaBookingDetail Booking, bool CanReschedule = false);

public sealed record AgendaBookingRescheduleViewModel(
    OwnerAgendaBookingDetail Booking,
    OwnerRescheduleInput Input);

public sealed class OwnerRescheduleInput
{
    [Required(ErrorMessage = "Elegí un día.")]
    public DateOnly? Date { get; set; }

    [Required(ErrorMessage = "Elegí un horario.")]
    public TimeOnly? StartTime { get; set; }
}

public sealed record AgendaBookingFormViewModel(
    OwnerBookingForm Form,
    OwnerBookingCreateInput Input);

public sealed class OwnerBookingCreateInput
{
    [Required(ErrorMessage = "Elegí un servicio.")]
    [Range(1, int.MaxValue, ErrorMessage = "Elegí un servicio válido.")]
    public int? ServiceId { get; set; }

    [Required(ErrorMessage = "Elegí una trabajadora.")]
    [Range(1, int.MaxValue, ErrorMessage = "Elegí una trabajadora válida.")]
    public int? WorkerId { get; set; }

    [Required(ErrorMessage = "Elegí un día.")]
    public DateOnly? Date { get; set; }

    [Required(ErrorMessage = "Elegí un horario disponible.")]
    public TimeOnly? StartTime { get; set; }

    [Required(ErrorMessage = "Ingresá tu email.")]
    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254, ErrorMessage = "El email no puede superar los 254 caracteres.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el nombre.")]
    [StringLength(60, ErrorMessage = "El nombre no puede superar los 60 caracteres.")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el apellido.")]
    [StringLength(60, ErrorMessage = "El apellido no puede superar los 60 caracteres.")]
    public string LastName { get; set; } = "";
}
