using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WorkUp.ViewModels.ClientAccounts;

/// <summary>
/// Acceptance modes of an owner-assisted invitation (RF-26). The values are a server-side whitelist: the body only
/// selects one of them, and a value the form never offers is refused before anything is read or consumed.
/// </summary>
public static class ClientInvitationModes
{
    /// <summary>Create a new verified account bound to the invited ficha, with a chosen password and its repetition.</summary>
    public const string NewAccount = "nueva";

    /// <summary>Verify and bind a pre-existing pending account of the invited email, with its own password.</summary>
    public const string ExistingAccount = "existente";

    /// <summary>Whether the discriminant is one of the two modes the public form can legitimately submit.</summary>
    public static bool IsKnown(string? mode) => mode is NewAccount or ExistingAccount;
}

/// <summary>
/// Acceptance form of an owner-assisted invitation (RF-26). The tenant comes from the <c>/r/{slug}/cuenta</c> route
/// and the ficha from the token, so the form carries neither a business nor a client id: the only values bound from
/// the request body are the hidden token, the chosen mode and the password fields. The destination email and the
/// deadline are display-only and marked <see cref="BindNeverAttribute"/>, so no request can make the page echo an
/// address or a ficha of its own choosing.
/// </summary>
public sealed class ClientInvitationViewModel
{
    /// <summary>Single-use invitation token, posted back in a hidden field so the GET never spends it.</summary>
    public string Token { get; set; } = "";

    /// <summary>Chosen acceptance mode; validated against <see cref="ClientInvitationModes"/> before it is used.</summary>
    public string Mode { get; set; } = ClientInvitationModes.NewAccount;

    /// <summary>Password of the new account or of the pre-existing pending account, according to the chosen mode.</summary>
    [DataType(DataType.Password)]
    public string Password { get; set; } = "";

    /// <summary>Repetition of the new password; only the new-account mode asks for and validates it.</summary>
    [DataType(DataType.Password)]
    public string? ConfirmPassword { get; set; }

    /// <summary>Destination email the link proves; rendered from inspection and never bound from the request.</summary>
    [BindNever]
    public string Email { get; set; } = "";

    /// <summary>Deadline shown to the person; rendered from inspection and never bound from the request.</summary>
    [BindNever]
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Terminal screens of the invitation acceptance (RF-26). They reuse <see cref="ClientAccountStatusViewModel"/>, the
/// same record the verification and recovery flows render, so the public status page stays one page. Both messages
/// are generic on purpose: a missing, foreign, used, replayed, expired or revoked link answers the same
/// "unavailable" screen, and an account already linked to a ficha is sent to the business without naming or showing
/// that ficha. Neither message carries a token, a password or any history.
/// </summary>
public static class ClientInvitationStatus
{
    /// <summary>One generic answer for every unusable link; the only way forward is asking the business for a new one.</summary>
    public static ClientAccountStatusViewModel LinkUnavailable() => new(
        "El enlace no está disponible",
        "Este enlace no es válido o ya venció. Pedile al negocio una invitación nueva.",
        "error",
        "alert");

    /// <summary>An account already linked to a ficha: the business reviews it, without disclosing any ficha, email or history.</summary>
    public static ClientAccountStatusViewModel OwnerAssistanceRequired() => new(
        "Necesitamos que hables con el negocio",
        "No se pudo vincular esta cuenta. Contactá al negocio.",
        "warning",
        "info");
}
