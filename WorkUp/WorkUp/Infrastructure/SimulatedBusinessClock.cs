using WorkUp.BusinessLogic.Abstractions;

namespace WorkUp.Infrastructure;

/// <summary>
/// Control surface of the installation-wide simulated clock (T26). It is registered as a singleton next to
/// <see cref="IClock"/> and resolves to the same instance, so moving the clock is visible to every consumer of
/// <see cref="IClock"/> without rebuilding the service graph.
/// <para>
/// The user decision for T26 is that the simulation is <em>installation-wide and in-process</em>: it is shared by both
/// demo businesses of the isolated demo, moving it changes both at the same time, and there is no SQL or SystemSettings
/// row behind it, so restarting the host resets the simulated time to real time.
/// </para>
/// </summary>
public interface ISimulatedClockControl
{
    /// <summary>
    /// True while the clock carries a non-zero offset. The owner-facing tools must show a prominent warning whenever
    /// this is true, because both demo businesses are being moved at once.
    /// </summary>
    bool IsSimulated { get; }

    /// <summary>
    /// Moves the simulated instant forward by <paramref name="hours"/> (1..72). A rejected input changes nothing; a
    /// resulting instant that leaves the representable <see cref="DateTime"/> range fails loudly instead of clamping.
    /// </summary>
    void AdvanceHours(int hours);

    /// <summary>
    /// Moves the simulated instant forward by <paramref name="days"/> (1..365). A rejected input changes nothing; a
    /// resulting instant that leaves the representable <see cref="DateTime"/> range fails loudly instead of clamping.
    /// </summary>
    void AdvanceDays(int days);

    /// <summary>Discards the whole offset and returns to the real business-local time. It never moves the clock back.</summary>
    void Restore();
}

