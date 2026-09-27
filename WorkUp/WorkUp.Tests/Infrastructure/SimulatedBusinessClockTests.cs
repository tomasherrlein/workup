using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.Infrastructure;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Infrastructure;

/// <summary>
/// T26: the simulated clock is one installation-wide, in-process wrapper around the real
/// <see cref="BusinessClock"/>. It is shared by both demo businesses, stores no truth in SQL or SystemSettings (so a
/// restart resets it), never falls back to <see cref="DateTime.Now"/> and never silently clamps an advance to a
/// different day: an unrepresentable instant is a loud failure that leaves the simulation untouched.
/// </summary>
public sealed class SimulatedBusinessClockTests
{
    /// <summary>A base with a sub-second component, so a truncation bug cannot hide behind a whole-second fixture.</summary>
    private static readonly DateTime BaseWithMillis = new(2026, 3, 10, 9, 0, 0, 750);

    private static readonly DateTime BaseTruncated = new(2026, 3, 10, 9, 0, 0);

    // ---- Offset, hours, days and restore at second precision ----

    [Fact]
    public void Without_an_offset_it_is_the_inner_clock_truncated_to_whole_seconds()
    {
        var clock = NewClock(BaseWithMillis);

        Assert.False(clock.IsSimulated);
        Assert.Equal(BaseTruncated, clock.Now);
        Assert.Equal(0L, clock.Now.Ticks % TimeSpan.TicksPerSecond);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(SimulatedBusinessClock.MaxAdvanceHours)]
    public void AdvanceHours_adds_whole_hours_at_second_precision(int hours)
    {
        var clock = NewClock(BaseWithMillis);

        clock.AdvanceHours(hours);

        Assert.True(clock.IsSimulated);
        Assert.Equal(BaseTruncated.AddHours(hours), clock.Now);
        Assert.Equal(0L, clock.Now.Ticks % TimeSpan.TicksPerSecond);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(SimulatedBusinessClock.MaxAdvanceDays)]
    public void AdvanceDays_adds_whole_days_at_second_precision(int days)
    {
        var clock = NewClock(BaseWithMillis);

        clock.AdvanceDays(days);

        Assert.True(clock.IsSimulated);
        Assert.Equal(BaseTruncated.AddDays(days), clock.Now);
        Assert.Equal(0L, clock.Now.Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Hours_and_days_accumulate_in_one_offset()
    {
        var clock = NewClock(BaseWithMillis);

        clock.AdvanceHours(2);
        clock.AdvanceDays(1);
        clock.AdvanceHours(3);

        Assert.Equal(BaseTruncated.AddDays(1).AddHours(5), clock.Now);
    }

    [Fact]
    public void Today_follows_now_across_midnight()
    {
        var clock = NewClock(new DateTime(2026, 3, 10, 23, 30, 0, 400));
        Assert.Equal(new DateOnly(2026, 3, 10), clock.Today);

        clock.AdvanceHours(2);

        Assert.Equal(new DateTime(2026, 3, 11, 1, 30, 0), clock.Now);
        Assert.Equal(new DateOnly(2026, 3, 11), clock.Today);
    }

    [Fact]
    public void Restore_resets_the_offset_to_the_real_time()
    {
        var clock = NewClock(BaseWithMillis);
        clock.AdvanceDays(3);

        clock.Restore();

        Assert.False(clock.IsSimulated);
        Assert.Equal(BaseTruncated, clock.Now);

        // Restoring twice keeps the same real instant: the operation is not a toggle.
        clock.Restore();
        Assert.Equal(BaseTruncated, clock.Now);
    }

    // ---- Bounded input and rejection ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(SimulatedBusinessClock.MaxAdvanceHours + 1)]
    [InlineData(int.MaxValue)]
    public void AdvanceHours_rejects_input_outside_the_bounded_range_and_leaves_the_simulation_untouched(int hours)
    {
        var clock = NewClock(BaseWithMillis);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceHours(hours));

        Assert.Equal(nameof(hours), error.ParamName);
        Assert.Contains(SimulatedBusinessClock.AdvanceHoursMessage, error.Message, StringComparison.Ordinal);
        Assert.False(clock.IsSimulated);
        Assert.Equal(BaseTruncated, clock.Now);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(SimulatedBusinessClock.MaxAdvanceDays + 1)]
    [InlineData(int.MaxValue)]
    public void AdvanceDays_rejects_input_outside_the_bounded_range_and_leaves_the_simulation_untouched(int days)
    {
        var clock = NewClock(BaseWithMillis);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceDays(days));

        Assert.Equal(nameof(days), error.ParamName);
        Assert.Contains(SimulatedBusinessClock.AdvanceDaysMessage, error.Message, StringComparison.Ordinal);
        Assert.False(clock.IsSimulated);
        Assert.Equal(BaseTruncated, clock.Now);
    }

    // ---- DateTime range overflow ----

    [Fact]
    public void Advancing_a_clock_at_DateTime_MaxValue_fails_loudly_and_does_not_move_the_simulation()
    {
        var clock = NewClock(DateTime.MaxValue);

        var error = Assert.Throws<InvalidOperationException>(() => clock.AdvanceHours(1));

        Assert.Equal(SimulatedBusinessClock.OverflowMessage, error.Message);
        Assert.False(clock.IsSimulated);
        // No silent clamp to a different day: the rejected advance changed nothing, and reading still works.
        Assert.Equal(Truncate(DateTime.MaxValue), clock.Now);
    }

