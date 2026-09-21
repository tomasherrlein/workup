using System.ComponentModel.DataAnnotations;

namespace WorkUp.ViewModels.Catalog;

public class ServiceFormViewModel
{
    [Required(ErrorMessage = "Ingresá el nombre del servicio.")]
    [StringLength(100)]
    [Display(Name = "Nombre")]
    public string Name { get; set; } = "";

    [Required]
    [Range(0.01, 999999, ErrorMessage = "El precio debe ser mayor a cero.")]
    [Display(Name = "Precio")]
    public decimal Price { get; set; }

    [Required]
    [Range(1, 600, ErrorMessage = "La duración debe ser mayor a cero.")]
    [Display(Name = "Duración (minutos)")]
    public int DurationMinutes { get; set; }

    [Required]
    [Range(1, 200, ErrorMessage = "El cupo máximo debe ser 1 o más.")]
    [Display(Name = "Cupo máximo")]
    public int MaxCapacity { get; set; } = 1;
}
