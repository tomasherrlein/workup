using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// RF-06 / T13: the week to generate always runs Monday to Sunday, whatever date the owner picks, and the rule stays
// safe at DateOnly.MinValue and DateOnly.MaxValue: the incomplete last week (from 9999-12-27 on) is rejected instead
// of clamping its non-existent Saturday and Sunday onto a duplicate date.
public class WeekCalendarTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Theory]
    [InlineData(2026, 3, 9)]  // Monday
    [InlineData(2026, 3, 11)] // Wednesday
    [InlineData(2026, 3, 15)] // Sunday
    public void Start_of_week_is_the_monday_of_that_week(int year, int month, int day)
    {
        Assert.Equal(D(2026, 3, 9), WeekCalendar.StartOfWeek(D(year, month, day)));
    }

    [Fact]
    public void Sunday_belongs_to_the_week_that_started_on_monday()
    {
        Assert.Equal(D(2026, 3, 9), WeekCalendar.StartOfWeek(D(2026, 3, 15)));
        Assert.Equal(D(2026, 3, 15), WeekCalendar.EndOfWeek(D(2026, 3, 15)));
    }

    [Fact]
    public void Week_ends_on_sunday_for_any_day_of_the_week()
    {
        Assert.Equal(D(2026, 3, 15), WeekCalendar.EndOfWeek(D(2026, 3, 9)));
        Assert.Equal(D(2026, 3, 15), WeekCalendar.EndOfWeek(D(2026, 3, 11)));
        Assert.Equal(D(2026, 3, 15), WeekCalendar.EndOfWeek(D(2026, 3, 15)));
    }

    [Fact]
    public void Week_crosses_the_year_boundary()
    {
        // 2026-12-31 is a Thursday.
        Assert.Equal(D(2026, 12, 28), WeekCalendar.StartOfWeek(D(2026, 12, 31)));
        Assert.Equal(D(2027, 1, 3), WeekCalendar.EndOfWeek(D(2026, 12, 31)));
        Assert.Equal(D(2027, 1, 4), WeekCalendar.NextMonday(D(2026, 12, 31)));
    }

    [Fact]
    public void Try_get_week_resolves_a_complete_monday_to_sunday_week()
    {
        Assert.True(WeekCalendar.TryGetWeek(D(2026, 3, 11), out var weekStart, out var weekEnd));
        Assert.Equal(D(2026, 3, 9), weekStart);
        Assert.Equal(D(2026, 3, 15), weekEnd);
    }

    [Fact]
    public void Next_monday_always_skips_to_the_following_week()
    {
        Assert.Equal(D(2026, 3, 16), WeekCalendar.NextMonday(D(2026, 3, 9)));  // Monday
        Assert.Equal(D(2026, 3, 16), WeekCalendar.NextMonday(D(2026, 3, 11))); // midweek
        Assert.Equal(D(2026, 3, 16), WeekCalendar.NextMonday(D(2026, 3, 15))); // Sunday
    }

    [Fact]
    public void Week_is_safe_at_the_minimum_date()
    {
        // 0001-01-01 is a Monday: there is no earlier day to subtract and the first week is complete.
        Assert.Equal(DateOnly.MinValue, WeekCalendar.StartOfWeek(DateOnly.MinValue));
        Assert.Equal(D(1, 1, 7), WeekCalendar.EndOfWeek(DateOnly.MinValue));
        Assert.True(WeekCalendar.TryGetWeek(DateOnly.MinValue, out var weekStart, out var weekEnd));
        Assert.Equal(DateOnly.MinValue, weekStart);
        Assert.Equal(D(1, 1, 7), weekEnd);

        // Every weekday maps to a distinct real date, so the first week can never duplicate a session.
        var dates = Enum.GetValues<DayOfWeek>()
            .Select(day => WeekCalendar.DateInWeek(weekStart, day))
            .ToArray();
        Assert.Equal(7, dates.Distinct().Count());
        Assert.Equal(DateOnly.MinValue, dates.Min());
        Assert.Equal(D(1, 1, 7), dates.Max());
    }

    [Fact]
    public void Start_of_week_still_resolves_the_monday_of_the_last_calendar_week()
    {
        // 9999-12-31 is a Friday and the last representable day: its Monday is real, but the week is incomplete.
        Assert.Equal(D(9999, 12, 27), WeekCalendar.StartOfWeek(DateOnly.MaxValue));
        Assert.False(WeekCalendar.TryGetWeek(DateOnly.MaxValue, out _, out _));
    }

    [Fact]
    public void The_incomplete_last_week_fails_closed_instead_of_synthesizing_dates()
    {
        // A complete week would need Saturday 10000-01-01 and Sunday 10000-01-02, which do not exist. Clamping
        // projects Saturday and Sunday onto Friday 9999-12-31, generating duplicate sessions for that date.
        Assert.Throws<ArgumentOutOfRangeException>(() => WeekCalendar.EndOfWeek(DateOnly.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => WeekCalendar.DateInWeek(D(9999, 12, 27), DayOfWeek.Saturday));
        Assert.Throws<ArgumentOutOfRangeException>(() => WeekCalendar.DateInWeek(D(9999, 12, 27), DayOfWeek.Sunday));
    }

    [Fact]
    public void The_last_complete_week_ends_on_sunday_9999_12_26()
    {
        Assert.True(WeekCalendar.TryGetWeek(D(9999, 12, 26), out var weekStart, out var weekEnd));
        Assert.Equal(D(9999, 12, 20), weekStart);
        Assert.Equal(D(9999, 12, 26), weekEnd);
        Assert.Equal(D(9999, 12, 26), WeekCalendar.EndOfWeek(D(9999, 12, 26)));
        Assert.Equal(D(9999, 12, 25), WeekCalendar.DateInWeek(weekStart, DayOfWeek.Saturday));
        Assert.Equal(D(9999, 12, 26), WeekCalendar.DateInWeek(weekStart, DayOfWeek.Sunday));
    }

    [Fact]
    public void Next_monday_is_rejected_when_the_following_week_is_not_representable()
    {
        // The Monday after the last complete week starts the incomplete last week, and the Monday after 9999-12-31
        // would not even exist (10000-01-02).
        Assert.Null(WeekCalendar.NextMonday(D(9999, 12, 26)));
        Assert.Null(WeekCalendar.NextMonday(D(9999, 12, 27)));
        Assert.Null(WeekCalendar.NextMonday(DateOnly.MaxValue));
    }

    [Fact]
    public void Date_in_week_places_each_weekday_after_the_monday()
    {
        var monday = D(2026, 3, 9);

        Assert.Equal(D(2026, 3, 9), WeekCalendar.DateInWeek(monday, DayOfWeek.Monday));
        Assert.Equal(D(2026, 3, 10), WeekCalendar.DateInWeek(monday, DayOfWeek.Tuesday));
        Assert.Equal(D(2026, 3, 15), WeekCalendar.DateInWeek(monday, DayOfWeek.Sunday));
    }
}
