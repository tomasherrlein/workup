using System.Data;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// T21.4 reschedule of an individual turn from the authenticated client panel (RF-09). Every call carries the
/// trusted (businessId, clientId) pair of the client session cookie plus the booking id; the service never accepts a
/// worker, an email or a private-link token, and it reuses the owner transaction, availability, link and notification
/// core instead of duplicating it. The fake repository scopes its rows by both ids exactly like the SQL adapter, so a
/// foreign ficha or tenant looks like a row that does not exist and never reaches availability or the outbox.
/// </summary>
public sealed class ClientRescheduleTests
{
    private const int BusinessId = 1;
    private const int ClientId = 7;
    private const int OtherClientId = 8;
    private const int OtherBusinessId = 2;
    private const int BookingId = 12;
    private const int WorkerId = 2;
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime OriginalStart = new(2026, 3, 11, 10, 0, 0);
    private static readonly DateTime NewStart = new(2026, 3, 12, 10, 15, 0);

    private sealed record Fixture(
        OwnerRescheduleService Service,
        FakeOwnerRescheduleRepository Repository,
        ClientRescheduleUnitOfWork UnitOfWork,
        FakeClock Clock);

    private static Fixture Built(
        DateTime? now = null,
        BookingStatus status = BookingStatus.Pending,
        DateTime? confirmedAt = null)
    {
        var repository = CreateRepository(status, confirmedAt);
        var unitOfWork = new ClientRescheduleUnitOfWork(repository);
        var clock = new FakeClock { Now = now ?? Now };
        return new Fixture(new OwnerRescheduleService(repository, unitOfWork, clock), repository, unitOfWork, clock);
    }

