using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerBookingActionsTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    [Theory]
    [InlineData(BookingStatus.Pending, null)]
    [InlineData(BookingStatus.Confirmed, "2026-03-09T12:00:00")]
    public async Task Owner_cancels_a_consistent_active_individual_booking_before_start(
        BookingStatus status, string? confirmedAt)
    {
        var repository = new FakeOwnerBookingActionsRepository { Booking = State(status, confirmedAt) };
        var unitOfWork = new FakeUnitOfWork();
        var clock = new FakeClock { Now = Now };
        var service = CreateService(repository, unitOfWork, clock);

        var result = await service.CancelIndividualBookingAsync(7, 12);

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Cancelled, repository.Booking!.BookingStatus);
        Assert.Equal(CancelledBy.Owner, repository.Booking.CancelledBy);
        Assert.Equal(Now, repository.Booking.CancelledAt);
        Assert.Equal([(7, 12, Now)], repository.CancellationRequests);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(BookingStatus.Attended, null, null, null, 0)]
    [InlineData(BookingStatus.Absent, null, null, null, 0)]
    [InlineData(BookingStatus.Cancelled, null, null, null, 0)]
    [InlineData(BookingStatus.Pending, "2026-03-09T12:00:00", null, null, 0)]
    [InlineData(BookingStatus.Confirmed, null, null, null, 0)]
    [InlineData(BookingStatus.Confirmed, "2026-03-09T12:00:00", "2026-03-09T12:30:00", "Owner", 0)]
    [InlineData(BookingStatus.Pending, null, null, null, 1)]
    public async Task Refuses_bookings_without_a_consistent_active_state(
        BookingStatus status, string? confirmedAt, string? cancelledAt, string? cancelledBy, int attendance)
    {
        var repository = new FakeOwnerBookingActionsRepository
        {
            Booking = State(status, confirmedAt) with
            {
                CancelledAt = Parse(cancelledAt),
                CancelledBy = cancelledBy is null ? null : Enum.Parse<CancelledBy>(cancelledBy),
                AttendanceRecordedAt = attendance == 0 ? null : Now
            }
        };
        var unitOfWork = new FakeUnitOfWork();
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.CancelIndividualBookingAsync(7, 12);

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_unavailable", result.Error!.Code);
        Assert.Empty(repository.CancellationRequests);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Refuses_group_walk_in_suspended_or_enrollment_bookings_and_absence_records()
    {
        var invalidStates = new[]
        {
            State(BookingStatus.Pending) with { AppointmentKind = AppointmentKind.ClassSession },
            State(BookingStatus.Pending) with { AppointmentStatus = AppointmentStatus.Suspended },
            State(BookingStatus.Pending) with { Origin = AppointmentOrigin.WalkIn },
            State(BookingStatus.Pending) with { MaxCapacity = 2 },
            State(BookingStatus.Pending) with { Modality = BookingModality.DropIn },
            State(BookingStatus.Pending) with { EnrollmentId = 5 },
            State(BookingStatus.Pending) with { AbsenceSource = AbsenceSource.ClientLink }
        };

        foreach (var state in invalidStates)
        {
            var repository = new FakeOwnerBookingActionsRepository { Booking = state };
            var unitOfWork = new FakeUnitOfWork();
            var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

            var result = await service.CancelIndividualBookingAsync(7, 12);

            Assert.False(result.Succeeded);
            Assert.Empty(repository.CancellationRequests);
            Assert.Equal(0, unitOfWork.CommitCount);
        }
    }

    [Fact]
    public async Task Refuses_cancellation_at_or_after_the_appointment_start()
    {
        foreach (var now in new[] { new DateTime(2026, 3, 10, 10, 0, 0), new DateTime(2026, 3, 10, 10, 0, 1) })
        {
            var repository = new FakeOwnerBookingActionsRepository { Booking = State(BookingStatus.Pending) };
            var unitOfWork = new FakeUnitOfWork();
            var service = CreateService(repository, unitOfWork, new FakeClock { Now = now });

            var result = await service.CancelIndividualBookingAsync(7, 12);

            Assert.False(result.Succeeded);
            Assert.Empty(repository.CancellationRequests);
            Assert.Equal(0, unitOfWork.CommitCount);
        }
    }

    [Fact]
    public async Task Owner_assisted_booking_uses_the_public_worker_lock_availability_link_and_outbox_transaction()
    {
        var publicRepository = new FakeBookingRepository
        {
            Worker = new Worker { Id = 2, BusinessId = 7, FirstName = "Ana", LastName = "Pérez", IsActive = true },
            Service = new Service
            {
                Id = 3, BusinessId = 7, Name = "Corte", Price = 5000m,
                DurationMinutes = 30, MaxCapacity = 1, IsActive = true
            },
            Snapshot = new BookingAvailabilitySnapshot(
                Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
                {
                    WorkerId = 2, DayOfWeek = day, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0)
                }).ToArray(), [], [], [])
        };
        var unitOfWork = new FakeUnitOfWork();
        var clock = new FakeClock { Now = Now };
        var publicBooking = new BookingService(
            publicRepository, unitOfWork, new TestLinkTokenProtector(), new TestAppointmentUrlBuilder(), clock);
        var service = new OwnerBookingActionsService(null!, publicBooking, new FakeOwnerBookingActionsRepository(), unitOfWork, clock);

        var result = await service.BookIndividualAsync(7, new BookIndividualAppointmentRequest(
            3, 2, new DateOnly(2026, 3, 11), new TimeOnly(10, 0), "sol@example.com", "Sol", "Díaz"));

        Assert.True(result.Succeeded);
        Assert.Single(publicRepository.Appointments);
        Assert.Single(publicRepository.Bookings);
        Assert.Single(publicRepository.Links);
        Assert.Single(publicRepository.Notifications);
        Assert.Contains("lock-worker", publicRepository.Calls);
        Assert.True(publicRepository.Calls.IndexOf("lock-worker") < publicRepository.Calls.IndexOf("availability"));
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Owner_booking_form_uses_public_tenant_scoped_active_projections_after_matching_the_owner_business()
    {
        var businesses = new FakeBusinessRepository();
        var businessId = await businesses.InsertAsync(new Business { Name = "Salón", Slug = "salon" });
        var browseRepository = new FakeOwnerBookingBrowseRepository();
        browseRepository.Services.Add(new Service
        {
            Id = 3, BusinessId = businessId, Name = "Corte", Price = 5000m,
            DurationMinutes = 30, MaxCapacity = 1, IsActive = true
        });
        browseRepository.Services.Add(new Service
        {
            Id = 4, BusinessId = businessId, Name = "Clase", Price = 1000m,
            DurationMinutes = 60, MaxCapacity = 8, IsActive = true
        });
        browseRepository.Workers.Add(new PublicBookingWorkerOption(2, "Ana Pérez"));
        var publicRepository = new FakeBookingRepository
        {
            Worker = new Worker { Id = 2, BusinessId = businessId, FirstName = "Ana", LastName = "Pérez", IsActive = true },
            Service = browseRepository.Services[0],
            Snapshot = new BookingAvailabilitySnapshot(
                Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
                {
                    WorkerId = 2, DayOfWeek = day, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0)
                }).ToArray(), [], [], [])
        };
        var clock = new FakeClock { Now = Now };
        var browseService = new PublicBookingBrowseService(businesses, browseRepository, publicRepository, clock);
        var service = new OwnerBookingActionsService(
            browseService, null!, new FakeOwnerBookingActionsRepository(), new FakeUnitOfWork(), clock);

        var form = await service.GetBookingFormAsync(
            businessId, "salon", 3, 2, new DateOnly(2026, 3, 11));

        Assert.NotNull(form);
        Assert.Equal(businessId, form.Catalog.Business.Id);
        Assert.Equal([3], form.Catalog.Services.Select(option => option.Id));
        Assert.Equal([new PublicBookingWorkerOption(2, "Ana Pérez")], form.Workers);
        Assert.Contains(new TimeOnly(10, 0), form.AvailableStartTimes);
        Assert.DoesNotContain("Phone", typeof(PublicBookingWorkerOption).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task Owner_booking_form_rejects_a_slug_that_resolves_to_another_business_before_worker_lookup()
    {
        var businesses = new FakeBusinessRepository();
        var ownerBusinessId = await businesses.InsertAsync(new Business { Name = "Dueña", Slug = "duena" });
        await businesses.InsertAsync(new Business { Name = "Otro", Slug = "otro" });
        var browseRepository = new FakeOwnerBookingBrowseRepository();
        var publicRepository = new FakeBookingRepository();
        var clock = new FakeClock { Now = Now };
        var browseService = new PublicBookingBrowseService(businesses, browseRepository, publicRepository, clock);
        var service = new OwnerBookingActionsService(
            browseService, null!, new FakeOwnerBookingActionsRepository(), new FakeUnitOfWork(), clock);

        var form = await service.GetBookingFormAsync(ownerBusinessId, "otro", 3, 2, new DateOnly(2026, 3, 11));

        Assert.Null(form);
        Assert.Equal(0, browseRepository.WorkerListRequests);
    }

    [Fact]
    public async Task Tenant_mismatch_and_missing_identifiers_are_generic_and_do_not_commit()
    {
        var repository = new FakeOwnerBookingActionsRepository { Booking = State(BookingStatus.Pending) };
        var unitOfWork = new FakeUnitOfWork();
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var crossTenant = await service.CancelIndividualBookingAsync(8, 12);
        var invalidId = await service.CancelIndividualBookingAsync(7, 0);

        Assert.False(crossTenant.Succeeded);
        Assert.Equal("owner_booking_not_found", crossTenant.Error!.Code);
        Assert.False(invalidId.Succeeded);
        Assert.Equal("owner_booking_not_found", invalidId.Error!.Code);
        Assert.Equal([(8, 12)], repository.LockRequests);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Does_not_commit_when_the_conditional_cancellation_update_loses_a_race()
    {
        var repository = new FakeOwnerBookingActionsRepository
        {
            Booking = State(BookingStatus.Pending),
            AllowCancellation = false
        };
        var unitOfWork = new FakeUnitOfWork();
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.CancelIndividualBookingAsync(7, 12);

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_unavailable", result.Error!.Code);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Owner_booking_form_offers_the_nearest_slot_of_the_same_worker_when_the_day_is_full()
    {
        var fixture = await CreateFormFixtureAsync(workingDay: DayOfWeek.Friday);

        var form = await fixture.Service.GetBookingFormAsync(
            fixture.BusinessId, "salon", 3, 2, new DateOnly(2026, 3, 11));

        Assert.NotNull(form);
        Assert.Empty(form.AvailableStartTimes);
        Assert.Equal(2, form.WorkerId);
        Assert.Equal(new DateTime(2026, 3, 13, 9, 0, 0), form.NearestAlternative);
        Assert.False(form.IsPastDate);
    }

    [Fact]
    public async Task Owner_booking_form_keeps_the_selection_and_flags_a_past_day_instead_of_hiding_it()
    {
        var fixture = await CreateFormFixtureAsync(workingDay: null);

        var form = await fixture.Service.GetBookingFormAsync(
            fixture.BusinessId, "salon", 3, 2, new DateOnly(2026, 3, 9));

        Assert.NotNull(form);
        Assert.True(form.IsPastDate);
        Assert.Equal(3, form.ServiceId);
        Assert.Equal(2, form.WorkerId);
        Assert.Empty(form.AvailableStartTimes);
        Assert.Null(form.NearestAlternative);
    }

    private static async Task<(OwnerBookingActionsService Service, int BusinessId)> CreateFormFixtureAsync(DayOfWeek? workingDay)
    {
        var businesses = new FakeBusinessRepository();
        var businessId = await businesses.InsertAsync(new Business { Name = "Salón", Slug = "salon" });
        var browseRepository = new FakeOwnerBookingBrowseRepository();
        browseRepository.Services.Add(new Service
        {
            Id = 3, BusinessId = businessId, Name = "Corte", Price = 5000m,
            DurationMinutes = 30, MaxCapacity = 1, IsActive = true
        });
        browseRepository.Workers.Add(new PublicBookingWorkerOption(2, "Ana Pérez"));
        var days = workingDay is { } day ? new[] { day } : Enum.GetValues<DayOfWeek>();
        var publicRepository = new FakeBookingRepository
        {
            Worker = new Worker { Id = 2, BusinessId = businessId, FirstName = "Ana", LastName = "Pérez", IsActive = true },
            Service = browseRepository.Services[0],
            Snapshot = new BookingAvailabilitySnapshot(
                days.Select(workDay => new WorkerSchedule
                {
                    WorkerId = 2, DayOfWeek = workDay, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0)
                }).ToArray(), [], [], [])
        };
        var clock = new FakeClock { Now = Now };
        var browseService = new PublicBookingBrowseService(businesses, browseRepository, publicRepository, clock);
        return (new OwnerBookingActionsService(
            browseService, null!, new FakeOwnerBookingActionsRepository(), new FakeUnitOfWork(), clock), businessId);
    }

    private static OwnerBookingActionsService CreateService(
        FakeOwnerBookingActionsRepository repository, FakeUnitOfWork unitOfWork, FakeClock clock) =>
        new(null!, null!, repository, unitOfWork, clock);

    private static OwnerBookingCancellationState State(BookingStatus status, string? confirmedAt = null) => new(
        12,
        34,
        new DateTime(2026, 3, 10, 10, 0, 0),
        AppointmentKind.Individual,
        AppointmentStatus.Scheduled,
        AppointmentOrigin.Reserved,
        1,
        status,
        null,
        null,
        Parse(confirmedAt),
        null,
        null,
        null,
        null);

    private static DateTime? Parse(string? value) => value is null ? null : DateTime.Parse(value);

    private sealed class FakeOwnerBookingBrowseRepository : IPublicBookingBrowseRepository
    {
        public List<Service> Services { get; } = [];
        public List<PublicBookingWorkerOption> Workers { get; } = [];
        public int WorkerListRequests { get; private set; }

        public Task<IReadOnlyList<Service>> ListActiveIndividualServicesAsync(int businessId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Service>>(Services.Where(service => service.BusinessId == businessId).ToArray());

        public Task<IReadOnlyList<PublicBookingWorkerOption>> ListActiveWorkersAsync(int businessId, CancellationToken ct = default)
        {
            WorkerListRequests++;
            return Task.FromResult<IReadOnlyList<PublicBookingWorkerOption>>(Workers.ToArray());
        }

        public Task<PublicBookingWorkerOption?> GetActiveWorkerAsync(int businessId, int workerId, CancellationToken ct = default) =>
            Task.FromResult(Workers.SingleOrDefault(worker => worker.Id == workerId));
    }

    private sealed class TestLinkTokenProtector : ILinkTokenProtector
    {
        public string Protect(string token) => "protected:" + token;
        public string Unprotect(string protectedToken) => protectedToken["protected:".Length..];
    }

    private sealed class TestAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string Build(string token) => "https://workup.test/t/" + token;
    }
}
