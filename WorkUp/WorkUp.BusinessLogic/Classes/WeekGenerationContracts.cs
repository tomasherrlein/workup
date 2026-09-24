using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>Whether the preview would create the session or already found it dated in the week (RF-06).</summary>
public enum WeekSessionKind
{
    New,
    Existing
}

/// <summary>
/// State of a fixed student as shown by the preview. <c>PendingRenewal</c> is derived for the preview only and never
/// persisted as a status (D-23).
/// </summary>
public enum WeekFixedStudentState
{
    PendingPayment,
    Active,
    PendingRenewal
}

/// <summary>One active grid row of the business, projected for week generation.</summary>
public sealed record WeekGridClassRow(
    int GridClassId,
    int ServiceId,
    int WorkerId,
    DayOfWeek DayOfWeek,
    TimeOnly StartTime,
    int DurationMinutes,
    int MaxCapacity);

/// <summary>
/// An existing booking in a dated session. Cancelled bookings are included so the preview can keep them visible
/// instead of announcing a recreation.
/// </summary>
public sealed record WeekSessionBookingRow(
    int BookingId,
    int EnrollmentId,
    int ClientId,
    string ClientName,
    BookingStatus Status);

/// <summary>A dated session already generated from a grid class, with its attendees.</summary>
public sealed record WeekSessionRow(
    int AppointmentId,
    int GridClassId,
    DateOnly Date,
    TimeOnly StartTime,
    AppointmentStatus Status,
    IReadOnlyList<WeekSessionBookingRow> Bookings);

/// <summary>
/// A fixed-class assignment with every membership period paid for its enrollment. Date validity and coverage are
/// resolved by the caller for the concrete session date, because both depend on that date (D-16, D-23).
/// </summary>
public sealed record WeekFixedAssignmentRow(
    int EnrollmentId,
    int GridClassId,
    int ClientId,
    string ClientName,
    EnrollmentStatus EnrollmentStatus,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    IReadOnlyList<MembershipPeriod> Periods)
{
    public bool IsValidOn(DateOnly date) => date >= ValidFrom && (ValidTo is null || date <= ValidTo);
}

/// <summary>
/// Tenant-scoped, read-only inputs for one week. <see cref="BusinessId"/> is echoed so the service can reject a
/// snapshot that belongs to another business before projecting anything.
/// </summary>
public sealed record WeekGenerationSnapshot(
    int BusinessId,
    IReadOnlyList<WeekGridClassRow> GridClasses,
    IReadOnlyList<WeekSessionRow> Sessions,
    IReadOnlyList<WeekFixedAssignmentRow> FixedAssignments);

/// <summary>A fixed student of one previewed session.</summary>
public sealed record WeekFixedStudentPreview(
    int EnrollmentId,
    int ClientId,
    string ClientName,
    WeekFixedStudentState State,
    bool AlreadyBooked,
    bool Cancelled);

/// <summary>One session of the previewed week: new or already generated, with its fixed students.</summary>
public sealed record WeekSessionPreview(
    int GridClassId,
    int ServiceId,
    int WorkerId,
    DateOnly Date,
    TimeOnly StartTime,
    WeekSessionKind Kind,
    int? AppointmentId,
    AppointmentStatus? Status,
    bool Suspended,
    bool HasCancelledBookings,
    IReadOnlyList<WeekFixedStudentPreview> FixedStudents)
{
    public bool IsNew => Kind == WeekSessionKind.New;
}

/// <summary>Read-only preview of a Monday-to-Sunday week. Confirming it is a later unit of work (T13).</summary>
public sealed record WeekGenerationPreview(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    IReadOnlyList<WeekSessionPreview> Sessions)
{
    public int NewSessionCount => Sessions.Count(session => session.Kind == WeekSessionKind.New);

    public int ExistingSessionCount => Sessions.Count(session => session.Kind == WeekSessionKind.Existing);
}

/// <summary>A fixed student still to be annotated in a planned session, as the confirm flow will write it (RF-06).</summary>
public sealed record WeekStudentToAnnotate(int EnrollmentId, int ClientId);

/// <summary>
/// One session the confirm flow will write for a grid class of the week. <see cref="AppointmentId"/> is null only for a
/// session that does not exist yet; an existing one is reused as it is, including a suspension, and only its missing
/// fixed annotations are added. <see cref="Students"/> excludes any enrollment that already has a booking in the
/// session, active or cancelled, so regenerating never duplicates and never resurrects a cancellation.
/// </summary>
public sealed record WeekSessionPlan(
    int GridClassId,
    int ServiceId,
    int WorkerId,
    DateOnly Date,
    TimeOnly StartTime,
    int DurationMinutes,
    int MaxCapacity,
    int? AppointmentId,
    IReadOnlyList<WeekStudentToAnnotate> Students);

/// <summary>
/// Everything one confirm call writes, resolved from a single read of the week. <see cref="ExistingSessionCount"/> counts
/// the sessions that already exist, and <see cref="PendingRenewalStudentCount"/> the annotated students whose derived
/// state is "Pendiente de renovación" (D-23); that state is counted for the owner, never persisted.
/// </summary>
public sealed record WeekGenerationPlan(
    int BusinessId,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    DateTime Now,
    IReadOnlyList<WeekSessionPlan> Sessions,
    int ExistingSessionCount,
    int PendingRenewalStudentCount);

/// <summary>What the write actually created, so a retried generation reports zero instead of failing.</summary>
public sealed record WeekGenerationWriteOutcome(int CreatedSessions, int CreatedFixedBookings);

/// <summary>Result shown to the owner after confirming a week: the bounds, what was created and what was reused.</summary>
public sealed record WeekGenerationConfirmation(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    int CreatedSessions,
    int CreatedFixedBookings,
    int ExistingSessions,
    int PendingRenewalStudents);
