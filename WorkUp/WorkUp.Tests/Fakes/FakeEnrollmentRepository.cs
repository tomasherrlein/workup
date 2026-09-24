using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Fakes;

/// <summary>One client of the fake, owned by a business so a foreign tenant can be simulated.</summary>
public sealed record FakeEnrollmentClient(int BusinessId, EnrollmentClient Client);

/// <summary>One grid class of the fake, owned by a business, with the capacity of its service.</summary>
public sealed record FakeEnrollmentGridClass(int BusinessId, EnrollmentGridClassRow Row);

/// <summary>One mutable annotation of a fake session, so a cancellation can be observed like the stored row.</summary>
public sealed class FakeEnrollmentSyncBooking(int enrollmentId, int clientId, BookingStatus status)
{
    public int EnrollmentId { get; } = enrollmentId;
    public int ClientId { get; } = clientId;
    public BookingStatus Status { get; set; } = status;
}

/// <summary>One generated session of the fake, owned by a business, with its mutable annotations.</summary>
public sealed class FakeEnrollmentSyncSession(
    int businessId, int appointmentId, int gridClassId, DateTime startsAt, int maxCapacity)
{
    public int BusinessId { get; } = businessId;
    public int AppointmentId { get; } = appointmentId;
    public int GridClassId { get; } = gridClassId;
    public DateTime StartsAt { get; } = startsAt;
    public int MaxCapacity { get; } = maxCapacity;
    public List<FakeEnrollmentSyncBooking> Bookings { get; } = [];
}

/// <summary>
/// In-memory stand-in for the enrollment persistence port. Every call is recorded and the tenant scope is emulated: a
/// client, a grid class, an enrollment or a session registered under another business is invisible to the requested
/// one, exactly like the SQL filters by <c>BusinessId</c>. <see cref="LockEnrollmentAsync"/> returns a detached copy
/// and <see cref="UpdateEnrollmentAsync"/> is the only way to write it back, so a service that forgets to update (or
/// updates before deciding) cannot pass by mutating the stored instance.
/// <para>
/// The emulation covers the tenant scope, the guarded results and the transaction outcomes, not the SQL statements.
/// <see cref="ListFixedSeatsAsync"/> returns every assignment of the grid class without narrowing by status or window,
/// so the caller's <see cref="FixedSeatCapacity"/> predicate is what the tests exercise; the opt-in SQL fixture is
/// what must prove the query itself.
/// </para>
/// </summary>
public sealed class FakeEnrollmentRepository : IEnrollmentRepository
{
    public List<FakeEnrollmentClient> Clients { get; } = [];
    public List<FakeEnrollmentGridClass> GridClasses { get; } = [];
    public List<Enrollment> Enrollments { get; } = [];
    public List<EnrollmentFixedClass> FixedClasses { get; } = [];
    public List<MembershipPeriod> Periods { get; } = [];
    public List<Payment> Payments { get; } = [];
    public List<FakeEnrollmentSyncSession> SyncSessions { get; } = [];

    /// <summary>Fixed annotations the guarded insert actually wrote.</summary>
    public List<FixedAnnotationInsert> InsertedAnnotations { get; } = [];

    public int NextEnrollmentId { get; set; } = 1000;
    public int NextPaymentId { get; set; } = 2000;
    public int NextPeriodId { get; set; } = 3000;

    /// <summary>When true the guarded insert reports the lost race for the last permanent seat.</summary>
    public bool TryInsertReturnsNoSeatLeft { get; set; }

    /// <summary>Grid class the lost race is reported for; the first chosen one when it is not set.</summary>
    public int? TryInsertNoSeatGridClassId { get; set; }

    /// <summary>
    /// When set, every fixed-annotation insert reports exactly this result instead of writing. It emulates both a lost
    /// race at the write (<c>NoSeatLeft</c>, <c>AlreadyPresent</c>) and a value no implementation may ever produce.
    /// </summary>
    public FixedAnnotationInsertResultType? FixedAnnotationResultOverride { get; set; }

    /// <summary>
    /// When true, the fixed-annotation insert answers with no results at all, like an implementation that did not
    /// report back per requested annotation.
    /// </summary>
    public bool DropAnnotationResults { get; set; }

    /// <summary>
    /// When true, the sync read ignores the start bound and returns sessions that already started. It emulates a query
    /// that returned too much, so the caller's own "has not passed" rule can be observed instead of trusted.
    /// </summary>
    public bool IncludeStartedSessions { get; set; }

