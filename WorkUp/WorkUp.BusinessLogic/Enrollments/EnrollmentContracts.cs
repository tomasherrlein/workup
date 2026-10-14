using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Enrollments;

/// <summary>
/// Views of the membership list (RF-29). <see cref="All"/> is the only view that shows annulled and terminated
/// enrollments, each one with its own status label; every other view is a derived calendar state, never a persisted
/// one. The filter is a request value and is never stored, so its zero member is <see cref="All"/> on purpose.
/// </summary>
public enum EnrollmentListFilter
{
    All = 0,
    Current = 1,
    ExpiringSoon = 2,
    Expired = 3,
    PendingPayment = 4
}

/// <summary>Thresholds of the derived membership list, kept in one place so the screen and the service cannot diverge.</summary>
public static class MembershipListRules
{
    /// <summary>
    /// Days of notice of the expiry warning. It is three because RF-30 step 1 sends the "your membership expires"
    /// email three days before the expiry date, and the <c>Vencen pronto</c> view exists so the owner sees exactly the
    /// memberships that warning is about; changing one without the other would show the owner a list that disagrees
    /// with the emails the students received.
    /// </summary>
    public const int ExpiresSoonThresholdDays = 3;
}

/// <summary>The client of an enrollment, reduced to what the flow reads: the id it validates and the name it lists.</summary>
public sealed record EnrollmentClient(int ClientId, string FullName);

/// <summary>
/// One active-or-archived grid class of the business with the capacity of its service (RF-04, RF-05 step 2). The
/// capacity is <c>Services.MaxCapacity</c>, the same number the dated sessions are generated with, so the permanent
/// seats of the grid class and the places of its sessions share one limit.
/// </summary>
public sealed record EnrollmentGridClassRow(
    int GridClassId,
    int ServiceId,
    int WorkerId,
    DayOfWeek DayOfWeek,
    TimeOnly StartTime,
    int MaxCapacity,
    bool IsActive);

/// <summary>
/// The enrollment the owner registers for a student (RF-05 steps 1-3). The amount is optional: when it is omitted the
/// agreed price is the monthly price configured for that weekly frequency, and any other amount needs
/// <see cref="PriceReason"/> (D-20). <see cref="EffectiveFrom"/> is the first day the chosen fixed classes are valid,
/// which is also the reference date of the permanent-seat check.
/// </summary>
public sealed record CreateEnrollmentRequest(
    int ClientId,
    int WeeklyFrequency,
    decimal? AgreedPrice,
    string? PriceReason,
    IReadOnlyList<int> GridClassIds,
    DateOnly EffectiveFrom);

/// <summary>
/// A membership payment (RF-05 step 5, RF-29 step 3). An omitted amount charges the agreed price of the enrollment;
/// any other amount needs <see cref="Reason"/> (D-20).
/// </summary>
public sealed record ChargeMembershipRequest(int EnrollmentId, decimal? Amount, string? Reason);

/// <summary>The pending enrollment the owner annuls (RF-20).</summary>
public sealed record AnnulEnrollmentRequest(int EnrollmentId);

/// <summary>The membership the owner ends, from the chosen effective date on (RF-29 step 6).</summary>
public sealed record TerminateEnrollmentRequest(int EnrollmentId, DateOnly EffectiveFrom);

/// <summary>
/// The registered pending enrollment (RF-05 output). It already holds its fixed seats; no membership period exists
/// until the first payment.
/// </summary>
public sealed record PendingEnrollmentOutcome(
    int EnrollmentId,
    int ClientId,
    int WeeklyFrequency,
    decimal AgreedPrice,
    DateOnly EffectiveFrom,
    IReadOnlyList<int> GridClassIds);

