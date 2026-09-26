using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.ViewModels.Shared;

namespace WorkUp.ViewModels.ClientPanel;

/// <summary>
/// One row of the authenticated client's own agenda (T21.1, RF-08, RF-22). It carries only the safe public
/// projection the panel needs to render a row: date and time, service, worker, the class or turn label, its state
/// and whether the still-available actions can be offered. It never carries a health observation, an owner note or
/// a private link token, and it deliberately holds no client or business identity: the panel of a session can only
/// ever show the rows the service scoped to that session.
/// <para>
/// The eligibility is a label, not a control: T21.1 is read-only, and the confirm/cancel/reschedule actions arrive
/// as separate work units. Nothing here renders a dead link.
/// </para>
/// </summary>
public sealed record ClientAppointmentRowViewModel(
    int BookingId,
    DateTime StartsAt,
    DateTime EndsAt,
    string ServiceName,
    string WorkerName,
    string KindLabel,
    StatusTag Status,
    StatusTag? SuspensionStatus,
    bool CanConfirmIntention,
    bool CanCancel)
{
    /// <summary>
    /// The sentence that names what the person can still do with this row, or <c>null</c> when there is nothing left
    /// to do before the start. It is shown as text; no link or form is rendered for it yet.
    /// </summary>
    public string? EligibilityLabel
    {
        get
        {
            if (CanConfirmIntention && CanCancel)
                return "Podés confirmar tu asistencia o cancelar.";
            if (CanConfirmIntention)
                return "Podés confirmar tu asistencia.";
            if (CanCancel)
                return "Podés cancelar.";
            return null;
        }
    }

    public static ClientAppointmentRowViewModel From(ClientPanelBooking booking) => new(
        booking.BookingId,
        booking.StartsAt,
        booking.EndsAt,
        booking.ServiceName,
        booking.WorkerName,
        KindLabelOf(booking),
        StatusTag.For(booking.BookingStatus),
        booking.AppointmentStatus == AppointmentStatus.Suspended ? StatusTag.For(booking.AppointmentStatus) : null,
        booking.CanConfirmIntention,
        booking.CanCancel);

    /// <summary>The word the panel shows for the kind of booking: an individual turn, a fixed class or a drop-in.</summary>
    private static string KindLabelOf(ClientPanelBooking booking) => booking.AppointmentKind switch
    {
        AppointmentKind.Individual => "Turno individual",
        AppointmentKind.ClassSession => booking.Modality switch
        {
            BookingModality.Fixed => "Clase fija",
            BookingModality.DropIn => "Clase suelta",
            _ => "Clase"
        },
        _ => "Turno"
    };
}

/// <summary>
/// The whole read-only panel page (T21.1): the tenant slug for its own links plus the agenda already split into
/// upcoming and history by the service. Each section has its own accessible empty message, so an empty agenda is a
/// clear screen and never a blank one.
/// </summary>
public sealed record ClientAppointmentsViewModel(
    string Slug,
    IReadOnlyList<ClientAppointmentRowViewModel> Upcoming,
    IReadOnlyList<ClientAppointmentRowViewModel> History)
{
    public const string UpcomingEmptyMessage = "No tenés turnos ni clases próximos.";

    public const string HistoryEmptyMessage = "Todavía no tenés turnos ni clases anteriores.";

    public bool HasUpcoming => Upcoming.Count > 0;

    public bool HasHistory => History.Count > 0;

    public static ClientAppointmentsViewModel From(string slug, ClientPanelAgenda agenda) => new(
        slug,
        agenda.Upcoming.Select(ClientAppointmentRowViewModel.From).ToArray(),
        agenda.History.Select(ClientAppointmentRowViewModel.From).ToArray());
}
