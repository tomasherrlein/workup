using System.ComponentModel.DataAnnotations;

namespace WorkUp.ViewModels.Businesses;

public class BusinessSettingsViewModel
{
    [Required(ErrorMessage = "Ingresá el nombre del negocio.")]
    [StringLength(120)]
    [Display(Name = "Nombre del negocio")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el slug público.")]
    [StringLength(60)]
    [Display(Name = "Slug público")]
    public string Slug { get; set; } = "";

    [Required(ErrorMessage = "Ingresá un teléfono de contacto.")]
    [StringLength(30)]
    [Display(Name = "Teléfono de contacto")]
    public string ContactPhone { get; set; } = "";

    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254)]
    [Display(Name = "Email de avisos")]
    public string? NotificationEmail { get; set; }

    [Display(Name = "Admite atención sin turno")]
    public bool AllowsWalkIns { get; set; }

    [Required]
    [Range(1, 168, ErrorMessage = "Tiene que estar entre 1 y 168 horas.")]
    [Display(Name = "Anticipación del recordatorio (horas)")]
    public int ReminderLeadHours { get; set; }

    [Required(ErrorMessage = "Ingresá la hora del resumen diario.")]
    [Display(Name = "Hora del resumen diario")]
    public TimeOnly DailySummaryTime { get; set; }

    [Required]
    [Range(1, 90, ErrorMessage = "Tiene que estar entre 1 y 90 días.")]
    [Display(Name = "Revisión de existencias cada (días)")]
    public int StockReviewEveryDays { get; set; }
}
