using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Finds the offered free start of one worker closest to a target time within an inclusive date range.</summary>
internal static class NearestSlotSearch
{
    /// <remarks>Ties prefer the earlier start, matching the convention fixed in T08.</remarks>
    public static DateTime? Find(
        int workerId,
        DateOnly startDate,
        DateOnly endDate,
        DateTime target,
        Service service,
        BookingAvailabilitySnapshot snapshot,
        Func<DateTime, bool> isOffered)
    {
        DateTime? nearest = null;
        var nearestDistance = long.MaxValue;
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            foreach (var slot in AvailabilityCalculator.GetAvailableSlots(
                         workerId, date, service, snapshot.Schedules, snapshot.Breaks, snapshot.Appointments, snapshot.Bookings))
            {
                if (!isOffered(slot))
                    continue;

                var distance = Math.Abs(slot.Ticks - target.Ticks);
                if (distance < nearestDistance || (distance == nearestDistance && (nearest is null || slot < nearest.Value)))
                {
                    nearest = slot;
                    nearestDistance = distance;
                }
            }

            if (date == endDate)
                break;
        }

        return nearest;
    }
}
