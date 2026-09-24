using System.Globalization;
using System.Net;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Booking a drop-in into an already generated class session (RF-07 cupo &gt; 1, RF-22, RF-11, D-16, D-23, D-25).
/// The offer lists the sessions of the business that are a <c>ClassSession</c> (never an individual turn), are
/// <c>Programado</c>, start in the future and still have a free place, where the occupancy counts every booking that
/// is not <c>Cancelado</c>: the fixed annotations keep their places across their three states, so a student pending
/// renewal also blocks a drop-in. Booking revalidates all of that under the repository's row lock, identifies the
/// client by email without requiring a password (or reads the client the owner picked), rejects a client that already
/// holds a booking of the session before looking at the occupancy, writes the drop-in through the guarded insert, and
/// commits as a single unit, so the last place goes to exactly one client.
/// <para>
/// The immediate email is only queued when the class starts in less than 24 hours (D-22, RF-17 step 4): with more
/// notice the scheduled reminder owns that email, and with less notice the immediate email is the reminder itself,
/// sharing its dedup key so neither is sent twice. Both paths queue it, because both resolve a stored recipient: the
/// validated email of the public form and the email on file of the client the owner picked. The service composes the
/// whole notice, because the outbox columns are not nullable.
/// </para>
/// </summary>
public sealed class ClassDropInService(
    IClassDropInRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>Anticipation of the scheduled reminder (RF-17): booked with less notice, there is no future reminder.</summary>
    private const int ReminderLeadHours = 24;

    private const int MaxNameLength = 60;
    private const int MaxEmailLength = 254;

    /// <summary>Stable codes of the owner cancellation (RF-22). They are the caller's contract, not messages.</summary>
    private const string DropInNotFoundCode = "drop_in_not_found";

    private const string DropInNotAvailableCode = "drop_in_not_available";

    private const string DropInAlreadyCancelledCode = "drop_in_already_cancelled";

    private const string DropInNotFoundMessage = "No encontramos esa reserva suelta.";

    private const string DropInNotAvailableMessage =
        "La clase ya empezó o está suspendida: esa reserva suelta ya no se puede cancelar.";

    private const string DropInAlreadyCancelledMessage = "Esa reserva suelta ya estaba cancelada.";

    /// <summary>
    /// Read-only offer of the bookable sessions in the inclusive date range, optionally narrowed to one service,
    /// ordered by start. A session that is suspended or already started is never offered (RF-07 step 2, RF-22), and
    /// neither is a full one. An individual turn is never offered either: it is not a class, whatever the repository
    /// returned.
    /// </summary>
    public async Task<Result<IReadOnlyList<ClassSessionOption>>> ListOptionsAsync(
        int businessId, DateOnly from, DateOnly to, int? serviceId, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Result<IReadOnlyList<ClassSessionOption>>.Failure(
                "drop_in_invalid_request", "No pudimos identificar el negocio.");

        // The inclusive end date is expressed to storage as the exclusive instant (toInclusive + 1 day) 00:00, and
        // that instant does not exist after 9999-12-31. Accepting the range would hand an implementation an
        // arithmetic overflow it cannot turn into a correct bound, so the request is rejected here, before any
        // storage call, instead of asking the repository to guess.
        if (to == DateOnly.MaxValue)
            return Result<IReadOnlyList<ClassSessionOption>>.Failure(
                "drop_in_invalid_request", "Elegí un rango de fechas válido.");

        // The single clock read of the listing: every session is judged against the same "now".
        var now = clock.Now;

        var sessions = await repository.ListSessionsAsync(businessId, from, to, serviceId, ct);

        var options = new List<ClassSessionOption>(sessions.Count);
        foreach (var session in sessions)
        {
            if (session.Kind != AppointmentKind.ClassSession)
                continue;

            if (!ClassCapacity.IsBookable(session.Status, session.StartsAt, now))
                continue;

            var statuses = session.Bookings.Select(booking => booking.Status).ToArray();
            var remaining = ClassCapacity.RemainingPlaces(session.MaxCapacity, statuses);
            if (remaining == 0)
                continue;

            options.Add(new ClassSessionOption(
                session.AppointmentId,
                session.ServiceId,
                session.WorkerId,
                session.Date,
                session.StartTime,
                session.MaxCapacity,
                ClassCapacity.PlacesTaken(session.MaxCapacity, statuses),
                remaining));
        }

        return Result<IReadOnlyList<ClassSessionOption>>.Success(
            options
                .OrderBy(option => option.Date)
                .ThenBy(option => option.StartTime)
                .ThenBy(option => option.AppointmentId)
                .ToArray());
    }

    /// <summary>
    /// Read-only roster of the upcoming classes of the business with their drop-in annotations, for the owner screen
    /// (RF-22, RF-07 cupo &gt; 1). It reads the clock exactly once and passes that instant to storage: the "upcoming"
    /// rule and every session judgement derive from the same value. A session that already started is excluded; a
    /// suspended one is not, because suspending a class cancels nobody and the owner still needs to see who is booked
    /// in it. Only annotations with <c>Modality = Suelta</c> are projected: a fixed annotation of the same class is
    /// not a drop-in and is never presented as one. The read opens no transaction and writes nothing.
    /// </summary>
    public async Task<Result<IReadOnlyList<ClassDropInRosterSession>>> ListRosterAsync(
        int businessId, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Result<IReadOnlyList<ClassDropInRosterSession>>.Failure(
                "drop_in_invalid_request", "No pudimos identificar el negocio.");

        // The single clock read of the roster: the query bound and the judgement of every returned session use it.
        var now = clock.Now;

        var sessions = await repository.ListUpcomingRosterAsync(businessId, now, ct);

        var roster = new List<ClassDropInRosterSession>(sessions.Count);
        foreach (var session in sessions)
        {
            // The discriminant is not decorative: an individual turn has capacity 1 and can also be `Programado`, so
            // without this check a repository that forgot the kind filter would list it as a class. The start is
            // re-judged here for the same reason: "upcoming" is a business rule, not only a query bound.
            if (session.Kind != AppointmentKind.ClassSession || session.StartsAt <= now)
                continue;

            // Only a `Suelta` annotation is a drop-in. A fixed annotation of the same class occupies a place, but it
            // is not somebody who booked a single class: presenting it here would show the owner a drop-in that does
            // not exist and would hide the difference between a fixed student and a drop-in student.
            var dropIns = session.Bookings
                .Where(booking => booking.Modality == BookingModality.DropIn)
                .Select(booking => new ClassDropInRosterEntry(
                    booking.BookingId, booking.ClientId, booking.ClientName, booking.Status))
                .ToArray();

            roster.Add(new ClassDropInRosterSession(
                session.AppointmentId,
                session.ServiceId,
                session.WorkerId,
                session.StartsAt,
                session.MaxCapacity,
                session.Status,
                dropIns));
        }

        return Result<IReadOnlyList<ClassDropInRosterSession>>.Success(
            roster
                .OrderBy(session => session.StartsAt)
                .ThenBy(session => session.AppointmentId)
                .ToArray());
    }

    /// <summary>
    /// Books the client of the public form into the chosen session (RF-07). The client is identified by email and
    /// created without a password when unknown. Every rejection reports a stable error code and rolls back the
    /// transaction it opened.
    /// </summary>
    public async Task<Result<DropInBookingOutcome>> BookPublicAsync(
        int businessId, DropInBookingRequest request, CancellationToken ct = default)
    {
        var validation = ValidateRequest(
            businessId, request, out var email, out var firstName, out var lastName, out var phone);
        if (validation is not null)
            return Result<DropInBookingOutcome>.Failure(validation);

        return await BookAsync(
            businessId, request.AppointmentId, clientId: null, email, firstName, lastName, phone, ct);
    }

    /// <summary>
    /// Books a client the owner picked into the chosen session (RF-11). It shares the whole core with the public
    /// path, except that the client must already belong to the business: an unknown or foreign one is rejected instead
    /// of created, and the stored client is what supplies the notice recipient and name.
    /// </summary>
    public async Task<Result<DropInBookingOutcome>> BookForClientAsync(
        int businessId, int appointmentId, int clientId, CancellationToken ct = default)
    {
        if (businessId <= 0 || appointmentId <= 0 || clientId <= 0)
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_invalid_request", "No pudimos registrar la reserva. Volvé a intentar.");

        return await BookAsync(
            businessId, appointmentId, clientId, email: "", firstName: "", lastName: "", phone: null, ct);
    }

    /// <summary>
    /// Cancels a drop-in annotation of the business before its class starts (RF-22). The whole operation is one unit:
    /// the booking is read under the repository's row lock, judged, and cancelled inside the same transaction, which
    /// commits exactly once; every rejection returns before the commit, so it rolls back without writing. The
    /// cancellation marks the annotation <c>Cancelado</c>, stamps <see cref="IClock.Now"/> — the single clock read of
    /// the operation — and records the owner as the author, which is what releases the place: the same client can book
    /// the session again, because a cancelled booking no longer occupies a place and no longer trips
    /// <c>UX_Bookings_ActiveClient</c>.
    /// <para>
    /// A booking of another business and a booking that does not exist are the same answer
    /// (<c>drop_in_not_found</c>), and so is a booking that is not a drop-in of a class; an annotation whose
    /// cancellation already happened answers <c>drop_in_already_cancelled</c> and is never written twice, so the
    /// original instant and author survive a double click.
    /// </para>
    /// </summary>
    public async Task<Result> CancelDropInAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        // A missing identifier is answered like a missing booking: there is no booking of the business to cancel, and
        // the caller must not be able to tell "you did not name it" from "it is not yours".
        if (businessId <= 0 || bookingId <= 0)
            return Result.Failure(DropInNotFoundCode, DropInNotFoundMessage);

        // The single clock read of the cancellation: the availability check and the instant that gets persisted
        // derive from the same value, so a clock that advances mid-call cannot split them.
        var now = clock.Now;

        await using var tx = await unitOfWork.BeginAsync(ct: ct);

        // The booking row is the lock. It carries its session, and a booking of another business is simply not
        // found, so nothing about another tenant's rows leaks through the answer.
        var state = await repository.LockDropInForCancellationAsync(businessId, bookingId, ct);
        if (state is null || state.Kind != AppointmentKind.ClassSession || state.Modality != BookingModality.DropIn)
            return Result.Failure(DropInNotFoundCode, DropInNotFoundMessage);

        // The availability of the class is judged before the state of the annotation, so the caller hears what it can
        // do with this booking (nothing, the class is gone) before the report of what already happened to it.
        if (!ClassCapacity.IsBookable(state.AppointmentStatus, state.StartsAt, now))
            return Result.Failure(DropInNotAvailableCode, DropInNotAvailableMessage);

        if (state.Status == BookingStatus.Cancelled)
            return Result.Failure(DropInAlreadyCancelledCode, DropInAlreadyCancelledMessage);

        // The guarded statement repeats every rule inside one atomic update. A row that stopped matching between the
        // lock and the update — a session suspended in between, for instance — is left untouched and reported as
        // "no row changed", and that is a state that no longer admits the cancellation.
        if (!await repository.CancelDropInAsync(businessId, bookingId, now, ct))
            return Result.Failure(DropInNotAvailableCode, DropInNotAvailableMessage);

        await tx.CommitAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// The core of both booking paths. It reads the clock exactly once: the bookability check, the 24-hour window
    /// and every created timestamp derive from that single value, so a clock that advances mid-call cannot split
    /// them. The transaction owns the commit, and returning before it disposes the transaction and rolls back, so a
    /// rejected booking never leaves a partial write.
    /// </summary>
    private async Task<Result<DropInBookingOutcome>> BookAsync(
        int businessId,
        int appointmentId,
        int? clientId,
        string email,
        string firstName,
        string lastName,
        string? phone,
        CancellationToken ct)
    {
        var now = clock.Now;

        await using var tx = await unitOfWork.BeginAsync(ct: ct);

        // The owner path only books its own clients; resolving the client before the lock keeps the "not found"
        // answer about the client it names, not about the session, and yields the stored contact the notice needs.
        DropInClient? ownerClient = null;
        if (clientId is { } requestedClientId)
        {
            ownerClient = await repository.FindClientAsync(businessId, requestedClientId, ct);
            if (ownerClient is null)
                return Result<DropInBookingOutcome>.Failure(
                    "drop_in_client_not_found", "No encontramos a esa alumna en tu negocio.");
        }

        // The session row is the lock: the repository takes UPDLOCK, returns the live occupancy and null when the
        // session is not this business's, so the count below and the insert cannot be raced.
        var session = await repository.LockSessionAsync(businessId, appointmentId, ct);
        if (session is null)
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_not_found", "No encontramos esa clase.");

        // The discriminant is not decorative: an individual turn has capacity 1 and can also be `Programado`, so
        // without this check a repository that forgot the kind filter would let it be booked as a class.
        if (session.Kind != AppointmentKind.ClassSession)
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_not_found", "No encontramos esa clase.");

        if (!ClassCapacity.IsBookable(session.Status, session.StartsAt, now))
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_not_available", "Esa clase ya no está disponible para anotarse.");

        // The client is resolved before the occupancy is judged, because the client's own booking is decided first,
        // just below. Creating a public client here is safe: every rejection after this point returns before the
        // commit, so the transaction rolls that row back with the rest of the unit.
        var recipient = email;
        var recipientName = firstName;
        var resolvedClientId = clientId;
        if (ownerClient is not null)
        {
            // The assisted path carries no contact form: the recipient and the name are the ones stored on the client.
            resolvedClientId = ownerClient.ClientId;
            recipient = ownerClient.Email;
            recipientName = ownerClient.FirstName;
        }
        else
        {
            var client = await repository.IdentifyClientAsync(businessId, email, firstName, lastName, phone, ct);
            resolvedClientId = client.ClientId;
            // The identified email is the notice recipient: dropping it would leave the outbox without a Recipient.
            recipient = client.Email;
        }

        // UX_Bookings_ActiveClient (AppointmentId, ClientId) WHERE Status <> 5: the same client cannot hold two
        // non-cancelled bookings of the session. It is checked before the occupancy on purpose: a client who already
        // holds a place must hear that she is already booked, even when the session her own booking occupies is the
        // very reason it is full. Checking it here answers with a stable code instead of letting the insert surface a
        // raw unique-key exception; a cancelled booking released its place and can book again.
        if (session.Bookings.Any(booking =>
            booking.ClientId == resolvedClientId.Value && ClassCapacity.OccupiesPlace(booking.Status)))
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_already_booked", "Ya estás anotada en esa clase.");

        var statuses = session.Bookings.Select(booking => booking.Status).ToArray();
        if (!ClassCapacity.HasPlace(session.MaxCapacity, statuses))
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_full", "La clase está completa. Probá con otro día u horario.");

        // The guarded insert is the real protection of the last place: it reports the losing outcome without writing,
        // so the loser rolls back instead of overbooking the class or duplicating the client.
        var insert = await repository.TryInsertDropInAsync(
            new DropInInsert(businessId, appointmentId, resolvedClientId.Value, now), ct);
        switch (insert.Result)
        {
            case DropInInsertResultType.Inserted:
                break;
            case DropInInsertResultType.NoPlaceLeft:
                return Result<DropInBookingOutcome>.Failure(
                    "drop_in_full", "La clase está completa. Probá con otro día u horario.");
            case DropInInsertResultType.AlreadyBooked:
                return Result<DropInBookingOutcome>.Failure(
                    "drop_in_already_booked", "Ya estás anotada en esa clase.");
            default:
                // The enum knows exactly three outcomes, and `DropInInsertResultType` has no zero member: any other
                // value is a broken implementation. Failing loud beats falling through as a successful insert, which
                // would commit a booking that was never written and queue `reminder:booking:0` for it.
                throw new InvalidOperationException(
                    $"El insert de la reserva suelta devolvió un resultado desconocido: {(int)insert.Result}.");
        }

        // Both paths carry a stored recipient: the validated email of the public form or the email on file of the
        // client the owner picked. The immediate email is queued only within the window, because with more notice the
        // scheduled reminder (T24) owns it, and the shared dedup key stops a second send for the same booking.
        var withinReminderWindow = session.StartsAt - now < TimeSpan.FromHours(ReminderLeadHours);
        var immediateEmailQueued = withinReminderWindow;
        if (immediateEmailQueued)
        {
            await repository.EnqueueNotificationAsync(
                ComposeDropInReminder(businessId, recipient, recipientName, session, insert.BookingId, now), ct);
        }

        await tx.CommitAsync(ct);

        return Result<DropInBookingOutcome>.Success(new DropInBookingOutcome(
            session.AppointmentId,
            insert.BookingId,
            resolvedClientId.Value,
            session.Date,
            session.StartTime,
            immediateEmailQueued));
    }

    /// <summary>
    /// Composes the drop-in notice for the outbox. The class and teacher names are only ids in this unit, so the body
    /// carries what is available (the name of the client, from the form or from the client on file, and the session
    /// date and time) in the same shape as the individual booking path, and every value is HTML-encoded.
    /// </summary>
    private static Notification ComposeDropInReminder(
        int businessId, string recipient, string firstName, ClassSessionRow session, int bookingId, DateTime now)
    {
        var safeFirstName = WebUtility.HtmlEncode(firstName);
        var when = WebUtility.HtmlEncode(session.StartsAt.ToString(
            "dddd d 'de' MMMM 'a las' HH:mm", CultureInfo.GetCultureInfo("es-AR")));

        return new Notification
        {
            BusinessId = businessId,
            Type = NotificationType.DropInReminder,
            Recipient = recipient,
            Subject = "Recordatorio de tu clase suelta",
            BodyHtml = $"<p>Hola {safeFirstName},</p>" +
                       $"<p>Tu lugar en la clase suelta del {when} quedó reservado.</p>",
            DedupKey = $"reminder:booking:{bookingId}",
            CreatedAt = now
        };
    }

    /// <summary>
    /// Validates the public form: business, chosen session, contact data and email shape. It reads no clock, so an
    /// invalid request fails before any storage call and before the transaction exists.
    /// </summary>
    private static BusinessError? ValidateRequest(
        int businessId,
        DropInBookingRequest request,
        out string email,
        out string firstName,
        out string lastName,
        out string? phone)
    {
        email = Text.NormalizeEmail(request.Email ?? "");
        firstName = request.FirstName?.Trim() ?? "";
        lastName = request.LastName?.Trim() ?? "";
        phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();

        if (businessId <= 0)
            return new BusinessError("drop_in_invalid_request", "No pudimos identificar el negocio.");

        if (request.AppointmentId <= 0)
            return new BusinessError(
                "drop_in_invalid_request", "Elegí una clase disponible.", nameof(request.AppointmentId));

        if (firstName.Length == 0)
            return new BusinessError("drop_in_invalid_request", "Ingresá tu nombre.", nameof(request.FirstName));
        if (firstName.Length > MaxNameLength)
            return new BusinessError(
                "drop_in_invalid_request", "El nombre no puede superar los 60 caracteres.", nameof(request.FirstName));

        if (lastName.Length == 0)
            return new BusinessError("drop_in_invalid_request", "Ingresá tu apellido.", nameof(request.LastName));
        if (lastName.Length > MaxNameLength)
            return new BusinessError(
                "drop_in_invalid_request", "El apellido no puede superar los 60 caracteres.", nameof(request.LastName));

        if (email.Length == 0)
            return new BusinessError("drop_in_invalid_email", "Ingresá tu email.", nameof(request.Email));
        if (email.Length > MaxEmailLength || !EmailFormat.IsValid(email))
            return new BusinessError("drop_in_invalid_email", "Ingresá un email válido.", nameof(request.Email));

        return null;
    }
}
