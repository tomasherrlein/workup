using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

/// <summary>One generated class session of the fake, owned by a business and dated. No bookings are needed: RF-21 only
/// asks whether a session already exists on or after the effective date.</summary>
public sealed record FakeGeneratedClassSession(int BusinessId, int GridClassId, DateOnly Date);

/// <summary>
/// In-memory stand-in for the schedule-change persistence port (RF-21). It shares the
/// <see cref="FakeEnrollmentRepository"/> rows, exactly like both SQL repositories share one database and one
/// transaction: the guarded write closes the still-open assignment and inserts the new one in the caller's storage, so
/// a test reads the resulting rows instead of trusting a recorded request.
/// <para>
/// It emulates the tenant scope, the strictly on-or-after session read, the guarded write and the lost race. It does
/// not emulate SQL, locks or the check constraint; the caller's own guards are what the tests exercise, and the opt-in
/// SQL fixture is what must prove the statements.
/// </para>
/// </summary>
public sealed class FakeFixedScheduleChangeRepository(FakeEnrollmentRepository enrollments)
    : IFixedScheduleChangeRepository
{
    public List<FakeGeneratedClassSession> Sessions { get; } = [];

    public int NextFixedClassId { get; set; } = 5000;

    /// <summary>
    /// When true the guarded write finds the previous assignment already closed, like a concurrent change that won
    /// between the validation and the write.
    /// </summary>
    public bool MarkCurrentAssignmentClosedBeforeWrite { get; set; }

    public int LastGeneratedSessionCallCount { get; private set; }
    public IReadOnlyList<int>? LastGeneratedGridClassIds { get; private set; }
    public DateOnly? LastGeneratedFromDate { get; private set; }

    public int ReplaceCallCount { get; private set; }
    public (int EnrollmentId, int CurrentGridClassId, DateOnly CurrentValidTo, EnrollmentFixedClass NewFixedClass)?
        LastReplaceRequest { get; private set; }

    public Task<DateOnly?> LastGeneratedSessionDateAsync(
        int businessId, IReadOnlyList<int> gridClassIds, DateOnly fromDate, CancellationToken ct = default)
    {
        LastGeneratedSessionCallCount++;
        LastGeneratedGridClassIds = gridClassIds;
        LastGeneratedFromDate = fromDate;

        var last = Sessions
            .Where(session => session.BusinessId == businessId)
            .Where(session => gridClassIds.Contains(session.GridClassId))
            .Where(session => session.Date >= fromDate)
            .Select(session => (DateOnly?)session.Date)
            .Max();

        return Task.FromResult(last);
    }

    /// <summary>
    /// Emulates the guarded close and insert. The previous row is only closed while it is still open and belongs to the
    /// requested grid class, so a lost race reports <c>PreviousAssignmentChanged</c> without touching any row.
    /// </summary>
    public Task<FixedScheduleChangeWriteResult> TryReplaceFixedClassAsync(
        int businessId,
        int enrollmentId,
        int currentGridClassId,
        DateOnly currentValidTo,
        EnrollmentFixedClass newFixedClass,
        CancellationToken ct = default)
    {
        ReplaceCallCount++;
        LastReplaceRequest = (enrollmentId, currentGridClassId, currentValidTo, newFixedClass);

        var previous = enrollments.FixedClasses.FirstOrDefault(fixedClass =>
            fixedClass.EnrollmentId == enrollmentId
            && fixedClass.GridClassId == currentGridClassId
            && fixedClass.ValidTo is null);

        if (MarkCurrentAssignmentClosedBeforeWrite && previous is not null)
            previous.ValidTo = previous.ValidFrom;

        if (previous is null || previous.ValidTo is not null)
            return Task.FromResult(FixedScheduleChangeWriteResult.PreviousAssignmentChanged());

        previous.ValidTo = currentValidTo;

        var newId = NextFixedClassId++;
        enrollments.FixedClasses.Add(new EnrollmentFixedClass
        {
            Id = newId,
            EnrollmentId = enrollmentId,
            GridClassId = newFixedClass.GridClassId,
            ValidFrom = newFixedClass.ValidFrom,
            ValidTo = newFixedClass.ValidTo
        });

        return Task.FromResult(FixedScheduleChangeWriteResult.Replaced(newId));
    }
}
