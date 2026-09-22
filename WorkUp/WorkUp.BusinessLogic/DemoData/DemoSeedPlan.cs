namespace WorkUp.BusinessLogic.DemoData;

public sealed record DemoBusinessSeed(
    string Name,
    string Slug,
    string ContactPhone,
    string NotificationEmail,
    string OwnerUsername,
    IReadOnlyList<DemoWorkerSeed> Workers,
    IReadOnlyList<DemoServiceSeed> Services,
    IReadOnlyList<DemoGridClassSeed> GridClasses);

public sealed record DemoWorkerSeed(
    string Key,
    string FirstName,
    string LastName,
    string Phone,
    string Cuil,
    IReadOnlyList<DemoScheduleSeed> Schedules);

public sealed record DemoScheduleSeed(DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

public sealed record DemoServiceSeed(string Key, string Name, decimal Price, int DurationMinutes, int MaxCapacity);

public sealed record DemoGridClassSeed(string ServiceKey, string WorkerKey, DayOfWeek DayOfWeek, TimeOnly StartTime);

/// <summary>Deterministic, fictional demo-only content. Non-relevamiento values are explicitly synthetic.</summary>
public static class DemoSeedPlan
{
    public static IReadOnlyList<DemoBusinessSeed> Create()
    {
        var salonSchedule = Enumerable.Range((int)DayOfWeek.Monday, 5)
            .Select(day => new DemoScheduleSeed((DayOfWeek)day, new TimeOnly(9, 0), new TimeOnly(18, 0)))
            .Append(new DemoScheduleSeed(DayOfWeek.Saturday, new TimeOnly(9, 0), new TimeOnly(13, 0)))
            .ToArray();
        var yogaSchedule = Enumerable.Range((int)DayOfWeek.Monday, 5)
            .Select(day => new DemoScheduleSeed((DayOfWeek)day, new TimeOnly(8, 0), new TimeOnly(20, 0)))
            .ToArray();

        return
        [
            new DemoBusinessSeed(
                Name: "Peluquería de Marina (demo)",
                Slug: "peluqueria-marina-demo",
                ContactPhone: "0000000001",
                NotificationEmail: "marina@demo.invalid",
                OwnerUsername: "marina.demo",
                Workers:
                [
                    new("salon-worker-1", "Ana", "Demo", "0000000011", "00000000001", salonSchedule),
                    new("salon-worker-2", "Clara", "Demo", "0000000012", "00000000002", salonSchedule)
                ],
                Services:
                [
                    new("haircut", "Corte", 18000m, 45, 1),
                    new("color", "Coloración", 42000m, 120, 1),
                    new("brushing", "Brushing", 14000m, 45, 1)
                ],
                GridClasses: []),
            new DemoBusinessSeed(
                Name: "Estudio de yoga de Lucía (demo)",
                Slug: "estudio-yoga-lucia-demo",
                ContactPhone: "0000000002",
                NotificationEmail: "lucia@demo.invalid",
                OwnerUsername: "lucia.demo",
                Workers:
                [
                    new("yoga-worker-1", "Martina", "Demo", "0000000021", "00000000003", yogaSchedule),
                    new("yoga-worker-2", "Valeria", "Demo", "0000000022", "00000000004", yogaSchedule)
                ],
                Services:
                [
                    new("hatha", "Hatha yoga", 6500m, 60, 12),
                    new("vinyasa", "Vinyasa yoga", 7000m, 60, 12)
                ],
                GridClasses:
                [
                    new("hatha", "yoga-worker-1", DayOfWeek.Monday, new TimeOnly(18, 0)),
                    new("vinyasa", "yoga-worker-1", DayOfWeek.Wednesday, new TimeOnly(18, 0)),
                    new("hatha", "yoga-worker-2", DayOfWeek.Tuesday, new TimeOnly(18, 0)),
                    new("vinyasa", "yoga-worker-2", DayOfWeek.Thursday, new TimeOnly(18, 0))
                ])
        ];
    }
}
