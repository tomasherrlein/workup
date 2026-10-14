using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Enlace privado "Ver mi turno" (RF-23): reusable, bound to one individual booking, expires 24 hours after the
/// appointment starts. A replacement keeps the original expiry.
/// </summary>
public class AppointmentLink : Entity
{
    public int BookingId { get; set; }

    /// <summary>SHA-256 of the token, used for lookup. The raw token is never stored in clear.</summary>
    public byte[] TokenHash { get; set; } = [];

    /// <summary>Token protected with ASP.NET Data Protection so the owner can copy the current link again.</summary>
    public string ProtectedToken { get; set; } = "";

    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsUsable(DateTime now) => RevokedAt is null && now < ExpiresAt;
}
