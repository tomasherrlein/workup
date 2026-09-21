using WorkUp.Business.Abstractions;

namespace WorkUp.Tests.Fakes;

/// <summary>Settable clock for service tests. Never <see cref="DateTime.Now"/>.</summary>
public sealed class FakeClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 3, 10, 9, 0, 0);
}
