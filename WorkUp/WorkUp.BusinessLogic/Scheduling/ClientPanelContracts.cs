using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>
/// Persistence projection of one booking of the authenticated client's own agenda (T21.1, T21.4). It carries only
/// what the panel needs to render a row and to decide which actions are still available before the start; it never
/// selects health observations, owner notes or the private link token. The eligibility of a reprogramming (T21.4)
/// needs the same raw shape the transactional core revalidates under lock, so the projection keeps the appointment
/// origin and capacity, the enrollment and the confirmation/cancellation stamps; none of them reaches the public
/// view model, which exposes only the resulting typed flag.
/// </summary>
public sealed record ClientPanelBookingRecord(
    int BookingId,
    DateTime StartsAt,
    DateTime EndsAt,
    string ServiceName,
    string WorkerName,
    AppointmentKind AppointmentKind,
    AppointmentStatus AppointmentStatus,
    BookingStatus BookingStatus,
    BookingModality? Modality,
    AppointmentOrigin? Origin,
    int MaxCapacity,
    int? EnrollmentId,
    DateTime? ConfirmedAt,
    DateTime? CancelledAt,
    CancelledBy? CancelledBy,
    DateTime? AttendanceRecordedAt,
    AbsenceSource? AbsenceSource);

/// <summary>
/// Safe public projection of one of the client's own bookings (T21.1, RF-08, RF-09, RF-22). It is deliberately
/// minimal: date and time, service, worker, the booking and class state, and whether the still-available actions can
/// be offered. It never exposes clinical observations, owner notes or any token.
/// <see cref="CanReschedule"/> is the typed eligibility of a reprogramming (T21.4); it is derived from the raw shape,
/// never from a display label.
/// </summary>
public sealed record ClientPanelBooking(
    int BookingId,
    DateTime StartsAt,
    DateTime EndsAt,
    string ServiceName,
    string WorkerName,
    AppointmentKind AppointmentKind,
    AppointmentStatus AppointmentStatus,
    BookingStatus BookingStatus,
    BookingModality? Modality,
    bool CanConfirmIntention,
    bool CanCancel,
    bool CanReschedule);

/// <summary>
/// The client's own agenda split by the clock: future bookings first, then history, both deterministically ordered
/// (T21.1, RF-11). The exact start already counts as started, so that booking belongs to history and offers no action.
/// </summary>
public sealed record ClientPanelAgenda(
    IReadOnlyList<ClientPanelBooking> Upcoming,
    IReadOnlyList<ClientPanelBooking> History);
