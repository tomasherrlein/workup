using System.ComponentModel.DataAnnotations;

namespace WorkUp.ViewModels.ClientAccounts;

/// <summary>
/// Self-registration form (RF-25). No field names the business: the tenant comes exclusively from the
/// <c>/r/{slug}</c> route, so a caller can never register an account in a business it did not reach publicly.
/// </summary>
public sealed class ClientRegisterViewModel
{
    [Required(ErrorMessage = "Ingresá el nombre.")]
    [StringLength(60, ErrorMessage = "El nombre no puede superar los 60 caracteres.")]
    [Display(Name = "Nombre")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el apellido.")]
    [StringLength(60, ErrorMessage = "El apellido no puede superar los 60 caracteres.")]
    [Display(Name = "Apellido")]
    public string LastName { get; set; } = "";

    [Required(ErrorMessage = "Ingresá el email.")]
    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254, ErrorMessage = "El email no puede superar los 254 caracteres.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Ingresá una contraseña.")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "La contraseña debe tener al menos 8 caracteres.")]
    [DataType(DataType.Password)]
    [Display(Name = "Contraseña")]
    public string Password { get; set; } = "";

    [Required(ErrorMessage = "Repetí la contraseña.")]
    [Compare(nameof(Password), ErrorMessage = "Las contraseñas no coinciden.")]
    [DataType(DataType.Password)]
    [Display(Name = "Repetí la contraseña")]
    public string ConfirmPassword { get; set; } = "";
}

/// <summary>
/// Sign-in form (RF-15). <see cref="ReturnUrl"/> is offered back to the business only when it is a safe local path of
/// the same business; the controller never redirects to an arbitrary value the request brought.
/// </summary>
public sealed class ClientLoginViewModel
{
    [Required(ErrorMessage = "Ingresá tu email.")]
    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254, ErrorMessage = "El email no puede superar los 254 caracteres.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Ingresá tu contraseña.")]
    [DataType(DataType.Password)]
    [StringLength(100, ErrorMessage = "La contraseña no puede superar los 100 caracteres.")]
    [Display(Name = "Contraseña")]
    public string Password { get; set; } = "";

    /// <summary>Local destination offered by the panel; only echoed when it points inside this business.</summary>
    public string? ReturnUrl { get; set; }
}

/// <summary>Password recovery request form (RF-27). The answer is the same whether the email has an account or not.</summary>
public sealed class ClientRecoverPasswordViewModel
{
    [Required(ErrorMessage = "Ingresá tu email.")]
    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254, ErrorMessage = "El email no puede superar los 254 caracteres.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = "";
}

/// <summary>
/// New-password form reached from the recovery email (RF-27). The token travels in a hidden field so the page that
/// renders it never consumes it: only the POST does, and it never starts a session.
/// </summary>
public sealed class ClientResetPasswordViewModel
{
    /// <summary>Single-use recovery token, bound from the hidden field of the form.</summary>
    public string Token { get; set; } = "";

    [Required(ErrorMessage = "Ingresá una contraseña nueva.")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "La contraseña debe tener al menos 8 caracteres.")]
    [DataType(DataType.Password)]
    [Display(Name = "Contraseña nueva")]
    public string NewPassword { get; set; } = "";

    [Required(ErrorMessage = "Repetí la contraseña nueva.")]
    [Compare(nameof(NewPassword), ErrorMessage = "Las contraseñas no coinciden.")]
    [DataType(DataType.Password)]
    [Display(Name = "Repetí la contraseña nueva")]
    public string ConfirmPassword { get; set; } = "";
}

/// <summary>New verification link request (RF-25). Generic answer: it never reveals whether the email exists.</summary>
public sealed class ClientResendVerificationViewModel
{
    [Required(ErrorMessage = "Ingresá tu email.")]
    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254, ErrorMessage = "El email no puede superar los 254 caracteres.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = "";
}

/// <summary>
/// Confirmation form of the emailed verification link (RF-25). The token comes from the route and is posted back in a
/// hidden field: opening the link only shows this form, and the single use is spent by the POST.
/// </summary>
public sealed class ClientVerifyEmailViewModel
{
    /// <summary>Single-use verification token, bound from the hidden field of the form.</summary>
    public string Token { get; set; } = "";
}

/// <summary>
/// Terminal outcome of a token action: one message, one tone and at most one way forward. It replaces the form when
/// the link cannot be used, so a dead link is never a silent screen.
/// </summary>
public sealed record ClientAccountStatusViewModel(
    string Title,
    string Message,
    string Tone,
    string Icon,
    string? ActionLabel = null,
    string? ActionUrl = null)
{
    /// <summary>Invalid, expired, replayed or foreign verification link: one generic answer, plus a way to get a new one.</summary>
    public static ClientAccountStatusViewModel VerificationLinkUnavailable(string slug) => new(
        "El enlace no está disponible",
        "Este enlace no es válido o ya venció. Pedí uno nuevo para confirmar tu cuenta.",
        "error",
        "alert",
        "Pedir un enlace nuevo",
        $"/r/{Uri.EscapeDataString(slug)}/cuenta/reenviar");

    /// <summary>
    /// The business already has a ficha with that email, so the account cannot be linked by the person alone: the
    /// screen sends her to the business itself and offers no link, because the linking is the owner-assisted
    /// invitation (RF-26, T22) and never an automatic match by email.
    /// </summary>
    public static ClientAccountStatusViewModel OwnerAssistanceRequired(string businessName) => new(
        "Necesitamos que hables con el negocio",
        $"Ya existe una ficha con ese email en {businessName}. Escribile al negocio con el mismo email para que vincule tu cuenta.",
        "warning",
        "info");

    /// <summary>Invalid, expired, replayed or foreign recovery link: one generic answer, plus a way to get a new one.</summary>
    public static ClientAccountStatusViewModel PasswordResetLinkUnavailable(string slug) => new(
        "El enlace no está disponible",
        "Este enlace no es válido o ya venció. Pedí uno nuevo para restablecer tu contraseña.",
        "error",
        "alert",
        "Pedir un enlace nuevo",
        $"/r/{Uri.EscapeDataString(slug)}/cuenta/recuperar");
}
