using WorkUp.Domain.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Entities;

/// <summary>Single-use token for email verification (RF-25) or password reset, valid 30 minutes (RF-27).</summary>
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
