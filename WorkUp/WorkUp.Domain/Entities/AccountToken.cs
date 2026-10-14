using WorkUp.Domain.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Single-use token for email verification (RF-25) or password reset (RF-27). Lifetimes differ by purpose: the
/// verification link lives 48 h (plan assumption for T20, D-18) and the recovery link 30 minutes (RF-27); both are
/// stored only as their SHA-256 hash.
/// </summary>
public class AccountToken : Entity
{
    public int ClientAccountId { get; set; }
    public AccountTokenPurpose Purpose { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsUsable(DateTime now) => UsedAt is null && now < ExpiresAt;
}
