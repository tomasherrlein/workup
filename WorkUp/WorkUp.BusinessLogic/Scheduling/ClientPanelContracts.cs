using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>
/// Persistence projection of one booking of the authenticated client's own agenda (T21.1). It carries only what the
/// panel needs to render a row and to decide which actions are still available before the start; it never selects
/// health observations, owner notes or the private link token.
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
    BookingModality? Modality);

/// <summary>
/// Safe public projection of one of the client's own bookings (T21.1, RF-08, RF-22). It is deliberately minimal:
/// date and time, service, worker, the booking and class state, and whether the still-available actions can be
/// offered. It never exposes clinical observations, owner notes or any token.
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
    bool CanCancel);

/// <summary>
/// The client's own agenda split by the clock: future bookings first, then history, both deterministically ordered
/// (T21.1, RF-11). The exact start already counts as started, so that booking belongs to history and offers no action.
/// </summary>
public sealed record ClientPanelAgenda(
    IReadOnlyList<ClientPanelBooking> Upcoming,
    IReadOnlyList<ClientPanelBooking> History);
