using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Invitación de vinculación (RF-26): valid 48 hours, single use, revocable; issuing a new one invalidates the
/// previous one. Linking never merges client histories.
/// </summary>
public class AccountInvitation : BusinessEntity
{
    public int ClientId { get; set; }
    public string Email { get; set; } = "";
    public byte[] TokenHash { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsUsable(DateTime now) => UsedAt is null && RevokedAt is null && now < ExpiresAt;
}
