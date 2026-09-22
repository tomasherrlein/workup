using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Models.Reservations;

public sealed record PublicBookingServicesViewModel(
    PublicBookingBusiness Business,
    IReadOnlyList<PublicBookingServiceOption> Services);

public sealed record PublicBookingWorkersViewModel(
    PublicBookingBusiness Business,
    PublicBookingServiceOption Service,
    IReadOnlyList<PublicBookingWorkerOption> Workers);

public sealed record PublicBookingAvailabilityViewModel(
    PublicBookingBusiness Business,
    PublicBookingServiceOption Service,
    PublicBookingWorkerOption Worker,
    DateOnly Date,
    DateOnly Today,
    IReadOnlyList<TimeOnly> AvailableStartTimes,
    DateTime? NearestFutureAlternative,
    string? Notice = null);

public sealed class PublicBookingContactViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Elegí un servicio para reservar.")]
    public int ServiceId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Elegí una trabajadora para reservar.")]
    public int WorkerId { get; set; }

    public DateOnly Date { get; set; }

    public TimeOnly StartTime { get; set; }

    [Required(ErrorMessage = "Ingresá tu email.")]
    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254, ErrorMessage = "El email no puede superar los 254 caracteres.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Ingresá tu nombre.")]
    [StringLength(60, ErrorMessage = "El nombre no puede superar los 60 caracteres.")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Ingresá tu apellido.")]
    [StringLength(60, ErrorMessage = "El apellido no puede superar los 60 caracteres.")]
    public string LastName { get; set; } = "";

    [BindNever]
    public PublicBookingBusiness Business { get; set; } = new(0, "", "");

    [BindNever]
    public PublicBookingServiceOption Service { get; set; } = new(0, "", 0m, 0);

    [BindNever]
    public PublicBookingWorkerOption Worker { get; set; } = new(0, "");

    [BindNever]
    public DateTime? AlternativeStart { get; set; }
}

public sealed record PublicBookingConfirmationViewModel(
    PublicBookingBusiness Business,
    PublicBookingConfirmation Confirmation);