/// <summary>
/// What the fixed-annotation sync did when a membership was activated or renewed (RF-05 step 6, RF-06 step 3).
/// <see cref="CreatedAnnotations"/> counts the annotations this call wrote, <see cref="OmittedAnnotations"/> the
/// sessions the enrollment already had an annotation in — present or cancelled, because a cancelled one is never
/// resurrected — and <see cref="RefusedWithoutSeat"/> the sessions that had no free place left, so the fixed seat
/// never overbooks a generated session. <see cref="SessionIds"/> is every session the sync considered, ordered by id,
/// so the caller can report what it inspected and not only what it wrote. It is the T15 channel for the sync result:
/// <c>WeekGenerationConfirmation</c> (T13) is not modified.
/// </summary>
public sealed record EnrollmentSyncOutcome(
    int CreatedAnnotations,
    int OmittedAnnotations,
    int RefusedWithoutSeat,
    IReadOnlyList<int> SessionIds);

/// <summary>
/// Result of a membership payment (RF-05 step 6, RF-29). One payment writes exactly one period
/// (<c>UQ_MembershipPeriods_Payment</c>), so <see cref="CoverageStart"/>..<see cref="ExpiresOn"/> is the coverage the
/// amount bought and <see cref="ReferenceDay"/> the day of the month the next renewal is computed from (D-23).
/// <see cref="Activated"/> distinguishes the first payment (which activates the enrollment) from a renewal.
/// </summary>
public sealed record MembershipChargeOutcome(
    int EnrollmentId,
    int ClientId,
    int PaymentId,
    int MembershipPeriodId,
    DateOnly CoverageStart,
    DateOnly ExpiresOn,
    int ReferenceDay,
    decimal Amount,
    bool Activated,
    EnrollmentSyncOutcome Sync);

/// <summary>What annulling a pending enrollment released (RF-20). Rows are never deleted.</summary>
public sealed record EnrollmentAnnulmentOutcome(int EnrollmentId, int CancelledAnnotations);

/// <summary>What ending a membership released from the effective date on (RF-29 step 6).</summary>
public sealed record EnrollmentTerminationOutcome(
    int EnrollmentId, DateOnly EffectiveFrom, int CancelledAnnotations);

/// <summary>
/// One row of the membership list (RF-29). Every state is derived on read and never persisted (D-23):
/// <see cref="MembershipState"/> is the calendar state of the paid periods on the reference date,
/// <see cref="CurrentPeriodEnd"/> is the expiry the owner has to resolve — the one in force, or the day the membership
/// lapsed — <see cref="PendingPayment"/> is the pending-payment registration (RF-05 step 3) and
/// <see cref="ExpiresSoon"/> is the derived <c>Vencen pronto</c> flag, three days before the expiry (RF-30).
/// </summary>
public sealed record EnrollmentListItem(
    int EnrollmentId,
    int ClientId,
    string ClientName,
    int WeeklyFrequency,
    EnrollmentStatus Status,
    DateOnly? CurrentPeriodEnd,
    MembershipState MembershipState,
    bool PendingPayment,
    bool ExpiresSoon);

/// <summary>
/// One enrollment read under the lock of its own row (its status is what the payment, annulment and termination
/// transitions change), with everything those rules need: its fixed assignments and its paid periods.
/// </summary>
public sealed record EnrollmentState(
    Enrollment Enrollment,
    IReadOnlyList<EnrollmentFixedClass> FixedClasses,
    IReadOnlyList<MembershipPeriod> Periods);

/// <summary>One enrollment of the list with the periods its derived state is computed from. No write uses this shape.</summary>
public sealed record EnrollmentListRow(
    int EnrollmentId,
    int ClientId,
    string ClientName,
    int WeeklyFrequency,
    EnrollmentStatus Status,
    int? ReferenceDay,
    DateTime? ActivatedAt,
    IReadOnlyList<MembershipPeriod> Periods);

/// <summary>The enrollment and its fixed assignments to write together, in the caller's transaction.</summary>
public sealed record EnrollmentInsert(Enrollment Enrollment, IReadOnlyList<EnrollmentFixedClass> FixedClasses);

