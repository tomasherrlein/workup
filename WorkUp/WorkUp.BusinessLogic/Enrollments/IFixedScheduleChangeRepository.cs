using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Enrollments;

/// <summary>
/// Persistence boundary of the guided fixed-schedule change (RF-21, D-19). It carries only what the existing
/// enrollment boundary does not already own: the read that decides whether future generated sessions block the change,
/// and the guarded write that closes one assignment and opens another. Every method is scoped by <c>businessId</c> and
/// runs inside the caller's ambient transaction, which owns the commit.
/// <para>
/// The locks, the permanent-seat read and the enrollment state come from <see cref="IEnrollmentRepository"/>: the
/// change requests the old and the new grid class in ascending id order and the enrollment row after them, which is
/// the intended serialization order for concurrent changes of one slot. It is not a deadlock-freedom guarantee: the
/// order in which SQL Server acquires those requested locks is not fixed by the request order. The capacity predicate
/// lives in <see cref="Domain.Rules.FixedSeatCapacity"/> and is never duplicated here.
/// </para>
/// </summary>
public interface IFixedScheduleChangeRepository
{
    /// <summary>
    /// The latest generated class session of the affected grid classes that falls on or after
    /// <paramref name="fromDate"/>, or <c>null</c> when there is none. Only the sessions of the requested grid classes
    /// of the business are read: an unrelated class never blocks the change. The date is what the caller answers with
    /// so a rejected change can ask for a later effective date.
    /// <para>
    /// It must run inside the transaction that holds the grid class locks, and a session that already exists on the
    /// effective date or later is reason enough for the caller to refuse: migrating or rewriting existing
    /// annotations is out of scope, so the owner moves the effective date instead.
    /// </para>
    /// </summary>
    Task<DateOnly?> LastGeneratedSessionDateAsync(
        int businessId, IReadOnlyList<int> gridClassIds, DateOnly fromDate, CancellationToken ct = default);

    /// <summary>
    /// Closes the still-open assignment of <paramref name="currentGridClassId"/> with
    /// <paramref name="currentValidTo"/> (the day before the effective date) and inserts
    /// <paramref name="newFixedClass"/> as the new open assignment, atomically inside the caller's transaction. It
    /// writes only while the previous row is still open and belongs to the enrollment and the business, so the losing
    /// side of a race changes nothing and is reported as
    /// <see cref="FixedScheduleChangeWriteResultType.PreviousAssignmentChanged"/> instead of producing a second open
    /// assignment for the same slot. No row is deleted and an already closed assignment is never reopened.
    /// <para>
    /// <paramref name="currentValidTo"/> must never precede the start of the previous row
    /// (<c>CK_EnrollmentFixedClasses_Range</c>); the caller rejects that case before writing, so a malformed range is
    /// never handed to storage.
    /// </para>
    /// </summary>
    Task<FixedScheduleChangeWriteResult> TryReplaceFixedClassAsync(
        int businessId,
        int enrollmentId,
        int currentGridClassId,
        DateOnly currentValidTo,
        EnrollmentFixedClass newFixedClass,
        CancellationToken ct = default);
}
