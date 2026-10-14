using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Classes;

/// <summary>
/// SQL Server persistence for the two owner actions over a generated class date (RF-10, RF-22, D-15, D-24): cancelling
/// one annotation of that date and suspending the whole class session. Every statement is scoped by
/// <c>BusinessId</c>, so a session, a booking or a client of another tenant is invisible. Nothing here commits or rolls
/// back: each command joins the caller's ambient transaction through <see cref="DbSession.Transaction"/>, which owns
/// the whole unit.
/// <para>
/// The lock order is the one the README calls the class lock: the <c>Appointments</c> row first and the
/// <c>Bookings</c> rows afterwards. Cancelling resolves the session of the annotation without a hint, locks that
/// session row, and only then locks and reads the annotation; suspending locks the session row and reads the
/// annotations under that same lock. That single order is what the actions of this repository share with each other and
/// with <c>SqlClassAttendanceRepository</c>, so a suspension, a cancellation and a per-student attendance of the same
/// class all serialize on the <c>Appointments</c> row instead of interleaving. It is not a global no-deadlock
/// guarantee: the T14 drop-in cancellation (<c>SqlClassDropInRepository</c>) covers the annotation row first and
/// reaches the session row afterwards, the opposite order, so a two-writer inversion between that path and this one is
/// not excluded by this order. Both writes are guarded statements whose predicate repeats every storage-expressible
/// rule, so a double submission or a class whose state moved in the meantime changes no row instead of overwriting a
/// recorded instant.
/// </para>
/// </summary>
public sealed class SqlClassDateActionsRepository(DbSession session)
    : SqlRepositoryBase(session), IClassDateActionsRepository
{
    /// <summary>
    /// Resolves the class session of the annotation before any row is locked. It carries no hint, so it holds nothing:
    /// it only translates the booking the caller named into the session whose row must be locked first, so the order
    /// stays session then annotation. The tenant rides on both tables, so a foreign booking does not resolve.
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
    /// update lock is held until the caller commits, so a competing suspension of that same class — the write that
    /// sets <c>Appointments.Status = Suspendido</c> — waits for this unit instead of landing between the read and the
    /// guarded cancellation. The tenant is part of the predicate, so a session of another business is not found, and
    /// the kind is not filtered: the service decides what to do with an appointment that is not a class, so the
    /// discriminant is never hidden by the port.
    /// </summary>
    private const string LockSessionSql = """
        SELECT a.Id AS AppointmentId
          FROM Appointments AS a WITH (UPDLOCK, ROWLOCK)
         WHERE a.Id = @appointmentId
           AND a.BusinessId = @businessId
        """;

    /// <summary>
    /// Locks the annotation row of the business inside the caller's transaction and reads it with its session facts.
    /// The session row was already locked above, and <c>b.AppointmentId = @appointmentId</c> keeps this read on that
    /// very session, so the class state returned beside the annotation stays valid for the rest of the unit. The tenant
    /// rides on both tables, so a booking of another business is simply not found. The kind and the modality are not
    /// filtered here: the service decides what to do with a booking that is not a class annotation.
    /// </summary>
    private const string LockBookingSql = """
        SELECT b.BusinessId, b.Id AS BookingId, a.Id AS AppointmentId, a.StartsAt, a.Kind,
               a.Status AS AppointmentStatus, b.Status AS BookingStatus, b.Modality
          FROM Bookings AS b WITH (UPDLOCK, HOLDLOCK)
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND b.Id = @bookingId
           AND b.AppointmentId = @appointmentId
        """;

    /// <summary>
    /// The guarded cancellation: one statement that writes <c>Cancelado</c>, <c>CancelledBy = Owner</c> and
    /// <c>CancelledAt = @now</c> only while the annotation is still a fixed or drop-in annotation of a class session
    /// of the business that is not cancelled and carries no recorded cancellation instant, and whose class is still
    /// <c>Programado</c> and starting after <paramref name="now"/>. The three columns are written by the same
    /// statement, so there is no read-modify-write window between them, and a double cancellation reports zero changed
    /// rows instead of overwriting the original instant and author. Nothing else is touched: the enrollment, the grid,
    /// the membership periods and every payment stay exactly as they were (RF-22, D-24).
    /// </summary>
    private const string CancelBookingSql = """
        UPDATE b
           SET Status = @cancelled, CancelledBy = @owner, CancelledAt = @now
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND b.Id = @bookingId
           AND a.Kind = @classSession
           AND a.Status = @scheduled
           AND a.StartsAt > @now
           AND b.Modality IN (@fixed, @dropIn)
           AND b.Status <> @cancelled
           AND b.CancelledAt IS NULL
        """;

    /// <summary>
    /// Locks the class session row of the business — the class lock of the README — and reads it inside the caller's
    /// transaction. The update lock is held until the caller commits, so a cancellation of one of its annotations
    /// cannot land between the read below and the guarded suspension write: it waits for this unit. The tenant is part
    /// of the predicate, so a session of another business is not found, and the kind is not filtered: the service
    /// decides what to do with an appointment that is not a class.
    /// </summary>
    private const string LockSessionForSuspensionSql = """
        SELECT a.BusinessId, a.Id AS AppointmentId, a.StartsAt, a.Kind, a.Status AS SessionStatus
          FROM Appointments AS a WITH (UPDLOCK, ROWLOCK)
         WHERE a.Id = @appointmentId
           AND a.BusinessId = @businessId
        """;

    /// <summary>
    /// Every annotation of that locked session, cancelled ones included, each with its client's name and email. The
    /// read is taken <c>UPDLOCK, HOLDLOCK</c> — after the session row was locked, never before, so the table order of
    /// every writer stays the same — which is what keeps a cancellation of one of these annotations out of the window
    /// between this read and the suspension write. The <c>LEFT JOIN</c> to <c>Clients</c> keeps an annotation whose
    /// client row cannot be resolved visible, with an empty name and email, instead of dropping a student from the
    /// suspension; the tenant condition keeps another business's client out of the answer.
    /// </summary>
    private const string LockSessionBookingsSql = """
        SELECT b.Id AS BookingId, b.ClientId, b.Status AS BookingStatus, b.Modality,
               c.Email, c.FirstName, c.LastName
          FROM Bookings AS b WITH (UPDLOCK, HOLDLOCK)
          LEFT JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
         WHERE b.AppointmentId = @appointmentId
           AND b.BusinessId = @businessId
         ORDER BY b.Id
        """;

    /// <summary>
    /// The guarded suspension: one statement that writes <c>Suspendido</c>, <c>SuspendedAt = @now</c> and the trimmed
    /// reason only while the appointment is still a class session of the business that is <c>Programado</c> and
    /// starting after <paramref name="now"/>. A second suspension, or a class that started in the meantime, reports
    /// zero changed rows. It writes the session row only: every annotation, enrollment and payment stays untouched,
    /// because suspending a class never recovers it, never charges it and never refunds or credits anything (RF-10,
    /// Regla 17, D-15, D-24).
    /// </summary>
    private const string SuspendSql = """
        UPDATE a
           SET Status = @suspended, SuspendedAt = @now, SuspensionReason = @reason
          FROM Appointments AS a
         WHERE a.Id = @appointmentId
           AND a.BusinessId = @businessId
           AND a.Kind = @classSession
           AND a.Status = @scheduled
           AND a.StartsAt > @now
        """;

    /// <summary>
    /// The outbox row of one suspension notice. It joins the caller's transaction, so the queued email commits with
    /// the suspension it announces and disappears with a rollback. <c>Id</c> is not read back: a queued notice has no
    /// use for it.
    /// </summary>
    private const string InsertNotificationSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt)
        VALUES (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL)
        """;

    public async Task<ClassBookingCancellationState?> LockBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        // The session is resolved without a lock and locked before the annotation: that is the order the README
        // prescribes for the class lock. Because the lock is held until the caller commits, a competing suspension of
        // that row waits for this unit instead of landing between the read of the class state and the guarded write.
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

        return await QuerySingleOrDefaultAsync(
            LockBookingSql,
            MapCancellationState,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@bookingId", bookingId)
                .AddValue("@appointmentId", resolved.AppointmentId),
            ct);
    }

    public async Task<bool> CancelBookingAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            CancelBookingSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@bookingId", bookingId)
                .AddValue("@classSession", AppointmentKind.ClassSession)
                .AddValue("@scheduled", AppointmentStatus.Scheduled)
                .AddValue("@fixed", BookingModality.Fixed)
                .AddValue("@dropIn", BookingModality.DropIn)
                .AddValue("@cancelled", BookingStatus.Cancelled)
                .AddValue("@owner", CancelledBy.Owner)
                .AddDateTime2("@now", now),
            ct) == 1;

    public async Task<ClassSuspensionSession?> LockSessionAsync(
        int businessId, int appointmentId, CancellationToken ct = default)
    {
        // The class lock goes first and lives until the caller's transaction ends: the annotations below are read
        // while it is held, so no cancellation of one of them can slip between this read and the suspension write.
        var locked = await QuerySingleOrDefaultAsync(
            LockSessionForSuspensionSql,
            MapSession,
            p => p.AddValue("@businessId", businessId).AddValue("@appointmentId", appointmentId),
            ct);

        if (locked is null)
            return null;

        var bookings = await QueryAsync(
            LockSessionBookingsSql,
            MapBooking,
            p => p.AddValue("@businessId", businessId).AddValue("@appointmentId", appointmentId),
            ct);

        return new ClassSuspensionSession(
            locked.BusinessId,
            locked.AppointmentId,
            locked.StartsAt,
            locked.Kind,
            locked.Status,
            bookings
                .Select(booking => new ClassSuspensionBooking(
                    booking.BookingId,
                    booking.ClientId,
                    booking.Email ?? "",
                    ReadClientName(booking.FirstName, booking.LastName),
                    booking.Modality,
                    booking.Status))
                .ToArray());
    }

    public async Task<bool> SuspendAsync(
        int businessId, int appointmentId, string reason, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            SuspendSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@appointmentId", appointmentId)
                .AddValue("@reason", reason)
                .AddValue("@classSession", AppointmentKind.ClassSession)
                .AddValue("@scheduled", AppointmentStatus.Scheduled)
                .AddValue("@suspended", AppointmentStatus.Suspended)
                .AddDateTime2("@now", now),
            ct) == 1;

    public async Task<bool> EnqueueNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            return await ExecuteAsync(
                InsertNotificationSql,
                p => p
                    .AddValue("@businessId", notification.BusinessId)
                    .AddValue("@type", notification.Type)
                    .AddValue("@recipient", notification.Recipient)
                    .AddValue("@subject", notification.Subject)
                    .AddValue("@bodyHtml", notification.BodyHtml)
                    .AddValue("@dedupKey", notification.DedupKey)
                    .AddValue("@status", notification.Status)
                    .AddDateTime2("@createdAt", notification.CreatedAt),
                ct) == 1;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_Notifications_DedupKey: that notice is already queued for the same student and the same class, so a
            // retry is a deduplication hit instead of a second email or a failure the caller cannot fix.
            return false;
        }
    }

    /// <summary>
    /// Composes the client's display name from the <c>LEFT JOIN</c>ed row. It stays empty for a row that could not be
    /// resolved: the annotation is still part of the suspension, it is the display name that is missing.
    /// </summary>
    private static string ReadClientName(string? firstName, string? lastName) =>
        $"{firstName} {lastName}".Trim();

    private static ClassBookingCancellationState MapCancellationState(SqlDataReader reader) => new(
        reader.Get<int>("BusinessId"),
        reader.Get<int>("BookingId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<DateTime>("StartsAt"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetEnum<BookingStatus>("BookingStatus"),
        reader.GetNullableEnum<BookingModality>("Modality"));

    private static SessionProjection MapSession(SqlDataReader reader) => new(
        reader.Get<int>("BusinessId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<DateTime>("StartsAt"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetEnum<AppointmentStatus>("SessionStatus"));

    private static BookingProjection MapBooking(SqlDataReader reader) => new(
        reader.Get<int>("BookingId"),
        reader.Get<int>("ClientId"),
        reader.GetEnum<BookingStatus>("BookingStatus"),
        reader.GetNullableEnum<BookingModality>("Modality"),
        reader.GetNullableString("Email"),
        reader.GetNullableString("FirstName"),
        reader.GetNullableString("LastName"));

    /// <summary>The session id resolved or locked before the annotation: both statements only need to name the row.</summary>
    private sealed record AppointmentMarker(int AppointmentId);

    private static AppointmentMarker MapMarker(SqlDataReader reader) => new(reader.Get<int>("AppointmentId"));

    /// <summary>One locked class session reduced to what the suspension rules and the notices need.</summary>
    private sealed record SessionProjection(
        int BusinessId,
        int AppointmentId,
        DateTime StartsAt,
        AppointmentKind Kind,
        AppointmentStatus Status);

    /// <summary>One annotation of that session, with the client's contact fields resolved by the left join.</summary>
    private sealed record BookingProjection(
        int BookingId,
        int ClientId,
        BookingStatus Status,
        BookingModality? Modality,
        string? Email,
        string? FirstName,
        string? LastName);
}
