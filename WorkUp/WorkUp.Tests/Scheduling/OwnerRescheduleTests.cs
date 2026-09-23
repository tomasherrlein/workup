using System.Data;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerRescheduleTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime OriginalStart = new(2026, 3, 11, 10, 0, 0);
    private static readonly DateTime NewStart = new(2026, 3, 12, 10, 15, 0);

    [Fact]
    public async Task Reschedules_atomically_on_the_same_worker_and_preserves_the_active_private_link()
    {
        var repository = CreateRepository();
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var clock = new FakeClock { Now = Now };
        unitOfWork.BeforeCommit = () =>
        {
            Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
            Assert.Equal(BookingStatus.Pending, repository.Booking.BookingStatus);
            Assert.Equal(OriginalStart.AddHours(24), Assert.Single(repository.Links).ExpiresAt);
            Assert.Empty(repository.Notifications);
        };
        var service = CreateService(repository, unitOfWork, clock);

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.True(result.Succeeded);
        Assert.Equal(NewStart, repository.Booking!.StartsAt);
        Assert.Equal(NewStart.AddMinutes(45), repository.Booking.EndsAt);
        Assert.Equal(BookingStatus.Confirmed, repository.Booking.BookingStatus);
        Assert.Equal(Now, repository.Booking.ConfirmedAt);
        Assert.Equal(1, unitOfWork.CommitCount);
        Assert.Equal(["lookup-worker", "lock-worker", "lock-booking", "availability", "active-links", "apply-reschedule", "update-link-expiry", "insert-notification"], repository.Calls);

        var link = Assert.Single(repository.Links);
        Assert.Equal(NewStart.AddHours(24), link.ExpiresAt);
        Assert.Equal([1, 2, 3, 4], link.TokenHash);
        Assert.Equal("protected-existing-token", link.ProtectedToken);

        var notification = Assert.Single(repository.Notifications);
        Assert.Equal(NotificationType.Rescheduled, notification.Type);
        Assert.Equal("sol@example.com", notification.Recipient);
        Assert.StartsWith("rescheduled:booking:40:", notification.DedupKey, StringComparison.Ordinal);
        Assert.Contains("Sol &amp;", notification.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("A&amp;B &lt;Salon&gt;", notification.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Color &lt;red&gt;", notification.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Ana &amp; Paz", notification.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("jueves 12 de marzo a las 10:15", notification.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("private health notes", notification.BodyHtml, StringComparison.Ordinal);
        Assert.Equal(1, repository.NotificationInsertCount);
    }

    [Fact]
    public async Task Rejects_an_occupied_fresh_slot_without_releasing_the_original_reservation()
    {
        var repository = CreateRepository();
        repository.Snapshot = repository.Snapshot with
        {
            Appointments =
            [
                new Appointment
                {
                    Id = 99, BusinessId = 7, WorkerId = 2, ServiceId = 3,
                    Kind = AppointmentKind.Individual, Origin = AppointmentOrigin.Reserved,
                    Status = AppointmentStatus.Scheduled, StartsAt = NewStart, EndsAt = NewStart.AddMinutes(30)
                }
            ],
            Bookings = [new Booking { Id = 100, BusinessId = 7, AppointmentId = 99, Status = BookingStatus.Pending }]
        };
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_reschedule_slot_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.Equal(BookingStatus.Pending, repository.Booking.BookingStatus);
        Assert.Equal(0, unitOfWork.CommitCount);
        Assert.Empty(repository.Notifications);
        Assert.Equal(OriginalStart.AddHours(24), Assert.Single(repository.Links).ExpiresAt);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Attended)]
    [InlineData(BookingStatus.Absent)]
    public async Task Rejects_cancelled_attended_or_absent_bookings(BookingStatus status)
    {
        var repository = CreateRepository();
        repository.Booking = repository.Booking! with
        {
            BookingStatus = status,
            ConfirmedAt = null,
            CancelledAt = status == BookingStatus.Cancelled ? Now : null,
            CancelledBy = status == BookingStatus.Cancelled ? CancelledBy.Owner : null,
            AttendanceRecordedAt = status == BookingStatus.Attended ? Now : null,
            AbsenceSource = status == BookingStatus.Absent ? AbsenceSource.Owner : null
        };
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_unavailable", result.Error!.Code);
        Assert.Equal(status, repository.Booking.BookingStatus);
        Assert.Equal(OriginalStart, repository.Booking.StartsAt);
        Assert.Equal(0, unitOfWork.CommitCount);
        Assert.DoesNotContain("apply-reschedule", repository.Calls);
    }

    [Fact]
    public async Task Rejects_a_new_slot_that_cannot_fit_the_full_duration_inside_the_worker_schedule()
    {
        var repository = CreateRepository();
        repository.Snapshot = repository.Snapshot with
        {
            Schedules = [new WorkerSchedule
            {
                WorkerId = 2, DayOfWeek = DayOfWeek.Thursday,
                StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(10, 45)
            }]
        };
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_reschedule_slot_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Rejects_a_slot_overlapping_a_non_working_break()
    {
        var repository = CreateRepository();
        repository.Snapshot = repository.Snapshot with
        {
            Breaks = [new WorkerBreak
            {
                WorkerId = 2, DayOfWeek = DayOfWeek.Thursday,
                StartTime = new TimeOnly(10, 30), EndTime = new TimeOnly(10, 45)
            }]
        };
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_reschedule_slot_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Refuses_a_new_start_that_is_no_longer_in_the_future()
    {
        var repository = CreateRepository();
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = NewStart });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Leaves_a_confirmed_booking_confirmed_and_refreshes_its_confirmation_time()
    {
        var repository = CreateRepository();
        repository.Booking = repository.Booking! with
        {
            BookingStatus = BookingStatus.Confirmed,
            ConfirmedAt = Now.AddHours(-1)
        };
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Confirmed, repository.Booking!.BookingStatus);
        Assert.Equal(Now, repository.Booking.ConfirmedAt);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Does_not_commit_when_the_conditional_sql_update_loses_a_race()
    {
        var repository = CreateRepository();
        repository.AllowReschedule = false;
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.Equal(OriginalStart.AddHours(24), Assert.Single(repository.Links).ExpiresAt);
        Assert.Empty(repository.Notifications);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Rejects_group_suspended_walk_in_and_inconsistent_booking_shapes()
    {
        var invalidStates = new[]
        {
            CreateRepository().Booking! with { AppointmentKind = AppointmentKind.ClassSession },
            CreateRepository().Booking! with { AppointmentStatus = AppointmentStatus.Suspended },
            CreateRepository().Booking! with { Origin = AppointmentOrigin.WalkIn },
            CreateRepository().Booking! with { MaxCapacity = 2 },
            CreateRepository().Booking! with { Modality = BookingModality.DropIn },
            CreateRepository().Booking! with { EnrollmentId = 5 },
            CreateRepository().Booking! with { BookingStatus = BookingStatus.Confirmed },
            CreateRepository().Booking! with { BookingStatus = BookingStatus.Pending, ConfirmedAt = Now },
            CreateRepository().Booking! with { CancelledAt = Now }
        };

        foreach (var invalidState in invalidStates)
        {
            var repository = CreateRepository();
            repository.Booking = invalidState;
            var unitOfWork = new RescheduleUnitOfWork(repository);
            var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

            var result = await service.RescheduleIndividualAsync(7, 40, Request());

            Assert.False(result.Succeeded);
            Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
            Assert.Equal(0, unitOfWork.CommitCount);
            Assert.DoesNotContain("apply-reschedule", repository.Calls);
        }
    }

    [Fact]
    public async Task Rejects_a_booking_that_has_started_even_when_it_is_still_pending()
    {
        var repository = CreateRepository();
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = OriginalStart });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Tenant_mismatch_returns_the_same_generic_not_found_result()
    {
        var repository = CreateRepository();
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(8, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_not_found", result.Error!.Code);
        Assert.DoesNotContain("business", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["lookup-worker"], repository.Calls);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Refuses_a_requested_worker_other_than_the_reservation_worker()
    {
        var repository = CreateRepository();
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request(workerId: 99));

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.DoesNotContain("availability", repository.Calls);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Fails_closed_if_more_than_one_current_private_link_exists()
    {
        var repository = CreateRepository();
        repository.Links.Add(new AppointmentLink
        {
            Id = 51, BookingId = 40, TokenHash = [5, 6, 7], ProtectedToken = "another-token",
            ExpiresAt = OriginalStart.AddHours(25), CreatedAt = Now
        });
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.False(result.Succeeded);
        Assert.Equal("owner_booking_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.All(repository.Links, link => Assert.NotEqual(NewStart.AddHours(24), link.ExpiresAt));
        Assert.Empty(repository.Notifications);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Leaves_revoked_links_revoked_and_does_not_extend_them()
    {
        var repository = CreateRepository();
        var link = Assert.Single(repository.Links);
        link.RevokedAt = Now.AddMinutes(-1);
        var oldExpiry = link.ExpiresAt;
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        var result = await service.RescheduleIndividualAsync(7, 40, Request());

        Assert.True(result.Succeeded);
        Assert.Equal(Now.AddMinutes(-1), link.RevokedAt);
        Assert.Equal(oldExpiry, link.ExpiresAt);
        Assert.Single(repository.Notifications);
    }

    [Fact]
    public async Task Rolls_back_the_appointment_booking_link_and_email_if_outbox_insert_throws()
    {
        var repository = CreateRepository();
        repository.ThrowOnNotificationInsert = true;
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RescheduleIndividualAsync(7, 40, Request()));

        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.Equal(OriginalStart.AddMinutes(45), repository.Booking.EndsAt);
        Assert.Equal(BookingStatus.Pending, repository.Booking.BookingStatus);
        Assert.Null(repository.Booking.ConfirmedAt);
        Assert.Equal(OriginalStart.AddHours(24), Assert.Single(repository.Links).ExpiresAt);
        Assert.Empty(repository.Notifications);
        Assert.Equal(0, unitOfWork.CommitCount);
        Assert.Equal(1, unitOfWork.RollbackCount);
    }

    [Theory]
    [InlineData(10, 7, 0)]
    [InlineData(10, 0, 1)]
    [InlineData(10, 0, 30)]
    public async Task Requires_a_future_start_on_the_fifteen_minute_grid(int hour, int minute, int second)
    {
        var repository = CreateRepository();
        var unitOfWork = new RescheduleUnitOfWork(repository);
        var service = CreateService(repository, unitOfWork, new FakeClock { Now = Now });
        var invalidStart = new DateTime(2026, 3, 12, hour, minute, second);

        var result = await service.RescheduleIndividualAsync(7, 40, Request(invalidStart));

        Assert.False(result.Succeeded);
        Assert.Equal("owner_reschedule_invalid_time", result.Error!.Code);
        Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
        Assert.Equal(0, unitOfWork.CommitCount);
    }

    private static FakeOwnerRescheduleRepository CreateRepository()
    {
        var repository = new FakeOwnerRescheduleRepository
        {
            Booking = new OwnerRescheduleBooking(
                BusinessId: 7,
                BookingId: 40,
                AppointmentId: 30,
                WorkerId: 2,
                AppointmentKind: AppointmentKind.Individual,
                AppointmentStatus: AppointmentStatus.Scheduled,
                Origin: AppointmentOrigin.Reserved,
                MaxCapacity: 1,
                Modality: null,
                EnrollmentId: null,
                BookingStatus: BookingStatus.Pending,
                StartsAt: OriginalStart,
                EndsAt: OriginalStart.AddMinutes(45),
                ConfirmedAt: null,
                CancelledAt: null,
                CancelledBy: null,
                AttendanceRecordedAt: null,
                AbsenceSource: null,
                ServiceDurationMinutes: 45,
                BusinessName: "A&B <Salon>",
                ClientFirstName: "Sol &",
                ClientEmail: "sol@example.com",
                ServiceName: "Color <red>",
                WorkerName: "Ana & Paz"),
            Snapshot = new BookingAvailabilitySnapshot(
                Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
                {
                    WorkerId = 2, DayOfWeek = day, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0)
                }).ToArray(), [], [], [])
        };
        repository.Links.Add(new AppointmentLink
        {
            Id = 50, BookingId = 40, TokenHash = [1, 2, 3, 4], ProtectedToken = "protected-existing-token",
            ExpiresAt = OriginalStart.AddHours(24), CreatedAt = Now
        });
        return repository;
    }

    private static OwnerRescheduleService CreateService(
        FakeOwnerRescheduleRepository repository, RescheduleUnitOfWork unitOfWork, FakeClock clock) =>
        new(repository, unitOfWork, clock);

    private static OwnerRescheduleRequest Request(DateTime? startsAt = null, int workerId = 2) =>
        new(workerId, startsAt ?? NewStart);

    private sealed class RescheduleUnitOfWork(FakeOwnerRescheduleRepository repository) : IUnitOfWork
    {
        public FakeOwnerRescheduleRepository Repository { get; } = repository;
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public Action? BeforeCommit { get; set; }

        public Task<ITransaction> BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default) =>
            Task.FromResult<ITransaction>(new Transaction(this));

        private sealed class Transaction(RescheduleUnitOfWork owner) : ITransaction
        {
            private bool _committed;

            public Task CommitAsync(CancellationToken ct = default)
            {
                owner.BeforeCommit?.Invoke();
                owner.Repository.CommitPendingChanges();
                owner.CommitCount++;
                _committed = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!_committed)
                {
                    owner.Repository.DiscardPendingChanges();
                    owner.RollbackCount++;
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
