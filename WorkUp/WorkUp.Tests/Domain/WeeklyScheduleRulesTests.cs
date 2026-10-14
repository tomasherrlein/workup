using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// RF-03, Regla 1: a worker's weekly schedule is at most one working range per day plus zero or more breaks inside
// it. Reused as-is by the grid (T04) and availability (T08) through IsAvailable.
public class WeeklyScheduleRulesTests
{
    private static WorkerSchedule Schedule(DayOfWeek day, int startHour, int endHour) =>
        new() { DayOfWeek = day, StartTime = new TimeOnly(startHour, 0), EndTime = new TimeOnly(endHour, 0) };

    private static WorkerBreak Break(DayOfWeek day, int startHour, int startMinute, int endHour, int endMinute) =>
        new() { DayOfWeek = day, StartTime = new TimeOnly(startHour, startMinute), EndTime = new TimeOnly(endHour, endMinute) };

    [Fact]
    public void A_schedule_with_no_working_days_and_no_breaks_has_no_problems()
    {
        var problems = WeeklyScheduleRules.Validate([], []);

        Assert.Empty(problems);
    }

    [Fact]
    public void A_single_valid_working_day_with_no_breaks_has_no_problems()
    {
        var problems = WeeklyScheduleRules.Validate([Schedule(DayOfWeek.Monday, 9, 20)], []);

        Assert.Empty(problems);
    }

    [Fact]
    public void A_break_fully_inside_the_days_range_has_no_problems()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Monday, 13, 0, 16, 0)]);

        Assert.Empty(problems);
    }

    [Fact]
    public void Two_non_overlapping_breaks_on_the_same_day_have_no_problems()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 8, 20)],
            [Break(DayOfWeek.Monday, 10, 0, 11, 0), Break(DayOfWeek.Monday, 13, 0, 14, 0)]);

        Assert.Empty(problems);
    }

    [Fact]
    public void Reports_a_day_whose_range_ends_before_it_starts()
    {
        var problems = WeeklyScheduleRules.Validate([Schedule(DayOfWeek.Tuesday, 20, 9)], []);

        var problem = Assert.Single(problems);
        Assert.Equal(DayOfWeek.Tuesday, problem.Day);
        Assert.Equal(ScheduleProblemKind.InvalidRange, problem.Kind);
    }

    [Fact]
    public void Reports_a_day_whose_range_starts_and_ends_at_the_same_time()
    {
        var problems = WeeklyScheduleRules.Validate([Schedule(DayOfWeek.Tuesday, 9, 9)], []);

        var problem = Assert.Single(problems);
        Assert.Equal(ScheduleProblemKind.InvalidRange, problem.Kind);
    }

    [Fact]
    public void Reports_a_break_that_ends_before_it_starts()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Monday, 16, 0, 13, 0)]);

        var problem = Assert.Single(problems);
        Assert.Equal(ScheduleProblemKind.InvalidBreakRange, problem.Kind);
    }

    [Fact]
    public void Reports_a_break_that_starts_before_the_days_range()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Monday, 8, 0, 10, 0)]);

        var problem = Assert.Single(problems);
        Assert.Equal(DayOfWeek.Monday, problem.Day);
        Assert.Equal(ScheduleProblemKind.BreakOutsideRange, problem.Kind);
    }

    [Fact]
    public void Reports_a_break_that_ends_after_the_days_range()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Monday, 19, 0, 21, 0)]);

        var problem = Assert.Single(problems);
        Assert.Equal(ScheduleProblemKind.BreakOutsideRange, problem.Kind);
    }

    [Fact]
    public void Reports_a_break_registered_on_a_day_off()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Sunday, 13, 0, 14, 0)]);

        var problem = Assert.Single(problems);
        Assert.Equal(DayOfWeek.Sunday, problem.Day);
        Assert.Equal(ScheduleProblemKind.BreakOnDayOff, problem.Kind);
    }

    [Fact]
    public void Reports_two_breaks_of_the_same_day_that_overlap()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 8, 20)],
            [Break(DayOfWeek.Monday, 10, 0, 13, 0), Break(DayOfWeek.Monday, 12, 0, 14, 0)]);

        Assert.Contains(problems, p => p.Day == DayOfWeek.Monday && p.Kind == ScheduleProblemKind.OverlappingBreaks);
    }

    [Fact]
    public void Does_not_report_two_breaks_that_only_touch_at_the_edge()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 8, 20)],
            [Break(DayOfWeek.Monday, 10, 0, 12, 0), Break(DayOfWeek.Monday, 12, 0, 14, 0)]);

        Assert.Empty(problems);
    }

    [Fact]
    public void Reports_problems_of_different_days_together()
    {
        var problems = WeeklyScheduleRules.Validate(
            [Schedule(DayOfWeek.Monday, 20, 9)],
            [Break(DayOfWeek.Sunday, 13, 0, 14, 0)]);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Day == DayOfWeek.Monday && p.Kind == ScheduleProblemKind.InvalidRange);
        Assert.Contains(problems, p => p.Day == DayOfWeek.Sunday && p.Kind == ScheduleProblemKind.BreakOnDayOff);
    }

    // IsAvailable: the pure helper T04 and T08 reuse to check a candidate [start, end) against the worker's schedule.

    [Fact]
    public void IsAvailable_true_when_fully_inside_the_working_range_and_outside_breaks()
    {
        var available = WeeklyScheduleRules.IsAvailable(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Monday, 13, 0, 16, 0)],
            DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0));

        Assert.True(available);
    }

    [Fact]
    public void IsAvailable_false_when_the_day_has_no_working_range()
    {
        var available = WeeklyScheduleRules.IsAvailable(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [],
            DayOfWeek.Sunday, new TimeOnly(9, 0), new TimeOnly(10, 0));

        Assert.False(available);
    }

    [Fact]
    public void IsAvailable_false_when_it_starts_before_the_working_range()
    {
        var available = WeeklyScheduleRules.IsAvailable(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [],
            DayOfWeek.Monday, new TimeOnly(8, 30), new TimeOnly(9, 30));

        Assert.False(available);
    }

    [Fact]
    public void IsAvailable_false_when_it_ends_after_the_working_range()
    {
        var available = WeeklyScheduleRules.IsAvailable(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [],
            DayOfWeek.Monday, new TimeOnly(19, 30), new TimeOnly(20, 30));

        Assert.False(available);
    }

    [Fact]
    public void IsAvailable_false_when_it_overlaps_a_break()
    {
        var available = WeeklyScheduleRules.IsAvailable(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Monday, 13, 0, 16, 0)],
            DayOfWeek.Monday, new TimeOnly(12, 30), new TimeOnly(13, 30));

        Assert.False(available);
    }

    [Fact]
    public void IsAvailable_true_when_it_ends_exactly_when_a_break_starts()
    {
        var available = WeeklyScheduleRules.IsAvailable(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Monday, 13, 0, 16, 0)],
            DayOfWeek.Monday, new TimeOnly(12, 0), new TimeOnly(13, 0));

        Assert.True(available);
    }

    [Fact]
    public void IsAvailable_true_when_it_starts_exactly_when_a_break_ends()
    {
        var available = WeeklyScheduleRules.IsAvailable(
            [Schedule(DayOfWeek.Monday, 9, 20)],
            [Break(DayOfWeek.Monday, 13, 0, 16, 0)],
            DayOfWeek.Monday, new TimeOnly(16, 0), new TimeOnly(17, 0));

        Assert.True(available);
    }
}
