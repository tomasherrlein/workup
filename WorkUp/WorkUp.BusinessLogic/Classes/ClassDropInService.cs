using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// Booking a drop-in into an already generated class session (RF-07 cupo &gt; 1, RF-22, RF-11, D-16, D-23, D-25).
/// The offer lists the sessions of the business that are <c>Programado</c>, start in the future and still have a
/// free place, where the occupancy counts every booking that is not <c>Cancelado</c>: the fixed annotations keep
/// their places across their three states, so a student pending renewal also blocks a drop-in. Booking revalidates
/// all of that under the repository's row lock, identifies the client by email without requiring a password, writes
/// the drop-in through the guarded insert, and commits as a single unit, so the last place goes to exactly one
/// client.
/// <para>
/// The immediate email is only queued when the class starts in less than 24 hours (D-22, RF-17 step 4): with more
/// notice the scheduled reminder owns that email, and with less notice the immediate email is the reminder itself,
/// sharing its dedup key so neither is sent twice.
/// </para>
/// </summary>
public sealed class ClassDropInService(
    IClassDropInRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>Anticipation of the scheduled reminder (RF-17): booked with less notice, there is no future reminder.</summary>
    private const int ReminderLeadHours = 24;

    private const int MaxNameLength = 60;
    private const int MaxEmailLength = 254;

    /// <summary>
    /// Read-only offer of the bookable sessions in the inclusive date range, optionally narrowed to one service,
    /// ordered by start. A session that is suspended or already started is never offered (RF-07 step 2, RF-22), and
    /// neither is a full one.
    /// </summary>
    public async Task<Result<IReadOnlyList<ClassSessionOption>>> ListOptionsAsync(
        int businessId, DateOnly from, DateOnly to, int? serviceId, CancellationToken ct = default)
    {
        if (businessId <= 0)
            return Result<IReadOnlyList<ClassSessionOption>>.Failure(
                "drop_in_invalid_request", "No pudimos identificar el negocio.");

        // The single clock read of the listing: every session is judged against the same "now".
        var now = clock.Now;

        var sessions = await repository.ListSessionsAsync(
            businessId, from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MaxValue), serviceId, ct);

        var options = new List<ClassSessionOption>(sessions.Count);
        foreach (var session in sessions)
        {
            if (!ClassCapacity.IsBookable(session.Status, session.StartsAt, now))
                continue;

            var remaining = ClassCapacity.RemainingPlaces(session.MaxCapacity, session.BookingStatuses);
            if (remaining == 0)
                continue;

            options.Add(new ClassSessionOption(
                session.AppointmentId,
                session.ServiceId,
                session.WorkerId,
                session.Date,
                session.StartTime,
                session.MaxCapacity,
                ClassCapacity.PlacesTaken(session.MaxCapacity, session.BookingStatuses),
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
    /// path, except that the client must already belong to the business: an unknown or foreign one is rejected
    /// instead of created.
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

        // The owner path only books its own clients; checking it before the lock keeps the "not found" answer about
        // the client it names, not about the session.
        if (clientId is { } requestedClientId &&
            !await repository.ClientExistsAsync(businessId, requestedClientId, ct))
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_client_not_found", "No encontramos a esa alumna en tu negocio.");

        // The session row is the lock: the repository takes UPDLOCK, returns the live occupancy and null when the
        // session is not this business's, so the count below and the insert cannot be raced.
        var session = await repository.LockSessionAsync(businessId, appointmentId, ct);
        if (session is null)
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_not_found", "No encontramos esa clase.");

        if (!ClassCapacity.IsBookable(session.Status, session.StartsAt, now))
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_not_available", "Esa clase ya no está disponible para anotarse.");

        if (!ClassCapacity.HasPlace(session.MaxCapacity, session.BookingStatuses))
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_full", "La clase está completa. Probá con otro día u horario.");

        var resolvedClientId = clientId;
        if (resolvedClientId is null)
        {
            var client = await repository.IdentifyClientAsync(businessId, email, firstName, lastName, phone, ct);
            resolvedClientId = client.ClientId;
        }

        // The guarded insert is the real protection of the last place: it returns null when it lost the race, and the
        // loser reports a full class instead of overbooking it.
        var bookingId = await repository.TryInsertDropInAsync(
            new DropInInsert(businessId, appointmentId, resolvedClientId.Value, now), ct);
        if (bookingId is null)
            return Result<DropInBookingOutcome>.Failure(
                "drop_in_full", "La clase está completa. Probá con otro día u horario.");

        var immediateEmailQueued = session.StartsAt - now < TimeSpan.FromHours(ReminderLeadHours);
        if (immediateEmailQueued)
        {
            // The dedup key is shared with the scheduled reminder (T24) so this booking receives exactly one email.
            await repository.EnqueueNotificationAsync(new DropInNotification(
                businessId,
                bookingId.Value,
                NotificationType.DropInReminder,
                $"reminder:booking:{bookingId.Value}",
                now), ct);
        }

        await tx.CommitAsync(ct);

        return Result<DropInBookingOutcome>.Success(new DropInBookingOutcome(
            session.AppointmentId,
            bookingId.Value,
            resolvedClientId.Value,
            session.Date,
            session.StartTime,
            immediateEmailQueued));
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
