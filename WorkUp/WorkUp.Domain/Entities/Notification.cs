using WorkUp.Domain.Common;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Entities;

/// <summary>
/// Email in the outbox. <see cref="DedupKey"/> is unique, so a technical retry never sends the same notice twice
/// (RF-10, RF-17).
/// </summary>
public class Notification : Entity
{
    public int? BusinessId { get; set; }
    public NotificationType Type { get; set; }
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string DedupKey { get; set; } = "";
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
