using WorkUp.Domain.Entities;

namespace WorkUp.Domain.Rules;

public enum ScheduleProblemKind
{
    /// <summary>A day's working range ends at or before it starts.</summary>
    InvalidRange,

    /// <summary>A break's range ends at or before it starts.</summary>
    InvalidBreakRange,

    /// <summary>A break is not fully contained in that day's working range.</summary>
    BreakOutsideRange,

    /// <summary>Two breaks of the same day overlap each other.</summary>
    OverlappingBreaks,

    /// <summary>A break was registered on a day with no working range (a day off).</summary>
    BreakOnDayOff
}

/// <summary>One rule violation found in a weekly schedule, tied to the day where it happened.</summary>
public sealed record ScheduleProblem(DayOfWeek Day, ScheduleProblemKind Kind);

/// <summary>
/// Pure validation of a worker's weekly schedule (RF-03): at most one working range per day, plus zero or more
/// non-working breaks each day, each inside that day's range and not overlapping another break of the same day. A
/// day with no working range is a day off; a break on a day off is invalid. Reused by the grid (T04) and
/// availability (T08) through <see cref="IsAvailable"/>, which answers whether a time range on a given day is fully
/// inside the working range and outside every break.
/// </summary>
public static class WeeklyScheduleRules
{
    public static IReadOnlyList<ScheduleProblem> Validate(IEnumerable<WorkerSchedule> schedules, IEnumerable<WorkerBreak> breaks)
    {
        var problems = new List<ScheduleProblem>();
        var schedulesByDay = schedules.ToLookup(s => s.DayOfWeek);
        var breaksByDay = breaks.ToLookup(b => b.DayOfWeek);

        foreach (var day in AllDays)
        {
            var schedule = schedulesByDay[day].FirstOrDefault();
            if (schedule is not null && schedule.EndTime <= schedule.StartTime)
                problems.Add(new ScheduleProblem(day, ScheduleProblemKind.InvalidRange));

            var dayBreaks = breaksByDay[day].OrderBy(b => b.StartTime).ToList();
            for (var i = 0; i < dayBreaks.Count; i++)
            {
                var brk = dayBreaks[i];
                if (brk.EndTime <= brk.StartTime)
                {
                    problems.Add(new ScheduleProblem(day, ScheduleProblemKind.InvalidBreakRange));
                    continue;
                }

                if (schedule is null)
                {
                    problems.Add(new ScheduleProblem(day, ScheduleProblemKind.BreakOnDayOff));
                    continue;
                }

                if (schedule.EndTime > schedule.StartTime && (brk.StartTime < schedule.StartTime || brk.EndTime > schedule.EndTime))
                    problems.Add(new ScheduleProblem(day, ScheduleProblemKind.BreakOutsideRange));

                if (i > 0 && brk.StartTime < dayBreaks[i - 1].EndTime)
                    problems.Add(new ScheduleProblem(day, ScheduleProblemKind.OverlappingBreaks));
            }
        }

        return problems;
    }

    /// <summary>Whether [start, end) on the given day is fully inside the working range and outside every break.</summary>
    public static bool IsAvailable(IEnumerable<WorkerSchedule> schedules, IEnumerable<WorkerBreak> breaks, DayOfWeek day, TimeOnly start, TimeOnly end)
    {
        var schedule = schedules.FirstOrDefault(s => s.DayOfWeek == day);
        if (schedule is null || start < schedule.StartTime || end > schedule.EndTime)
            return false;

        return breaks
            .Where(b => b.DayOfWeek == day)
            .All(b => end <= b.StartTime || start >= b.EndTime);
    }

    private static readonly DayOfWeek[] AllDays =
    [
        DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday
    ];
}
