using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Rules;

/// <summary>
/// Pure availability rules for individual reservations (RF-07, Regla 1, D-25). Slots start on wall-clock
/// quarter-hour boundaries, occupy the service's full duration, and are limited to the requested worker and date.
/// </summary>
public static class AvailabilityCalculator
{
    private const int SlotIntervalMinutes = 15;
    private static readonly long SlotIntervalTicks = TimeSpan.FromMinutes(SlotIntervalMinutes).Ticks;

    /// <summary>Returns the free start times for one worker on one date, in ascending order.</summary>
    public static IReadOnlyList<DateTime> GetAvailableSlots(
        int workerId,
        DateOnly date,
        Service service,
        IEnumerable<WorkerSchedule> schedules,
        IEnumerable<WorkerBreak> breaks,
        IEnumerable<Appointment> appointments,
        IEnumerable<Booking> bookings)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(breaks);
        ArgumentNullException.ThrowIfNull(appointments);
        ArgumentNullException.ThrowIfNull(bookings);

        if (service.DurationMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(service), "Service duration must be positive.");

        var day = date.DayOfWeek;
        var schedule = schedules.FirstOrDefault(s => s.WorkerId == workerId && s.DayOfWeek == day);
        if (schedule is null || schedule.EndTime <= schedule.StartTime)
            return [];

        var workerBreaks = breaks
            .Where(b => b.WorkerId == workerId && b.DayOfWeek == day)
            .ToArray();
        var scheduledAppointments = GetBlockingAppointments(workerId, appointments, bookings).ToArray();
        var durationTicks = TimeSpan.FromMinutes(service.DurationMinutes).Ticks;
        var latestStartTicks = schedule.EndTime.Ticks - durationTicks;
        if (latestStartTicks < schedule.StartTime.Ticks)
            return [];

        var firstStartTicks = RoundUpToSlotBoundary(schedule.StartTime.Ticks);
        var availableSlots = new List<DateTime>();
        for (var startTicks = firstStartTicks; startTicks <= latestStartTicks; startTicks += SlotIntervalTicks)
        {
            var startTime = TimeOnly.FromTimeSpan(TimeSpan.FromTicks(startTicks));
            var endTime = TimeOnly.FromTimeSpan(TimeSpan.FromTicks(startTicks + durationTicks));
            if (!WeeklyScheduleRules.IsAvailable([schedule], workerBreaks, day, startTime, endTime))
                continue;

            var startsAt = date.ToDateTime(startTime, DateTimeKind.Unspecified);
            var endsAt = startsAt.AddTicks(durationTicks);
            if (scheduledAppointments.Any(a => Overlaps(startsAt, endsAt, a.StartsAt, a.EndsAt)))
                continue;

            availableSlots.Add(startsAt);
        }

        return availableSlots;
    }

    /// <summary>
    /// Returns the nearest free slot for the worker within the caller-supplied inclusive date horizon. The caller
    /// owns the horizon policy; equal-distance choices prefer the earlier slot, and no availability returns null.
    /// </summary>
    public static DateTime? FindNearestAvailableSlot(
        int workerId,
        DateTime requestedStart,
        DateOnly searchStartDate,
        DateOnly searchEndDate,
        Service service,
        IEnumerable<WorkerSchedule> schedules,
        IEnumerable<WorkerBreak> breaks,
        IEnumerable<Appointment> appointments,
        IEnumerable<Booking> bookings)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(breaks);
        ArgumentNullException.ThrowIfNull(appointments);
        ArgumentNullException.ThrowIfNull(bookings);
        if (searchEndDate < searchStartDate)
            throw new ArgumentOutOfRangeException(nameof(searchEndDate), "Search end date must not precede its start date.");

        var scheduleSnapshot = schedules.ToArray();
        var breakSnapshot = breaks.ToArray();
        var appointmentSnapshot = appointments.ToArray();
        var bookingSnapshot = bookings.ToArray();
        DateTime? nearest = null;
        var nearestDistance = long.MaxValue;

        for (var date = searchStartDate; date <= searchEndDate;)
        {
            var slots = GetAvailableSlots(
                workerId,
                date,
                service,
                scheduleSnapshot,
                breakSnapshot,
                appointmentSnapshot,
                bookingSnapshot);

            foreach (var slot in slots)
            {
                var distance = slot.Ticks >= requestedStart.Ticks
                    ? slot.Ticks - requestedStart.Ticks
                    : requestedStart.Ticks - slot.Ticks;
                if (distance < nearestDistance ||
                    (distance == nearestDistance && (nearest is null || slot < nearest.Value)))
                {
                    nearest = slot;
                    nearestDistance = distance;
                }
            }

            if (date == searchEndDate)
                break;
            date = date.AddDays(1);
        }

        return nearest;
    }

    private static IEnumerable<Appointment> GetBlockingAppointments(
        int workerId,
        IEnumerable<Appointment> appointments,
        IEnumerable<Booking> bookings)
    {
        var fullyCancelledAppointmentIds = bookings
            .GroupBy(b => b.AppointmentId)
            .Where(appointmentBookings => appointmentBookings.All(b => b.Status == BookingStatus.Cancelled))
            .Select(appointmentBookings => appointmentBookings.Key)
            .ToHashSet();

        return appointments.Where(appointment =>
            appointment.WorkerId == workerId &&
            appointment.Status == AppointmentStatus.Scheduled &&
            (appointment.Kind == AppointmentKind.ClassSession ||
             (appointment.Kind == AppointmentKind.Individual &&
              !fullyCancelledAppointmentIds.Contains(appointment.Id))));
    }

    private static long RoundUpToSlotBoundary(long ticks)
    {
        var remainder = ticks % SlotIntervalTicks;
        return remainder == 0 ? ticks : ticks + SlotIntervalTicks - remainder;
    }

    private static bool Overlaps(DateTime firstStart, DateTime firstEnd, DateTime secondStart, DateTime secondEnd) =>
        firstStart < secondEnd && secondStart < firstEnd;
}
