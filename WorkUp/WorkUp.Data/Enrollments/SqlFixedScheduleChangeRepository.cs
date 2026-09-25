using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Enrollments;

/// <summary>
/// SQL Server persistence for the guided fixed-schedule change (RF-21, D-19). It owns the two statements the
/// enrollment boundary does not already carry: the range read that decides whether future generated sessions block
/// the change — and that holds the lock keeping a concurrent week generation from slipping a session into the
/// affected range before the change commits — and the guarded close-and-open write that replaces one window without
/// deleting a row. Both run inside the caller's ambient transaction, which owns the commit: nothing here commits,
/// rolls back or writes outside the requested tenant.
/// <para>
/// The locks, the permanent-seat read and the enrollment state come from <see cref="IEnrollmentRepository"/> in the
/// service, so this class never re-implements the seat rule
/// (<see cref="Domain.Rules.FixedSeatCapacity"/>) nor the lock request order: the caller asks for the two grid
/// classes in ascending id order and then for the enrollment row, and both statements here run under those locks.
/// That request order is not evidence about the order SQL Server acquires them, so this class claims no freedom from
/// a two-writer deadlock.
/// </para>
/// </summary>
public sealed class SqlFixedScheduleChangeRepository(DbSession session)
    : SqlRepositoryBase(session), IFixedScheduleChangeRepository
{
    /// <summary>
    /// The generated sessions of one grid class from the effective date on, read with <c>UPDLOCK, HOLDLOCK</c>.
    /// <para>
    /// The read is deliberately a range read and not an aggregate. Its predicate is written to match the
    /// <c>(GridClassId, StartsAt)</c> prefix of
    /// <c>UX_Appointments_GridSession (GridClassId, StartsAt) WHERE GridClassId IS NOT NULL</c> from the effective
    /// date on, so the optimizer can seek that index and the combination <c>UPDLOCK, HOLDLOCK</c> asks for the
    /// serializable range lock held until the caller commits. The plan is deliberately not pinned: no <c>INDEX</c> or
    /// <c>FORCESEEK</c> hint is added, so a different plan — a scan, or an index whose key list does not carry the
    /// whole predicate — remains possible, and nothing here promises that this read always locks exactly that prefix.
    /// What the fixture observed instead is the behaviour that matters: while the guard transaction stayed open, the
    /// real week generation (T13) could not insert a session of an affected grid class on another connection and
    /// timed out, and the same insert went through right after the commit. The empty range is the case that matters,
    /// where the guard answers "no session" and lets the change proceed, so the gap itself must stay locked. A
    /// <c>MAX</c> aggregate is avoided for the same reason: the optimizer can answer it with a single backward seek
    /// that only walks and locks the keys it happens to read, which can leave the gap between the effective date and
    /// the last key unprotected. No <c>ROWLOCK</c> or <c>PAGLOCK</c> hint is used either: a row or page lock locks
    /// what exists and leaves an empty range unlocked. The combination <c>UPDLOCK, HOLDLOCK</c> is the one the
    /// week-generation guard already uses on this index.
    /// </para>
    /// <para>
    /// One statement per grid class instead of a single <c>IN</c> list: each call keeps its own equality predicate on
    /// <c>GridClassId</c> and its own lock range, and the locks accumulate in the caller's transaction. That keeps the
    /// requested classes independent — no class's range is stated in terms of the other's — without forcing any
    /// particular plan.
    /// </para>
    /// </summary>
    private const string LastGeneratedSessionSql = """
        SELECT a.StartsAt
          FROM Appointments AS a WITH (UPDLOCK, HOLDLOCK)
         WHERE a.BusinessId = @businessId
           AND a.GridClassId = @gridClassId
           AND a.StartsAt >= @fromDate
        """;

    /// <summary>
    /// Closes the window that is still open on the replaced grid class. The guard is the statement itself: it writes
    /// only while the row is still open, belongs to the enrollment of the business through the tenant join, is the
    /// requested grid class, and starts before the effective date
    /// (<c>efc.ValidFrom &lt; @newValidFrom</c>, which is also what keeps <c>ValidTo &gt;= ValidFrom</c> for
    /// <c>CK_EnrollmentFixedClasses_Range</c>). A writer that already closed it leaves this <c>UPDATE</c> with zero
    /// rows, and the caller answers a lost race instead of opening a second window on the same slot. The row lock the
    /// write takes is what serializes two replacements: the second one waits and re-evaluates its own predicate once
    /// the first commits, so it finds nothing open. The affected count is the answer; no row is deleted and an already
    /// closed window is never rewritten.
    /// </summary>
    private const string ClosePreviousFixedClassSql = """
        UPDATE efc
           SET ValidTo = @currentValidTo
          FROM EnrollmentFixedClasses AS efc
          JOIN Enrollments AS e ON e.Id = efc.EnrollmentId AND e.BusinessId = @businessId
         WHERE efc.EnrollmentId = @enrollmentId
           AND efc.GridClassId = @gridClassId
           AND efc.ValidTo IS NULL
           AND efc.ValidFrom < @newValidFrom
        """;

    /// <summary>
    /// Opens the new window from the effective date. It runs only after the guarded close wrote exactly one row, both
    /// inside the caller's transaction, so the unit leaves one open window on the slot and nothing else.
    /// </summary>
    private const string InsertReplacementFixedClassSql = """
        INSERT INTO EnrollmentFixedClasses (EnrollmentId, GridClassId, ValidFrom, ValidTo)
        OUTPUT INSERTED.Id
        VALUES (@enrollmentId, @gridClassId, @validFrom, @validTo)
        """;

    public async Task<DateOnly?> LastGeneratedSessionDateAsync(
        int businessId, IReadOnlyList<int> gridClassIds, DateOnly fromDate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gridClassIds);

        DateOnly? lastDate = null;

        // Repeating a grid class would only repeat the same seek and the same lock, so each requested class is read
        // once. No requested class is no range to lock and the answer is null, without opening a statement.
        foreach (var gridClassId in gridClassIds.Distinct())
        {
            var startsAt = await QueryAsync(
                LastGeneratedSessionSql,
                reader => reader.Get<DateTime>("StartsAt"),
                p => p
                    .AddValue("@businessId", businessId)
                    .AddValue("@gridClassId", gridClassId)
                    .AddValue("@fromDate", fromDate),
                ct);

            foreach (var instant in startsAt)
            {
                var date = DateOnly.FromDateTime(instant);
                if (lastDate is null || date > lastDate)
                    lastDate = date;
            }
        }

        return lastDate;
    }

    public async Task<FixedScheduleChangeWriteResult> TryReplaceFixedClassAsync(
        int businessId,
        int enrollmentId,
        int currentGridClassId,
        DateOnly currentValidTo,
        EnrollmentFixedClass newFixedClass,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newFixedClass);

        var closed = await ExecuteAsync(
            ClosePreviousFixedClassSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@enrollmentId", enrollmentId)
                .AddValue("@gridClassId", currentGridClassId)
                .AddValue("@currentValidTo", currentValidTo)
                .AddValue("@newValidFrom", newFixedClass.ValidFrom),
            ct);

        // Zero rows closed means the expected row was no longer open — another writer won the race, or it was closed
        // by an annulment or a termination. Nothing is inserted and nothing is committed here: the losing side changes
        // no row and the caller reports it, so the slot never ends with two open windows.
        if (closed == 0)
            return FixedScheduleChangeWriteResult.PreviousAssignmentChanged();

        // The enrollment of the new row is the tenant-scoped identity of the call, never the one carried by the
        // instance, so a mismatched argument cannot write an assignment for another enrollment.
        var newFixedClassId = await InsertAsync(
            InsertReplacementFixedClassSql,
            p => p
                .AddValue("@enrollmentId", enrollmentId)
                .AddValue("@gridClassId", newFixedClass.GridClassId)
                .AddValue("@validFrom", newFixedClass.ValidFrom)
                .AddValue("@validTo", newFixedClass.ValidTo),
            ct);

        return FixedScheduleChangeWriteResult.Replaced(newFixedClassId);
    }
}
