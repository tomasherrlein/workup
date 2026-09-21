using System.ComponentModel.DataAnnotations;

namespace WorkUp.ViewModels.Businesses;

public class RegisterBusinessViewModel
{
    [Required(ErrorMessage = "Ingresá el nombre del negocio.")]
    [StringLength(120, ErrorMessage = "El nombre puede tener hasta 120 caracteres.")]
    [Display(Name = "Nombre del negocio")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el slug público.")]
    [StringLength(60, ErrorMessage = "El slug puede tener hasta 60 caracteres.")]
    [Display(Name = "Slug público")]
    public string Slug { get; set; } = "";

    [Required(ErrorMessage = "Ingresá un teléfono de contacto.")]
    [StringLength(30, ErrorMessage = "El teléfono puede tener hasta 30 caracteres.")]
    [Display(Name = "Teléfono de contacto")]
    public string ContactPhone { get; set; } = "";

    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254)]
    [Display(Name = "Email de avisos")]
    public string? NotificationEmail { get; set; }

    [Required(ErrorMessage = "Ingresá un usuario.")]
    [StringLength(60, ErrorMessage = "El usuario puede tener hasta 60 caracteres.")]
    [Display(Name = "Usuario")]
    public string Username { get; set; } = "";

    [Required(ErrorMessage = "Ingresá una contraseña.")]
    [DataType(DataType.Password)]
    [Display(Name = "Contraseña")]
    public string Password { get; set; } = "";

    [Required(ErrorMessage = "Repetí la contraseña.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Las contraseñas no coinciden.")]
    [Display(Name = "Repetir contraseña")]
    public string ConfirmPassword { get; set; } = "";
}
