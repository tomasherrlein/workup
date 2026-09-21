using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// D-23 / RF-29: a membership lasts one month from its activation; the expiry date is the first day without coverage.
public class MembershipCalendarTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Fact]
    public void First_period_expires_on_the_same_day_of_the_next_month()
    {
        var period = MembershipCalendar.FirstPeriod(paidOn: D(2026, 3, 15));

        Assert.Equal(D(2026, 3, 15), period.CoverageStart);
        Assert.Equal(D(2026, 4, 15), period.ExpiresOn);
        Assert.Equal(15, period.ReferenceDay);
    }

    [Fact]
    public void First_period_expires_on_the_last_day_of_the_month_when_the_day_does_not_exist()
    {
        var period = MembershipCalendar.FirstPeriod(paidOn: D(2026, 1, 31));

        Assert.Equal(D(2026, 2, 28), period.ExpiresOn);
        Assert.Equal(31, period.ReferenceDay);
    }

    [Fact]
    public void Early_renewal_starts_when_the_previous_period_expires_and_keeps_the_reference_day()
    {
        var renewal = MembershipCalendar.Renewal(previousExpiresOn: D(2026, 2, 28), referenceDay: 31, paidOn: D(2026, 2, 20));

        Assert.Equal(D(2026, 2, 28), renewal.CoverageStart);
        Assert.Equal(D(2026, 3, 31), renewal.ExpiresOn);
        Assert.Equal(31, renewal.ReferenceDay);
    }

    [Fact]
    public void Renewal_paid_on_the_expiry_day_counts_as_without_interruption()
    {
        var renewal = MembershipCalendar.Renewal(previousExpiresOn: D(2026, 2, 28), referenceDay: 31, paidOn: D(2026, 2, 28));

        Assert.Equal(D(2026, 2, 28), renewal.CoverageStart);
        Assert.Equal(D(2026, 3, 31), renewal.ExpiresOn);
        Assert.Equal(31, renewal.ReferenceDay);
    }

    [Fact]
    public void Late_renewal_starts_on_the_payment_day_without_retroactive_coverage_and_resets_the_reference_day()
    {
        var renewal = MembershipCalendar.Renewal(previousExpiresOn: D(2026, 3, 15), referenceDay: 15, paidOn: D(2026, 3, 20));

        Assert.Equal(D(2026, 3, 20), renewal.CoverageStart);
        Assert.Equal(D(2026, 4, 20), renewal.ExpiresOn);
        Assert.Equal(20, renewal.ReferenceDay);
    }

    [Fact]
    public void State_is_current_inside_coverage_and_expired_from_the_expiry_date()
    {
        var periods = new[]
        {
            new MembershipPeriod { CoverageStart = D(2026, 3, 15), ExpiresOn = D(2026, 4, 15) }
        };

        Assert.Equal(MembershipState.Current, MembershipCalendar.StateOn(periods, D(2026, 3, 15)));
        Assert.Equal(MembershipState.Current, MembershipCalendar.StateOn(periods, D(2026, 4, 14)));
        Assert.Equal(MembershipState.Expired, MembershipCalendar.StateOn(periods, D(2026, 4, 15)));
    }

    [Fact]
    public void State_is_none_when_no_period_was_ever_paid()
    {
        Assert.Equal(MembershipState.None, MembershipCalendar.StateOn([], D(2026, 3, 15)));
    }
}
