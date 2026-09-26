using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>
/// Persistence projection of one booking that the authenticated client may confirm or cancel from the panel (T21.2,
/// RF-08, RF-22). It carries the booking and class state the rules need, plus the tenant the row belongs to, and
/// nothing else: no email, no private link token, no clinical or owner data. The row is always read scoped by
/// <c>BusinessId</c> and <c>ClientId</c> taken from the client session cookie, never by email or token.
/// </summary>
public sealed record ClientBookingActionState(
    int BusinessId,
    int BookingId,
    int AppointmentId,
    int WorkerId,
    DateTime StartsAt,
    DateTime EndsAt,
    AppointmentKind AppointmentKind,
    AppointmentStatus AppointmentStatus,
    AppointmentOrigin? Origin,
    int MaxCapacity,
    BookingModality? Modality,
    int? EnrollmentId,
    BookingStatus BookingStatus,
    DateTime? ConfirmedAt,
    DateTime? CancelledAt,
    CancelledBy? CancelledBy,
    DateTime? AttendanceRecordedAt,
    AbsenceSource? AbsenceSource);
