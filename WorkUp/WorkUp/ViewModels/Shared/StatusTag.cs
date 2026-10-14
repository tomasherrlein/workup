using WorkUp.Domain.Enums;

namespace WorkUp.ViewModels.Shared;

/// <summary>A state shown as its glossary word, a tone and an icon (The Word-First Rule). Rendered by _StatusTag.</summary>
public sealed record StatusTag(string Label, string CssClass, string Icon)
{
    public static StatusTag For(BookingStatus status) => status switch
    {
        BookingStatus.Pending => new("Pendiente", "tag-pending", "clock"),
        BookingStatus.Confirmed => new("Confirmado", "tag-confirmed", "check"),
        BookingStatus.Attended => new("Atendido", "tag-attended", "check-double"),
        BookingStatus.Absent => new("Ausente", "tag-alert", "cross"),
        BookingStatus.Cancelled => new("Cancelado", "tag-cancelled", "cross"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static StatusTag For(AppointmentStatus status) => status switch
    {
        AppointmentStatus.Scheduled => new("Programado", "tag-scheduled", "calendar"),
        AppointmentStatus.Suspended => new("Suspendido", "tag-alert", "pause"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static StatusTag For(MembershipState state) => state switch
    {
        MembershipState.Current => new("Vigente", "tag-attended", "check-double"),
        MembershipState.Expired => new("Vencido", "tag-alert", "cross"),
        MembershipState.None => new("Sin abono", "tag-neutral", "info"),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    /// <summary><c>Cobrado</c>: the strongest tag, because money in hand is a fact.</summary>
    public static readonly StatusTag Paid = new("Cobrado", "tag-paid", "coin");

    public static readonly StatusTag PendingRenewal = new("Pendiente de renovación", "tag-pending", "repeat");

    public static readonly StatusTag PendingPayment = new("Pendiente de pago", "tag-pending", "coin");

    public static readonly StatusTag Annulled = new("Anulado", "tag-cancelled", "cross");

    /// <summary>Generic on/off state for catalogues that are archived instead of deleted (RF-01, RF-13).</summary>
    public static readonly StatusTag Active = new("Activo", "tag-confirmed", "check");

    public static readonly StatusTag Archived = new("Archivado", "tag-neutral", "pause");
}
