using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Classes;

/// <summary>
/// SQL Server persistence for the per-student attendance and absence of a generated class session (RF-11, RF-12,
/// D-16, D-20, D-23). Every statement is scoped by <c>BusinessId</c>, so a booking, a client or an enrollment of
/// another tenant is invisible; nothing here commits or rolls back, because each command joins the caller's ambient
/// transaction through <see cref="DbSession.Transaction"/>, which owns the whole unit.
/// <para>
/// The lock takes two rows in one fixed order: the class session first (<c>Appointments</c>, the row the README calls
/// the class lock) and the annotation second (<c>Bookings</c>). The session row is resolved without a lock, locked
/// with an update lock that is held until the caller commits, and only then is the annotation locked and read with
/// its session. That order is what serializes two operations on the same annotation — a double attendance with a
/// charge, for instance — and, just as important, it is what keeps every later writer of that same <c>Appointments</c>
/// row out of the window between the read of the session state and the guarded status write: while this unit runs,
/// such a writer waits for the lock instead of splitting the note from the status, and the suspension of a class
/// (RF-10) is one of those writers: <c>SqlClassDateActionsRepository</c> — the suspension path that now exists — takes
/// the same <c>Appointments</c> row first, so the session state read below stays stable while this lock is the one
/// every writer of that row respects. The
/// effective-payment check reads under that same lock, so a second charge can never slip in, and a class that was
/// already suspended when the lock was taken is seen as suspended by the read below. The guarded status statement
/// repeats every storage-expressible rule — the discriminant, the modality, the open status, the absent cancellation,
/// the absent recorded instant and the session status — and changes no row when the annotation is no longer open or
/// its class is suspended, which is what makes a double submission or a suspended class report "no row changed"
/// instead of overwriting the first record. The coverage and charge rules stay the service's, because the statement
/// cannot express them.
/// </para>
/// </summary>
public sealed class SqlClassAttendanceRepository(DbSession session)
    : SqlRepositoryBase(session), IClassAttendanceRepository
{
    /// <summary>
    /// Resolves the class session of the annotation before any row is locked. It carries no hint, so it holds
    /// nothing: it only translates the booking the caller named into the session whose row must be locked first, so
    /// the order stays session then annotation (README: the class lock is the <c>Appointments</c> row). The tenant
    /// rides on both tables, so a foreign booking does not resolve.
    /// </summary>
    private const string ResolveSessionSql = """
        SELECT b.AppointmentId
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND b.Id = @bookingId
        """;

    /// <summary>
    /// Locks the class session row of the business inside the caller's transaction, before the annotation row. The
    /// update lock is held until the caller commits, so no write of that row can land between the read of the session
    /// state and the guarded status write: the suspension of a class — the write that sets
    /// <c>Appointments.Status = Suspendido</c> — waits for this unit instead of splitting the operation and leaving
    /// the observation orphaned.
    /// The tenant is part of the predicate, so a session of another business is simply not found, and the kind is not
    /// filtered: the service decides what to do with an appointment that is not a class, so the discriminant is never
    /// hidden by the port.
    /// </summary>
    private const string LockSessionSql = """
        SELECT a.Id AS AppointmentId
          FROM Appointments AS a WITH (UPDLOCK, ROWLOCK)
         WHERE a.Id = @appointmentId
           AND a.BusinessId = @businessId
        """;

    /// <summary>
    /// Locks the annotation row of the business inside the caller's transaction and reads it with its session, the
    /// status of that session, its client, the enrollment status and the class tariff. The session row was already
    /// locked above, and <c>b.AppointmentId = @appointmentId</c> keeps this read on that very session, so the status
    /// returned beside the annotation stays valid for the rest of the unit. The tenant rides on the booking predicate
    /// and on the client, service, appointment and enrollment joins, so a booking of another business is simply not
    /// found. The kind and the modality are not filtered here: the service decides what to do with a booking that is
    /// not a class annotation, so the discriminant is never hidden by the query. The enrollment join is a
    /// <c>LEFT JOIN</c>: a fixed annotation whose enrollment row cannot be resolved still comes back, and its missing
    /// status is judged as no coverage. The session status travels with the row (RF-10), so the suspension rule is
    /// judged from this read and no longer changes under it.
    /// </summary>
    private const string LockSql = """
        SELECT b.BusinessId, b.Id AS BookingId, b.ClientId, a.Id AS AppointmentId, a.StartsAt, a.Kind,
               a.Status AS SessionStatus, b.Status AS BookingStatus, b.Modality, b.EnrollmentId,
               e.Status AS EnrollmentStatus, s.Price AS ListPrice
          FROM Bookings AS b WITH (UPDLOCK, HOLDLOCK)
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
          JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
          JOIN Services AS s ON s.Id = a.ServiceId AND s.BusinessId = b.BusinessId
          LEFT JOIN Enrollments AS e ON e.Id = b.EnrollmentId AND e.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND b.Id = @bookingId
           AND b.AppointmentId = @appointmentId
        """;

    /// <summary>
    /// Every paid month of the fixed annotation's enrollment. The tenant condition rides on the joined enrollment,
    /// so the periods inherit the scope of the business-scoped enrollment they hang off. A drop-in has no enrollment
    /// and reads no period at all.
    /// </summary>
    private const string SelectPeriodsSql = """
        SELECT p.Id, p.EnrollmentId, p.CoverageStart, p.ExpiresOn, p.PaymentId, p.CreatedAt
          FROM MembershipPeriods AS p
          JOIN Enrollments AS e ON e.Id = p.EnrollmentId AND e.BusinessId = @businessId
         WHERE p.EnrollmentId = @enrollmentId
         ORDER BY p.CoverageStart, p.Id
        """;

    /// <summary>
    /// Whether the annotation already has a payment in force. An annulled payment does not count: it is no longer a
    /// real charge (RF-26, D-20). The read is taken under the caller's booking lock, so it cannot be raced by a
    /// concurrent charge of the same booking.
    /// </summary>
    private const string HasEffectivePaymentSql = """
        SELECT TOP (1) p.Id
          FROM Payments AS p WITH (UPDLOCK, HOLDLOCK)
          LEFT JOIN PaymentAnnulments AS pa ON pa.PaymentId = p.Id
         WHERE p.BusinessId = @businessId
           AND p.BookingId = @bookingId
           AND pa.Id IS NULL
        """;

    /// <summary>
    /// The guarded status write: one statement that records the attendance or the absence only while the annotation
    /// is still a class annotation of the business, still open, with no recorded instant, and whose class is not
    /// suspended (RF-10: a suspended class is lost, so nobody attends it). The three columns are written by the same
    /// statement, so there is no read-modify-write window between them, and a double submission, a cancelled
    /// annotation or a suspended class reports zero changed rows instead of overwriting the original instant or
    /// reviving a cancellation. The coverage and charge rules are not repeated here because the statement cannot
    /// express them; the service judges them from the lock.
    /// </summary>
    private const string SetStatusSql = """
        UPDATE b
           SET Status = @status, AttendanceRecordedAt = @now, AbsenceSource = @absenceSource
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND b.Id = @bookingId
           AND a.Kind = @classSession
           AND a.Status <> @suspended
           AND b.Modality IN (@fixed, @dropIn)
           AND b.Status IN (@pending, @confirmed)
           AND b.CancelledAt IS NULL
           AND b.AttendanceRecordedAt IS NULL
           AND b.AbsenceSource IS NULL
        """;

    /// <summary>
    /// The drop-in charge. Its concept is <c>DropInClass</c> and its reference is the booking, with no enrollment, so
    /// <c>CK_Payments_Reference</c> holds. It joins the caller's transaction: the payment commits with the
    /// attendance it accompanies and disappears with a rollback.
    /// </summary>
    private const string InsertPaymentSql = """
        INSERT INTO Payments (BusinessId, ClientId, Concept, BookingId, Amount, ListPrice, PriceReason, PaidAt, RecordedAt)
        VALUES (@businessId, @clientId, @concept, @bookingId, @amount, @listPrice, @priceReason, @now, @now)
        """;

    /// <summary>
    /// The observation on the client's record, tied to the session it was taken in, exactly like the individual path
    /// writes it (RF-11 step 4). The insert selects from the business-scoped client and session, so it writes no row
    /// when either does not belong to the business; the caller turns that "no row" into a failure, which is why it
    /// asks for this write before the status write and the charge: nothing persistent is in the unit yet when the
    /// note refuses to land.
    /// </summary>
    private const string InsertNoteSql = """
        INSERT INTO ClientNotes (ClientId, AppointmentId, Text, CreatedAt)
        SELECT c.Id, a.Id, @note, @now
          FROM Clients AS c
          JOIN Appointments AS a ON a.BusinessId = c.BusinessId AND a.Id = @appointmentId
         WHERE c.BusinessId = @businessId
           AND c.Id = @clientId
        """;

    public async Task<ClassAttendanceLock?> LockAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        // The session is resolved without a lock and locked before the annotation: that is the order the README
        // prescribes for the class lock. Because the session lock is held until the caller commits, the status
        // returned below cannot move between this read and the guarded status write: any writer of that row, the
        // suspension of a class among them, waits for this unit. That path lives in SqlClassDateActionsRepository and
        // takes the same row first, so the two directions agree on the lock order.
        var resolved = await QuerySingleOrDefaultAsync(
            ResolveSessionSql,
            MapMarker,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId),
            ct);

        if (resolved is null)
            return null;

        var lockedSession = await QuerySingleOrDefaultAsync(
            LockSessionSql,
            MapMarker,
            p => p.AddValue("@businessId", businessId).AddValue("@appointmentId", resolved.AppointmentId),
            ct);

        if (lockedSession is null)
            return null;

        var locked = await QuerySingleOrDefaultAsync(
            LockSql,
            MapRow,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@bookingId", bookingId)
                .AddValue("@appointmentId", resolved.AppointmentId),
            ct);

        if (locked is null)
            return null;

        // The periods are read after the booking row is locked and within the same transaction, so the coverage the
        // service judges cannot change under it. A drop-in has no enrollment and reads none.
        var periods = locked.Annotation.EnrollmentId is { } enrollmentId
            ? await QueryAsync(
                SelectPeriodsSql,
                MapPeriod,
                p => p.AddValue("@businessId", businessId).AddValue("@enrollmentId", enrollmentId),
                ct)
            : [];

        return new ClassAttendanceLock(locked.Annotation with { Periods = periods }, locked.SessionStatus);
    }

    public async Task<bool> HasEffectivePaymentAsync(
        int businessId, int bookingId, CancellationToken ct = default) =>
        await QuerySingleOrDefaultAsync(
            HasEffectivePaymentSql,
            reader => new PaymentMarker(reader.GetInt32(0)),
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId),
            ct) is not null;

    public async Task<bool> SetStatusAsync(
        int businessId, int bookingId, BookingStatus status, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            SetStatusSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@bookingId", bookingId)
                .AddValue("@status", status)
                .AddDateTime2("@now", now)
                .AddValue("@absenceSource", status == BookingStatus.Absent ? AbsenceSource.Owner : null)
                .AddValue("@classSession", AppointmentKind.ClassSession)
                .AddValue("@suspended", AppointmentStatus.Suspended)
                .AddValue("@fixed", BookingModality.Fixed)
                .AddValue("@dropIn", BookingModality.DropIn)
                .AddValue("@pending", BookingStatus.Pending)
                .AddValue("@confirmed", BookingStatus.Confirmed),
            ct) == 1;

    public async Task InsertPaymentAsync(ClassAttendancePayment payment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        await ExecuteAsync(
            InsertPaymentSql,
            p => p
                .AddValue("@businessId", payment.BusinessId)
                .AddValue("@clientId", payment.ClientId)
                .AddValue("@concept", payment.Concept)
                .AddValue("@bookingId", payment.BookingId)
                .AddValue("@amount", payment.Amount)
                .AddValue("@listPrice", payment.ListPrice)
                .AddValue("@priceReason", payment.PriceReason)
                .AddDateTime2("@now", payment.PaidAt),
            ct);
    }

    public async Task<bool> InsertNoteAsync(
        int businessId, int clientId, int appointmentId, string note, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            InsertNoteSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@clientId", clientId)
                .AddValue("@appointmentId", appointmentId)
                .AddValue("@note", note)
                .AddDateTime2("@now", now),
            ct) == 1;

    private static LockedRow MapRow(SqlDataReader reader) => new(
        new ClassAttendanceBookingState(
            reader.Get<int>("BusinessId"),
            reader.Get<int>("BookingId"),
            reader.Get<int>("ClientId"),
            reader.Get<int>("AppointmentId"),
            reader.Get<DateTime>("StartsAt"),
            reader.GetEnum<AppointmentKind>("Kind"),
            reader.GetEnum<BookingStatus>("BookingStatus"),
            reader.GetNullableEnum<BookingModality>("Modality"),
            reader.GetNullable<int>("EnrollmentId"),
            reader.GetNullableEnum<EnrollmentStatus>("EnrollmentStatus"),
            reader.Get<decimal>("ListPrice"),
            []),
        reader.GetEnum<AppointmentStatus>("SessionStatus"));

    /// <summary>
    /// One paid month rebuilt as the entity, exactly as the class detail read rebuilds it: only the coverage window
    /// feeds the rule, but the whole entity travels so the contract stays the membership period.
    /// </summary>
    private static MembershipPeriod MapPeriod(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        EnrollmentId = reader.Get<int>("EnrollmentId"),
        CoverageStart = reader.Get<DateOnly>("CoverageStart"),
        ExpiresOn = reader.Get<DateOnly>("ExpiresOn"),
        PaymentId = reader.Get<int>("PaymentId"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };

    private sealed record PaymentMarker(int Id);

    /// <summary>The session id resolved or locked before the annotation: both statements only need to name the row.</summary>
    private sealed record AppointmentMarker(int AppointmentId);

    private static AppointmentMarker MapMarker(SqlDataReader reader) => new(reader.Get<int>("AppointmentId"));

    /// <summary>One locked row: the annotation state plus the status of the class it belongs to.</summary>
    private sealed record LockedRow(ClassAttendanceBookingState Annotation, AppointmentStatus SessionStatus);
}
