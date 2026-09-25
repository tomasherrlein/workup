using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Derived state of a fixed annotation of one dated class session (D-16, D-23). It is never persisted: the service
/// recomputes it from the enrollment status and the membership periods that cover the class date, so it is defined
/// here instead of reusing the week generation surface (T13 keeps its own <c>WeekFixedStudentState</c> contract).
/// <para>
/// <c>PendingPayment</c> is an enrollment that has not paid its first period yet; <c>Active</c> is an enrollment whose
/// paid periods cover the class date; <c>PendingRenewal</c> is an active enrollment whose periods do not cover it and
/// that keeps its place until the owner renews or cancels (D-23). A drop-in annotation has no fixed state at all.
/// </para>
/// <para>
/// The state is derived from the enrollment status and the membership calendar on the class date; the read never
/// re-decides whether the enrollment is eligible. An <c>Annulled</c> or <c>Terminated</c> enrollment whose
/// non-cancelled fixed annotation survived (T15 cancels the fixed annotations when it annuls or terminates, but no
/// schema constraint prevents the anomaly) therefore falls through to the calendar: <c>Active</c> when a paid period
/// covers the class date, <c>PendingRenewal</c> otherwise. That divergence from T13's explicit exclusion of those
/// statuses is deliberate: T13 answers which future sessions to annotate, while this state answers what an existing
/// dated annotation means, and for a past class the coverage is a fact even after the enrollment ends. The read shows
/// the row instead of hiding it because it is a real annotation of the session, and an annulled enrollment carries no
/// periods, so it reads <c>PendingRenewal</c> rather than coverage.
/// </para>
/// </summary>
public enum ClassFixedStudentState
{
    PendingPayment,
    Active,
    PendingRenewal
}

/// <summary>
/// The dated class session the detail shows (RF-10, RF-11, RF-12, RF-22): its identity, the service and the worker,
/// the capacity, the appointment status and the suspension reason when it was suspended. <see cref="SuspensionReason"/>
/// is only present with <see cref="AppointmentStatus.Suspended"/>; suspending a class never alters the grid or its
/// annotations (RF-10, D-15).
/// </summary>
public sealed record ClassDetailSession(
    int AppointmentId,
    int ServiceId,
    int WorkerId,
    DateTime StartsAt,
    int MaxCapacity,
    AppointmentStatus Status,
    string? SuspensionReason)
{
    public DateOnly Date => DateOnly.FromDateTime(StartsAt);

    public TimeOnly StartTime => TimeOnly.FromDateTime(StartsAt);

    public bool Suspended => Status == AppointmentStatus.Suspended;
}

/// <summary>
/// One annotation of the session as the detail reads it (RF-11, RF-12, RF-22): which booking it is, which client
/// holds it, that client's full name and email, the modality, the enrollment it belongs to when it is fixed, its
/// booking status and the derived fixed state. <see cref="ClientEmail"/> is the address the per-student suspension
/// notice is sent to, and it is empty when the client row could not be resolved: the annotation still belongs to the
/// session and is still counted, only that display and contact field is missing.
/// <para>
/// Cancelled annotations stay visible with <see cref="BookingStatus.Cancelled"/> as their status: the owner has to
/// see that the place was released («Cancelada (lugar liberado)») and whose place it was, and the status is what lets
/// the screen tell an active annotation from a cancelled one. Only <see cref="BookingStatus.Absent"/> counts as an
/// absence — no other status is one, and a cancelled annotation is not an absence either (RF-22, RF-12).
/// </para>
/// <para>
/// <see cref="FixedState"/> is <c>null</c> for a drop-in annotation, which has no fixed state; <see cref="EnrollmentId"/>
/// is only set for a fixed one. A fixed annotation keeps its derived state whatever its booking status is, because the
/// state describes the enrollment, not the attendance.
/// </para>
/// </summary>
public sealed record ClassDetailStudent(
    int BookingId,
    int ClientId,
    string ClientName,
    string ClientEmail,
    BookingModality? Modality,
    int? EnrollmentId,
    BookingStatus Status,
    ClassFixedStudentState? FixedState)
{
    public bool IsFixed => Modality == BookingModality.Fixed;

    public bool IsDropIn => Modality == BookingModality.DropIn;

    public bool IsCancelled => Status == BookingStatus.Cancelled;

    public bool IsAbsent => Status == BookingStatus.Absent;
}

/// <summary>
/// Read-only detail of one class session (RF-10, RF-11, RF-12, RF-22): the session, every annotation it holds and the
/// occupancy resolved by the single capacity rule. Cancelled annotations are part of <see cref="Students"/> — the
/// detail never hides who released a place — but they do not occupy one, so <see cref="PlacesTaken"/> counts every
/// non-cancelled annotation, fixed and drop-in alike, exactly as <c>ClassCapacity</c> does. <see cref="Students"/> is
/// in a deterministic order (by booking id, the order storage assigns) so the screen and its tests never depend on
/// the read's row order.
/// </summary>
public sealed record ClassDetail(
    ClassDetailSession Session,
    IReadOnlyList<ClassDetailStudent> Students,
    int PlacesTaken,
    int RemainingPlaces)
{
    public bool Suspended => Session.Suspended;
}

/// <summary>
/// One annotation of the detail read with everything the derived state and the per-student notice need (RF-11,
/// RF-22): the booking, the client's name and email, its modality and, for a fixed annotation, the enrollment status
/// and every membership period paid for that enrollment. <see cref="ClientEmail"/> is empty when the client row could
/// not be resolved. <see cref="EnrollmentStatus"/> and <see cref="Periods"/> are empty or <c>null</c> for a drop-in,
/// which has neither.
/// </summary>
public sealed record ClassDetailBookingRow(
    int BookingId,
    int ClientId,
    string ClientName,
    string ClientEmail,
    BookingModality? Modality,
    int? EnrollmentId,
    BookingStatus Status,
    EnrollmentStatus? EnrollmentStatus,
    IReadOnlyList<MembershipPeriod> Periods);

/// <summary>
/// A class session with every annotation it holds, as storage returns it (RF-10, RF-11, RF-22). The read is scoped by
/// business and matched by id: a foreign or missing session is not found, never an error, and a session that is not a
/// <c>ClassSession</c> is not found either. The <see cref="Kind"/> discriminant travels with the row so the service
/// can reject an individual turn even if an implementation forgot to filter it. The service derives the states and
/// the occupancy from this shape; the read only supplies the facts.
/// </summary>
public sealed record ClassDetailSessionRow(
    int AppointmentId,
    int ServiceId,
    int WorkerId,
    DateTime StartsAt,
    int MaxCapacity,
    AppointmentKind Kind,
    AppointmentStatus Status,
    string? SuspensionReason,
    IReadOnlyList<ClassDetailBookingRow> Bookings);