/// <summary>
/// The simulated clock of the T26 test tools: the real business-local, second-precision instant of
/// <see cref="BusinessClock"/> plus one in-process offset. It is the single <see cref="IClock"/> of the installation
/// in every deployment, because with a zero offset it is exactly the real clock; only the development-only tools
/// (gated by <see cref="DevToolsGate"/>) can move it.
/// <para>
/// Contract details that matter:
/// <list type="bullet">
/// <item><see cref="Now"/> is always truncated to whole seconds, exactly like <see cref="BusinessClock"/>, because
/// every stored instant is <c>DATETIME2(0)</c>. There is no <see cref="DateTime.Now"/> fallback anywhere: the only
/// source of real time is the wrapped clock, and background jobs keep using the instant the scheduler runner reads
/// once per invocation instead of reading the clock again.</item>
/// <item>The offset is a single 64-bit tick count mutated only through <see cref="Interlocked"/> compare-and-swap, so
/// concurrent advances and restores can neither lose an update nor expose a torn value.</item>
/// <item>Every advance validates its input range and the resulting instant. An advance that would leave the
/// representable <see cref="DateTime"/> range (<see cref="DateTime.MinValue"/>..<see cref="DateTime.MaxValue"/>) throws
/// and leaves the current simulation untouched: the clock is never silently clamped to a different day.</item>
/// <item>Nothing is persisted. The offset lives in memory by design, so a restart is the safe default reset.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SimulatedBusinessClock : IClock, ISimulatedClockControl
{
    /// <summary>Smallest accepted hour advance.</summary>
    public const int MinAdvanceHours = 1;

    /// <summary>Largest accepted hour advance: three days in a single step.</summary>
    public const int MaxAdvanceHours = 72;

    /// <summary>Smallest accepted day advance.</summary>
    public const int MinAdvanceDays = 1;

    /// <summary>Largest accepted day advance: one year in a single step.</summary>
    public const int MaxAdvanceDays = 365;

    /// <summary>Fixed, non-echoing message of an hour advance outside <see cref="MinAdvanceHours"/>..<see cref="MaxAdvanceHours"/>.</summary>
    public const string AdvanceHoursMessage = "El avance en horas debe estar entre 1 y 72.";

    /// <summary>Fixed, non-echoing message of a day advance outside <see cref="MinAdvanceDays"/>..<see cref="MaxAdvanceDays"/>.</summary>
    public const string AdvanceDaysMessage = "El avance en días debe estar entre 1 y 365.";

    /// <summary>Fixed message of an advance whose resulting instant leaves the representable date range.</summary>
    public const string OverflowMessage = "El avance del reloj simulado sale del rango de fechas soportado.";

    /// <summary>
    /// Upper bound of the offset, expressed in ticks. It is the whole representable date range, so the offset itself
    /// always stays a representable date span and the sum of a <see cref="DateTime"/> tick count plus this bound can
    /// never overflow <see cref="long"/>.
    /// </summary>
    private static readonly long MaxOffsetTicks = DateTime.MaxValue.Ticks - DateTime.MinValue.Ticks;

    private readonly IClock _inner;
    private long _offsetTicks;

    public SimulatedBusinessClock(IClock inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The real business-local instant plus the current offset, always at whole-second precision.</summary>
    public DateTime Now
    {
        get
        {
            var ticks = TruncateToSecond(_inner.Now.Ticks) + Volatile.Read(ref _offsetTicks);

            // Both terms are inside the DateTime range, so the sum cannot overflow long; it has to be checked against
            // DateTime explicitly, and a value outside the range is a loud failure instead of a wrapped instant.
            if (ticks > DateTime.MaxValue.Ticks)
                throw new InvalidOperationException(OverflowMessage);

            return new DateTime(ticks, DateTimeKind.Unspecified);
        }
    }

    /// <summary>Today follows <see cref="Now"/>, so advancing across midnight moves the calendar day too.</summary>
    public DateOnly Today => DateOnly.FromDateTime(Now);

    /// <inheritdoc />
    public bool IsSimulated => Volatile.Read(ref _offsetTicks) != 0;

    /// <inheritdoc />
    public void AdvanceHours(int hours)
    {
        if (hours < MinAdvanceHours || hours > MaxAdvanceHours)
            throw new ArgumentOutOfRangeException(nameof(hours), AdvanceHoursMessage);

        AdvanceTicks(checked(hours * TimeSpan.TicksPerHour));
    }

    /// <inheritdoc />
    public void AdvanceDays(int days)
    {
        if (days < MinAdvanceDays || days > MaxAdvanceDays)
            throw new ArgumentOutOfRangeException(nameof(days), AdvanceDaysMessage);

        AdvanceTicks(checked(days * TimeSpan.TicksPerDay));
    }

    /// <inheritdoc />
    public void Restore() => Interlocked.Exchange(ref _offsetTicks, 0L);

    /// <summary>
    /// Applies one positive tick delta with a compare-and-swap loop, so concurrent advances and restores never lose an
    /// update or observe a half-written offset. The range check happens before the swap, so a rejected advance leaves
    /// the simulation exactly as it was.
    /// </summary>
    private void AdvanceTicks(long deltaTicks)
    {
        // One reading of the real clock per advance: it binds the accepted offset to a representable instant.
        var baseTicks = TruncateToSecond(_inner.Now.Ticks);

        while (true)
        {
            var current = Volatile.Read(ref _offsetTicks);
            var candidate = current + deltaTicks;

            // Both operands are bounded, so the sum cannot wrap long; the explicit checks cover the offset cap and the
            // resulting DateTime instant. Short-circuiting keeps the sum from being evaluated after a cap rejection.
            if (candidate > MaxOffsetTicks || baseTicks + candidate > DateTime.MaxValue.Ticks)
                throw new InvalidOperationException(OverflowMessage);

            if (Interlocked.CompareExchange(ref _offsetTicks, candidate, current) == current)
                return;
        }
    }

    private static long TruncateToSecond(long ticks) => ticks - ticks % TimeSpan.TicksPerSecond;
}