    private static FakeOwnerRescheduleRepository CreateRepository(
        BookingStatus status = BookingStatus.Pending, DateTime? confirmedAt = null)
    {
        var repository = new FakeOwnerRescheduleRepository
        {
            BookingClientId = ClientId,
            WorkerBusinessId = BusinessId,
            Booking = new OwnerRescheduleBooking(
                BusinessId: BusinessId,
                BookingId: BookingId,
                AppointmentId: 30,
                WorkerId: WorkerId,
                AppointmentKind: AppointmentKind.Individual,
                AppointmentStatus: AppointmentStatus.Scheduled,
                Origin: AppointmentOrigin.Reserved,
                MaxCapacity: 1,
                Modality: null,
                EnrollmentId: null,
                BookingStatus: status,
                StartsAt: OriginalStart,
                EndsAt: OriginalStart.AddMinutes(45),
                ConfirmedAt: confirmedAt,
                CancelledAt: null,
                CancelledBy: null,
                AttendanceRecordedAt: null,
                AbsenceSource: null,
                ServiceDurationMinutes: 45,
                BusinessName: "Demo",
                ClientFirstName: "Sol",
                ClientEmail: "sol@example.invalid",
                ServiceName: "Corte",
                WorkerName: "Ana"),
            Snapshot = new BookingAvailabilitySnapshot(
                Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
                {
                    WorkerId = WorkerId, DayOfWeek = day, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0)
                }).ToArray(), [], [], [])
        };
        repository.Links.Add(new AppointmentLink
        {
            Id = 50, BookingId = BookingId, TokenHash = [1, 2, 3, 4], ProtectedToken = "protected-existing-token",
            ExpiresAt = OriginalStart.AddHours(24), CreatedAt = Now
        });
        return repository;
    }

    [Fact]
    public async Task Reschedules_the_authenticated_clients_own_pending_turn_and_confirms_it()
    {
        var fixture = Built();
        var repository = fixture.Repository;
        fixture.UnitOfWork.BeforeCommit = () =>
        {
            // Nothing is visible to anyone else until the commit: the original start, the pending status and the
            // current link expiry are all still the old ones, and the outbox has not been touched.
            Assert.Equal(OriginalStart, repository.Booking!.StartsAt);
            Assert.Equal(BookingStatus.Pending, repository.Booking.BookingStatus);
            Assert.Equal(OriginalStart.AddHours(24), Assert.Single(repository.Links).ExpiresAt);
            Assert.Empty(repository.Notifications);
        };

        var result = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart);

        Assert.True(result.Succeeded);
        Assert.Equal(NewStart, result.Confirmation.StartsAt);
        Assert.Equal(NewStart.AddMinutes(45), result.Confirmation.EndsAt);
        Assert.Equal(NewStart, repository.Booking!.StartsAt);
        Assert.Equal(NewStart.AddMinutes(45), repository.Booking.EndsAt);
        Assert.Equal(BookingStatus.Confirmed, repository.Booking.BookingStatus);
        Assert.Equal(Now, repository.Booking.ConfirmedAt);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
        Assert.Equal(
            ["lookup-worker", "lock-worker", "lock-booking", "availability", "active-links", "apply-reschedule",
             "update-link-expiry", "insert-notification"],
            repository.Calls);
        Assert.Equal(WorkerId, repository.AvailabilityWorkerId);

        // The private link keeps its token material: only its expiry moves to the new start plus 24 hours (RF-09).
        var link = Assert.Single(repository.Links);
        Assert.Equal(NewStart.AddHours(24), link.ExpiresAt);
        Assert.Equal([1, 2, 3, 4], link.TokenHash);
        Assert.Equal("protected-existing-token", link.ProtectedToken);

        var notification = Assert.Single(repository.Notifications);
        Assert.Equal(NotificationType.Rescheduled, notification.Type);
        Assert.Equal("sol@example.invalid", notification.Recipient);
        Assert.StartsWith($"rescheduled:booking:{BookingId}:", notification.DedupKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeps_the_original_ConfirmedAt_when_the_clients_turn_was_already_confirmed()
    {
        // T27 subfinding: a move must not rewrite the confirmation the client already gave.
        var originalConfirmation = Now.AddHours(-1);
        var fixture = Built(status: BookingStatus.Confirmed, confirmedAt: originalConfirmation);

        var result = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart);

        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Confirmed, fixture.Repository.Booking!.BookingStatus);
        Assert.Equal(originalConfirmation, fixture.Repository.Booking.ConfirmedAt);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
        Assert.Single(fixture.Repository.Notifications);
    }

    [Fact]
    public async Task Another_fichas_or_another_tenants_row_is_a_generic_not_found_without_locks_or_availability()
    {
        var fixture = Built();
        fixture.Repository.BookingClientId = OtherClientId;

        var foreignFicha = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart);

        Assert.False(foreignFicha.Succeeded);
        Assert.Equal("client_booking_not_found", foreignFicha.Error!.Code);
        Assert.Equal("No encontramos ese turno.", foreignFicha.Error.Message);
        Assert.Equal(["lookup-worker"], fixture.Repository.Calls);
        Assert.Null(fixture.Repository.AvailabilityWorkerId);
        Assert.Empty(fixture.Repository.Notifications);
        Assert.Equal(OriginalStart, fixture.Repository.Booking!.StartsAt);
        Assert.Equal(OriginalStart.AddHours(24), Assert.Single(fixture.Repository.Links).ExpiresAt);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);

        fixture.Repository.Booking = fixture.Repository.Booking! with { BusinessId = OtherBusinessId };
        fixture.Repository.Calls.Clear();

        var foreignTenant = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart);

        Assert.False(foreignTenant.Succeeded);
        Assert.Equal("client_booking_not_found", foreignTenant.Error!.Code);
        Assert.Equal(["lookup-worker"], fixture.Repository.Calls);
        Assert.Empty(fixture.Repository.Notifications);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Never_accepts_or_probes_a_worker_other_than_the_locked_bookings_worker()
    {
        // The client API takes no worker: the only worker reachable is the one the locked booking already has.
        var fixture = Built();
        fixture.Repository.LookupWorkerIdOverride = 99;

        var result = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart);

        Assert.False(result.Succeeded);
        Assert.Equal("client_booking_unavailable", result.Error!.Code);
        Assert.DoesNotContain("availability", fixture.Repository.Calls);
        Assert.Null(fixture.Repository.AvailabilityWorkerId);
        Assert.Empty(fixture.Repository.Notifications);
        Assert.Equal(OriginalStart, fixture.Repository.Booking!.StartsAt);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Non_positive_identifiers_are_not_found_before_any_lock_or_transaction()
    {
        var fixture = Built();

        var attempts = new[]
        {
            await fixture.Service.RescheduleOwnIndividualAsync(0, ClientId, BookingId, NewStart),
            await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, 0, BookingId, NewStart),
            await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, 0, NewStart),
            await fixture.Service.RescheduleOwnIndividualAsync(-1, -1, -1, NewStart)
        };

        Assert.All(attempts, attempt =>
        {
            Assert.False(attempt.Succeeded);
            Assert.Equal("client_booking_not_found", attempt.Error!.Code);
        });
        Assert.Empty(fixture.Repository.Calls);
        Assert.Equal(0, fixture.UnitOfWork.BeginCount);
    }

    [Fact]
    public async Task Rejects_the_current_start_without_writing()
    {
        var fixture = Built();

        var result = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, OriginalStart);

        Assert.False(result.Succeeded);
        Assert.Equal("client_reschedule_same_slot", result.Error!.Code);
        Assert.DoesNotContain("apply-reschedule", fixture.Repository.Calls);
        Assert.Empty(fixture.Repository.Notifications);
        Assert.Equal(OriginalStart, fixture.Repository.Booking!.StartsAt);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(10, 7, 0)]
    [InlineData(10, 0, 1)]
    [InlineData(10, 0, 30)]
    public async Task Requires_a_future_start_on_the_fifteen_minute_grid(int hour, int minute, int second)
    {
        var fixture = Built();
        var invalidStart = new DateTime(2026, 3, 12, hour, minute, second);

        var result = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, invalidStart);

        Assert.False(result.Succeeded);
        Assert.Equal("client_reschedule_invalid_time", result.Error!.Code);
        Assert.Equal(OriginalStart, fixture.Repository.Booking!.StartsAt);
        Assert.Equal(0, fixture.UnitOfWork.BeginCount);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Refuses_a_start_that_is_no_longer_in_the_future()
    {
        var fixture = Built(now: NewStart);

        var result = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart);

        Assert.False(result.Succeeded);
        Assert.Equal("client_booking_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, fixture.Repository.Booking!.StartsAt);
        Assert.Equal(BookingStatus.Pending, fixture.Repository.Booking.BookingStatus);
        Assert.Empty(fixture.Repository.Notifications);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Refuses_an_occupied_slot_without_releasing_the_original_reservation()
    {
        var fixture = Built();
        fixture.Repository.Snapshot = fixture.Repository.Snapshot with
        {
            Appointments =
            [
                new Appointment
                {
                    Id = 99, BusinessId = BusinessId, WorkerId = WorkerId, ServiceId = 3,
                    Kind = AppointmentKind.Individual, Origin = AppointmentOrigin.Reserved,
                    Status = AppointmentStatus.Scheduled, StartsAt = NewStart, EndsAt = NewStart.AddMinutes(30)
                }
            ],
            Bookings = [new Booking { Id = 100, BusinessId = BusinessId, AppointmentId = 99, Status = BookingStatus.Pending }]
        };

        var result = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart);

        Assert.False(result.Succeeded);
        Assert.Equal("client_reschedule_slot_unavailable", result.Error!.Code);
        Assert.Equal(OriginalStart, fixture.Repository.Booking!.StartsAt);
        Assert.Equal(BookingStatus.Pending, fixture.Repository.Booking.BookingStatus);
        Assert.DoesNotContain("apply-reschedule", fixture.Repository.Calls);
        Assert.Equal(OriginalStart.AddHours(24), Assert.Single(fixture.Repository.Links).ExpiresAt);
        Assert.Empty(fixture.Repository.Notifications);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Rolls_back_the_appointment_booking_link_and_email_when_the_outbox_insert_throws()
    {
        var fixture = Built();
        fixture.Repository.ThrowOnNotificationInsert = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart));

        Assert.Equal(OriginalStart, fixture.Repository.Booking!.StartsAt);
        Assert.Equal(OriginalStart.AddMinutes(45), fixture.Repository.Booking.EndsAt);
        Assert.Equal(BookingStatus.Pending, fixture.Repository.Booking.BookingStatus);
        Assert.Null(fixture.Repository.Booking.ConfirmedAt);
        Assert.Equal(OriginalStart.AddHours(24), Assert.Single(fixture.Repository.Links).ExpiresAt);
        Assert.Equal([1, 2, 3, 4], fixture.Repository.Links[0].TokenHash);
        Assert.Equal("protected-existing-token", fixture.Repository.Links[0].ProtectedToken);
        Assert.Empty(fixture.Repository.Notifications);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
        Assert.Equal(1, fixture.UnitOfWork.RollbackCount);
    }

    [Fact]
    public async Task Options_are_scoped_and_offer_only_the_same_workers_free_starts()
    {
        var fixture = Built();
        var repository = fixture.Repository;
        repository.Snapshot = repository.Snapshot with
        {
            Appointments =
            [
                new Appointment
                {
                    Id = 30, BusinessId = BusinessId, WorkerId = WorkerId, ServiceId = 3,
                    Kind = AppointmentKind.Individual, Origin = AppointmentOrigin.Reserved,
                    Status = AppointmentStatus.Scheduled, StartsAt = OriginalStart, EndsAt = OriginalStart.AddMinutes(45)
                },
                new Appointment
                {
                    Id = 99, BusinessId = BusinessId, WorkerId = WorkerId, ServiceId = 3,
                    Kind = AppointmentKind.Individual, Origin = AppointmentOrigin.Reserved,
                    Status = AppointmentStatus.Scheduled, StartsAt = new DateTime(2026, 3, 11, 12, 0, 0),
                    EndsAt = new DateTime(2026, 3, 11, 12, 30, 0)
                }
            ],
            Bookings =
            [
                new Booking { Id = BookingId, BusinessId = BusinessId, AppointmentId = 30, Status = BookingStatus.Pending },
                new Booking { Id = 100, BusinessId = BusinessId, AppointmentId = 99, Status = BookingStatus.Pending }
            ]
        };

        var options = await fixture.Service.GetOwnOptionsAsync(
            BusinessId, ClientId, BookingId, DateOnly.FromDateTime(OriginalStart));

        Assert.NotNull(options);
        Assert.Equal(OriginalStart, options.CurrentStartsAt);
        Assert.Contains(new TimeOnly(10, 15), options.AvailableStartTimes);
        Assert.DoesNotContain(new TimeOnly(10, 0), options.AvailableStartTimes);
        Assert.DoesNotContain(new TimeOnly(12, 0), options.AvailableStartTimes);
        Assert.DoesNotContain("lock-worker", repository.Calls);
        Assert.DoesNotContain("lock-booking", repository.Calls);
    }

    [Fact]
    public async Task Options_are_null_for_another_fichas_another_tenants_or_a_started_turn()
    {
        var date = DateOnly.FromDateTime(OriginalStart);

        var foreignFicha = Built();
        foreignFicha.Repository.BookingClientId = OtherClientId;
        Assert.Null(await foreignFicha.Service.GetOwnOptionsAsync(BusinessId, ClientId, BookingId, date));
        Assert.Equal(["read-booking"], foreignFicha.Repository.Calls);

        var foreignTenant = Built();
        Assert.Null(await foreignTenant.Service.GetOwnOptionsAsync(OtherBusinessId, ClientId, BookingId, date));

        var started = Built(now: OriginalStart);
        Assert.Null(await started.Service.GetOwnOptionsAsync(BusinessId, ClientId, BookingId, date));

        var nonPositive = Built();
        Assert.Null(await nonPositive.Service.GetOwnOptionsAsync(0, ClientId, BookingId, date));
        Assert.Null(await nonPositive.Service.GetOwnOptionsAsync(BusinessId, 0, BookingId, date));
        Assert.Null(await nonPositive.Service.GetOwnOptionsAsync(BusinessId, ClientId, 0, date));
        Assert.Empty(nonPositive.Repository.Calls);
    }

    [Fact]
    public async Task Same_worker_requirement_rejects_a_group_suspended_walk_in_or_inconsistent_shape()
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
            CreateRepository().Booking! with { CancelledAt = Now },
            CreateRepository().Booking! with { CancelledBy = CancelledBy.Client }
        };

        foreach (var invalidState in invalidStates)
        {
            var fixture = Built();
            fixture.Repository.Booking = invalidState;

            var result = await fixture.Service.RescheduleOwnIndividualAsync(BusinessId, ClientId, BookingId, NewStart);

            Assert.False(result.Succeeded);
            Assert.Equal("client_booking_unavailable", result.Error!.Code);
            Assert.Equal(OriginalStart, fixture.Repository.Booking!.StartsAt);
            Assert.DoesNotContain("apply-reschedule", fixture.Repository.Calls);
            Assert.Empty(fixture.Repository.Notifications);
            Assert.Equal(0, fixture.UnitOfWork.CommitCount);
        }
    }

    private sealed class ClientRescheduleUnitOfWork(FakeOwnerRescheduleRepository repository) : IUnitOfWork
    {
        public FakeOwnerRescheduleRepository Repository { get; } = repository;
        public int BeginCount { get; private set; }
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public Action? BeforeCommit { get; set; }

        public Task<ITransaction> BeginAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default)
        {
            BeginCount++;
            return Task.FromResult<ITransaction>(new Transaction(this));
        }

        private sealed class Transaction(ClientRescheduleUnitOfWork owner) : ITransaction
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
