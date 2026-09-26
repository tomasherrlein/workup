using System.Globalization;
using System.Net;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>
/// Atomically moves an existing individual reservation, reusing one transaction, availability, link and notification
/// core for both entry points. The owner path
/// (<see cref="RescheduleIndividualAsync"/>, <see cref="GetOptionsAsync"/>) works from the owner session and accepts
/// the worker, which must be the reservation's own worker. The client panel path
/// (<see cref="RescheduleOwnIndividualAsync"/>, <see cref="GetOwnOptionsAsync"/>, RF-09) proves ownership with the
/// trusted (businessId, clientId) pair of the client session cookie and never accepts a worker: the lock order, the
/// availability revalidation, the private-link expiry update and the outbox notification are exactly the owner ones,
/// only scoped by both ids. A booking of another ficha or another tenant is indistinguishable from one that does not
/// exist and never reaches availability or the outbox.
/// </summary>
public sealed class OwnerRescheduleService(
    IOwnerRescheduleRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const int AlternativeSearchDays = 30;

    private static readonly RescheduleCodes OwnerCodes = new(
        NotFound: "owner_booking_not_found",
        Unavailable: "owner_booking_unavailable",
        InvalidTime: "owner_reschedule_invalid_time",
        SameSlot: "owner_reschedule_same_slot",
        SlotUnavailable: "owner_reschedule_slot_unavailable");

    private static readonly RescheduleCodes ClientCodes = new(
        NotFound: "client_booking_not_found",
        Unavailable: "client_booking_unavailable",
        InvalidTime: "client_reschedule_invalid_time",
        SameSlot: "client_reschedule_same_slot",
        SlotUnavailable: "client_reschedule_slot_unavailable");

    /// <summary>Owner-initiated reschedule of her own business's individual reservation (RF-09).</summary>
    public Task<OwnerRescheduleAttempt> RescheduleIndividualAsync(
        int businessId,
        int bookingId,
        OwnerRescheduleRequest request,
        CancellationToken ct = default) =>
        RescheduleAsync(businessId, clientId: null, bookingId, request.WorkerId, request.NewStartsAt, OwnerCodes, ct);

    /// <summary>
    /// Client-initiated reschedule of the authenticated ficha's own future individual turn (T21.4, RF-09), with the
    /// same worker as the reservation. <paramref name="clientId"/> comes straight from the client session cookie, so
    /// a foreign ficha or tenant is reported as not found without disclosing the row.
    /// </summary>
    public Task<OwnerRescheduleAttempt> RescheduleOwnIndividualAsync(
        int businessId,
        int clientId,
        int bookingId,
        DateTime newStartsAt,
        CancellationToken ct = default) =>
        RescheduleAsync(businessId, clientId, bookingId, requestedWorkerId: null, newStartsAt, ClientCodes, ct);

    private async Task<OwnerRescheduleAttempt> RescheduleAsync(
        int businessId,
        int? clientId,
        int bookingId,
        int? requestedWorkerId,
        DateTime newStartsAt,
        RescheduleCodes codes,
        CancellationToken ct)
    {
        if (businessId <= 0 || bookingId <= 0 || requestedWorkerId is <= 0 || clientId is <= 0)
            return NotFound(codes);
        if (!IsQuarterHour(newStartsAt))
            return Failed(codes.InvalidTime, "Elegí un horario de la grilla de 15 minutos.");

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // The initial read chooses the lock row and is scoped by every identity the caller proved: the tenant, and
        // for the client path also the ficha. A booking that does not match stops here, before any lock or
        // availability read. Always acquire the worker lock before the booking lock, matching public reservations and
        // preventing a lock-order cycle with competing booking attempts.
        var workerId = await repository.GetWorkerIdAsync(businessId, bookingId, clientId, ct);
        if (workerId is null)
            return NotFound(codes);
        if (!await repository.LockWorkerAsync(businessId, workerId.Value, ct))
            return Unavailable(codes);

        var booking = await repository.LockOwnerBookingAsync(businessId, bookingId, clientId, ct);
        if (booking is null || booking.BusinessId != businessId || booking.BookingId != bookingId)
            return NotFound(codes);
        if (booking.WorkerId != workerId.Value ||
            (requestedWorkerId is not null && booking.WorkerId != requestedWorkerId.Value) ||
            !CanReschedule(booking))
            return Unavailable(codes);
        if (newStartsAt == booking.StartsAt)
            return Failed(codes.SameSlot, "Elegí un horario distinto al actual.");

        var stateCheckTime = clock.Now;
        if (booking.StartsAt <= stateCheckTime || newStartsAt <= stateCheckTime || booking.ServiceDurationMinutes <= 0)
            return Unavailable(codes);

        DateTime newEndsAt;
        DateTime newLinkExpiry;
        try
        {
            newEndsAt = newStartsAt.AddMinutes(booking.ServiceDurationMinutes);
            newLinkExpiry = newStartsAt.AddHours(24);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Failed(codes.InvalidTime, "Elegí otro horario disponible.");
        }

        if (booking.EndsAt != booking.StartsAt.AddMinutes(booking.ServiceDurationMinutes))
            return Unavailable(codes);

        // This snapshot is deliberately read only after both locks. Exclude exactly the original appointment so its
        // current reservation does not conflict with its replacement; every other scheduled appointment still does.
        var snapshot = await repository.GetAvailabilitySnapshotAsync(
            businessId, booking.WorkerId, DateOnly.FromDateTime(newStartsAt), ct);
        var service = ServiceFor(booking);
        var otherAppointments = snapshot.Appointments.Where(appointment => appointment.Id != booking.AppointmentId).ToArray();
        var availableSlots = AvailabilityCalculator.GetAvailableSlots(
            booking.WorkerId,
            DateOnly.FromDateTime(newStartsAt),
            service,
            snapshot.Schedules,
            snapshot.Breaks,
            otherAppointments,
            snapshot.Bookings);
        if (!availableSlots.Contains(newStartsAt))
            return Failed(codes.SlotUnavailable, "Ese horario no está disponible. Elegí otro.");

        var now = clock.Now;
        if (booking.StartsAt <= now || newStartsAt <= now)
            return Unavailable(codes);

        var currentLinks = await repository.GetCurrentLinksAsync(businessId, bookingId, now, clientId, ct);
        if (currentLinks.Count > 1)
            return Unavailable(codes);

        now = clock.Now;
        if (booking.StartsAt <= now || newStartsAt <= now ||
            (currentLinks.Count == 1 && currentLinks[0].ExpiresAt <= now))
            return Unavailable(codes);

        if (!await repository.TryApplyRescheduleAsync(businessId, booking, newStartsAt, newEndsAt, now, clientId, ct))
            return Unavailable(codes);

        if (currentLinks.Count == 1)
        {
            var currentLink = currentLinks[0];
            if (!await repository.UpdateCurrentLinkExpiryAsync(
                    businessId, bookingId, currentLink.LinkId, currentLink.ExpiresAt, newLinkExpiry, now, clientId, ct))
                return Unavailable(codes);
        }

        await repository.InsertNotificationAsync(new Notification
        {
            BusinessId = businessId,
            Type = NotificationType.Rescheduled,
            Recipient = booking.ClientEmail,
            Subject = "Tu turno fue reprogramado",
            BodyHtml = BuildEmailBody(booking, newStartsAt),
            DedupKey = $"rescheduled:booking:{bookingId}:{Guid.NewGuid():N}",
            CreatedAt = now
        }, ct);

        await transaction.CommitAsync(ct);
        return new OwnerRescheduleAttempt(Result<OwnerRescheduleConfirmation>.Success(
            new OwnerRescheduleConfirmation(bookingId, booking.AppointmentId, newStartsAt, newEndsAt)));
    }

    /// <summary>
    /// Unlocked preview of the same worker's free starts on <paramref name="date"/> (RF-09 step 3). The original
    /// appointment does not block its own move and its current start is not offered. When the day has no option,
    /// the nearest free start of the same worker in the following 30 days is suggested. Saving revalidates under lock.
    /// </summary>
    public Task<OwnerRescheduleOptions?> GetOptionsAsync(
        int businessId, int bookingId, DateOnly date, CancellationToken ct = default) =>
        GetOptionsCoreAsync(businessId, clientId: null, bookingId, date, ct);

    /// <summary>
    /// Unlocked preview for the authenticated ficha's own turn (T21.4). Scoped by both ids; a foreign ficha or
    /// tenant yields <c>null</c>, exactly like a row that does not exist.
    /// </summary>
    public Task<OwnerRescheduleOptions?> GetOwnOptionsAsync(
        int businessId, int clientId, int bookingId, DateOnly date, CancellationToken ct = default) =>
        GetOptionsCoreAsync(businessId, clientId, bookingId, date, ct);

    private async Task<OwnerRescheduleOptions?> GetOptionsCoreAsync(
        int businessId, int? clientId, int bookingId, DateOnly date, CancellationToken ct)
    {
        if (businessId <= 0 || bookingId <= 0 || clientId is <= 0 ||
            date.DayNumber >= DateOnly.MaxValue.DayNumber - AlternativeSearchDays)
            return null;

        var booking = await repository.GetOwnerBookingAsync(businessId, bookingId, clientId, ct);
        var now = clock.Now;
        if (booking is null || booking.BusinessId != businessId || booking.BookingId != bookingId ||
            !CanReschedule(booking) || booking.StartsAt <= now || booking.ServiceDurationMinutes <= 0)
            return null;

        var today = DateOnly.FromDateTime(now);
        var searchStart = date < today ? today : date;
        var searchEnd = searchStart.AddDays(AlternativeSearchDays);
        var fetched = await repository.GetAvailabilitySnapshotAsync(businessId, booking.WorkerId, searchStart, searchEnd, ct);
        var snapshot = fetched with
        {
            Appointments = fetched.Appointments.Where(appointment => appointment.Id != booking.AppointmentId).ToArray()
        };
        var service = ServiceFor(booking);
        bool IsOffered(DateTime slot) => slot > now && slot != booking.StartsAt;

        IReadOnlyList<TimeOnly> startTimes = date < today
            ? []
            : AvailabilityCalculator.GetAvailableSlots(
                    booking.WorkerId, date, service, snapshot.Schedules, snapshot.Breaks, snapshot.Appointments, snapshot.Bookings)
                .Where(IsOffered)
                .Select(TimeOnly.FromDateTime)
                .ToArray();
        var nearest = startTimes.Count == 0
            ? NearestSlotSearch.Find(
                booking.WorkerId, searchStart, searchEnd, searchStart.ToDateTime(new TimeOnly(12, 0)), service, snapshot, IsOffered)
            : null;

        return new OwnerRescheduleOptions(date, booking.StartsAt, startTimes, nearest);
    }

    private static Service ServiceFor(OwnerRescheduleBooking booking) => new()
    {
        BusinessId = booking.BusinessId,
        Id = 0,
        Name = booking.ServiceName,
        DurationMinutes = booking.ServiceDurationMinutes,
        MaxCapacity = 1,
        IsActive = true
    };

    private static bool CanReschedule(OwnerRescheduleBooking booking) =>
        booking.AppointmentKind == AppointmentKind.Individual &&
        booking.Origin == AppointmentOrigin.Reserved &&
        booking.MaxCapacity == 1 &&
        booking.AppointmentStatus == AppointmentStatus.Scheduled &&
        booking.Modality is null &&
        booking.EnrollmentId is null &&
        booking.CancelledAt is null &&
        booking.CancelledBy is null &&
        booking.AttendanceRecordedAt is null &&
        booking.AbsenceSource is null &&
        ((booking.BookingStatus == BookingStatus.Pending && booking.ConfirmedAt is null) ||
         (booking.BookingStatus == BookingStatus.Confirmed && booking.ConfirmedAt is not null));

    private static bool IsQuarterHour(DateTime value) =>
        value.Ticks % TimeSpan.TicksPerMinute == 0 && value.Minute % 15 == 0;

    private static string BuildEmailBody(OwnerRescheduleBooking booking, DateTime newStartsAt)
    {
        var firstName = WebUtility.HtmlEncode(booking.ClientFirstName);
        var businessName = WebUtility.HtmlEncode(booking.BusinessName);
        var serviceName = WebUtility.HtmlEncode(booking.ServiceName);
        var workerName = WebUtility.HtmlEncode(booking.WorkerName);
        var when = WebUtility.HtmlEncode(newStartsAt.ToString(
            "dddd d 'de' MMMM 'a las' HH:mm", CultureInfo.GetCultureInfo("es-AR")));
        return $"<p>Hola {firstName},</p><p>Tu turno fue reprogramado.</p>" +
               $"<p>{serviceName} con {workerName}, {when}.</p><p>{businessName}</p>";
    }

    private static OwnerRescheduleAttempt NotFound(RescheduleCodes codes) =>
        Failed(codes.NotFound, "No encontramos ese turno.");

    private static OwnerRescheduleAttempt Unavailable(RescheduleCodes codes) =>
        Failed(codes.Unavailable, "No se puede reprogramar este turno.");

    private static OwnerRescheduleAttempt Failed(string code, string message) =>
        new(Result<OwnerRescheduleConfirmation>.Failure(code, message));

    /// <summary>Stable error codes per entry point; the owner path keeps its original codes byte for byte.</summary>
    private sealed record RescheduleCodes(
        string NotFound,
        string Unavailable,
        string InvalidTime,
        string SameSlot,
        string SlotUnavailable);
}
