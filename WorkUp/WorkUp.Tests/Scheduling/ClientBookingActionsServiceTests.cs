using WorkUp.BusinessLogic.Common;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// T21.2 confirm/cancel of an individual turn from the authenticated client panel (RF-08, RF-22). Every booking is
/// keyed by the trusted (businessId, clientId, bookingId) triple of the client session cookie, the clock comes from
/// <see cref="FakeClock"/>, and the fake repository applies the same guard predicates as the SQL adapter so a lost
/// action writes nothing.
/// </summary>
public sealed class ClientBookingActionsServiceTests
{
    private const int BusinessId = 1;
    private const int ClientId = 7;
    private const int OtherClientId = 8;
    private const int OtherBusinessId = 2;
    private const int BookingId = 12;
    private const int WorkerId = 2;
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    private sealed record Fixture(
        ClientBookingActionsService Service,
        FakeClientBookingActionsRepository Repository,
        FakeUnitOfWork UnitOfWork,
        FakeClock Clock);

    private static ClientBookingActionState State(
        BookingStatus status = BookingStatus.Pending,
        DateTime? confirmedAt = null,
        DateTime? startsAt = null,
        AppointmentKind kind = AppointmentKind.Individual,
        AppointmentStatus appointmentStatus = AppointmentStatus.Scheduled,
        AppointmentOrigin? origin = AppointmentOrigin.Reserved,
        int maxCapacity = 1,
        BookingModality? modality = null,
        int? enrollmentId = null,
        DateTime? cancelledAt = null,
        CancelledBy? cancelledBy = null,
        DateTime? attendanceRecordedAt = null,
        AbsenceSource? absenceSource = null,
        int workerId = WorkerId)
    {
        var start = startsAt ?? Now.AddHours(2);
        return new ClientBookingActionState(
            BusinessId, BookingId, BookingId + 100, workerId, start, start.AddMinutes(30),
            kind, appointmentStatus, origin, maxCapacity, modality, enrollmentId,
            status, confirmedAt, cancelledAt, cancelledBy, attendanceRecordedAt, absenceSource);
    }

    private static Fixture Built(ClientBookingActionState? state = null, DateTime? now = null)
    {
        var repository = new FakeClientBookingActionsRepository();
        var unitOfWork = new FakeUnitOfWork();
        var clock = new FakeClock { Now = now ?? Now };
        var service = new ClientBookingActionsService(repository, unitOfWork, clock);
        if (state is not null)
            repository.Add(BusinessId, ClientId, state);
        return new Fixture(service, repository, unitOfWork, clock);
    }

    private static ClientBookingActionState? Stored(Fixture fixture) =>
        fixture.Repository.Get(BusinessId, ClientId, BookingId);

    [Fact]
    public async Task Confirm_turns_the_authenticated_clients_pending_turn_into_confirmed_with_the_clock()
    {
        var before = State();
        var fixture = Built(before);

        var result = await fixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, BookingId);

        Assert.True(result.Succeeded);
        Assert.Equal(before with { BookingStatus = BookingStatus.Confirmed, ConfirmedAt = Now }, Stored(fixture));
        Assert.Equal(1, fixture.Repository.ConfirmWriteCount);
        Assert.Equal(0, fixture.Repository.CancelWriteCount);
        Assert.Equal(1, fixture.UnitOfWork.BeginCount);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Confirming_again_before_the_start_is_idempotent_and_keeps_the_original_ConfirmedAt()
    {
        var firstConfirmation = Now.AddMinutes(-30);
        var fixture = Built(State(BookingStatus.Confirmed, firstConfirmation));

        var result = await fixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, BookingId);

