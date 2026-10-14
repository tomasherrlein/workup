using System.ComponentModel.DataAnnotations;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Areas.Owner.Models.Agenda;

public sealed record AgendaIndexViewModel(
    OwnerAgendaResult Agenda,
    OwnerAgendaPeriod Period,
    int? WorkerId,
    BookingStatus? BookingStatus,
    AppointmentOrigin? Origin)
{
    public bool IsWeek => Period == OwnerAgendaPeriod.Week;

    public bool HasFilters => WorkerId is not null || BookingStatus is not null || Origin is not null;

    public DateOnly PreviousDate => Agenda.SelectedDate.AddDays(IsWeek ? -7 : -1);

    public DateOnly NextDate => Agenda.SelectedDate.AddDays(IsWeek ? 7 : 1);

    /// <summary>Name of the filtered worker, or a neutral label when the id no longer matches an active worker.</summary>
    public string? WorkerName => WorkerId is null
        ? null
        : Agenda.Workers.FirstOrDefault(worker => worker.Id == WorkerId)?.Name ?? "Trabajadora no disponible";

    public IEnumerable<IGrouping<DateOnly, OwnerAgendaRow>> EntriesByDay =>
        Agenda.Entries.GroupBy(entry => DateOnly.FromDateTime(entry.StartsAt));

    /// <summary>
    /// Query values for an agenda link that keeps the current view and filters, optionally changing the date or
    /// period and dropping one filter. Used by the date navigation, the view toggle and the removable filter chips.
    /// </summary>
    public Dictionary<string, string> Query(
        DateOnly? date = null,
        OwnerAgendaPeriod? period = null,
        bool keepWorker = true,
        bool keepStatus = true,
        bool keepOrigin = true)
    {
        var query = new Dictionary<string, string>
        {
            ["date"] = (date ?? Agenda.SelectedDate).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["period"] = (period ?? Period).ToString()
        };
        if (keepWorker && WorkerId is { } workerId)
            query["workerId"] = workerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (keepStatus && BookingStatus is { } status)
            query["status"] = status.ToString();
        if (keepOrigin && Origin is { } origin)
            query["origin"] = origin.ToString();
        return query;
    }
}

public sealed record AgendaBookingDetailViewModel(OwnerAgendaBookingDetail Booking)
{
    public bool CanReschedule => Booking.CanReschedule;
}

public sealed record AgendaBookingRescheduleViewModel(
    OwnerAgendaBookingDetail Booking,
    OwnerRescheduleInput Input,
    OwnerRescheduleOptions? Options = null);

public sealed class OwnerRescheduleInput
{
    [Required(ErrorMessage = "Elegí un día.")]
    public DateOnly? Date { get; set; }

    [Required(ErrorMessage = "Elegí un horario.")]
    public TimeOnly? StartTime { get; set; }
}

public sealed record AgendaBookingFormViewModel(
    OwnerBookingForm Form,
    OwnerBookingCreateInput Input,
    DateTime? ConflictAlternative = null);

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

    [Required(ErrorMessage = "Ingresá el email.")]
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
