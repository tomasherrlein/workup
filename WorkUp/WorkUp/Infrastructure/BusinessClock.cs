using WorkUp.BusinessLogic.Abstractions;

namespace WorkUp.Infrastructure;

/// <summary>
/// Real time in the business time zone, truncated to seconds to match DATETIME2(0) columns.
/// The simulated clock of the dev tools (T26) wraps this class.
/// </summary>
public sealed class BusinessClock(TimeZoneInfo zone) : IClock
{
    public DateTime Now
    {
        get
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            return new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
        }
    }
}
