using System.Globalization;
using System.Net;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Atomically moves an existing individual reservation for its owner.</summary>
public sealed class OwnerRescheduleService(
    IOwnerRescheduleRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const int AlternativeSearchDays = 30;

    public async Task<OwnerRescheduleAttempt> RescheduleIndividualAsync(
        int businessId,
        int bookingId,
        OwnerRescheduleRequest request,
        CancellationToken ct = default)
    {
        if (businessId <= 0 || bookingId <= 0 || request.WorkerId <= 0)
            return NotFound();
        if (!IsQuarterHour(request.NewStartsAt))
            return Failed("owner_reschedule_invalid_time", "Elegí un horario de la grilla de 15 minutos.");

        await using var transaction = await unitOfWork.BeginAsync(ct: ct);

        // The initial tenant-scoped read chooses the lock row. Always acquire the worker lock before the booking
        // lock, matching public reservations and preventing a lock-order cycle with competing booking attempts.
        var workerId = await repository.GetWorkerIdAsync(businessId, bookingId, ct);
        if (workerId is null)
            return NotFound();
        if (!await repository.LockWorkerAsync(businessId, workerId.Value, ct))
            return Unavailable();

        var booking = await repository.LockOwnerBookingAsync(businessId, bookingId, ct);
        if (booking is null || booking.BusinessId != businessId || booking.BookingId != bookingId)
            return NotFound();
        if (booking.WorkerId != workerId.Value || booking.WorkerId != request.WorkerId || !CanReschedule(booking))
            return Unavailable();
        if (request.NewStartsAt == booking.StartsAt)
            return Failed("owner_reschedule_same_slot", "Elegí un horario distinto al actual.");

        var stateCheckTime = clock.Now;
        if (booking.StartsAt <= stateCheckTime || request.NewStartsAt <= stateCheckTime || booking.ServiceDurationMinutes <= 0)
            return Unavailable();

        DateTime newEndsAt;
        DateTime newLinkExpiry;
        try
        {
            newEndsAt = request.NewStartsAt.AddMinutes(booking.ServiceDurationMinutes);
            newLinkExpiry = request.NewStartsAt.AddHours(24);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Failed("owner_reschedule_invalid_time", "Elegí otro horario disponible.");
        }

        if (booking.EndsAt != booking.StartsAt.AddMinutes(booking.ServiceDurationMinutes))
            return Unavailable();

        // This snapshot is deliberately read only after both locks. Exclude exactly the original appointment so its
        // current reservation does not conflict with its replacement; every other scheduled appointment still does.
        var snapshot = await repository.GetAvailabilitySnapshotAsync(
            businessId, booking.WorkerId, DateOnly.FromDateTime(request.NewStartsAt), ct);
        var service = ServiceFor(booking);
        var otherAppointments = snapshot.Appointments.Where(appointment => appointment.Id != booking.AppointmentId).ToArray();
        var availableSlots = AvailabilityCalculator.GetAvailableSlots(
            booking.WorkerId,
            DateOnly.FromDateTime(request.NewStartsAt),
            service,
            snapshot.Schedules,
            snapshot.Breaks,
            otherAppointments,
            snapshot.Bookings);
        if (!availableSlots.Contains(request.NewStartsAt))
            return Failed("owner_reschedule_slot_unavailable", "Ese horario no está disponible. Elegí otro.");

        var now = clock.Now;
        if (booking.StartsAt <= now || request.NewStartsAt <= now)
            return Unavailable();

        var currentLinks = await repository.GetCurrentLinksAsync(businessId, bookingId, now, ct);
        if (currentLinks.Count > 1)
            return Unavailable();

        now = clock.Now;
        if (booking.StartsAt <= now || request.NewStartsAt <= now ||
            (currentLinks.Count == 1 && currentLinks[0].ExpiresAt <= now))
            return Unavailable();

        if (!await repository.TryApplyRescheduleAsync(businessId, booking, request.NewStartsAt, newEndsAt, now, ct))
            return Unavailable();

        if (currentLinks.Count == 1)
        {
            var currentLink = currentLinks[0];
            if (!await repository.UpdateCurrentLinkExpiryAsync(
                    businessId, bookingId, currentLink.LinkId, currentLink.ExpiresAt, newLinkExpiry, now, ct))
                return Unavailable();
        }

        await repository.InsertNotificationAsync(new Notification
        {
            BusinessId = businessId,
            Type = NotificationType.Rescheduled,
            Recipient = booking.ClientEmail,
            Subject = "Tu turno fue reprogramado",
            BodyHtml = BuildEmailBody(booking, request.NewStartsAt),
            DedupKey = $"rescheduled:booking:{bookingId}:{Guid.NewGuid():N}",
            CreatedAt = now
        }, ct);

        await transaction.CommitAsync(ct);
        return new OwnerRescheduleAttempt(Result<OwnerRescheduleConfirmation>.Success(
            new OwnerRescheduleConfirmation(bookingId, booking.AppointmentId, request.NewStartsAt, newEndsAt)));
    }

    /// <summary>
    /// Unlocked preview of the same worker's free starts on <paramref name="date"/> (RF-09 step 3). The original
    /// appointment does not block its own move and its current start is not offered. When the day has no option,
    /// the nearest free start of the same worker in the following 30 days is suggested. Saving revalidates under lock.
    /// </summary>
    public async Task<OwnerRescheduleOptions?> GetOptionsAsync(
        int businessId, int bookingId, DateOnly date, CancellationToken ct = default)
    {
        if (businessId <= 0 || bookingId <= 0 || date.DayNumber >= DateOnly.MaxValue.DayNumber - AlternativeSearchDays)
            return null;

        var booking = await repository.GetOwnerBookingAsync(businessId, bookingId, ct);
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

    private static OwnerRescheduleAttempt NotFound() =>
        Failed("owner_booking_not_found", "No encontramos ese turno.");

    private static OwnerRescheduleAttempt Unavailable() =>
        Failed("owner_booking_unavailable", "No se puede reprogramar este turno.");

    private static OwnerRescheduleAttempt Failed(string code, string message) =>
        new(Result<OwnerRescheduleConfirmation>.Failure(code, message));
}
