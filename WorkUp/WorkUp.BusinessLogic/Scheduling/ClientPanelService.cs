using WorkUp.BusinessLogic.Abstractions;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>
/// Read-only "Mis turnos y clases" of the authenticated client panel (T21.1, T21.4, RF-08, RF-09, RF-22). Every read
/// is scoped to the tenant and ficha taken from the client session cookie (<c>BusinessId</c>, <c>ClientId</c>); the
/// entry point receives both as trusted identity arguments and never as a route or form value, so a caller cannot
/// ask for another client's agenda. The result is the safe public projection plus the actions still available before
/// the start; clinical observations and private link tokens never leave storage here.
/// <para>
/// The clock decides what is future: a booking whose start is still ahead is upcoming, and from its exact start on
/// it belongs to history and offers no action (RF-22). Fixed class dates are shown but never cancellable from this
/// panel (T21 scope decision, RF-21); individual turns and drop-ins are. The reprogramming flag (T21.4) mirrors the
/// eligibility the transactional core revalidates under lock, so the panel never offers a link for a shape the save
/// would refuse.
/// </para>
/// </summary>
public sealed class ClientPanelService(IClientPanelRepository repository, IClock clock)
{
    /// <summary>
    /// Agenda of the client identified by <paramref name="businessId"/> and <paramref name="clientId"/>, split into
    /// upcoming first and then history, both ordered deterministically by start (upcoming ascending, history
    /// descending, ties by booking id ascending).
    /// </summary>
    public async Task<ClientPanelAgenda> GetAgendaAsync(int businessId, int clientId, CancellationToken ct = default)
    {
        var now = clock.Now;
        var rows = await repository.ListAgendaAsync(businessId, clientId, ct);

        var upcoming = new List<ClientPanelBooking>();
        var history = new List<ClientPanelBooking>();
        foreach (var row in rows)
            (row.StartsAt > now ? upcoming : history).Add(Map(row, now));

        upcoming.Sort(CompareUpcoming);
        history.Sort(CompareHistory);
        return new ClientPanelAgenda(upcoming, history);
    }

    private static ClientPanelBooking Map(ClientPanelBookingRecord row, DateTime now)
    {
        var notStarted = now < row.StartsAt;
        var scheduled = row.AppointmentStatus == AppointmentStatus.Scheduled;
        var isIndividual = row.AppointmentKind == AppointmentKind.Individual;
        var pending = row.BookingStatus == BookingStatus.Pending;
        var confirmed = row.BookingStatus == BookingStatus.Confirmed;

        // Only individual turns and drop-in classes are cancelled from the panel; a fixed class date keeps its place
        // (RF-21, T21 scope decision). Confirmation is exclusive to individual turns (RF-08).
        var cancellableKind = isIndividual || row.Modality == BookingModality.DropIn;

        return new ClientPanelBooking(
            row.BookingId,
            row.StartsAt,
            row.EndsAt,
            row.ServiceName,
            row.WorkerName,
            row.AppointmentKind,
            row.AppointmentStatus,
            row.BookingStatus,
            row.Modality,
            CanConfirmIntention: isIndividual && scheduled && pending && notStarted,
            CanCancel: cancellableKind && scheduled && (pending || confirmed) && notStarted,
            CanReschedule: CanReschedule(row, notStarted));
    }

    /// <summary>
    /// Typed eligibility of a reprogramming (T21.4), byte for byte the shape the transactional client core accepts:
    /// an individual turn reserved by the client, with capacity one, scheduled, without modality or enrollment and
    /// without a cancellation, absence or attendance stamp, still ahead of the clock, and whose booking status agrees
    /// with its confirmation instant. A walk-in, a class date (fixed or drop-in), a cancelled or suspended turn, one
    /// already started and an inconsistent annotation are all excluded. The flag is derived from the raw shape, never
    /// from a display label.
    /// </summary>
    private static bool CanReschedule(ClientPanelBookingRecord row, bool notStarted) =>
        notStarted &&
        row.AppointmentKind == AppointmentKind.Individual &&
        row.Origin == AppointmentOrigin.Reserved &&
        row.MaxCapacity == 1 &&
        row.AppointmentStatus == AppointmentStatus.Scheduled &&
        row.Modality is null &&
        row.EnrollmentId is null &&
        row.CancelledAt is null &&
        row.CancelledBy is null &&
        row.AttendanceRecordedAt is null &&
        row.AbsenceSource is null &&
        ((row.BookingStatus == BookingStatus.Pending && row.ConfirmedAt is null) ||
         (row.BookingStatus == BookingStatus.Confirmed && row.ConfirmedAt is not null));

    private static int CompareUpcoming(ClientPanelBooking left, ClientPanelBooking right)
    {
        var byStart = left.StartsAt.CompareTo(right.StartsAt);
        return byStart != 0 ? byStart : left.BookingId.CompareTo(right.BookingId);
    }

    private static int CompareHistory(ClientPanelBooking left, ClientPanelBooking right)
    {
        var byStart = right.StartsAt.CompareTo(left.StartsAt);
        return byStart != 0 ? byStart : left.BookingId.CompareTo(right.BookingId);
    }
}
