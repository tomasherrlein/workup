using System.ComponentModel.DataAnnotations;

namespace WorkUp.ViewModels.Businesses;

public class LoginViewModel
{
    [Required(ErrorMessage = "Ingresá tu usuario.")]
    [Display(Name = "Usuario")]
    public string Username { get; set; } = "";

    [Required(ErrorMessage = "Ingresá tu contraseña.")]
    [DataType(DataType.Password)]
    [Display(Name = "Contraseña")]
    public string Password { get; set; } = "";
}
