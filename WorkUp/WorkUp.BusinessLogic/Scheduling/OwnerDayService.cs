using WorkUp.BusinessLogic.Abstractions;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Builds the owner's selected local day from tenant-scoped appointments and their attendees.</summary>
public sealed class OwnerDayService(IOwnerDayRepository repository, IClock clock)
{
    public async Task<OwnerDayResult> GetDayAsync(
        int businessId, DateOnly? date = null, CancellationToken ct = default)
    {
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);
        var selectedDate = date ?? today;
        if (selectedDate == DateOnly.MaxValue)
            selectedDate = today;
        if (businessId <= 0 || selectedDate == DateOnly.MaxValue)
            return OwnerDayResult.Empty(selectedDate);

        var rows = await repository.ListDayAsync(businessId, selectedDate, ct);
        var totalPaid = await repository.GetPaidTotalAsync(businessId, selectedDate, ct);
        var items = rows
            .GroupBy(row => row.AppointmentId)
            .Select(group => BuildItem(group.ToArray()))
            .OrderBy(item => item.StartsAt)
            .ThenBy(item => item.WorkerName, StringComparer.Ordinal)
            .ThenBy(item => item.ServiceName, StringComparer.Ordinal)
            .ThenBy(item => item.AppointmentId)
            .ToArray();

        var upcoming = new List<OwnerDayItem>();
        var nowItems = new List<OwnerDayItem>();
        var finished = new List<OwnerDayItem>();
        var isToday = selectedDate == today;
        var isPast = selectedDate < today;

        foreach (var item in items)
        {
            if (IsFinished(item) || isPast || item.EndsAt <= now)
            {
                finished.Add(item with { Section = OwnerDaySection.Finished });
            }
            else if (isToday && item.StartsAt <= now && now < item.EndsAt)
            {
                nowItems.Add(item with { Section = OwnerDaySection.Now });
            }
            else
            {
                upcoming.Add(item);
            }
        }

        var nextStart = upcoming.Count == 0 ? (DateTime?)null : upcoming.Min(item => item.StartsAt);
        var next = upcoming
            .Where(item => item.StartsAt == nextStart)
            .Select(item => item with { Section = OwnerDaySection.Next })
            .ToArray();
        var later = upcoming
            .Where(item => item.StartsAt != nextStart)
            .Select(item => item with { Section = OwnerDaySection.Later })
            .ToArray();
        var pendingCount = items.Sum(item => item.Attendees.Count(attendee =>
            attendee.PaymentState == OwnerDayPaymentState.Pending));

        return new OwnerDayResult(selectedDate, totalPaid, nowItems, next, later, finished, pendingCount);
    }

    private static OwnerDayItem BuildItem(IReadOnlyList<OwnerDayRow> rows)
    {
        var first = rows[0];
        var attendees = rows
            .Where(row => row.BookingId is not null && row.BookingStatus is not null && row.ClientName is not null)
            .GroupBy(row => row.BookingId!.Value)
            .Select(group => group.First())
            .Select(row => new OwnerDayAttendee(
                row.BookingId!.Value,
                row.ClientName!,
                row.BookingStatus!.Value,
                first.AppointmentStatus == AppointmentStatus.Suspended
                    ? OwnerDayPaymentState.NotApplicable
                    : row.PaymentState,
                row.AgreedPrice))
            .ToArray();

        return new OwnerDayItem(
            first.AppointmentId,
            first.StartsAt,
            first.EndsAt,
            first.WorkerId,
            first.Kind,
            first.AppointmentStatus,
            first.Origin,
            first.ServiceName,
            first.WorkerName,
            attendees,
            OwnerDaySection.Later);
    }

    private static bool IsFinished(OwnerDayItem item) =>
        item.AppointmentStatus == AppointmentStatus.Suspended ||
        (item.Attendees.Count > 0 && item.Attendees.All(attendee => attendee.Status is
            BookingStatus.Attended or BookingStatus.Absent or BookingStatus.Cancelled));
}