        Assert.True(result.Succeeded);
        Assert.Equal(firstConfirmation, Stored(fixture)!.ConfirmedAt);
        Assert.Equal(BookingStatus.Confirmed, Stored(fixture)!.BookingStatus);
        Assert.Equal(0, fixture.Repository.ConfirmWriteCount);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(BookingStatus.Pending, null)]
    [InlineData(BookingStatus.Confirmed, "2026-03-10T08:30:00")]
    public async Task Cancel_turns_own_active_individual_turn_into_cancelled_by_client_and_changes_nothing_else(
        BookingStatus status, string? confirmedAt)
    {
        var before = State(status, confirmedAt is null ? null : DateTime.Parse(confirmedAt));
        var fixture = Built(before);

        var result = await fixture.Service.CancelAsync(BusinessId, ClientId, BookingId);

        Assert.True(result.Succeeded);
        var after = Stored(fixture)!;
        Assert.Equal(
            before with { BookingStatus = BookingStatus.Cancelled, CancelledBy = CancelledBy.Client, CancelledAt = Now },
            after);
        Assert.Equal(1, fixture.Repository.CancelWriteCount);
        Assert.Equal(0, fixture.Repository.ConfirmWriteCount);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
    }

    [Theory]
    [InlineData("confirm", "2026-03-10T11:00:00")]
    [InlineData("confirm", "2026-03-10T11:00:01")]
    [InlineData("cancel", "2026-03-10T11:00:00")]
    [InlineData("cancel", "2026-03-10T11:00:01")]
    public async Task Refuses_both_actions_at_or_after_the_exact_start(string action, string now)
    {
        var fixture = Built(State(startsAt: new DateTime(2026, 3, 10, 11, 0, 0)), DateTime.Parse(now));

        var result = action == "confirm"
            ? await fixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, BookingId)
            : await fixture.Service.CancelAsync(BusinessId, ClientId, BookingId);

        Assert.False(result.Succeeded);
        Assert.Equal("client_booking_unavailable", result.Error!.Code);
        Assert.Equal(0, fixture.Repository.ConfirmWriteCount);
        Assert.Equal(0, fixture.Repository.CancelWriteCount);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Another_fichas_or_another_tenants_row_yields_the_same_not_found_without_writes()
    {
        var fixture = Built();
        fixture.Repository.Add(BusinessId, OtherClientId, State());
        fixture.Repository.Add(OtherBusinessId, ClientId, State());

        var attempts = new List<Result>
        {
            await fixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, BookingId),
            await fixture.Service.CancelAsync(BusinessId, ClientId, BookingId),
            await fixture.Service.ConfirmIntentionAsync(BusinessId, BookingId, BookingId),
            await fixture.Service.CancelAsync(OtherBusinessId, OtherClientId, BookingId)
        };

        foreach (var result in attempts)
        {
            Assert.False(result.Succeeded);
            Assert.Equal("client_booking_not_found", result.Error!.Code);
        }

        Assert.Equal(BookingStatus.Pending, fixture.Repository.Get(BusinessId, OtherClientId, BookingId)!.BookingStatus);
        Assert.Equal(BookingStatus.Pending, fixture.Repository.Get(OtherBusinessId, ClientId, BookingId)!.BookingStatus);
        Assert.Equal(0, fixture.Repository.ConfirmWriteCount);
        Assert.Equal(0, fixture.Repository.CancelWriteCount);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Non_positive_identifiers_are_not_found_before_any_lock_or_transaction()
    {
        var fixture = Built(State());

        var results = new List<Result>
        {
            await fixture.Service.ConfirmIntentionAsync(0, ClientId, BookingId),
            await fixture.Service.ConfirmIntentionAsync(BusinessId, 0, BookingId),
            await fixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, 0),
            await fixture.Service.CancelAsync(-1, ClientId, BookingId),
            await fixture.Service.CancelAsync(BusinessId, -1, BookingId),
            await fixture.Service.CancelAsync(BusinessId, ClientId, -1)
        };

        Assert.All(results, result => Assert.Equal("client_booking_not_found", result.Error!.Code));
        Assert.Empty(fixture.Repository.Calls);
        Assert.Equal(0, fixture.UnitOfWork.BeginCount);
    }

    [Fact]
    public async Task Refuses_wrong_kind_origin_capacity_modality_or_enrollment_for_both_actions()
    {
        var invalidStates = new[]
        {
            State(kind: AppointmentKind.ClassSession, modality: BookingModality.DropIn),
            State(origin: AppointmentOrigin.WalkIn),
            State(maxCapacity: 2),
            State(modality: BookingModality.DropIn),
            State(enrollmentId: 5),
            State(appointmentStatus: AppointmentStatus.Suspended)
        };

        foreach (var state in invalidStates)
        {
            var confirmFixture = Built(state);
            var cancelFixture = Built(state);

            var confirmed = await confirmFixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, BookingId);
            var cancelled = await cancelFixture.Service.CancelAsync(BusinessId, ClientId, BookingId);

            Assert.False(confirmed.Succeeded);
            Assert.False(cancelled.Succeeded);
            Assert.Equal("client_booking_unavailable", confirmed.Error!.Code);
            Assert.Equal("client_booking_unavailable", cancelled.Error!.Code);
            Assert.Equal(0, confirmFixture.UnitOfWork.CommitCount);
            Assert.Equal(0, cancelFixture.UnitOfWork.CommitCount);
            Assert.Equal(state, confirmFixture.Repository.Get(BusinessId, ClientId, BookingId));
            Assert.Equal(state, cancelFixture.Repository.Get(BusinessId, ClientId, BookingId));
        }
    }

    [Fact]
    public async Task Refuses_attended_absent_cancelled_and_inconsistent_bookings_for_both_actions()
    {
        var invalidStates = new[]
        {
            State(BookingStatus.Attended, attendanceRecordedAt: Now.AddHours(-1)),
            State(BookingStatus.Absent, absenceSource: AbsenceSource.ClientLink, attendanceRecordedAt: Now.AddHours(-1)),
            State(BookingStatus.Cancelled, cancelledAt: Now.AddHours(-1), cancelledBy: CancelledBy.Client),
            State(BookingStatus.Pending, confirmedAt: Now.AddHours(-1)),
            State(BookingStatus.Confirmed),
            State(absenceSource: AbsenceSource.ClientLink)
        };

        foreach (var state in invalidStates)
        {
            var fixture = Built(state);

            var confirmed = await fixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, BookingId);
            var cancelled = await fixture.Service.CancelAsync(BusinessId, ClientId, BookingId);

            Assert.Equal("client_booking_unavailable", confirmed.Error!.Code);
            Assert.Equal("client_booking_unavailable", cancelled.Error!.Code);
            Assert.Equal(0, fixture.Repository.ConfirmWriteCount);
            Assert.Equal(0, fixture.Repository.CancelWriteCount);
            Assert.Equal(0, fixture.UnitOfWork.CommitCount);
            Assert.Equal(state, Stored(fixture));
        }
    }

    [Fact]
    public async Task Confirm_refuses_and_writes_nothing_when_a_cancellation_wins_between_lock_and_update()
    {
        var fixture = Built(State());
        fixture.Repository.OnLocked = () => fixture.Repository.Mutate(BusinessId, ClientId, BookingId, state => state with
        {
            BookingStatus = BookingStatus.Cancelled,
            CancelledBy = CancelledBy.Client,
            CancelledAt = Now
        });

        var result = await fixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, BookingId);

        Assert.False(result.Succeeded);
        Assert.Equal("client_booking_unavailable", result.Error!.Code);
        Assert.Equal(0, fixture.Repository.ConfirmWriteCount);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
        Assert.Equal(BookingStatus.Cancelled, Stored(fixture)!.BookingStatus);
    }

    [Fact]
    public async Task Cancel_refuses_and_writes_nothing_when_another_actor_already_cancelled_under_the_lock()
    {
        var fixture = Built(State());
        fixture.Repository.OnLocked = () => fixture.Repository.Mutate(BusinessId, ClientId, BookingId, state => state with
        {
            BookingStatus = BookingStatus.Cancelled,
            CancelledBy = CancelledBy.Owner,
            CancelledAt = Now.AddSeconds(-1)
        });

        var result = await fixture.Service.CancelAsync(BusinessId, ClientId, BookingId);

        Assert.False(result.Succeeded);
        Assert.Equal("client_booking_unavailable", result.Error!.Code);
        Assert.Equal(0, fixture.Repository.CancelWriteCount);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
        Assert.Equal(CancelledBy.Owner, Stored(fixture)!.CancelledBy);
    }

    [Fact]
    public async Task Cancel_is_still_applied_when_a_confirmation_wins_between_lock_and_update()
    {
        var fixture = Built(State());
        fixture.Repository.OnLocked = () => fixture.Repository.Mutate(BusinessId, ClientId, BookingId, state => state with
        {
            BookingStatus = BookingStatus.Confirmed,
            ConfirmedAt = Now.AddSeconds(-1)
        });

        var result = await fixture.Service.CancelAsync(BusinessId, ClientId, BookingId);

        Assert.True(result.Succeeded);
        Assert.Equal(1, fixture.Repository.CancelWriteCount);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
        var after = Stored(fixture)!;
        Assert.Equal(BookingStatus.Cancelled, after.BookingStatus);
        Assert.Equal(CancelledBy.Client, after.CancelledBy);
        Assert.Equal(Now, after.CancelledAt);
        Assert.Equal(Now.AddSeconds(-1), after.ConfirmedAt);
    }

    [Fact]
    public async Task Locks_the_worker_before_the_booking_inside_a_single_transaction()
    {
        var fixture = Built(State());

        var result = await fixture.Service.ConfirmIntentionAsync(BusinessId, ClientId, BookingId);

        Assert.True(result.Succeeded);
        Assert.Equal(["worker-id", "lock-worker", "lock-booking", "confirm"], fixture.Repository.Calls);
        Assert.Equal(1, fixture.UnitOfWork.BeginCount);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
    }
}