    [Fact]
    public void Advancing_a_clock_at_DateTime_MinValue_stays_representable_and_keeps_the_exact_day()
    {
        var clock = NewClock(DateTime.MinValue);

        clock.AdvanceHours(24);

        Assert.True(clock.IsSimulated);
        Assert.Equal(new DateTime(1, 1, 2, 0, 0, 0), clock.Now);
        Assert.Equal(new DateOnly(1, 1, 2), clock.Today);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task Concurrent_advances_never_lose_an_update()
    {
        const int workers = 8;
        const int advancesPerWorker = 50;
        var clock = NewClock(BaseWithMillis);
        // A gate maximises contention without blocking a pool thread: every writer awaits the same signal.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < advancesPerWorker; i++)
                    clock.AdvanceHours(1);
            }))
            .ToArray();

        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(BaseTruncated.AddHours(workers * advancesPerWorker), clock.Now);
    }

    [Fact]
    public async Task Concurrent_advances_and_restores_never_tear_the_offset()
    {
        const int writers = 6;
        const int iterations = 200;
        var clock = NewClock(BaseWithMillis);
        var observed = new ConcurrentBag<DateTime>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writersTasks = Enumerable.Range(0, writers)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < iterations; i++)
                {
                    clock.AdvanceDays(1);
                    if (i % 7 == 0)
                        clock.Restore();
                }
            }))
            .ToArray();
        var reader = Task.Run(async () =>
        {
            await gate.Task;
            for (var i = 0; i < 400; i++)
                observed.Add(clock.Now);
        });

        gate.SetResult();
        await Task.WhenAll([.. writersTasks, reader]);

        // Every observed reading, including the ones racing with an advance or a restore, is the real instant plus a
        // whole number of days at second precision: a torn or mixed offset could not satisfy that invariant.
        foreach (var reading in observed)
            AssertWholeDaysAhead(reading);

        AssertWholeDaysAhead(clock.Now);

        clock.Restore();
        Assert.Equal(BaseTruncated, clock.Now);
    }

    [Fact]
    public void A_null_inner_clock_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SimulatedBusinessClock(null!));
    }

    // ---- Composition root: one shared instance as IClock and as the control interface ----

    [Fact]
    public void The_composition_root_exposes_one_singleton_simulated_clock_for_every_scope()
    {
        var services = new ServiceCollection();
        services.AddWorkUp(BuildConfiguration(DefaultSettings()), new FakeHostEnvironment());

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(BusinessClock)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(SimulatedBusinessClock)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(IClock)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(ISimulatedClockControl)).Lifetime);

        using var provider = services.BuildServiceProvider();
        var asClock = provider.GetRequiredService<IClock>();
        var control = provider.GetRequiredService<ISimulatedClockControl>();
        Assert.IsType<SimulatedBusinessClock>(asClock);
        Assert.Same(asClock, control);

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        Assert.Same(asClock, first.ServiceProvider.GetRequiredService<IClock>());
        Assert.Same(control, second.ServiceProvider.GetRequiredService<ISimulatedClockControl>());
    }

    [Fact]
    public void IClock_is_the_simulated_wrapper_of_the_registered_real_BusinessClock()
    {
        var services = new ServiceCollection();
        services.AddWorkUp(BuildConfiguration(DefaultSettings()), new FakeHostEnvironment());
        using var provider = services.BuildServiceProvider();

        var real = provider.GetRequiredService<BusinessClock>();
        var simulated = provider.GetRequiredService<SimulatedBusinessClock>();
        var asClock = provider.GetRequiredService<IClock>();
        Assert.Same(simulated, asClock);

        // With no offset the wrapper is the real business-local second-precision instant.
        Assert.True((simulated.Now - real.Now).Duration() < TimeSpan.FromMinutes(1));

        provider.GetRequiredService<ISimulatedClockControl>().AdvanceHours(3);

        // The wrapper moved three hours ahead of the same real clock every consumer shares.
        Assert.InRange(
            simulated.Now - real.Now,
            TimeSpan.FromHours(3) - TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
    }

    // ---- Helpers ----

    private static SimulatedBusinessClock NewClock(DateTime innerNow) =>
        new(new FakeClock { Now = innerNow });

    private static DateTime Truncate(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);

    private static void AssertWholeDaysAhead(DateTime reading)
    {
        var delta = reading - BaseTruncated;
        Assert.True(delta >= TimeSpan.Zero, $"the simulated clock went before the real instant: {reading:O}");
        Assert.Equal(0L, delta.Ticks % TimeSpan.TicksPerDay);
        Assert.Equal(0L, reading.Ticks % TimeSpan.TicksPerSecond);
    }

    private static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["ConnectionStrings:WorkUp"] =
            @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT26Clock;Trusted_Connection=True;TrustServerCertificate=True",
        ["WorkUp:PublicBaseUrl"] = "https://reservas.example.com",
        ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires"
    };

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
