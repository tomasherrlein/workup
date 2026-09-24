using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Data.Enrollments;

/// <summary>
/// SQL Server persistence for fixed-class enrollments and monthly memberships (RF-05, RF-20, RF-29, D-16, D-20, D-21,
/// D-23, D-24, Reglas 16-19 and 23). Every statement is scoped by <c>BusinessId</c>: a client, a grid class, an
/// enrollment, a period or a session of another tenant is invisible to the caller and is never leaked. Nothing here
/// commits or rolls back: each command joins the caller's ambient transaction through <see cref="DbSession.Transaction"/>,
/// which owns the whole unit. No statement deletes a row — annulling and terminating close the validity of the fixed
/// assignments and cancel the annotations.
/// <para>
/// Two reads are the locks of this module. <see cref="LockGridClassesAsync"/> locks the chosen grid class rows
/// <c>UPDLOCK, ROWLOCK</c> in ascending id order — the deterministic order that keeps two enrollments choosing the
/// same pair of classes in opposite order from deadlocking — and the seat read plus the guarded insert run under those
/// locks. <see cref="LockEnrollmentAsync"/> locks the enrollment row, so a second concurrent payment waits, reads the
/// period the first one wrote and renews from that expiry instead of writing an overlapping one.
/// </para>
/// <para>
/// The permanent-seat predicate is written once and repeated by <see cref="ListFixedSeatsAsync"/> and by the guard of
/// <see cref="TryInsertEnrollmentAsync"/>: the enrollment is <c>PendingPayment</c> or <c>Active</c> and the window has
/// not already ended (<c>ValidTo IS NULL OR ValidTo &gt;= @referenceDate</c>). The start of the window is deliberately
/// absent — the place is retained from registration (Regla 23, D-21), so a future-start assignment already occupies
/// its seat — and the reference date is the registration day of the caller's clock, never the effective date the
/// student asked for.
/// </para>
/// </summary>
public sealed class SqlEnrollmentRepository(DbSession session)
    : SqlRepositoryBase(session), IEnrollmentRepository
{
    /// <summary>
    /// Locks the requested grid class rows inside the caller's transaction. <c>UPDLOCK, ROWLOCK</c> over the primary
    /// key is what serializes two enrollments competing for the last permanent seat, and the <c>ORDER BY g.Id</c>
    /// acquires them in ascending id order so two enrollments that choose the same two classes in opposite order
    /// cannot deadlock. The service join carries the capacity of the service the grid class belongs to.
    /// </summary>
    private static string LockGridClassesSql(IReadOnlyList<int> gridClassIds) => $"""
        SELECT g.Id AS GridClassId, g.ServiceId, g.WorkerId, g.DayOfWeek, g.StartTime, s.MaxCapacity, g.IsActive
          FROM GridClasses AS g WITH (UPDLOCK, ROWLOCK)
          JOIN Services AS s ON s.Id = g.ServiceId AND s.BusinessId = g.BusinessId
         WHERE g.BusinessId = @businessId
           AND g.Id IN ({Placeholders(gridClassIds)})
         ORDER BY g.Id
        """;

    /// <summary>
    /// The permanent seats of one grid class under the corrected predicate: only an assignment of a pending-payment or
    /// active enrollment whose window has not already ended on the reference date. The start of the window is not
    /// filtered, so a future-start assignment still counts, and the tenant joins keep a foreign enrollment's assignment
    /// out even when it points at this grid class.
    /// </summary>
    private const string ListFixedSeatsSql = """
        SELECT efc.EnrollmentId, e.Status, efc.ValidFrom, efc.ValidTo
          FROM EnrollmentFixedClasses AS efc
          JOIN Enrollments AS e ON e.Id = efc.EnrollmentId AND e.BusinessId = @businessId
          JOIN GridClasses AS g ON g.Id = efc.GridClassId AND g.BusinessId = @businessId
         WHERE e.BusinessId = @businessId
           AND efc.GridClassId = @gridClassId
           AND e.Status IN (@pendingPayment, @active)
           AND (efc.ValidTo IS NULL OR efc.ValidTo >= @referenceDate)
         ORDER BY efc.EnrollmentId, efc.Id
        """;

    /// <summary>One still-open fixed assignment of the enrollment, written with the caller's effective date.</summary>
    private const string InsertEnrollmentFixedClassSql = """
        INSERT INTO EnrollmentFixedClasses (EnrollmentId, GridClassId, ValidFrom, ValidTo)
        VALUES (@enrollmentId, @gridClassId, @validFrom, @validTo)
        """;

    /// <summary>
    /// Locks the enrollment row of the tenant inside the caller's transaction: the read is the lock a payment, an
    /// annulment and a termination serialize on. A foreign or missing enrollment is simply not found, so the two are
    /// the same answer.
    /// </summary>
    private const string LockEnrollmentSql = """
        SELECT e.Id, e.BusinessId, e.ClientId, e.WeeklyFrequency, e.Status, e.AgreedPrice, e.PriceReason,
               CAST(e.ReferenceDay AS INT) AS ReferenceDay, e.CreatedAt, e.ActivatedAt, e.AnnulledAt, e.TerminatedAt
          FROM Enrollments AS e WITH (UPDLOCK, ROWLOCK)
         WHERE e.BusinessId = @businessId
           AND e.Id = @enrollmentId
        """;

    /// <summary>Every fixed assignment of the locked enrollment, ordered by coverage start so a schedule change reads chronologically.</summary>
    private const string LockedEnrollmentFixedClassesSql = """
        SELECT efc.Id, efc.EnrollmentId, efc.GridClassId, efc.ValidFrom, efc.ValidTo
          FROM EnrollmentFixedClasses AS efc
          JOIN Enrollments AS e ON e.Id = efc.EnrollmentId AND e.BusinessId = @businessId
         WHERE efc.EnrollmentId = @enrollmentId
         ORDER BY efc.ValidFrom, efc.Id
        """;

    /// <summary>The paid periods of the locked enrollment, ordered by coverage start: the latest expiry is what the next renewal follows.</summary>
    private const string LockedEnrollmentPeriodsSql = """
        SELECT mp.Id, mp.EnrollmentId, mp.CoverageStart, mp.ExpiresOn, mp.PaymentId, mp.CreatedAt
          FROM MembershipPeriods AS mp
          JOIN Enrollments AS e ON e.Id = mp.EnrollmentId AND e.BusinessId = @businessId
         WHERE mp.EnrollmentId = @enrollmentId
         ORDER BY mp.CoverageStart, mp.Id
        """;

    /// <summary>One client of the business, reduced to the id the flow validates and the name it lists.</summary>
    private const string FindClientSql = """
        SELECT c.Id AS ClientId, CONCAT(c.FirstName, N' ', c.LastName) AS FullName
          FROM Clients AS c
         WHERE c.BusinessId = @businessId
           AND c.Id = @clientId
        """;

    /// <summary>
    /// The membership payment. The enrollment is its concrete origin and the booking is absent, exactly what
    /// <c>CK_Payments_Reference</c> requires for the <c>Membership</c> concept.
    /// </summary>
    private const string InsertPaymentSql = """
        INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, EnrollmentId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt)
        OUTPUT INSERTED.Id
        VALUES (@businessId, @clientId, @concept, @bookingId, @enrollmentId, @amount, @listPrice, @priceReason, @paidAt, @recordedAt)
        """;

    /// <summary>One paid month, tied to the payment that bought it (<c>UQ_MembershipPeriods_Payment</c>).</summary>
    private const string InsertMembershipPeriodSql = """
        INSERT INTO MembershipPeriods (EnrollmentId, CoverageStart, ExpiresOn, PaymentId, CreatedAt)
        OUTPUT INSERTED.Id
        VALUES (@enrollmentId, @coverageStart, @expiresOn, @paymentId, @createdAt)
        """;

    /// <summary>Writes the mutable state of the enrollment: status, reference day and the three transition instants.</summary>
    private const string UpdateEnrollmentSql = """
        UPDATE Enrollments
           SET Status = @status,
               ReferenceDay = @referenceDay,
               ActivatedAt = @activatedAt,
               AnnulledAt = @annulledAt,
               TerminatedAt = @terminatedAt
         WHERE BusinessId = @businessId
           AND Id = @id
        """;

    /// <summary>
    /// Closes every still-open fixed assignment so none holds a seat from <paramref name="fromDate"/> on: the window
    /// ends the day before, and an assignment whose validity starts on or after that date is closed on its own start
    /// day because <c>ValidTo</c> can never precede <c>ValidFrom</c>. An already closed assignment is never reopened
    /// and no row is deleted.
    /// </summary>
    private const string CloseFixedClassesSql = """
        UPDATE efc
           SET ValidTo = CASE
                             WHEN @fromDate <= efc.ValidFrom THEN efc.ValidFrom
                             ELSE DATEADD(DAY, -1, @fromDate)
                         END
          FROM EnrollmentFixedClasses AS efc
          JOIN Enrollments AS e ON e.Id = efc.EnrollmentId AND e.BusinessId = @businessId
         WHERE efc.EnrollmentId = @enrollmentId
           AND efc.ValidTo IS NULL
        """;

    /// <summary>
    /// The generated class sessions of the enrollment's fixed grid classes that start strictly after the caller's
    /// instant and whose assignment covers the session date. The <c>EXISTS</c> avoids duplicating a session when the
    /// enrollment carries more than one assignment for the same grid class.
    /// </summary>
    private const string FixedSyncSessionsSql = """
        SELECT a.Id AS AppointmentId, a.GridClassId, a.StartsAt, a.MaxCapacity
          FROM Appointments AS a
         WHERE a.BusinessId = @businessId
           AND a.Kind = @classSession
           AND a.StartsAt > @fromExclusive
           AND EXISTS
               (
                   SELECT 1
                     FROM EnrollmentFixedClasses AS efc
                    WHERE efc.EnrollmentId = @enrollmentId
                      AND efc.GridClassId = a.GridClassId
                      AND efc.ValidFrom <= CAST(a.StartsAt AS date)
                      AND (efc.ValidTo IS NULL OR efc.ValidTo >= CAST(a.StartsAt AS date))
               )
         ORDER BY a.StartsAt, a.Id
        """;

    /// <summary>Every annotation of those same sessions, cancelled ones included, so the caller decides what it writes.</summary>
    private const string FixedSyncSessionBookingsSql = """
        SELECT b.AppointmentId, COALESCE(b.EnrollmentId, 0) AS EnrollmentId, b.Status
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND a.Kind = @classSession
           AND a.StartsAt > @fromExclusive
           AND EXISTS
               (
                   SELECT 1
                     FROM EnrollmentFixedClasses AS efc
                    WHERE efc.EnrollmentId = @enrollmentId
                      AND efc.GridClassId = a.GridClassId
                      AND efc.ValidFrom <= CAST(a.StartsAt AS date)
                      AND (efc.ValidTo IS NULL OR efc.ValidTo >= CAST(a.StartsAt AS date))
               )
         ORDER BY b.AppointmentId, b.Id
        """;

    /// <summary>
    /// The guarded fixed-annotation insert: one statement that writes the annotation only while the session is a class
    /// session of the business with a free place, the enrollment holds no annotation of that session — cancelled
    /// included, which keeps a cancellation from being resurrected — and the client holds no non-cancelled booking of
    /// it. The three reads it guards on are taken <c>UPDLOCK, HOLDLOCK</c>, so they are serialized against a competing
    /// insert and held until the caller commits.
    /// </summary>
    private const string GuardedInsertFixedAnnotationSql = """
        INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt)
        OUTPUT INSERTED.Id
        SELECT @businessId, @appointmentId, @clientId, @enrollmentId, @modality, @status, @createdAt
         WHERE EXISTS
               (
                   SELECT 1
                     FROM Appointments AS session WITH (UPDLOCK, HOLDLOCK)
                    WHERE session.Id = @appointmentId
                      AND session.BusinessId = @businessId
                      AND session.Kind = @classSession
                      AND session.MaxCapacity >
                          (
                              SELECT COUNT(*)
                                FROM Bookings AS taken WITH (UPDLOCK, HOLDLOCK)
                               WHERE taken.AppointmentId = @appointmentId
                                 AND taken.Status <> @cancelled
                          )
               )
           AND NOT EXISTS
               (
                   SELECT 1
                     FROM Bookings AS mine WITH (UPDLOCK, HOLDLOCK)
                    WHERE mine.AppointmentId = @appointmentId
                      AND mine.EnrollmentId = @enrollmentId
               )
           AND NOT EXISTS
               (
                   SELECT 1
                     FROM Bookings AS active WITH (UPDLOCK, HOLDLOCK)
                    WHERE active.AppointmentId = @appointmentId
                      AND active.ClientId = @clientId
                      AND active.Status <> @cancelled
               )
        """;

    /// <summary>
    /// Why the guarded annotation insert wrote nothing, read under the same transaction that already holds its locks.
    /// The enrollment's own annotation is decided first, matching the caller's rule: a cancelled annotation is an
    /// omission, never a resurrection. <c>0</c> means "no defined outcome" and the caller fails loudly.
    /// </summary>
    private const string DiagnoseFailedFixedAnnotationSql = """
        SELECT CASE
                   WHEN EXISTS
                        (
                            SELECT 1
                              FROM Bookings AS mine WITH (UPDLOCK, HOLDLOCK)
                             WHERE mine.AppointmentId = @appointmentId
                               AND mine.EnrollmentId = @enrollmentId
                        )
                       THEN @alreadyPresent
                   WHEN EXISTS
                        (
                            SELECT 1
                              FROM Bookings AS active WITH (UPDLOCK, HOLDLOCK)
                             WHERE active.AppointmentId = @appointmentId
                               AND active.ClientId = @clientId
                               AND active.Status <> @cancelled
                        )
                       THEN @alreadyPresent
                   WHEN EXISTS
                        (
                            SELECT 1
                              FROM Appointments AS session WITH (UPDLOCK, HOLDLOCK)
                             WHERE session.Id = @appointmentId
                               AND session.BusinessId = @businessId
                               AND session.Kind = @classSession
                        )
                       THEN @noSeatLeft
                   ELSE 0
               END
        """;

    /// <summary>
    /// Cancels the annotations the enrollment holds in generated class sessions starting at or after the received
    /// bound. An already cancelled annotation is never touched and no row is deleted; the cancellation instant is the
    /// bound itself — the only instant this port carries — because <c>CK_Bookings_Cancelled</c> requires one.
    /// </summary>
    private const string CancelFixedAnnotationsSql = """
        UPDATE b
           SET Status = @cancelled, CancelledBy = @owner, CancelledAt = @cancelledAt
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND b.EnrollmentId = @enrollmentId
           AND b.Status <> @cancelled
           AND a.Kind = @classSession
           AND a.StartsAt >= @fromInclusive
        """;

    /// <summary>
    /// Every enrollment of the business with its client's name, reduced to what the membership list derives its states
    /// from. The join to <c>Clients</c> is a <c>LEFT JOIN</c> with the tenant condition, so an enrollment whose client
    /// row cannot be resolved is still listed with an empty name instead of disappearing from the list.
    /// </summary>
    private const string ListEnrollmentsSql = """
        SELECT e.Id AS EnrollmentId, e.ClientId,
               CASE WHEN c.Id IS NULL THEN N'' ELSE CONCAT(c.FirstName, N' ', c.LastName) END AS ClientName,
               e.WeeklyFrequency, e.Status, CAST(e.ReferenceDay AS INT) AS ReferenceDay, e.ActivatedAt
          FROM Enrollments AS e
          LEFT JOIN Clients AS c ON c.Id = e.ClientId AND c.BusinessId = e.BusinessId
         WHERE e.BusinessId = @businessId
         ORDER BY e.Id
        """;

    /// <summary>The paid periods of every enrollment of the business, so the list derives each state on read (D-23).</summary>
    private const string ListEnrollmentPeriodsSql = """
        SELECT mp.Id, mp.EnrollmentId, mp.CoverageStart, mp.ExpiresOn, mp.PaymentId, mp.CreatedAt
          FROM MembershipPeriods AS mp
          JOIN Enrollments AS e ON e.Id = mp.EnrollmentId AND e.BusinessId = @businessId
         WHERE e.BusinessId = @businessId
         ORDER BY mp.EnrollmentId, mp.CoverageStart, mp.Id
        """;

    public Task<EnrollmentClient?> FindClientAsync(int businessId, int clientId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            FindClientSql,
            reader => new EnrollmentClient(reader.Get<int>("ClientId"), reader.Get<string>("FullName")),
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId),
            ct);

    public async Task<IReadOnlyList<EnrollmentGridClassRow>> LockGridClassesAsync(
        int businessId, IReadOnlyList<int> gridClassIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gridClassIds);

        // No requested class is no rows to lock: the guard fragments below also need a non-empty id list to be valid.
        if (gridClassIds.Count == 0)
            return Array.Empty<EnrollmentGridClassRow>();

        return await QueryAsync(
            LockGridClassesSql(gridClassIds),
            reader => new EnrollmentGridClassRow(
                reader.Get<int>("GridClassId"),
                reader.Get<int>("ServiceId"),
                reader.Get<int>("WorkerId"),
                reader.GetEnum<DayOfWeek>("DayOfWeek"),
                reader.Get<TimeOnly>("StartTime"),
                reader.Get<int>("MaxCapacity"),
                reader.Get<bool>("IsActive")),
            p => AddGridClassIdList(p, businessId, gridClassIds),
            ct);
    }

    public async Task<IReadOnlyList<FixedSeatAssignment>> ListFixedSeatsAsync(
        int businessId, int gridClassId, DateOnly referenceDate, CancellationToken ct = default) =>
        await QueryAsync(
            ListFixedSeatsSql,
            MapFixedSeatAssignment,
            p => AddSeatParameters(p, businessId, gridClassId, referenceDate),
            ct);

    public async Task<EnrollmentInsertResult> TryInsertEnrollmentAsync(
        EnrollmentInsert insert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(insert);

        // A call with no chosen grid class has no seat to guard and no valid guard fragment; the flow never makes it
        // (it requires at least one chosen class), so it fails loudly instead of writing an unguarded enrollment.
        if (insert.FixedClasses.Count == 0)
            throw new ArgumentException(
                "A pending enrollment must choose at least one grid class so its seat can be guarded.",
                nameof(insert));

        var gridClassIds = insert.FixedClasses.Select(fixedClass => fixedClass.GridClassId).ToArray();

        // The reference date is the registration day of the caller's single clock read, carried by the created
        // instant of the enrollment being written. It is never the effective date of a fixed assignment.
        var referenceDate = DateOnly.FromDateTime(insert.Enrollment.CreatedAt);

        // ExecuteReader through QueryAsync, not ExecuteScalar: OUTPUT is a result set and an empty one is the normal
        // "the guard refused" answer, not an error.
        var insertedIds = await QueryAsync(
            GuardedInsertEnrollmentSql(gridClassIds),
            reader => reader.GetInt32(0),
            p => AddEnrollmentInsertParameters(p, insert, referenceDate),
            ct);

        if (insertedIds.Count == 0)
        {
            // The transaction still holds the guard's locks, so this second statement answers which grid class ran out
            // and that answer cannot drift before the caller rolls back. A requested class that is missing from the
            // business is the diagnosis's first failing branch, so an unknown or foreign class is reported by its own
            // id, never as 0. The statement answers no id only when no requested class explains the refusal at all, and
            // an identity is never 0, so that case fails loudly instead of claiming a seat was lost.
            var exhausted = await ScalarAsync<int>(
                DiagnoseNoSeatSql(gridClassIds),
                p => AddEnrollmentInsertParameters(p, insert, referenceDate),
                ct);

            if (exhausted <= 0)
                throw new InvalidOperationException(
                    $"The guarded enrollment insert wrote no row for business {insert.Enrollment.BusinessId} and reports no grid class without a free seat ({exhausted}).");

            return EnrollmentInsertResult.NoSeatLeft(exhausted);
        }

        // The enrollment and its assignments are written together inside the caller's transaction, so the unit is one
        // and a failure after the enrollment row rolls the whole insert back.
        var enrollmentId = insertedIds[0];
        foreach (var fixedClass in insert.FixedClasses)
        {
            await ExecuteAsync(
                InsertEnrollmentFixedClassSql,
                p => p
                    .AddValue("@enrollmentId", enrollmentId)
                    .AddValue("@gridClassId", fixedClass.GridClassId)
                    .AddValue("@validFrom", fixedClass.ValidFrom)
                    .AddValue("@validTo", fixedClass.ValidTo),
                ct);
        }

        return EnrollmentInsertResult.Inserted(enrollmentId);
    }

    public async Task<EnrollmentState?> LockEnrollmentAsync(
        int businessId, int enrollmentId, CancellationToken ct = default)
    {
        // The lock is taken by this read and lives until the caller's transaction ends, so the fixed assignments and
        // the periods below are read under the same lock a concurrent transition waits on.
        var enrollment = await QuerySingleOrDefaultAsync(
            LockEnrollmentSql,
            MapEnrollment,
            p => p.AddValue("@businessId", businessId).AddValue("@enrollmentId", enrollmentId),
            ct);

        if (enrollment is null)
            return null;

        var fixedClasses = await QueryAsync(
            LockedEnrollmentFixedClassesSql,
            MapEnrollmentFixedClass,
            p => p.AddValue("@businessId", businessId).AddValue("@enrollmentId", enrollmentId),
            ct);

        var periods = await QueryAsync(
            LockedEnrollmentPeriodsSql,
            MapMembershipPeriod,
            p => p.AddValue("@businessId", businessId).AddValue("@enrollmentId", enrollmentId),
            ct);

        return new EnrollmentState(enrollment, fixedClasses, periods);
    }

    public Task<int> InsertPaymentAsync(Payment payment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return InsertAsync(
            InsertPaymentSql,
            p => p
                .AddValue("@businessId", payment.BusinessId)
                .AddValue("@clientId", payment.ClientId)
                .AddValue("@concept", payment.Concept)
                .AddValue("@bookingId", payment.BookingId)
                .AddValue("@enrollmentId", payment.EnrollmentId)
                .AddValue("@amount", payment.Amount)
                .AddValue("@listPrice", payment.ListPrice)
                .AddValue("@priceReason", payment.PriceReason)
                .AddDateTime2("@paidAt", payment.PaidAt)
                .AddDateTime2("@recordedAt", payment.RecordedAt),
            ct);
    }

    public Task<int> InsertMembershipPeriodAsync(MembershipPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(period);

        return InsertAsync(
            InsertMembershipPeriodSql,
            p => p
                .AddValue("@enrollmentId", period.EnrollmentId)
                .AddValue("@coverageStart", period.CoverageStart)
                .AddValue("@expiresOn", period.ExpiresOn)
                .AddValue("@paymentId", period.PaymentId)
                .AddDateTime2("@createdAt", period.CreatedAt),
            ct);
    }

    public Task UpdateEnrollmentAsync(Enrollment enrollment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(enrollment);

        return ExecuteAsync(
            UpdateEnrollmentSql,
            p =>
            {
                p.AddValue("@id", enrollment.Id)
                    .AddValue("@businessId", enrollment.BusinessId)
                    .AddValue("@status", enrollment.Status)
                    .AddValue("@referenceDay", enrollment.ReferenceDay);
                AddNullableDateTime2(p, "@activatedAt", enrollment.ActivatedAt);
                AddNullableDateTime2(p, "@annulledAt", enrollment.AnnulledAt);
                AddNullableDateTime2(p, "@terminatedAt", enrollment.TerminatedAt);
            },
            ct);
    }

    public Task<int> CloseFixedClassesAsync(
        int businessId, int enrollmentId, DateOnly fromDate, CancellationToken ct = default) =>
        ExecuteAsync(
            CloseFixedClassesSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@enrollmentId", enrollmentId)
                .AddValue("@fromDate", fromDate),
            ct);

    public async Task<IReadOnlyList<EnrollmentSyncSessionRow>> ListFixedSyncSessionsAsync(
        int businessId, int enrollmentId, DateTime fromExclusive, CancellationToken ct = default)
    {
        var sessions = await QueryAsync(
            FixedSyncSessionsSql,
            reader => new SyncSessionProjection(
                reader.Get<int>("AppointmentId"),
                reader.Get<int>("GridClassId"),
                reader.Get<DateTime>("StartsAt"),
                reader.Get<int>("MaxCapacity")),
            p => AddFixedSyncParameters(p, businessId, enrollmentId, fromExclusive),
            ct);

        var bookings = await QueryAsync(
            FixedSyncSessionBookingsSql,
            reader => new SyncBookingProjection(
                reader.Get<int>("AppointmentId"),
                new EnrollmentSyncBooking(
                    reader.Get<int>("EnrollmentId"),
                    reader.GetEnum<BookingStatus>("Status"))),
            p => AddFixedSyncParameters(p, businessId, enrollmentId, fromExclusive),
            ct);

        var bookingsByAppointment = bookings
            .GroupBy(booking => booking.AppointmentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<EnrollmentSyncBooking>)group.Select(booking => booking.Booking).ToArray());

        // A session nobody annotated is returned with an empty list, never dropped: it is the most annotatable one.
        return sessions
            .Select(session => new EnrollmentSyncSessionRow(
                session.AppointmentId,
                session.GridClassId,
                session.StartsAt,
                session.MaxCapacity,
                bookingsByAppointment.TryGetValue(session.AppointmentId, out var attached) ? attached : []))
            .ToArray();
    }

    public async Task<IReadOnlyList<FixedAnnotationInsertResult>> InsertFixedAnnotationsAsync(
        int businessId, IReadOnlyList<FixedAnnotationInsert> annotations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        // One result per requested annotation, in the requested order, so the caller can pair each outcome with its
        // session without guessing.
        var results = new List<FixedAnnotationInsertResult>(annotations.Count);
        foreach (var annotation in annotations)
            results.Add(await InsertFixedAnnotationAsync(businessId, annotation, ct));

        return results;
    }

    public Task<int> CancelFixedAnnotationsAsync(
        int businessId, int enrollmentId, DateTime fromInclusive, CancellationToken ct = default) =>
        ExecuteAsync(
            CancelFixedAnnotationsSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@enrollmentId", enrollmentId)
                .AddValue("@classSession", AppointmentKind.ClassSession)
                .AddValue("@cancelled", BookingStatus.Cancelled)
                .AddValue("@owner", CancelledBy.Owner)
                .AddDateTime2("@cancelledAt", fromInclusive)
                .AddDateTime2("@fromInclusive", fromInclusive),
            ct);

    public async Task<IReadOnlyList<EnrollmentListRow>> ListEnrollmentsAsync(
        int businessId, CancellationToken ct = default)
    {
        var enrollments = await QueryAsync(
            ListEnrollmentsSql,
            reader => new EnrollmentListProjection(
                reader.Get<int>("EnrollmentId"),
                reader.Get<int>("ClientId"),
                reader.Get<string>("ClientName"),
                reader.Get<int>("WeeklyFrequency"),
                reader.GetEnum<EnrollmentStatus>("Status"),
                reader.GetNullable<int>("ReferenceDay"),
                reader.GetNullable<DateTime>("ActivatedAt")),
            p => p.AddValue("@businessId", businessId),
            ct);

        var periods = await QueryAsync(
            ListEnrollmentPeriodsSql,
            MapMembershipPeriod,
            p => p.AddValue("@businessId", businessId),
            ct);

        var periodsByEnrollment = periods
            .GroupBy(period => period.EnrollmentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MembershipPeriod>)group
                    .OrderBy(period => period.CoverageStart)
                    .ThenBy(period => period.Id)
                    .ToArray());

        // An enrollment without a paid period is still listed, with an empty period list: that is what the pending
        // filter and the "never paid" state are derived from.
        return enrollments
            .Select(enrollment => new EnrollmentListRow(
                enrollment.EnrollmentId,
                enrollment.ClientId,
                enrollment.ClientName,
                enrollment.WeeklyFrequency,
                enrollment.Status,
                enrollment.ReferenceDay,
                enrollment.ActivatedAt,
                periodsByEnrollment.TryGetValue(enrollment.EnrollmentId, out var attached) ? attached : []))
            .ToArray();
    }

    private async Task<FixedAnnotationInsertResult> InsertFixedAnnotationAsync(
        int businessId, FixedAnnotationInsert annotation, CancellationToken ct)
    {
        try
        {
            var inserted = await QueryAsync(
                GuardedInsertFixedAnnotationSql,
                reader => reader.GetInt32(0),
                p => AddFixedAnnotationParameters(p, businessId, annotation),
                ct);

            if (inserted.Count > 0)
                return FixedAnnotationInsertResult.Inserted(annotation.AppointmentId);

            var outcome = await ScalarAsync<int>(
                DiagnoseFailedFixedAnnotationSql,
                p => AddFixedAnnotationParameters(p, businessId, annotation)
                    .AddValue("@noSeatLeft", FixedAnnotationInsertResultType.NoSeatLeft)
                    .AddValue("@alreadyPresent", FixedAnnotationInsertResultType.AlreadyPresent),
                ct);

            if (outcome == (int)FixedAnnotationInsertResultType.NoSeatLeft)
                return FixedAnnotationInsertResult.NoSeatLeft(annotation.AppointmentId);

            if (outcome == (int)FixedAnnotationInsertResultType.AlreadyPresent)
                return FixedAnnotationInsertResult.AlreadyPresent(annotation.AppointmentId);

            // The diagnosis returns 0 only when no guard explains the refusal — the session is not a class session of
            // this business, for instance. The enum has no zero member exactly so that this can be detected.
            throw new InvalidOperationException(
                $"The guarded fixed-annotation insert wrote nothing for appointment {annotation.AppointmentId} and reports no defined outcome ({outcome}).");
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // The guards already reject both unique identities, but a concurrent insert that slipped between two
            // guarded reads can still fire UX_Bookings_FixedPerSession or UX_Bookings_ActiveClient. The caller asked
            // for that collision to be named, never for a raw unique-key failure.
            return FixedAnnotationInsertResult.AlreadyPresent(annotation.AppointmentId);
        }
    }

    /// <summary>
    /// The guarded enrollment insert: one statement that writes the enrollment only while every requested grid class
    /// exists in the business and still has a free permanent seat under the corrected predicate. The reads are taken
    /// <c>UPDLOCK, HOLDLOCK</c>, so they are serialized against a competing enrollment and held until the caller
    /// commits; the enrollment and its assignments are therefore atomic with the guard.
    /// </summary>
    private static string GuardedInsertEnrollmentSql(IReadOnlyList<int> gridClassIds) => $"""
        INSERT INTO Enrollments
                    (BusinessId, ClientId, WeeklyFrequency, Status, AgreedPrice, PriceReason, ReferenceDay,
                     CreatedAt, ActivatedAt, AnnulledAt, TerminatedAt)
        OUTPUT INSERTED.Id
        SELECT @businessId, @clientId, @weeklyFrequency, @status, @agreedPrice, @priceReason, NULL,
               @createdAt, NULL, NULL, NULL
         WHERE NOT EXISTS
               (
                   SELECT 1
                     FROM {RequestedGridClasses(gridClassIds)}
                    WHERE NOT EXISTS
                          (
                              SELECT 1
                                FROM GridClasses AS g WITH (UPDLOCK, HOLDLOCK)
                               WHERE g.Id = requested.GridClassId
                                 AND g.BusinessId = @businessId
                          )
                       OR EXISTS
                          (
                              SELECT 1
                                FROM GridClasses AS g WITH (UPDLOCK, HOLDLOCK)
                                JOIN Services AS s ON s.Id = g.ServiceId AND s.BusinessId = g.BusinessId
                               WHERE g.Id = requested.GridClassId
                                 AND g.BusinessId = @businessId
                                 AND s.MaxCapacity <=
                                     (
                                         SELECT COUNT(DISTINCT efc.EnrollmentId)
                                           FROM EnrollmentFixedClasses AS efc WITH (UPDLOCK, HOLDLOCK)
                                           JOIN Enrollments AS e ON e.Id = efc.EnrollmentId AND e.BusinessId = @businessId
                                          WHERE efc.GridClassId = requested.GridClassId
                                            AND e.BusinessId = @businessId
                                            AND e.Status IN (@pendingPayment, @active)
                                            AND (efc.ValidTo IS NULL OR efc.ValidTo >= @referenceDate)
                                     )
                          )
               )
        """;

    /// <summary>
    /// Which requested grid class explains the refusal: the first one, in ascending order, that is missing from the
    /// business or has no free permanent seat under the same predicate. It is read under the guard's locks.
    /// </summary>
    private static string DiagnoseNoSeatSql(IReadOnlyList<int> gridClassIds) => $"""
        SELECT MIN(requested.GridClassId)
          FROM {RequestedGridClasses(gridClassIds)}
         WHERE NOT EXISTS
               (
                   SELECT 1
                     FROM GridClasses AS g WITH (UPDLOCK, HOLDLOCK)
                    WHERE g.Id = requested.GridClassId
                      AND g.BusinessId = @businessId
               )
            OR EXISTS
               (
                   SELECT 1
                     FROM GridClasses AS g WITH (UPDLOCK, HOLDLOCK)
                     JOIN Services AS s ON s.Id = g.ServiceId AND s.BusinessId = g.BusinessId
                    WHERE g.Id = requested.GridClassId
                      AND g.BusinessId = @businessId
                      AND s.MaxCapacity <=
                          (
                              SELECT COUNT(DISTINCT efc.EnrollmentId)
                                FROM EnrollmentFixedClasses AS efc WITH (UPDLOCK, HOLDLOCK)
                                JOIN Enrollments AS e ON e.Id = efc.EnrollmentId AND e.BusinessId = @businessId
                               WHERE efc.GridClassId = requested.GridClassId
                                 AND e.BusinessId = @businessId
                                 AND e.Status IN (@pendingPayment, @active)
                                 AND (efc.ValidTo IS NULL OR efc.ValidTo >= @referenceDate)
                          )
               )
        """;

    /// <summary>
    /// The requested ids as a <c>VALUES</c> derived table, aliased <c>requested (GridClassId)</c> so the guard can
    /// reference <c>requested.GridClassId</c>. SQL Server requires every row of a <c>VALUES</c> derived table to be
    /// parenthesized, so each placeholder travels in parentheses of its own and one id and many ids both produce a
    /// valid table. Only placeholders are interpolated; every value travels as a parameter, so the id list never
    /// concatenates caller data into SQL.
    /// </summary>
    private static string RequestedGridClasses(IReadOnlyList<int> gridClassIds) =>
        $"(VALUES {string.Join(", ", PlaceholderNames(gridClassIds).Select(name => $"({name})"))}) AS requested (GridClassId)";

    /// <summary>The names of the id-list parameters, in the order <see cref="AddGridClassIdList"/> binds them.</summary>
    private static IEnumerable<string> PlaceholderNames(IReadOnlyList<int> values) =>
        values.Select((_, index) => $"@gridClassId{index}");

    private static string Placeholders(IReadOnlyList<int> values) =>
        string.Join(", ", PlaceholderNames(values));

    private static void AddGridClassIdList(
        SqlParameterCollection parameters, int businessId, IReadOnlyList<int> gridClassIds)
    {
        parameters.AddValue("@businessId", businessId);
        for (var index = 0; index < gridClassIds.Count; index++)
            parameters.AddValue($"@gridClassId{index}", gridClassIds[index]);
    }

    /// <summary>
    /// The seat predicate parameters. The statuses travel as their enum values so the query carries no status number of
    /// its own, and the reference date is the registration day the caller computed.
    /// </summary>
    private static void AddSeatParameters(
        SqlParameterCollection parameters, int businessId, int gridClassId, DateOnly referenceDate) =>
        parameters
            .AddValue("@businessId", businessId)
            .AddValue("@gridClassId", gridClassId)
            .AddValue("@referenceDate", referenceDate)
            .AddValue("@pendingPayment", EnrollmentStatus.PendingPayment)
            .AddValue("@active", EnrollmentStatus.Active);

    private static void AddEnrollmentInsertParameters(
        SqlParameterCollection parameters, EnrollmentInsert insert, DateOnly referenceDate)
    {
        parameters
            .AddValue("@businessId", insert.Enrollment.BusinessId)
            .AddValue("@clientId", insert.Enrollment.ClientId)
            .AddValue("@weeklyFrequency", insert.Enrollment.WeeklyFrequency)
            .AddValue("@status", insert.Enrollment.Status)
            .AddValue("@agreedPrice", insert.Enrollment.AgreedPrice)
            .AddValue("@priceReason", insert.Enrollment.PriceReason)
            .AddDateTime2("@createdAt", insert.Enrollment.CreatedAt)
            .AddValue("@referenceDate", referenceDate)
            .AddValue("@pendingPayment", EnrollmentStatus.PendingPayment)
            .AddValue("@active", EnrollmentStatus.Active);

        for (var index = 0; index < insert.FixedClasses.Count; index++)
            parameters.AddValue($"@gridClassId{index}", insert.FixedClasses[index].GridClassId);
    }

    private static void AddFixedSyncParameters(
        SqlParameterCollection parameters, int businessId, int enrollmentId, DateTime fromExclusive) =>
        parameters
            .AddValue("@businessId", businessId)
            .AddValue("@enrollmentId", enrollmentId)
            .AddValue("@classSession", AppointmentKind.ClassSession)
            .AddDateTime2("@fromExclusive", fromExclusive);

    private static SqlParameterCollection AddFixedAnnotationParameters(
        SqlParameterCollection parameters, int businessId, FixedAnnotationInsert annotation) =>
        parameters
            .AddValue("@businessId", businessId)
            .AddValue("@appointmentId", annotation.AppointmentId)
            .AddValue("@clientId", annotation.ClientId)
            .AddValue("@enrollmentId", annotation.EnrollmentId)
            .AddValue("@modality", BookingModality.Fixed)
            .AddValue("@status", BookingStatus.Pending)
            .AddValue("@classSession", AppointmentKind.ClassSession)
            .AddValue("@cancelled", BookingStatus.Cancelled)
            .AddDateTime2("@createdAt", annotation.CreatedAt);

    /// <summary>Adds a nullable <c>DATETIME2</c> parameter explicitly, so the column type never depends on inference.</summary>
    private static void AddNullableDateTime2(SqlParameterCollection parameters, string name, DateTime? value) =>
        parameters.Add(name, SqlDbType.DateTime2).Value = (object?)value ?? DBNull.Value;

    private static FixedSeatAssignment MapFixedSeatAssignment(SqlDataReader reader) => new(
        reader.Get<int>("EnrollmentId"),
        reader.GetEnum<EnrollmentStatus>("Status"),
        reader.Get<DateOnly>("ValidFrom"),
        reader.GetNullable<DateOnly>("ValidTo"));

    private static Enrollment MapEnrollment(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        ClientId = reader.Get<int>("ClientId"),
        WeeklyFrequency = reader.Get<int>("WeeklyFrequency"),
        Status = reader.GetEnum<EnrollmentStatus>("Status"),
        AgreedPrice = reader.Get<decimal>("AgreedPrice"),
        PriceReason = reader.GetNullableString("PriceReason"),
        ReferenceDay = reader.GetNullable<int>("ReferenceDay"),
        CreatedAt = reader.Get<DateTime>("CreatedAt"),
        ActivatedAt = reader.GetNullable<DateTime>("ActivatedAt"),
        AnnulledAt = reader.GetNullable<DateTime>("AnnulledAt"),
        TerminatedAt = reader.GetNullable<DateTime>("TerminatedAt")
    };

    private static EnrollmentFixedClass MapEnrollmentFixedClass(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        EnrollmentId = reader.Get<int>("EnrollmentId"),
        GridClassId = reader.Get<int>("GridClassId"),
        ValidFrom = reader.Get<DateOnly>("ValidFrom"),
        ValidTo = reader.GetNullable<DateOnly>("ValidTo")
    };

    private static MembershipPeriod MapMembershipPeriod(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        EnrollmentId = reader.Get<int>("EnrollmentId"),
        CoverageStart = reader.Get<DateOnly>("CoverageStart"),
        ExpiresOn = reader.Get<DateOnly>("ExpiresOn"),
        PaymentId = reader.Get<int>("PaymentId"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };

    private sealed record SyncSessionProjection(
        int AppointmentId, int GridClassId, DateTime StartsAt, int MaxCapacity);

    private sealed record SyncBookingProjection(int AppointmentId, EnrollmentSyncBooking Booking);

    private sealed record EnrollmentListProjection(
        int EnrollmentId,
        int ClientId,
        string ClientName,
        int WeeklyFrequency,
        EnrollmentStatus Status,
        int? ReferenceDay,
        DateTime? ActivatedAt);
}
