using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Optional client account (D-18). Created by self-registration (RF-25) or by accepting an invitation (RF-26).
/// <see cref="ClientId"/> stays null until the email is verified or the invitation is accepted.
/// </summary>
public class ClientAccount : BusinessEntity
{
    public int? ClientId { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime? EmailVerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsActive => EmailVerifiedAt is not null && ClientId is not null;
}