    public int FindClientCallCount { get; private set; }
    public int LockGridClassesCallCount { get; private set; }
    public IReadOnlyList<int>? LockedGridClassIds { get; private set; }
    public int ListFixedSeatsCallCount { get; private set; }
    public int TryInsertCallCount { get; private set; }
    public EnrollmentInsert? LastEnrollmentInsert { get; private set; }
    public int LockEnrollmentCallCount { get; private set; }
    public int? LockedEnrollmentId { get; private set; }
    public int InsertPaymentCallCount { get; private set; }
    public int InsertPeriodCallCount { get; private set; }
    public int UpdateEnrollmentCallCount { get; private set; }
    public int CloseFixedClassesCallCount { get; private set; }
    public (int EnrollmentId, DateOnly FromDate)? LastCloseRequest { get; private set; }
    public int ListSyncSessionsCallCount { get; private set; }
    public DateTime? LastSyncFromExclusive { get; private set; }
    public int InsertAnnotationsCallCount { get; private set; }
    public int CancelAnnotationsCallCount { get; private set; }
    public (int EnrollmentId, DateTime FromInclusive)? LastCancelRequest { get; private set; }
    public int ListEnrollmentsCallCount { get; private set; }

    public Task<EnrollmentClient?> FindClientAsync(int businessId, int clientId, CancellationToken ct = default)
    {
        FindClientCallCount++;
        var client = Clients.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId && candidate.Client.ClientId == clientId);

