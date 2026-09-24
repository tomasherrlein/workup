using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Classes;

/// <summary>
/// SQL Server persistence for booking a drop-in into an already generated class session (RF-07 cupo &gt; 1, RF-22,
/// D-25), reading the owner's drop-in roster and cancelling a drop-in before its class starts. Every statement is
/// scoped by <c>BusinessId</c>, so a session, a booking or a client of another tenant is invisible. Nothing here
/// commits or rolls back: each command joins the caller's ambient transaction through
/// <see cref="DbSession.Transaction"/>, which owns the whole unit.
/// <para>
/// The discriminant <c>a.Kind = ClassSession</c> is part of every session statement, listing and roster included. An
/// individual turn is also <c>Programado</c> with capacity 1, so without the discriminant it would be offered, locked
/// and booked as a class. The roster carries the modality discriminant too: only <c>b.Modality = Suelta</c> rows are
/// ever read as drop-in annotations, so a fixed annotation of the same class can never be presented as one.
/// </para>
/// </summary>
public sealed class SqlClassDropInRepository(DbSession session)
    : SqlRepositoryBase(session), IClassDropInRepository
{
    /// <summary>
    /// Dated class sessions of the business whose start falls inside the half-open instant range
    /// <c>[@rangeStart, @dayAfterRangeEnd)</c>, optionally narrowed to one service. No status or occupancy filter:
    /// the bookable window and the capacity rule are the caller's business rules, and the read only supplies the
    /// facts. Ordered by start so the caller can reorder without a second sort key.
    /// </summary>
    private const string ListSessionsSql = """
        SELECT a.Id AS AppointmentId, a.ServiceId, a.WorkerId, a.StartsAt, a.MaxCapacity, a.Kind, a.Status
          FROM Appointments AS a
         WHERE a.BusinessId = @businessId
           AND a.Kind = @classSession
           AND a.StartsAt >= @rangeStart
           AND a.StartsAt < @dayAfterRangeEnd
           AND (@serviceId IS NULL OR a.ServiceId = @serviceId)
         ORDER BY a.StartsAt, a.Id
        """;

    /// <summary>
    /// Every booking of those same sessions, cancelled ones included, so the caller — and not this query — decides
    /// what a cancellation releases. Each row carries its booking id, its modality and the client's name, so the same
    /// shape serves the occupancy count, the owner roster and the cancellation read. The session filter is repeated
    /// instead of joining a temporary set; the join to <c>Appointments</c> carries the tenant on both sides, and the
    /// join to <c>Clients</c> is a <c>LEFT JOIN</c> with the tenant condition: a booking whose client row is missing
    /// or belongs to another business must still be counted against the capacity, never dropped from the occupancy
    /// silently.
    /// </summary>
    private const string ListSessionBookingsSql = """
        SELECT b.Id AS BookingId, b.AppointmentId, b.ClientId, b.Status, b.Modality, c.FirstName, c.LastName
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
          LEFT JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND a.Kind = @classSession
           AND a.StartsAt >= @rangeStart
           AND a.StartsAt < @dayAfterRangeEnd
           AND (@serviceId IS NULL OR a.ServiceId = @serviceId)
         ORDER BY b.AppointmentId, b.Id
        """;

    /// <summary>
    /// Locks the session row inside the caller's transaction. <c>UPDLOCK, ROWLOCK</c> over the primary key is what
    /// serializes two bookings that compete for the last place: the loser waits here, and by the time it reads the
    /// occupancy below, the winner's booking is committed and visible. The tenant and the kind are part of the
    /// predicate, so a foreign or individual appointment is simply not found.
    /// </summary>
    private const string LockSessionSql = """
        SELECT a.Id AS AppointmentId, a.ServiceId, a.WorkerId, a.StartsAt, a.MaxCapacity, a.Kind, a.Status
          FROM Appointments AS a WITH (UPDLOCK, ROWLOCK)
         WHERE a.Id = @appointmentId
           AND a.BusinessId = @businessId
           AND a.Kind = @classSession
        """;

    /// <summary>Live bookings of the locked session, under the same lock, cancelled ones included.</summary>
    private const string LockedSessionBookingsSql = """
        SELECT b.Id AS BookingId, b.AppointmentId, b.ClientId, b.Status, b.Modality, c.FirstName, c.LastName
          FROM Bookings AS b
          LEFT JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
         WHERE b.AppointmentId = @appointmentId
           AND b.BusinessId = @businessId
         ORDER BY b.Id
        """;

    /// <summary>
    /// Upcoming class sessions of the business: <c>Kind = ClassSession</c> with a start strictly after the caller's
    /// instant, suspended ones included because suspending a class cancels nobody and the owner still needs to see
    /// who is booked in it. Ordered by start, like the offer listing.
    /// </summary>
    private const string ListUpcomingRosterSessionsSql = """
        SELECT a.Id AS AppointmentId, a.ServiceId, a.WorkerId, a.StartsAt, a.MaxCapacity, a.Kind, a.Status
          FROM Appointments AS a
         WHERE a.BusinessId = @businessId
           AND a.Kind = @classSession
           AND a.StartsAt > @fromExclusive
         ORDER BY a.StartsAt, a.Id
        """;

    /// <summary>
    /// The drop-in annotations of those same sessions, with the client's name, cancelled ones included. The filter
    /// <c>b.Modality = @dropIn</c> is what keeps a fixed annotation out of the roster: an annotation without a
    /// modality belongs to an individual turn and is never a drop-in either. The <c>LEFT JOIN</c> to <c>Clients</c>
    /// keeps an annotation whose client row cannot be resolved visible with an empty name instead of hiding it from
    /// the owner, while the tenant condition keeps another business's client out of the answer.
    /// </summary>
    private const string ListUpcomingRosterDropInsSql = """
        SELECT b.Id AS BookingId, b.AppointmentId, b.ClientId, b.Status, b.Modality, c.FirstName, c.LastName
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
          LEFT JOIN Clients AS c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND a.Kind = @classSession
           AND a.StartsAt > @fromExclusive
           AND b.Modality = @dropIn
         ORDER BY b.AppointmentId, b.Id
        """;

    /// <summary>
    /// Locks the booking row of the business together with its session inside the caller's transaction, so the rules
    /// the caller reads here cannot change under it before it writes. The tenant is part of the predicate, so a
    /// booking of another business is simply not found and is indistinguishable from a missing one. The modality and
    /// the kind are not filtered: the caller decides what to do with a booking that is not a drop-in of a class.
    /// </summary>
    private const string LockDropInForCancellationSql = """
        SELECT b.Id AS BookingId, a.Id AS AppointmentId, a.StartsAt, a.Kind, a.Status AS AppointmentStatus,
               b.Status AS BookingStatus, b.Modality
          FROM Bookings AS b WITH (UPDLOCK, ROWLOCK)
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND b.Id = @bookingId
        """;

    /// <summary>
    /// The guarded cancellation: one statement that writes <c>Cancelado</c>, <c>CancelledAt</c> and
    /// <c>CancelledBy = Owner</c> only while the annotation is still a drop-in that is not cancelled and its session
    /// is still a <c>ClassSession</c>, <c>Programado</c> and starting after <paramref name="now"/>. The guard makes a
    /// double cancel report zero changed rows instead of overwriting the original instant and author, and because the
    /// three columns are written by the same statement there is no read-modify-write window between them.
    /// </summary>
    private const string CancelDropInSql = """
        UPDATE b
           SET Status = @cancelled, CancelledBy = @owner, CancelledAt = @now
          FROM Bookings AS b
          JOIN Appointments AS a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
         WHERE b.BusinessId = @businessId
           AND b.Id = @bookingId
           AND b.Modality = @dropIn
           AND b.Status <> @cancelled
           AND a.Kind = @classSession
           AND a.Status = @scheduled
           AND a.StartsAt > @now
        """;

    /// <summary>
    /// Identifies a client by its email inside one business (UQ_Clients_Email). The comparison relies on the
    /// case-insensitive collation the unique index itself relies on, and the projection returns the stored email —
    /// which is the recipient of the notice — not the requested one.
    /// </summary>
    private const string SelectClientByEmailSql = """
        SELECT c.Id AS ClientId, c.Email, c.FirstName, c.LastName
          FROM Clients AS c
         WHERE c.BusinessId = @businessId
           AND c.Email = @email
        """;

    /// <summary>Reads one client of the business, used by the owner-assisted path (RF-11).</summary>
    private const string SelectClientByIdSql = """
        SELECT c.Id AS ClientId, c.Email, c.FirstName, c.LastName
          FROM Clients AS c
         WHERE c.Id = @clientId
           AND c.BusinessId = @businessId
        """;

    /// <summary>
    /// Creates the identified client without a password: credentials live in <c>ClientAccounts</c>, so no account row
    /// is written here (the T09 flow, RF-07 step 5). The email is normalized by the caller of this statement.
    /// <para>
    /// <c>CreatedAt</c> is the server clock because the port carries no caller instant for the client row and the
    /// timestamp never feeds a business rule; the booking's own <c>CreatedAt</c> is the service's single clock read,
    /// which is what <see cref="DropInInsert.CreatedAt"/> persists.
    /// </para>
    /// </summary>
    private const string InsertClientSql = """
        INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt)
        OUTPUT INSERTED.Id
        VALUES (@businessId, @firstName, @lastName, @email, @phone, NULL, SYSDATETIME())
        """;

    /// <summary>
    /// The guarded drop-in insert: one statement that writes the booking only while the appointment is a class session
    /// of the business with a free place and the client holds no non-cancelled booking of it. The three reads it
    /// guards on are taken <c>UPDLOCK, HOLDLOCK</c>, so they are serialized against a competing booking of the same
    /// session and held until the caller commits: the count cannot go stale between this read and the row it writes.
    /// Nothing is returned when any guard fails, and the caller diagnoses which one it was.
    /// </summary>
    private const string GuardedInsertBookingSql = """
        INSERT INTO Bookings (BusinessId, AppointmentId, ClientId, EnrollmentId, Modality, Status, CreatedAt)
        OUTPUT INSERTED.Id
        SELECT @businessId, @appointmentId, @clientId, NULL, @modality, @status, @createdAt
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
                      AND mine.ClientId = @clientId
                      AND mine.Status <> @cancelled
               )
        """;

    /// <summary>
    /// Why <see cref="GuardedInsertBookingSql"/> wrote nothing, read under the same transaction that already holds the
    /// guard's locks, so the answer cannot change under us. The client's own booking is decided first, matching the
    /// caller's rule: a client that already holds a place must hear that, even when that very booking is what filled
    /// the class. <c>0</c> means "no defined outcome", the enum has no such member, and the caller fails loudly
    /// instead of guessing.
    /// </summary>
    private const string DiagnoseFailedInsertSql = """
        SELECT CASE
                   WHEN EXISTS
                        (
                            SELECT 1
                              FROM Bookings AS mine WITH (UPDLOCK, HOLDLOCK)
                             WHERE mine.AppointmentId = @appointmentId
                               AND mine.ClientId = @clientId
                               AND mine.Status <> @cancelled
                        )
                       THEN @alreadyBooked
                   WHEN EXISTS
                        (
                            SELECT 1
                              FROM Appointments AS session WITH (UPDLOCK, HOLDLOCK)
                             WHERE session.Id = @appointmentId
                               AND session.BusinessId = @businessId
                               AND session.Kind = @classSession
                        )
                       THEN @noPlaceLeft
                   ELSE 0
               END
        """;

    /// <summary>
    /// The outbox row. It joins the caller's transaction, so the queued email commits with the booking it announces
    /// and disappears with a rollback. <c>Id</c> is not read back: a queued notice has no use for it.
    /// </summary>
    private const string InsertNotificationSql = """
        INSERT INTO Notifications (BusinessId, Type, Recipient, Subject, BodyHtml, DedupKey, Status, Attempts, LastError, CreatedAt, SentAt)
        VALUES (@businessId, @type, @recipient, @subject, @bodyHtml, @dedupKey, @status, 0, NULL, @createdAt, NULL)
        """;

    public async Task<IReadOnlyList<ClassSessionRow>> ListSessionsAsync(
        int businessId, DateOnly fromInclusive, DateOnly toInclusive, int? serviceId, CancellationToken ct = default)
    {
        var (rangeStart, dayAfterRangeEnd) = RangeInstants(fromInclusive, toInclusive);

        var sessions = await QueryAsync(
            ListSessionsSql,
            MapSession,
            p => AddRangeParameters(p, businessId, serviceId, rangeStart, dayAfterRangeEnd),
            ct);

        var bookings = await QueryAsync(
            ListSessionBookingsSql,
            MapBooking,
            p => AddRangeParameters(p, businessId, serviceId, rangeStart, dayAfterRangeEnd),
            ct);

        var bookingsByAppointment = bookings
            .GroupBy(booking => booking.AppointmentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ClassSessionBooking>)group.Select(booking => booking.Booking).ToArray());

        // A session without bookings is returned with an empty list, never dropped: an empty class is the most
        // bookable one.
        return sessions
            .Select(row => new ClassSessionRow(
                row.AppointmentId,
                row.ServiceId,
                row.WorkerId,
                row.StartsAt,
                row.MaxCapacity,
                row.Kind,
                row.Status,
                bookingsByAppointment.TryGetValue(row.AppointmentId, out var attached) ? attached : []))
            .ToArray();
    }

    public async Task<ClassSessionRow?> LockSessionAsync(
        int businessId, int appointmentId, CancellationToken ct = default)
    {
        // The lock is taken by this read and lives until the caller's transaction ends: the row stays locked while
        // the caller counts the places and runs the guarded insert.
        var locked = await QuerySingleOrDefaultAsync(
            LockSessionSql,
            MapSession,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@appointmentId", appointmentId)
                .AddValue("@classSession", AppointmentKind.ClassSession),
            ct);

        if (locked is null)
            return null;

        var bookings = await QueryAsync(
            LockedSessionBookingsSql,
            MapBooking,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@appointmentId", appointmentId),
            ct);

        return new ClassSessionRow(
            locked.AppointmentId,
            locked.ServiceId,
            locked.WorkerId,
            locked.StartsAt,
            locked.MaxCapacity,
            locked.Kind,
            locked.Status,
            bookings.Select(booking => booking.Booking).ToArray());
    }

    public async Task<IReadOnlyList<ClassSessionRow>> ListUpcomingRosterAsync(
        int businessId, DateTime fromExclusive, CancellationToken ct = default)
    {
        var sessions = await QueryAsync(
            ListUpcomingRosterSessionsSql,
            MapSession,
            p => AddRosterParameters(p, businessId, fromExclusive),
            ct);

        var dropIns = await QueryAsync(
            ListUpcomingRosterDropInsSql,
            MapBooking,
            p => AddRosterParameters(p, businessId, fromExclusive),
            ct);

        var dropInsByAppointment = dropIns
            .GroupBy(booking => booking.AppointmentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ClassSessionBooking>)group.Select(booking => booking.Booking).ToArray());

        // A class nobody booked as a drop-in is still part of the roster, with an empty annotation list: the owner
        // screen shows the upcoming classes and, per class, who is booked as a drop-in.
        return sessions
            .Select(row => new ClassSessionRow(
                row.AppointmentId,
                row.ServiceId,
                row.WorkerId,
                row.StartsAt,
                row.MaxCapacity,
                row.Kind,
                row.Status,
                dropInsByAppointment.TryGetValue(row.AppointmentId, out var attached) ? attached : []))
            .ToArray();
    }

    public Task<DropInCancellationState?> LockDropInForCancellationAsync(
        int businessId, int bookingId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            LockDropInForCancellationSql,
            MapDropInCancellationState,
            p => p.AddValue("@businessId", businessId).AddValue("@bookingId", bookingId),
            ct);

    public async Task<bool> CancelDropInAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            CancelDropInSql,
            p => p
                .AddValue("@businessId", businessId)
                .AddValue("@bookingId", bookingId)
                .AddValue("@dropIn", BookingModality.DropIn)
                .AddValue("@classSession", AppointmentKind.ClassSession)
                .AddValue("@scheduled", AppointmentStatus.Scheduled)
                .AddValue("@cancelled", BookingStatus.Cancelled)
                .AddValue("@owner", CancelledBy.Owner)
                .AddDateTime2("@now", now),
            ct) == 1;

    public async Task<DropInClient> IdentifyClientAsync(
        int businessId, string email, string firstName, string lastName, string? phone,
        CancellationToken ct = default)
    {
        // Stored emails are lower-case (the schema's own convention), so the lookup and the insert use the same
        // normalized value and an existing client is found instead of duplicated.
        var normalizedEmail = Text.NormalizeEmail(email);

        var existing = await QuerySingleOrDefaultAsync(
            SelectClientByEmailSql,
            MapDropInClient,
            p => p.AddValue("@businessId", businessId).AddValue("@email", normalizedEmail),
            ct);

        // An existing client is returned exactly as stored: never duplicated, never updated.
        if (existing is not null)
            return existing;

        try
        {
            var clientId = await InsertAsync(
                InsertClientSql,
                p => p
                    .AddValue("@businessId", businessId)
                    .AddValue("@firstName", firstName)
                    .AddValue("@lastName", lastName)
                    .AddValue("@email", normalizedEmail)
                    .AddValue("@phone", phone),
                ct);

            return new DropInClient(clientId, normalizedEmail, firstName, lastName);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_Clients_Email won a concurrent creation of the same business and email. The caller asked to resolve
            // that collision, so the row is read back inside the same transaction instead of throwing at it.
            var raced = await QuerySingleOrDefaultAsync(
                SelectClientByEmailSql,
                MapDropInClient,
                p => p.AddValue("@businessId", businessId).AddValue("@email", normalizedEmail),
                ct);

            return raced ?? throw new InvalidOperationException(
                $"Client identification lost the unique key for business {businessId} but the stored client could not be read back.");
        }
    }

    public Task<DropInClient?> FindClientAsync(int businessId, int clientId, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(
            SelectClientByIdSql,
            MapDropInClient,
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId),
            ct);

    public async Task<DropInInsertResult> TryInsertDropInAsync(DropInInsert insert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(insert);

        try
        {
            // ExecuteReader through QueryAsync, not ExecuteScalar: OUTPUT is a result set and an empty one is the
            // normal "a guard refused" answer, not an error.
            var insertedIds = await QueryAsync(
                GuardedInsertBookingSql,
                reader => reader.GetInt32(0),
                p => AddInsertParameters(p, insert),
                ct);

            if (insertedIds.Count > 0)
                return DropInInsertResult.Inserted(insertedIds[0]);

            // The guard refused: the transaction still holds the locks the guard read under, so this second statement
            // answers which guard it was and that answer cannot drift before the caller rolls back.
            var outcome = await ScalarAsync<int>(
                DiagnoseFailedInsertSql,
                p => AddDiagnoseParameters(p, insert),
                ct);

            if (outcome == (int)DropInInsertResultType.NoPlaceLeft)
                return DropInInsertResult.NoPlaceLeft();

            if (outcome == (int)DropInInsertResultType.AlreadyBooked)
                return DropInInsertResult.AlreadyBooked();

            // The diagnosis returns 0 only when no guard explains the refusal — the session is not a class session of
            // this business, for instance. The enum has no zero member exactly so that this can be detected: returning
            // one of the three outcomes would claim something that did not happen.
            throw new InvalidOperationException(
                $"The guarded drop-in insert wrote no booking for appointment {insert.AppointmentId} and reports no defined outcome ({outcome}).");
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // The guards already reject a client that holds a non-cancelled booking of the session, but the unique
            // index UX_Bookings_ActiveClient can still fire for a concurrent identical booking that slipped between
            // two guarded reads. The caller asked for that collision to be named, never for a raw unique-key failure.
            return DropInInsertResult.AlreadyBooked();
        }
    }

    public async Task EnqueueNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            await ExecuteAsync(
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
                ct);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // UQ_Notifications_DedupKey: that notice is already queued, so a retry of the same booking is a no-op
            // instead of a second email or a failure the caller cannot fix.
        }
    }

    /// <summary>
    /// Translates the caller's inclusive dates into the half-open instant range <c>DATETIME2</c> can express:
    /// <c>[fromInclusive 00:00, (toInclusive + 1 day) 00:00)</c>. The exclusive bound is computed, never the last
    /// instant of the day, because <c>DATETIME2(0)</c> has no inclusive maximum for a whole day and "23:59:59" would
    /// leave the fractional part of the day outside. <see cref="DateOnly.MaxValue"/> has no next day and is rejected
    /// loudly instead of being widened or clamped.
    /// </summary>
    private static (DateTime RangeStart, DateTime DayAfterRangeEnd) RangeInstants(
        DateOnly fromInclusive, DateOnly toInclusive)
    {
        if (toInclusive == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(toInclusive),
                toInclusive,
                "An inclusive date range cannot end on DateOnly.MaxValue: (toInclusive + 1 day) 00:00 has no representable instant, so the exclusive bound cannot be computed.");

        return (fromInclusive.ToDateTime(TimeOnly.MinValue), toInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    private static void AddRangeParameters(
        SqlParameterCollection parameters, int businessId, int? serviceId, DateTime rangeStart,
        DateTime dayAfterRangeEnd) =>
        parameters
            .AddValue("@businessId", businessId)
            .AddValue("@classSession", AppointmentKind.ClassSession)
            .AddValue("@serviceId", serviceId)
            .AddDateTime2("@rangeStart", rangeStart)
            .AddDateTime2("@dayAfterRangeEnd", dayAfterRangeEnd);

    /// <summary>
    /// The two instants a range-based read needs. <c>DATETIME2</c> has no inclusive maximum for a whole day, so the
    /// bound is half-open on the end: <c>StartsAt &lt; (toInclusive + 1 day) 00:00</c>.
    /// </summary>
    private static void AddRosterParameters(
        SqlParameterCollection parameters, int businessId, DateTime fromExclusive) =>
        parameters
            .AddValue("@businessId", businessId)
            .AddValue("@classSession", AppointmentKind.ClassSession)
            .AddValue("@dropIn", BookingModality.DropIn)
            .AddDateTime2("@fromExclusive", fromExclusive);

    private static void AddInsertParameters(SqlParameterCollection parameters, DropInInsert insert) =>
        parameters
            .AddValue("@businessId", insert.BusinessId)
            .AddValue("@appointmentId", insert.AppointmentId)
            .AddValue("@clientId", insert.ClientId)
            .AddValue("@modality", BookingModality.DropIn)
            .AddValue("@status", BookingStatus.Pending)
            .AddValue("@classSession", AppointmentKind.ClassSession)
            .AddValue("@cancelled", BookingStatus.Cancelled)
            .AddDateTime2("@createdAt", insert.CreatedAt);

    /// <summary>
    /// The insert parameters plus the two outcomes the diagnosis can report, passed as the enum values so the SQL
    /// holds no outcome number of its own.
    /// </summary>
    private static void AddDiagnoseParameters(SqlParameterCollection parameters, DropInInsert insert)
    {
        AddInsertParameters(parameters, insert);
        parameters
            .AddValue("@noPlaceLeft", DropInInsertResultType.NoPlaceLeft)
            .AddValue("@alreadyBooked", DropInInsertResultType.AlreadyBooked);
    }

    private static SessionProjection MapSession(SqlDataReader reader) => new(
        reader.Get<int>("AppointmentId"),
        reader.Get<int>("ServiceId"),
        reader.Get<int>("WorkerId"),
        reader.Get<DateTime>("StartsAt"),
        reader.Get<int>("MaxCapacity"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetEnum<AppointmentStatus>("Status"));

    /// <summary>
    /// One booking reduced to what the contract keeps: which booking it is, who holds it, the modality, the client's
    /// name and whether it occupies a place, so the same shape serves the occupancy count, the owner roster and the
    /// cancellation read. The name is composed from the <c>LEFT JOIN</c>ed client and stays empty for a row that could
    /// not be resolved: the booking is still counted, it is the display name that is missing.
    /// </summary>
    private static DatedBooking MapBooking(SqlDataReader reader) => new(
        reader.Get<int>("AppointmentId"),
        new ClassSessionBooking(
            reader.Get<int>("ClientId"),
            reader.GetEnum<BookingStatus>("Status"),
            reader.Get<int>("BookingId"),
            reader.GetNullableEnum<BookingModality>("Modality"),
            ReadClientName(reader)));

    private static string ReadClientName(SqlDataReader reader) =>
        $"{reader.GetNullableString("FirstName")} {reader.GetNullableString("LastName")}".Trim();

    private static DropInCancellationState MapDropInCancellationState(SqlDataReader reader) => new(
        reader.Get<int>("BookingId"),
        reader.Get<int>("AppointmentId"),
        reader.Get<DateTime>("StartsAt"),
        reader.GetEnum<AppointmentKind>("Kind"),
        reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
        reader.GetEnum<BookingStatus>("BookingStatus"),
        reader.GetNullableEnum<BookingModality>("Modality"));

    private static DropInClient MapDropInClient(SqlDataReader reader) => new(
        reader.Get<int>("ClientId"),
        reader.Get<string>("Email"),
        reader.Get<string>("FirstName"),
        reader.Get<string>("LastName"));

    private sealed record SessionProjection(
        int AppointmentId,
        int ServiceId,
        int WorkerId,
        DateTime StartsAt,
        int MaxCapacity,
        AppointmentKind Kind,
        AppointmentStatus Status);

    private sealed record DatedBooking(int AppointmentId, ClassSessionBooking Booking);
}
