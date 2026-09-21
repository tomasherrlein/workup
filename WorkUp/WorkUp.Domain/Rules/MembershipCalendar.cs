using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Domain.Rules;

/// <summary>Dates of one membership month. <see cref="ExpiresOn"/> is the first day without coverage.</summary>
public readonly record struct MembershipPeriodDates(DateOnly CoverageStart, DateOnly ExpiresOn, int ReferenceDay);

/// <summary>
/// Monthly membership calendar (D-23, RF-29). A period expires on the reference day of the following month, or on
/// the last day of that month when the reference day does not exist in it.
/// </summary>
public static class MembershipCalendar
{
    /// <summary>First period, activated by the first payment (RF-05). The reference day is the payment day.</summary>
    public static MembershipPeriodDates FirstPeriod(DateOnly paidOn) =>
        new(paidOn, ExpiryAfter(paidOn, paidOn.Day), paidOn.Day);

    /// <summary>
    /// Next period. Paid on or before the previous expiry: continues without interruption from that expiry and
    /// keeps the reference day. Paid later: starts on the payment day, with no retroactive coverage, and the payment
    /// day becomes the new reference day.
    /// </summary>
    public static MembershipPeriodDates Renewal(DateOnly previousExpiresOn, int referenceDay, DateOnly paidOn)
    {
        if (paidOn <= previousExpiresOn)
            return new(previousExpiresOn, ExpiryAfter(previousExpiresOn, referenceDay), referenceDay);

        return FirstPeriod(paidOn);
    }

    /// <summary><c>Vigente</c> when some period covers the date; <c>Vencido</c> when periods exist but none does.</summary>
    public static MembershipState StateOn(IEnumerable<MembershipPeriod> periods, DateOnly date)
    {
        var any = false;
        foreach (var period in periods)
        {
            if (period.Covers(date))
                return MembershipState.Current;
            any = true;
        }

        return any ? MembershipState.Expired : MembershipState.None;
    }

    private static DateOnly ExpiryAfter(DateOnly start, int referenceDay)
    {
        var nextMonth = start.AddMonths(1);
        var day = Math.Min(referenceDay, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        return new DateOnly(nextMonth.Year, nextMonth.Month, day);
    }
}
