using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class PublicBookingFlowTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public async Task Catalog_contains_only_active_individual_services_for_the_business()
    {
        var fixture = new Fixture();
        fixture.BrowseRepository.Services.AddRange(
        [
            new Service { Id = 3, BusinessId = 1, Name = "Corte", Price = 5000m, DurationMinutes = 30, MaxCapacity = 1, IsActive = true },
            new Service { Id = 4, BusinessId = 1, Name = "Clase", Price = 3000m, DurationMinutes = 60, MaxCapacity = 8, IsActive = true },
            new Service { Id = 5, BusinessId = 1, Name = "Archivado", Price = 1000m, DurationMinutes = 30, MaxCapacity = 1, IsActive = false },
            new Service { Id = 6, BusinessId = 2, Name = "Otro negocio", Price = 1000m, DurationMinutes = 30, MaxCapacity = 1, IsActive = true }
        ]);

        var catalog = await fixture.Service.GetCatalogAsync("peluqueria");

        Assert.NotNull(catalog);
        Assert.Equal("Peluquería", catalog.Business.Name);
        Assert.Equal(new[] { new PublicBookingServiceOption(3, "Corte", 5000m, 30) }, catalog.Services);
    }

    [Fact]
    public async Task Worker_step_rejects_stale_services_and_lists_only_active_workers_in_the_business()
    {
        var fixture = new Fixture();
        fixture.BookingRepository.Service = new Service
        {
            Id = 3, BusinessId = 1, Name = "Corte", Price = 5000m, DurationMinutes = 30, MaxCapacity = 1, IsActive = true
        };
        fixture.BrowseRepository.AddWorker(1, true, new PublicBookingWorkerOption(2, "Ana Pérez"));
        fixture.BrowseRepository.AddWorker(1, false, new PublicBookingWorkerOption(3, "Eva Luna"));
        fixture.BrowseRepository.AddWorker(2, true, new PublicBookingWorkerOption(4, "Otra Persona"));

        var selection = await fixture.Service.GetWorkerSelectionAsync("peluqueria", 3);
        var staleSelection = await fixture.Service.GetWorkerSelectionAsync("peluqueria", 99);

        Assert.NotNull(selection);
        Assert.Equal(new[] { new PublicBookingWorkerOption(2, "Ana Pérez") }, selection.Workers);
        Assert.Null(staleSelection);
    }

    [Fact]
    public async Task Availability_preview_is_provisional_and_offers_the_nearest_future_slot_within_thirty_days()
    {
        var fixture = new Fixture();
        fixture.BookingRepository.Service = new Service
        {
            Id = 3, BusinessId = 1, Name = "Corte", Price = 5000m, DurationMinutes = 30, MaxCapacity = 1, IsActive = true
        };
        fixture.BrowseRepository.AddWorker(1, true, new PublicBookingWorkerOption(2, "Ana Pérez"));
        var requestedDate = new DateOnly(2026, 3, 11);
        fixture.BookingRepository.Snapshot = new BookingAvailabilitySnapshot(
            [new WorkerSchedule { WorkerId = 2, DayOfWeek = DayOfWeek.Wednesday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) }],
            [],
            [new Appointment
            {
                Id = 9, BusinessId = 1, WorkerId = 2, ServiceId = 3, Kind = AppointmentKind.Individual,
                Status = AppointmentStatus.Scheduled,
                StartsAt = requestedDate.ToDateTime(new TimeOnly(9, 0)),
                EndsAt = requestedDate.ToDateTime(new TimeOnly(17, 0))
            }],
            []);

        var preview = await fixture.Service.GetAvailabilityAsync("peluqueria", 3, 2, requestedDate);

        Assert.NotNull(preview);
        Assert.True(preview.IsProvisional);
        Assert.Empty(preview.AvailableStartTimes);
        Assert.Equal(new DateTime(2026, 3, 18, 9, 0, 0), preview.NearestFutureAlternative);
        Assert.Equal(requestedDate, fixture.BookingRepository.SnapshotStartDate);
        Assert.Equal(requestedDate.AddDays(30), fixture.BookingRepository.SnapshotEndDate);
        Assert.DoesNotContain("lock-worker", fixture.BookingRepository.Calls);
        Assert.Contains("availability", fixture.BookingRepository.Calls);
    }

    [Fact]
    public async Task Availability_preview_rejects_a_worker_from_another_business_without_loading_slots()
    {
        var fixture = new Fixture();
        fixture.BookingRepository.Service = new Service
        {
            Id = 3, BusinessId = 1, Name = "Corte", Price = 5000m, DurationMinutes = 30, MaxCapacity = 1, IsActive = true
        };
        fixture.BrowseRepository.AddWorker(2, true, new PublicBookingWorkerOption(4, "Otra Persona"));

        var preview = await fixture.Service.GetAvailabilityAsync("peluqueria", 3, 4, new DateOnly(2026, 3, 11));

        Assert.Null(preview);
        Assert.DoesNotContain("availability", fixture.BookingRepository.Calls);
    }

    [Fact]
    public async Task Availability_preview_rejects_past_dates_before_reading_the_availability_snapshot()
    {
        var fixture = new Fixture();
        var preview = await fixture.Service.GetAvailabilityAsync("peluqueria", 3, 2, new DateOnly(2026, 3, 9));

        Assert.Null(preview);
        Assert.Empty(fixture.BookingRepository.Calls);
    }

    [Theory]
    [InlineData("https://booking.example/app", "https://booking.example/app/t/opaque-token")]
    [InlineData("http://localhost:7160", "http://localhost:7160/t/opaque-token")]
    public void Appointment_urls_use_only_a_validated_configured_base_url(string baseUrl, string expected)
    {
        Assert.Equal(expected, PublicAppointmentUrlRules.Build(baseUrl, "opaque-token"));
    }

    [Theory]
    [InlineData("https://localhost:7160")]
    [InlineData("https://127.0.0.1:7160")]
    [InlineData("http://localhost:7160")]
    public void Non_development_requires_a_non_loopback_https_public_base_url(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => PublicAppointmentUrlRules.ValidateBaseUrl(baseUrl, isDevelopment: false));
    }

    [Fact]
    public void Non_development_accepts_a_public_https_base_url()
    {
        Assert.Equal("https://booking.example/app/", PublicAppointmentUrlRules.ValidateBaseUrl(
            "https://booking.example/app", isDevelopment: false).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://localhost:7160")]
    [InlineData("http://localhost:7160")]
    public void Development_accepts_loopback_https_and_http(string baseUrl)
    {
        Assert.True(PublicAppointmentUrlRules.ValidateBaseUrl(baseUrl, isDevelopment: true).IsLoopback);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("http://booking.example")]
    [InlineData("https://user:password@booking.example")]
    [InlineData("https://booking.example?host=attacker.example")]
    [InlineData("https://booking.example#fragment")]
    public void Appointment_url_configuration_rejects_unsafe_base_urls(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => PublicAppointmentUrlRules.Build(baseUrl, "opaque-token"));
    }

    private sealed class Fixture
    {
        public FakeBusinessRepository Businesses { get; } = new();
        public FakePublicBookingBrowseRepository BrowseRepository { get; } = new();
        public FakeBookingRepository BookingRepository { get; } = new();
        public FakeClock Clock { get; } = new() { Now = PublicBookingFlowTests.Now };
        public PublicBookingBrowseService Service { get; }

        public Fixture()
        {
            Businesses.InsertAsync(new Business { Name = "Peluquería", Slug = "peluqueria" }).GetAwaiter().GetResult();
            Service = new PublicBookingBrowseService(Businesses, BrowseRepository, BookingRepository, Clock);
        }
    }

    private sealed class FakePublicBookingBrowseRepository : IPublicBookingBrowseRepository
    {
        private readonly List<(int BusinessId, bool IsActive, PublicBookingWorkerOption Option)> _workers = [];
        public List<Service> Services { get; } = [];

        public void AddWorker(int businessId, bool isActive, PublicBookingWorkerOption option) =>
            _workers.Add((businessId, isActive, option));

        public Task<IReadOnlyList<Service>> ListActiveIndividualServicesAsync(int businessId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Service>>(Services
                .Where(service => service.BusinessId == businessId && service.IsActive && service.IsIndividual)
                .ToArray());

        public Task<IReadOnlyList<PublicBookingWorkerOption>> ListActiveWorkersAsync(int businessId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PublicBookingWorkerOption>>(_workers
                .Where(worker => worker.BusinessId == businessId && worker.IsActive)
                .Select(worker => worker.Option)
                .ToArray());

        public Task<PublicBookingWorkerOption?> GetActiveWorkerAsync(int businessId, int workerId, CancellationToken ct = default) =>
            Task.FromResult(_workers
                .Where(worker => worker.BusinessId == businessId && worker.IsActive && worker.Option.Id == workerId)
                .Select(worker => worker.Option)
                .SingleOrDefault());
    }
}