/// <summary>
/// How the guarded enrollment insert ended. <see cref="Inserted"/> carries the new enrollment id;
/// <see cref="NoSeatLeft"/> is the lost race for the last permanent seat of <c>GridClassId</c>, which means a
/// competing enrollment took it between the seat read and the write. Both members are outcomes and none of them is
/// <c>0</c>, so an undefined value — the enum default included — is a broken implementation the caller must fail on
/// instead of reading it as a successful insert with a zero id.
/// </summary>
public enum EnrollmentInsertResultType
{
    Inserted = 1,
    NoSeatLeft = 2
}

/// <summary>Result of the guarded enrollment insert. <see cref="EnrollmentId"/> only means something when it inserted.</summary>
public sealed record EnrollmentInsertResult(
    EnrollmentInsertResultType Result, int EnrollmentId, int GridClassId)
{
    public static EnrollmentInsertResult Inserted(int enrollmentId) =>
        new(EnrollmentInsertResultType.Inserted, enrollmentId, 0);

    public static EnrollmentInsertResult NoSeatLeft(int gridClassId) =>
        new(EnrollmentInsertResultType.NoSeatLeft, 0, gridClassId);

    public bool IsInserted => Result == EnrollmentInsertResultType.Inserted;
}

/// <summary>
/// One annotation of a generated session as the sync read returns it: which enrollment holds it and its status.
/// Cancelled annotations are included on purpose, because a cancelled one must never be resurrected, and the statuses
/// of every annotation — the fixed ones of other students and the drop-ins — are what the capacity check counts.
/// </summary>
public sealed record EnrollmentSyncBooking(int EnrollmentId, BookingStatus Status);

/// <summary>
/// A generated session the sync has to consider (RF-05 step 6). It is a session of one of the enrollment's fixed grid
/// classes whose start has not passed, with the capacity of the session and all of its annotations. An individual
/// turn is never part of the answer: only a dated class session carries fixed annotations.
/// </summary>
public sealed record EnrollmentSyncSessionRow(
    int AppointmentId,
    int GridClassId,
    DateTime StartsAt,
    int MaxCapacity,
    IReadOnlyList<EnrollmentSyncBooking> Bookings);

/// <summary>
/// The fixed annotation to write in a generated session when the enrollment is activated or renewed. The business is
/// the one of the method call, so the row does not carry a second copy of it.
/// </summary>
public sealed record FixedAnnotationInsert(
    int AppointmentId, int EnrollmentId, int ClientId, DateTime CreatedAt);

/// <summary>
/// How one fixed-annotation insert ended. <see cref="Inserted"/> wrote the annotation;
/// <see cref="NoSeatLeft"/> lost the race for the last place of the session, so the fixed seat never overbooks it;
/// <see cref="AlreadyPresent"/> means the enrollment already had an annotation — cancelled or not — or the client
/// already held a non-cancelled booking of the session, so nothing was written and no cancellation was resurrected.
/// Only these three members are outcomes and none of them is <c>0</c>: an undefined value is a broken implementation
/// and the caller must fail loudly.
/// </summary>
public enum FixedAnnotationInsertResultType
{
    Inserted = 1,
    NoSeatLeft = 2,
    AlreadyPresent = 3
}

/// <summary>Result of one guarded fixed-annotation insert, carrying the session it belongs to.</summary>
public sealed record FixedAnnotationInsertResult(
    FixedAnnotationInsertResultType Result, int AppointmentId)
{
    public static FixedAnnotationInsertResult Inserted(int appointmentId) =>
        new(FixedAnnotationInsertResultType.Inserted, appointmentId);

    public static FixedAnnotationInsertResult NoSeatLeft(int appointmentId) =>
        new(FixedAnnotationInsertResultType.NoSeatLeft, appointmentId);

    public static FixedAnnotationInsertResult AlreadyPresent(int appointmentId) =>
        new(FixedAnnotationInsertResultType.AlreadyPresent, appointmentId);
}
