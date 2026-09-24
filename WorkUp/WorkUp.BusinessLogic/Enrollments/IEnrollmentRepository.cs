using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Enrollments;

/// <summary>
/// Persistence boundary for fixed-class enrollments and monthly memberships (RF-05, RF-20, RF-29, D-21, D-23). Every
/// method is scoped by <c>businessId</c>: a client, a grid class, an enrollment or a session of another business is
/// invisible to the caller and is never leaked. Every write runs inside the caller's ambient transaction, which owns
/// the commit, so an operation is one unit and returning without committing rolls it back.
/// <para>
/// Two rows are the locks of this module. <see cref="LockGridClassesAsync"/> is what serializes two enrollments
/// competing for the last permanent seat of a grid class — the same row lock T14 takes on a session, and for the same
/// reason: no unique index protects <c>anotados ≤ MaxCapacity</c>, so the transactional lock plus the guarded insert
/// is the only guarantee, and the seat read must happen under it. <see cref="LockEnrollmentAsync"/> is what serializes
/// the membership transitions of one enrollment, so a second concurrent payment of the same enrollment waits for the
/// first to commit, reads the period it wrote and renews from that expiry instead of writing a period that overlaps
/// it; the second payment is a real second charge (one payment, one period, no proration), never a duplicated write of
/// the first one.
/// </para>
/// </summary>
public interface IEnrollmentRepository
{
    /// <summary>
    /// The client of the business identified by <paramref name="clientId"/>, or <c>null</c> when no such client exists
    /// in it. An enrollment is only registered for a student of the business (RF-05 step 1), so the flow never creates
    /// a client here and never accepts one of another tenant.
    /// </summary>
    Task<EnrollmentClient?> FindClientAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>
    /// Locks the grid class rows of the business named by <paramref name="gridClassIds"/>, like
    /// <c>SELECT ... WITH (UPDLOCK, ROWLOCK)</c>, and returns the rows that exist in it, ordered by id. Holding those
    /// rows is what serializes two enrollments competing for the last permanent seat: the seat read
    /// (<see cref="ListFixedSeatsAsync"/>) and the guarded insert (<see cref="TryInsertEnrollmentAsync"/>) happen
    /// under the same locks.
    /// <para>
    /// The implementation must take the locks in a deterministic order — ascending id is the natural one for SQL
    /// Server — because two enrollments that choose the same two grid classes in opposite order would otherwise
    /// deadlock. The returned list may come in any order, but a requested id that is missing from it means the grid
    /// class does not belong to the business, and the caller answers that as a whole: it never decides the seat of a
    /// grid class it could not lock.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<EnrollmentGridClassRow>> LockGridClassesAsync(
        int businessId, IReadOnlyList<int> gridClassIds, CancellationToken ct = default);

    /// <summary>
    /// The fixed assignments of the grid class that are valid on <paramref name="referenceDate"/>, with the status of
    /// the enrollment that owns each one and no cancellation concept (the table has none). The count of seats is not
    /// returned as a number: <see cref="FixedSeatCapacity"/> owns the predicate — an assignment holds a seat while its
    /// enrollment is pending payment or active and the window covers the date, counted once per enrollment — so the
    /// caller applies exactly one rule in one place.
    /// <para>
    /// It must run inside the transaction that holds <see cref="LockGridClassesAsync"/> on that grid class: reading
    /// the seats without the lock and inserting after it is the race that overbooks a grid class. An implementation
    /// may narrow the query by status and window; a wider answer is harmless, because the caller re-applies
    /// <see cref="FixedSeatCapacity"/> and can only reject more.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<FixedSeatAssignment>> ListFixedSeatsAsync(
        int businessId, int gridClassId, DateOnly referenceDate, CancellationToken ct = default);

    /// <summary>
    /// Guarded insert of the pending enrollment together with its fixed assignments. It must write only while every
    /// chosen grid class still has a free permanent seat on the enrollment's effective date, evaluated inside the
    /// transaction and under the locks taken by <see cref="LockGridClassesAsync"/>; the losing side of a race for the
    /// last seat changes no row and is reported as <see cref="EnrollmentInsertResult.NoSeatLeft"/> with the grid class
    /// that ran out, so the caller never overbooks a grid class and never receives an unhandled exception for it.
    /// <para>
    /// The enrollment row is written as <c>PendingPayment</c> with its agreed price and its created instant, and each
    /// fixed assignment is written with the effective date as <c>ValidFrom</c> and an open window. The rows are not
    /// deletable afterwards: annulling or terminating closes the window and changes the enrollment status
    /// (<see cref="CloseFixedClassesAsync"/>), never removes the seat's history.
    /// </para>
    /// </summary>
    Task<EnrollmentInsertResult> TryInsertEnrollmentAsync(EnrollmentInsert insert, CancellationToken ct = default);

    /// <summary>
    /// Locks the enrollment row of the business inside the caller's transaction, like
    /// <c>SELECT ... WITH (UPDLOCK, ROWLOCK)</c>, and returns it with its fixed assignments and its membership periods.
    /// It returns <c>null</c> when the enrollment does not belong to the business, so a foreign enrollment and a
    /// missing one are the same answer and cannot be told apart. The read is the lock: a payment, an annulment and a
    /// termination all decide on the status this read returns, so without the hint two of them could read the same
    /// pre-transition state and write conflicting rows.
    /// <para>
    /// The period list is ordered by coverage start; it is what <see cref="MembershipCalendar.Renewal"/> is computed
    /// from, so the latest expiry of a paid period is read under the same lock that keeps a concurrent payment from
    /// writing another one.
    /// </para>
    /// </summary>
    Task<EnrollmentState?> LockEnrollmentAsync(int businessId, int enrollmentId, CancellationToken ct = default);

    /// <summary>
    /// Inserts the membership payment (concept <c>Abono</c>, referencing the enrollment) and returns its new id. It
    /// runs inside the caller's transaction: a payment and its period are written together or not at all. It never
    /// updates or deletes: corrections are payment annulments (T18, D-20).
    /// </summary>
    Task<int> InsertPaymentAsync(Payment payment, CancellationToken ct = default);

    /// <summary>
    /// Inserts the membership period the payment bought and returns its new id. One payment writes exactly one period
    /// (<c>UQ_MembershipPeriods_Payment</c>): the period is the coverage <see cref="MembershipCalendar"/> computed and
    /// its <c>PaymentId</c> is the payment inserted just before, both in the same transaction.
    /// </summary>
    Task<int> InsertMembershipPeriodAsync(MembershipPeriod period, CancellationToken ct = default);

    /// <summary>
    /// Writes the mutable state of the enrollment — status, reference day and the activation, annulment and
    /// termination instants — from the instance the caller read under <see cref="LockEnrollmentAsync"/> and mutated.
    /// It is always called inside that transaction, so the row cannot change between the read and this write.
    /// </summary>
    Task UpdateEnrollmentAsync(Enrollment enrollment, CancellationToken ct = default);

    /// <summary>
    /// Closes the validity of every still-open fixed assignment of the enrollment so none of them holds a seat from
    /// <paramref name="fromDate"/> on, and returns how many rows it closed. A closed assignment keeps its
    /// <c>ValidFrom</c> and the dates it already covered; the window is set to <c>fromDate - 1 day</c>, and an
    /// assignment whose validity starts on or after <paramref name="fromDate"/> is closed on its own start day,
    /// because <c>ValidTo</c> can never precede <c>ValidFrom</c> (<c>CK_EnrollmentFixedClasses_Range</c>). No row is
    /// deleted and an already closed one is never reopened (RF-21 closes the previous schedule the same way).
    /// </summary>
    Task<int> CloseFixedClassesAsync(
        int businessId, int enrollmentId, DateOnly fromDate, CancellationToken ct = default);

    /// <summary>
    /// The already generated class sessions the sync has to consider for the enrollment: the sessions of its fixed
    /// grid classes whose start is strictly after <paramref name="fromExclusive"/> and whose fixed assignment is valid
    /// on the session date, each with its capacity and all of its annotations — cancelled ones included. It never
    /// returns a session that already started and never generates one: creating the sessions of the future weeks is
    /// T13's job (RF-06), and backfilling the past is not a membership operation.
    /// <para>
    /// The instant bound is a query convenience, not the rule: the caller re-judges "has not passed" against the same
    /// clock read, so a repository that returns a session starting exactly at the bound can never slip an annotation
    /// into a class that is already running.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<EnrollmentSyncSessionRow>> ListFixedSyncSessionsAsync(
        int businessId, int enrollmentId, DateTime fromExclusive, CancellationToken ct = default);

    /// <summary>
    /// Guarded insert of the fixed annotations the sync resolved, one result per requested annotation in the same
    /// order. Each insert must write only while the session is a generated class session of the business that still
    /// has a free place — no unique index guarantees <c>anotados ≤ MaxCapacity</c>, so the guard repeats the seat rule
    /// inside the statement, exactly like T14's guarded drop-in insert — and only when neither
    /// <c>UX_Bookings_FixedPerSession</c> (the enrollment has any annotation of that session, cancelled included, which
    /// keeps a cancellation from being resurrected) nor <c>UX_Bookings_ActiveClient</c> (the client already holds a
    /// non-cancelled booking of that session) is satisfied. The annotation is written with <c>Modality = Fija</c> and
    /// its pending status; an existing one is never modified.
    /// </summary>
    Task<IReadOnlyList<FixedAnnotationInsertResult>> InsertFixedAnnotationsAsync(
        int businessId, IReadOnlyList<FixedAnnotationInsert> annotations, CancellationToken ct = default);

    /// <summary>
    /// Cancels the fixed annotations the enrollment holds in generated sessions that start at or after
    /// <paramref name="fromInclusive"/>, and returns how many it cancelled. Only an annotation that is not cancelled
    /// yet is written, and an annotation of an earlier session is never touched: annulling an enrollment releases the
    /// places it still holds (RF-20) and ending a membership releases them from the effective date on (RF-29 step 6),
    /// keeping the attendance history of the dates that already happened. No row is deleted.
    /// </summary>
    Task<int> CancelFixedAnnotationsAsync(
        int businessId, int enrollmentId, DateTime fromInclusive, CancellationToken ct = default);

    /// <summary>
    /// Every enrollment of the business with its client's name and its paid periods, for the membership list
    /// (RF-29). The list is read-only and writes nothing; the derived states are computed by the caller from the
    /// periods, never persisted (D-23). Annulled and terminated enrollments are part of the answer: they are what the
    /// "all" view shows and what the other views filter out, so a repository that dropped them would make that false
    /// filtering impossible to detect.
    /// </summary>
    Task<IReadOnlyList<EnrollmentListRow>> ListEnrollmentsAsync(
        int businessId, CancellationToken ct = default);
}
