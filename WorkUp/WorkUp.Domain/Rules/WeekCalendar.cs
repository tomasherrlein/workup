namespace WorkUp.Domain.Rules;

/// <summary>
/// Week boundaries used to generate the classes of a week (RF-06). A WorkUp week always runs Monday to Sunday,
/// whatever date is picked. A week is only usable when all seven of its days exist in the calendar: the last
/// representable dates (from 9999-12-27 on) start a partial week whose Saturday and Sunday would overflow, so any
/// boundary that needs them fails closed instead of projecting them onto a duplicate date.
/// </summary>
public static class WeekCalendar
{
    /// <summary>Monday of the week that contains <paramref name="date"/>.</summary>
    public static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-DaysSinceMonday(date.DayOfWeek));

    /// <summary>
    /// Tries to resolve the complete Monday-to-Sunday week that contains <paramref name="date"/>. Returns
    /// <c>false</c> for the incomplete last week of the calendar, whose Saturday and Sunday are not representable and
    /// would otherwise be clamped onto <see cref="DateOnly.MaxValue"/>, generating duplicate sessions.
    /// </summary>
    public static bool TryGetWeek(DateOnly date, out DateOnly weekStart, out DateOnly weekEnd)
    {
        weekStart = StartOfWeek(date);
        if (!IsCompleteWeek(weekStart))
        {
            weekEnd = default;
            return false;
        }

        weekEnd = weekStart.AddDays(6);
        return true;
    }

    /// <summary>
    /// Sunday of the week that contains <paramref name="date"/>. Throws when the week is incomplete at the end of the
    /// calendar, because no real Sunday exists there; callers must use <see cref="TryGetWeek"/> to reject that week.
    /// </summary>
    public static DateOnly EndOfWeek(DateOnly date) =>
        TryGetWeek(date, out _, out var weekEnd)
            ? weekEnd
            : throw new ArgumentOutOfRangeException(
                nameof(date), date, "The week that contains the date is not fully representable.");

    /// <summary>
    /// Monday of the week after the one that contains <paramref name="from"/>, so the default preview never
    /// regenerates the week already in progress. Returns <c>null</c> when that week is not fully representable.
    /// </summary>
    public static DateOnly? NextMonday(DateOnly from)
    {
        var weekStart = StartOfWeek(from);
        if (weekStart.DayNumber > DateOnly.MaxValue.DayNumber - 7)
            return null;

        var nextMonday = weekStart.AddDays(7);
        return IsCompleteWeek(nextMonday) ? nextMonday : null;
    }

    /// <summary>
    /// Date of <paramref name="dayOfWeek"/> inside the week that starts on <paramref name="weekStart"/>, which must be
    /// a Monday as returned by <see cref="StartOfWeek"/>. Throws for an incomplete week instead of clamping a day onto
    /// another one.
    /// </summary>
    public static DateOnly DateInWeek(DateOnly weekStart, DayOfWeek dayOfWeek)
    {
        if (!IsCompleteWeek(weekStart))
            throw new ArgumentOutOfRangeException(
                nameof(weekStart), weekStart, "The week is not fully representable.");

        return weekStart.AddDays(DaysSinceMonday(dayOfWeek));
    }

    /// <summary>A week is complete when its Sunday still exists in the calendar.</summary>
    private static bool IsCompleteWeek(DateOnly weekStart) =>
        weekStart.DayNumber <= DateOnly.MaxValue.DayNumber - 6;

    private static int DaysSinceMonday(DayOfWeek dayOfWeek) => ((int)dayOfWeek + 6) % 7;
}
