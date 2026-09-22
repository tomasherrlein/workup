using System.ComponentModel.DataAnnotations;

namespace WorkUp.ViewModels.Classes;

public class GridClassFormViewModel
{
    [Required(ErrorMessage = "Elegí un tipo de clase.")]
    [Range(1, int.MaxValue, ErrorMessage = "Elegí un tipo de clase.")]
    [Display(Name = "Tipo de clase")]
    public int ServiceId { get; set; }

    [Required(ErrorMessage = "Elegí un día.")]
    [Display(Name = "Día")]
    public DayOfWeek? DayOfWeek { get; set; }

    [Required(ErrorMessage = "Ingresá el horario de inicio.")]
    [Display(Name = "Horario de inicio")]
    public TimeOnly? StartTime { get; set; }

    [Required(ErrorMessage = "Elegí una profesora.")]
    [Range(1, int.MaxValue, ErrorMessage = "Elegí una profesora.")]
    [Display(Name = "Profesora")]
    public int WorkerId { get; set; }

    /// <summary>Populated by the controller: active class-type services offered in the "Tipo de clase" select.</summary>
    public List<ClassTypeOptionViewModel> ServiceOptions { get; set; } = [];

    /// <summary>Populated by the controller: active workers offered in the "Profesora" select.</summary>
    public List<TeacherOptionViewModel> WorkerOptions { get; set; } = [];

    // Note: fully qualified as System.DayOfWeek because the enclosing type has an instance property named
    // DayOfWeek, which would otherwise shadow the enum in this static initializer (CS0236).
    /// <summary>Monday-first week order for the day dropdown; <see cref="System.DayOfWeek"/> itself starts on Sunday.</summary>
    public static readonly IReadOnlyList<System.DayOfWeek> MondayFirst =
    [
        System.DayOfWeek.Monday, System.DayOfWeek.Tuesday, System.DayOfWeek.Wednesday, System.DayOfWeek.Thursday,
        System.DayOfWeek.Friday, System.DayOfWeek.Saturday, System.DayOfWeek.Sunday
    ];
}

public sealed record ClassTypeOptionViewModel(int Id, string Name);

public sealed record TeacherOptionViewModel(int Id, string FullName);
