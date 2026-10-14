namespace WorkUp.BusinessLogic.Enrollments;

/// <summary>
/// The guided fixed-schedule change the owner registers for one fixed class of an enrollment (RF-21, D-19). The owner
/// picks the new weekly class from the grid and the first day it is valid; everything before that date keeps the old
/// schedule, so the request never rewrites the past.
/// </summary>
public sealed record ChangeFixedScheduleRequest(
    int EnrollmentId,
    int CurrentGridClassId,
    int NewGridClassId,
    DateOnly EffectiveFrom);

/// <summary>
/// What the change wrote (RF-21). <see cref="PreviousValidTo"/> is the last day the previous fixed class is valid —
/// the day before <see cref="EffectiveFrom"/> — and <see cref="NewFixedClassId"/> the row opened from the effective
/// date on. The enrollment itself is untouched: its status, frequency, agreed price, reference day and paid periods
/// are the same before and after, because a schedule change only moves the weekly slot (RF-29).
/// </summary>
public sealed record FixedScheduleChangeOutcome(
    int EnrollmentId,
    int CurrentGridClassId,
    int NewGridClassId,
    DateOnly EffectiveFrom,
    DateOnly PreviousValidTo,
    int NewFixedClassId);

/// <summary>
/// How the guarded schedule-change write ended. <see cref="Replaced"/> closed the previous assignment and opened the
/// new one; <see cref="PreviousAssignmentChanged"/> means the previous row was no longer open when the write ran, so
/// it wrote nothing. Both members are outcomes and none of them is <c>0</c>: an undefined value is a broken
/// implementation the caller must fail loudly on.
/// </summary>
public enum FixedScheduleChangeWriteResultType
{
    Replaced = 1,
    PreviousAssignmentChanged = 2
}

/// <summary>
/// Result of the guarded schedule-change write. <see cref="NewFixedClassId"/> only means something when it replaced.
/// </summary>
public sealed record FixedScheduleChangeWriteResult(
    FixedScheduleChangeWriteResultType Result, int NewFixedClassId)
{
    public static FixedScheduleChangeWriteResult Replaced(int newFixedClassId) =>
        new(FixedScheduleChangeWriteResultType.Replaced, newFixedClassId);

    public static FixedScheduleChangeWriteResult PreviousAssignmentChanged() =>
        new(FixedScheduleChangeWriteResultType.PreviousAssignmentChanged, 0);

    public bool IsReplaced => Result == FixedScheduleChangeWriteResultType.Replaced;
}