        return Task.FromResult(client?.Client);
    }

    public Task<IReadOnlyList<EnrollmentGridClassRow>> LockGridClassesAsync(
        int businessId, IReadOnlyList<int> gridClassIds, CancellationToken ct = default)
    {
        LockGridClassesCallCount++;
        LockedGridClassIds = gridClassIds;

        IReadOnlyList<EnrollmentGridClassRow> rows = GridClasses
            .Where(candidate => candidate.BusinessId == businessId && gridClassIds.Contains(candidate.Row.GridClassId))
            .Select(candidate => candidate.Row)
            .OrderBy(row => row.GridClassId)
            .ToList();

        return Task.FromResult(rows);
    }

    /// <summary>
    /// Emulates the tenant scope by joining the stored fixed assignments with their enrollments, like the real query
    /// joins <c>EnrollmentFixedClasses</c> with <c>Enrollments</c>, and returns every assignment of the grid class
    /// without narrowing by enrollment status or validity window: that predicate belongs to
    /// <see cref="FixedSeatCapacity"/> and the test must exercise it through the service.
    /// </summary>
    public Task<IReadOnlyList<FixedSeatAssignment>> ListFixedSeatsAsync(
        int businessId, int gridClassId, DateOnly referenceDate, CancellationToken ct = default)
    {
        ListFixedSeatsCallCount++;

        var enrollments = Enrollments.Where(candidate => candidate.BusinessId == businessId).ToArray();
        IReadOnlyList<FixedSeatAssignment> seats = FixedClasses
            .Where(fixedClass => fixedClass.GridClassId == gridClassId)
            .Join(
                enrollments,
                fixedClass => fixedClass.EnrollmentId,
                enrollment => enrollment.Id,
                (fixedClass, enrollment) => new FixedSeatAssignment(
                    enrollment.Id, enrollment.Status, fixedClass.ValidFrom, fixedClass.ValidTo))
            .OrderBy(assignment => assignment.EnrollmentId)
            .ToList();

        return Task.FromResult(seats);
    }

    public Task<EnrollmentInsertResult> TryInsertEnrollmentAsync(
        EnrollmentInsert insert, CancellationToken ct = default)
    {
        TryInsertCallCount++;
        LastEnrollmentInsert = insert;

        if (TryInsertReturnsNoSeatLeft)
        {
            var gridClassId = TryInsertNoSeatGridClassId
                ?? insert.FixedClasses.Select(fixedClass => fixedClass.GridClassId).DefaultIfEmpty(0).Min();
            return Task.FromResult(EnrollmentInsertResult.NoSeatLeft(gridClassId));
        }

        var enrollmentId = NextEnrollmentId++;
        var stored = Copy(insert.Enrollment, enrollmentId);
        Enrollments.Add(stored);
        foreach (var fixedClass in insert.FixedClasses)
            FixedClasses.Add(new EnrollmentFixedClass
            {
                EnrollmentId = enrollmentId,
                GridClassId = fixedClass.GridClassId,
                ValidFrom = fixedClass.ValidFrom,
                ValidTo = fixedClass.ValidTo
            });

        return Task.FromResult(EnrollmentInsertResult.Inserted(enrollmentId));
    }

    /// <summary>
    /// Mirrors the lock and returns a detached copy of the enrollment, so the service can only persist its transition
    /// through <see cref="UpdateEnrollmentAsync"/>, exactly like the real read returns a fresh row.
    /// </summary>
    public Task<EnrollmentState?> LockEnrollmentAsync(
        int businessId, int enrollmentId, CancellationToken ct = default)
    {
        LockEnrollmentCallCount++;
        LockedEnrollmentId = enrollmentId;

        var enrollment = Enrollments.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId && candidate.Id == enrollmentId);
        if (enrollment is null)
            return Task.FromResult<EnrollmentState?>(null);

        var state = new EnrollmentState(
            Copy(enrollment),
            FixedClasses.Where(fixedClass => fixedClass.EnrollmentId == enrollmentId).ToList(),
            Periods
                .Where(period => period.EnrollmentId == enrollmentId)
                .OrderBy(period => period.CoverageStart)
                .ThenBy(period => period.Id)
                .ToList());

        return Task.FromResult<EnrollmentState?>(state);
    }

    public Task<int> InsertPaymentAsync(Payment payment, CancellationToken ct = default)
    {
        InsertPaymentCallCount++;
        payment.Id = NextPaymentId++;
        Payments.Add(payment);
        return Task.FromResult(payment.Id);
    }

    public Task<int> InsertMembershipPeriodAsync(MembershipPeriod period, CancellationToken ct = default)
    {
        InsertPeriodCallCount++;
        period.Id = NextPeriodId++;
        Periods.Add(period);
        return Task.FromResult(period.Id);
    }

    public Task UpdateEnrollmentAsync(Enrollment enrollment, CancellationToken ct = default)
    {
        UpdateEnrollmentCallCount++;
        var index = Enrollments.FindIndex(candidate => candidate.Id == enrollment.Id);
        if (index < 0)
            throw new InvalidOperationException($"Enrollment {enrollment.Id} was not registered.");

        Enrollments[index] = enrollment;
        return Task.CompletedTask;
    }

    public Task<int> CloseFixedClassesAsync(
        int businessId, int enrollmentId, DateOnly fromDate, CancellationToken ct = default)
    {
        CloseFixedClassesCallCount++;
        LastCloseRequest = (enrollmentId, fromDate);

        var closed = 0;
        foreach (var fixedClass in FixedClasses.Where(candidate =>
            candidate.EnrollmentId == enrollmentId && candidate.ValidTo is null))
        {
            var lastValidDay = fromDate.AddDays(-1);
            fixedClass.ValidTo = lastValidDay >= fixedClass.ValidFrom ? lastValidDay : fixedClass.ValidFrom;
            closed++;
        }

        return Task.FromResult(closed);
    }

    /// <summary>
    /// Emulates the tenant scope, the strictly-future start and the fixed assignment being valid on the session date.
    /// It deliberately does not filter the kind nor the capacity: those are the caller's guards.
    /// <see cref="IncludeStartedSessions"/> lifts the start bound on purpose.
    /// </summary>
    public Task<IReadOnlyList<EnrollmentSyncSessionRow>> ListFixedSyncSessionsAsync(
        int businessId, int enrollmentId, DateTime fromExclusive, CancellationToken ct = default)
    {
        ListSyncSessionsCallCount++;
        LastSyncFromExclusive = fromExclusive;

        var assignments = FixedClasses.Where(candidate => candidate.EnrollmentId == enrollmentId).ToArray();
        IReadOnlyList<EnrollmentSyncSessionRow> rows = SyncSessions
            .Where(session => session.BusinessId == businessId)
            .Where(session => IncludeStartedSessions || session.StartsAt > fromExclusive)
            .Where(session => assignments.Any(assignment =>
                assignment.GridClassId == session.GridClassId
                && assignment.IsValidOn(DateOnly.FromDateTime(session.StartsAt))))
            .OrderBy(session => session.StartsAt)
            .ThenBy(session => session.AppointmentId)
            .Select(session => new EnrollmentSyncSessionRow(
                session.AppointmentId,
                session.GridClassId,
                session.StartsAt,
                session.MaxCapacity,
                session.Bookings
                    .Select(booking => new EnrollmentSyncBooking(booking.EnrollmentId, booking.Status))
                    .ToArray()))
            .ToList();

        return Task.FromResult(rows);
    }

    /// <summary>
    /// Emulates the guarded insert, guards included: an annotation the enrollment already has — cancelled included —
    /// and a client that already holds a non-cancelled booking of the session report <c>AlreadyPresent</c> without
    /// writing, and a session whose capacity is already taken reports <c>NoSeatLeft</c>.
    /// </summary>
    public Task<IReadOnlyList<FixedAnnotationInsertResult>> InsertFixedAnnotationsAsync(
        int businessId, IReadOnlyList<FixedAnnotationInsert> annotations, CancellationToken ct = default)
    {
        InsertAnnotationsCallCount++;

        if (DropAnnotationResults)
            return Task.FromResult<IReadOnlyList<FixedAnnotationInsertResult>>([]);

        var results = new List<FixedAnnotationInsertResult>(annotations.Count);
        foreach (var annotation in annotations)
        {
            if (FixedAnnotationResultOverride is { } overridden)
            {
                results.Add(new FixedAnnotationInsertResult(overridden, annotation.AppointmentId));
                continue;
            }

            var session = SyncSessions.FirstOrDefault(candidate =>
                candidate.BusinessId == businessId && candidate.AppointmentId == annotation.AppointmentId);
            if (session is null)
            {
                results.Add(FixedAnnotationInsertResult.AlreadyPresent(annotation.AppointmentId));
                continue;
            }

            if (session.Bookings.Any(booking => booking.EnrollmentId == annotation.EnrollmentId)
                || session.Bookings.Any(booking =>
                    booking.ClientId == annotation.ClientId && booking.Status != BookingStatus.Cancelled))
            {
                results.Add(FixedAnnotationInsertResult.AlreadyPresent(annotation.AppointmentId));
                continue;
            }

            if (session.Bookings.Count(booking => booking.Status != BookingStatus.Cancelled) >= session.MaxCapacity)
            {
                results.Add(FixedAnnotationInsertResult.NoSeatLeft(annotation.AppointmentId));
                continue;
            }

            session.Bookings.Add(new FakeEnrollmentSyncBooking(
                annotation.EnrollmentId, annotation.ClientId, BookingStatus.Pending));
            InsertedAnnotations.Add(annotation);
            results.Add(FixedAnnotationInsertResult.Inserted(annotation.AppointmentId));
        }

        return Task.FromResult<IReadOnlyList<FixedAnnotationInsertResult>>(results);
    }

    public Task<int> CancelFixedAnnotationsAsync(
        int businessId, int enrollmentId, DateTime fromInclusive, CancellationToken ct = default)
    {
        CancelAnnotationsCallCount++;
        LastCancelRequest = (enrollmentId, fromInclusive);

        var cancelled = 0;
        foreach (var session in SyncSessions.Where(candidate =>
            candidate.BusinessId == businessId && candidate.StartsAt >= fromInclusive))
        {
            foreach (var booking in session.Bookings.Where(candidate =>
                candidate.EnrollmentId == enrollmentId && candidate.Status != BookingStatus.Cancelled))
            {
                booking.Status = BookingStatus.Cancelled;
                cancelled++;
            }
        }

        return Task.FromResult(cancelled);
    }

    public Task<IReadOnlyList<EnrollmentListRow>> ListEnrollmentsAsync(
        int businessId, CancellationToken ct = default)
    {
        ListEnrollmentsCallCount++;

        IReadOnlyList<EnrollmentListRow> rows = Enrollments
            .Where(enrollment => enrollment.BusinessId == businessId)
            .OrderBy(enrollment => enrollment.Id)
            .Select(enrollment => new EnrollmentListRow(
                enrollment.Id,
                enrollment.ClientId,
                Clients.FirstOrDefault(client =>
                    client.BusinessId == businessId && client.Client.ClientId == enrollment.ClientId)
                    ?.Client.FullName ?? "",
                enrollment.WeeklyFrequency,
                enrollment.Status,
                enrollment.ReferenceDay,
                enrollment.ActivatedAt,
                Periods.Where(period => period.EnrollmentId == enrollment.Id).ToList()))
            .ToList();

        return Task.FromResult(rows);
    }

    private static Enrollment Copy(Enrollment source, int? id = null) => new()
    {
        Id = id ?? source.Id,
        BusinessId = source.BusinessId,
        ClientId = source.ClientId,
        WeeklyFrequency = source.WeeklyFrequency,
        Status = source.Status,
        AgreedPrice = source.AgreedPrice,
        PriceReason = source.PriceReason,
        ReferenceDay = source.ReferenceDay,
        CreatedAt = source.CreatedAt,
        ActivatedAt = source.ActivatedAt,
        AnnulledAt = source.AnnulledAt,
        TerminatedAt = source.TerminatedAt
    };
}
