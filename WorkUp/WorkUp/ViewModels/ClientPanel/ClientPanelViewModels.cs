using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.ViewModels.Shared;

namespace WorkUp.ViewModels.ClientPanel;

/// <summary>
/// One row of the authenticated client's own agenda (T21.1, T21.2, T21.4, RF-08, RF-09, RF-22). It carries only the
/// safe public projection the panel needs to render a row: date and time, service, worker, the class or turn label,
/// its state and whether the still-available actions can be offered. It never carries a health observation, an owner
/// note or a private link token, and it deliberately holds no client or business identity: the panel of a session
/// can only ever show the rows the service scoped to that session.
/// <para>
/// The kind of booking travels twice on purpose: <see cref="KindLabel"/> is display text only, while
/// <see cref="IsIndividualTurn"/> and <see cref="IsDropInClass"/> are the typed shapes derived from the appointment
/// kind and modality. Every action gate uses a typed flag, so renaming the label can never remove an action.
/// <see cref="EligibilityLabel"/> announces only the actions whose forms or links this view wires today, so a row
/// never promises an action that does not exist.
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
    bool IsDropInClass,
    StatusTag Status,
    StatusTag? SuspensionStatus,
    bool CanConfirmIntention,
    bool CanCancel,
    bool CanReschedule)
{
    /// <summary>
    /// The sentence that names what the person can still do with this row, or <c>null</c> when the panel has no
    /// action to offer before the start. It counts only the actions wired today: the individual turn (T21.2) offers
    /// confirmation and, while it stays individual, cancellation; the drop-in class (T21.3) offers cancellation; and
    /// the future reserved individual turn (T21.4) offers a link to reprogram it with the same worker. A fixed class
    /// offers nothing, so it is never announced. Whatever this sentence promises has a real button or link next to
    /// it.
    /// </summary>
    public string? EligibilityLabel
    {
        get
        {
            // The same typed gates the view uses for its forms: a cancellable row of a kind without a button must
            // not be announced, and text without an action is a dead promise.
            var canCancel = CanCancel && (IsIndividualTurn || IsDropInClass);
            if (CanConfirmIntention && canCancel)
                return CanReschedule
                    ? "Podés confirmar que vas a asistir, cancelar o reprogramar."
                    : "Podés confirmar que vas a asistir o cancelar.";
            if (CanConfirmIntention)
                return "Podés confirmar que vas a asistir.";
            if (canCancel)
                return CanReschedule ? "Podés cancelar o reprogramar." : "Podés cancelar.";
            return CanReschedule ? "Podés reprogramar." : null;
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
        booking.AppointmentKind == AppointmentKind.ClassSession && booking.Modality == BookingModality.DropIn,
        StatusTag.For(booking.BookingStatus),
        booking.AppointmentStatus == AppointmentStatus.Suspended ? StatusTag.For(booking.AppointmentStatus) : null,
        booking.CanConfirmIntention,
        booking.CanCancel,
        booking.CanReschedule);

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

/// <summary>
/// The chosen day and time of a client-initiated reprogramming (T21.4, RF-09). These are the only values the POST
/// carries: the worker, the ficha and the tenant always come from the own reservation and the session cookie, so a
/// request can neither retarget another worker nor another client. Both fields are optional in the type because a
/// form can arrive empty; the controller adds the safe field messages before it re-renders.
/// </summary>
public sealed class ClientRescheduleInput
{
    public DateOnly? Date { get; set; }

    public TimeOnly? StartTime { get; set; }
}

/// <summary>
/// The reprogramming screen of one own individual turn (T21.4, RF-09). It carries only the safe public fields of the
/// reservation plus the chosen day and the free starts the service offers for that day with the same worker; it
/// never carries a private link token, an owner note or a health observation. The day and time selection mirrors the
/// owner agenda so the same worker's availability is presented the same way.
/// </summary>
public sealed record ClientRescheduleViewModel(
    string Slug,
    int BookingId,
    string ServiceName,
    string WorkerName,
    DateTime CurrentStartsAt,
    ClientRescheduleInput Input,
    OwnerRescheduleOptions? Options)
{
    /// <summary>The day the form is showing: the requested one, or the current start's day before anything is chosen.</summary>
    public DateOnly SelectedDate => Input.Date ?? DateOnly.FromDateTime(CurrentStartsAt);

    public static ClientRescheduleViewModel From(
        string slug, ClientPanelBooking booking, ClientRescheduleInput input, OwnerRescheduleOptions? options) => new(
        slug,
        booking.BookingId,
        booking.ServiceName,
        booking.WorkerName,
        booking.StartsAt,
        input,
        options);
}
