using System.ComponentModel.DataAnnotations;

namespace WorkUp.ViewModels.Staff;

public class WorkerFormViewModel
{
    [Required(ErrorMessage = "Ingresá el nombre.")]
    [StringLength(60)]
    [Display(Name = "Nombre")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el apellido.")]
    [StringLength(60)]
    [Display(Name = "Apellido")]
    public string LastName { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el teléfono.")]
    [StringLength(30)]
    [Display(Name = "Teléfono")]
    public string Phone { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el CUIL.")]
    [StringLength(13)]
    [Display(Name = "CUIL")]
    public string Cuil { get; set; } = "";

    /// <summary>Always the 7 days of the week, Monday to Sunday, in this fixed order (RF-03).</summary>
    public List<DayScheduleFormViewModel> Days { get; set; } = MondayFirst.Select(d => new DayScheduleFormViewModel { DayOfWeek = d }).ToList();

    /// <summary>Monday-first week order for the form; <see cref="DayOfWeek"/> itself starts the week on Sunday.</summary>
    public static readonly IReadOnlyList<DayOfWeek> MondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];
}

public class DayScheduleFormViewModel
{
    public DayOfWeek DayOfWeek { get; set; }

    [Display(Name = "Trabaja este día")]
    public bool WorksThisDay { get; set; }

    [Display(Name = "Inicio")]
    public TimeOnly? StartTime { get; set; }

    [Display(Name = "Fin")]
    public TimeOnly? EndTime { get; set; }

    /// <summary>
    /// Fixed number of break slots per day (T03 decision): the form is server-rendered and must work without
    /// JavaScript, so instead of a dynamic add/remove list it always renders this many slots; an empty slot (both
    /// times blank) is ignored. Two slots cover a split shift (one break) with room for a second one.
    /// </summary>
    public List<BreakSlotFormViewModel> Breaks { get; set; } = [new(), new()];
}

public class BreakSlotFormViewModel
{
    [Display(Name = "Inicio de pausa")]
    public TimeOnly? StartTime { get; set; }

    [Display(Name = "Fin de pausa")]
    public TimeOnly? EndTime { get; set; }
}
