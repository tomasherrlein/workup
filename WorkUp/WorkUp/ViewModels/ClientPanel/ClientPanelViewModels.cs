using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.ViewModels.Shared;

namespace WorkUp.ViewModels.ClientPanel;

/// <summary>
/// One row of the authenticated client's own agenda (T21.1, T21.2, RF-08, RF-22). It carries only the safe public
/// projection the panel needs to render a row: date and time, service, worker, the class or turn label, its state
/// and whether the still-available actions can be offered. It never carries a health observation, an owner note or
/// a private link token, and it deliberately holds no client or business identity: the panel of a session can only
/// ever show the rows the service scoped to that session.
/// <para>
/// The kind of booking travels twice on purpose: <see cref="KindLabel"/> is display text only, while
/// <see cref="IsIndividualTurn"/> is the typed shape derived from the appointment kind. Every action gate uses the
/// typed flag, so renaming the label can never remove an action. <see cref="EligibilityLabel"/> announces only the
/// actions whose forms this view wires today, so a row never promises a button that does not exist.
/// </para>
/// </summary>
public sealed record ClientAppointmentRowViewModel(
    int BookingId,
    DateTime StartsAt,
    DateTime EndsAt,
    string ServiceName,
    string WorkerName,
    string KindLabel,
    bool IsIndividualTurn,
    StatusTag Status,
    StatusTag? SuspensionStatus,
    bool CanConfirmIntention,
    bool CanCancel)
{
    /// <summary>
    /// The sentence that names what the person can still do with this row, or <c>null</c> when the panel has no
    /// action to offer before the start. It counts only the actions wired today: the individual turn (T21.2) offers
    /// confirmation and, while it stays individual, cancellation; the drop-in cancel arrives in T21.3, so a drop-in
    /// is not announced here. Whatever this sentence promises has a real button next to it.
    /// </summary>
    public string? EligibilityLabel
    {
        get
        {
            // The same typed gate the view uses for the cancel form: a cancellable row of another kind has no
            // button yet, and text without a button is a dead promise.
            var canCancel = CanCancel && IsIndividualTurn;
            if (CanConfirmIntention && canCancel)
                return "Podés confirmar que vas a asistir o cancelar.";
            if (CanConfirmIntention)
                return "Podés confirmar que vas a asistir.";
            if (canCancel)
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
        booking.AppointmentKind == AppointmentKind.Individual,
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
/// The whole client agenda page (T21.1, T21.2): the tenant slug for its own links plus the agenda already split into
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
