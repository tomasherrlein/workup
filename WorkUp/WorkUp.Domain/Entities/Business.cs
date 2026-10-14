using WorkUp.Domain.Common;

namespace WorkUp.Domain.Entities;

/// <summary>Negocio: the tenant. Holds the operating parameters consumed by the rest of the system (RF-01).</summary>
public class Business : Entity
{
    public string Name { get; set; } = "";

    /// <summary>URL segment of the public booking link: <c>/r/{Slug}</c>.</summary>
    public string Slug { get; set; } = "";

    public string ContactPhone { get; set; } = "";

    /// <summary>Where owner notices (daily summary, stock review) are emailed. See plan assumption 3.</summary>
    public string? NotificationEmail { get; set; }

    /// <summary>Admite atención sin turno (Regla 15). Defaults to true.</summary>
    public bool AllowsWalkIns { get; set; } = true;

    /// <summary>Anticipación del recordatorio de turno, in hours (RF-17). Defaults to 24.</summary>
    public int ReminderLeadHours { get; set; } = 24;

    /// <summary>Hora de emisión del resumen de agenda (RF-18). Defaults to 21:00.</summary>
    public TimeOnly DailySummaryTime { get; set; } = new(21, 0);

    /// <summary>Periodicidad del aviso de revisión de existencias, in days (RF-19). Defaults to 7.</summary>
    public int StockReviewEveryDays { get; set; } = 7;

    public DateTime CreatedAt { get; set; }
}
