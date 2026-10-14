using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Classes;

// T21.3 business core (RF-22): the authenticated client cancels their own drop-in class annotation from the panel.
// The identity comes only from the (BusinessId, ClientId) pair of the caller's cookie, never from an email, a form
// clientId or a private link. A booking of another ficha or another tenant, a fixed annotation and an individual turn
// are all the same generic "not found"; a class that is suspended, started or past its exact start, and an annotation
// that is attended, absent or already cancelled, are rejected without writing. One transaction, one clock read, and
// the lock order the owner class actions use: session row first, annotation second.
public class ClientClassCancellationServiceTests
{
    private readonly FakeClassDropInRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();
    private readonly ClientClassCancellationService _service;

    private int _nextAppointmentId = 100;

    public ClientClassCancellationServiceTests()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday
        _service = new ClientClassCancellationService(_repository, _unitOfWork, _clock);
    }

    private Task<WorkUp.BusinessLogic.Common.Result> CancelAsync(
        int bookingId, int clientId = 42, int businessId = 1) =>
        _service.CancelOwnDropInAsync(businessId, clientId, bookingId);

    /// <summary>
    /// An upcoming (or not) class session holding exactly the annotation under test, registered both as a session row
    /// and as a cancellable drop-in, so the fake resolves the owning ficha exactly like the real reads do.
    /// </summary>
    private int AddDropInSessionAt(
        DateTime startsAt,
        int bookingId = 500,
        int clientId = 42,
        string clientName = "Ana Perez",
        BookingStatus bookingStatus = BookingStatus.Pending,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        int businessId = 1,
        AppointmentKind kind = AppointmentKind.ClassSession,
        BookingModality? modality = BookingModality.DropIn,
        int maxCapacity = 8)
    {
        var appointmentId = _nextAppointmentId++;
        _repository.Sessions.Add(new FakeClassDropInSession(
            businessId,
            new ClassSessionRow(
                appointmentId, 7, 3, startsAt, maxCapacity, kind, status,
                [new ClassSessionBooking(clientId, bookingStatus, bookingId, modality, clientName)])));

        _repository.DropInBookings.Add(new FakeClassDropInBooking(
            businessId,
            new DropInCancellationState(
                bookingId, appointmentId, startsAt, kind, status, bookingStatus, modality)));
        return appointmentId;
    }

    private void AddClient(int businessId, int clientId) =>
        _repository.Clients.Add(
            new FakeClassDropInClient(businessId, new DropInClient(clientId, "ana@example.com", "Ana", "Perez")));

    [Fact]
    public async Task Cancel_marks_the_own_annotation_cancelled_by_the_client_and_commits_once()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1), bookingId: 500, clientId: 42);

        var result = await CancelAsync(500);

        Assert.True(result.Succeeded);
        var stored = Assert.Single(_repository.DropInBookings);
        Assert.Equal(BookingStatus.Cancelled, stored.State.Status);
        Assert.Equal(CancelledBy.Client, stored.CancelledBy);
        Assert.Equal(_clock.Now, stored.CancelledAt);

        // The lock-free probe discovers the class, the session row is locked first and the own annotation second: the
        // same order the owner class actions use, so a competing owner action serializes instead of interleaving.
        Assert.Equal(new[] { "probe", "lock-session", "lock-client-drop-in", "cancel-client-drop-in" }, _repository.Calls);
        Assert.Equal(1, _repository.LockClientDropInCallCount);
        Assert.Equal(1, _repository.CancelClientCallCount);
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_hides_another_client_and_another_tenant_as_not_found()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1), bookingId: 500, clientId: 42);

        var crossClient = await CancelAsync(500, clientId: 43);
        var crossTenant = await CancelAsync(500, businessId: 2);
        var missing = await CancelAsync(999);

        foreach (var result in new[] { crossClient, crossTenant, missing })
        {
            Assert.False(result.Succeeded);
            Assert.Equal("client_class_booking_not_found", result.Error!.Code);
        }

        Assert.Equal(crossClient.Error!.Message, missing.Error!.Message);
        Assert.Equal(crossTenant.Error!.Message, missing.Error!.Message);
        Assert.Equal(0, _repository.LockClientDropInCallCount);
        Assert.Equal(0, _repository.CancelClientCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(3, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_hides_a_fixed_annotation_and_an_individual_turn_as_not_found()
    {
        // A fixed annotation is not a drop-in and an individual turn is not a class: neither can be cancelled here,
        // and both are answered exactly like a booking of another ficha.
        AddDropInSessionAt(
            _clock.Now.AddDays(1), bookingId: 700, clientId: 42,
            bookingStatus: BookingStatus.Confirmed, modality: BookingModality.Fixed);
        AddDropInSessionAt(
            _clock.Now.AddDays(1), bookingId: 701, clientId: 42, kind: AppointmentKind.Individual);

        var fixedResult = await CancelAsync(700);
        var individualResult = await CancelAsync(701);

        Assert.False(fixedResult.Succeeded);
        Assert.False(individualResult.Succeeded);
        Assert.Equal("client_class_booking_not_found", fixedResult.Error!.Code);
        Assert.Equal("client_class_booking_not_found", individualResult.Error!.Code);
        Assert.Equal(0, _repository.CancelClientCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(2, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_rejects_a_suspended_started_or_exact_start_class_without_writing()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1), bookingId: 500, status: AppointmentStatus.Suspended);
        AddDropInSessionAt(_clock.Now.AddMinutes(-1), bookingId: 501);
        AddDropInSessionAt(_clock.Now, bookingId: 502);

        foreach (var bookingId in new[] { 500, 501, 502 })
        {
            var result = await CancelAsync(bookingId);

            Assert.False(result.Succeeded);
            Assert.Equal("client_class_booking_unavailable", result.Error!.Code);
        }

        Assert.Equal(0, _repository.CancelClientCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(3, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_rejects_an_attended_or_absent_annotation_without_writing()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1), bookingId: 500, bookingStatus: BookingStatus.Attended);
        AddDropInSessionAt(_clock.Now.AddDays(1), bookingId: 501, bookingStatus: BookingStatus.Absent);

        var attended = await CancelAsync(500);
        var absent = await CancelAsync(501);

        Assert.False(attended.Succeeded);
        Assert.False(absent.Succeeded);
        Assert.Equal("client_class_booking_unavailable", attended.Error!.Code);
        Assert.Equal("client_class_booking_unavailable", absent.Error!.Code);
        Assert.Equal(0, _repository.CancelClientCallCount);
        Assert.Equal(2, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(2, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_rejects_a_second_cancellation_without_rewriting_the_original_instant()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1), bookingId: 500);
        var firstInstant = _clock.Now;

        var first = await CancelAsync(500);
        _clock.Now = _clock.Now.AddMinutes(5);
        var second = await CancelAsync(500);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal("client_class_booking_unavailable", second.Error!.Code);
        var stored = Assert.Single(_repository.DropInBookings);
        Assert.Equal(BookingStatus.Cancelled, stored.State.Status);
        Assert.Equal(CancelledBy.Client, stored.CancelledBy);
        Assert.Equal(firstInstant, stored.CancelledAt);
        Assert.Equal(1, _repository.CancelClientCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_frees_the_place_so_the_same_client_can_book_the_class_again()
    {
        var appointmentId = AddDropInSessionAt(
            _clock.Now.AddDays(1), bookingId: 500, clientId: 42, maxCapacity: 1);
        AddClient(1, 42);
        var dropInService = new ClassDropInService(_repository, _unitOfWork, _clock);

        var cancelled = await CancelAsync(500);
        var rebooked = await dropInService.BookForClientAsync(1, appointmentId, 42);

        Assert.True(cancelled.Succeeded);
        Assert.True(rebooked.Succeeded);
        Assert.Equal(42, Assert.Single(_repository.Inserts).ClientId);
        Assert.Equal(2, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Cancel_reads_the_clock_once_for_the_boundary_and_the_cancellation_instant()
    {
        // The class starts one second after the first read: a second read would put "now" exactly on the start, the
        // cancellation would be rejected and the instant written would differ, so a double read cannot pass.
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new ClientClassCancellationService(_repository, _unitOfWork, clock);
        AddDropInSessionAt(new DateTime(2026, 3, 10, 9, 0, 1), bookingId: 500);

        var result = await service.CancelOwnDropInAsync(1, 42, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(1, _repository.CancelClientCallCount);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0), Assert.Single(_repository.CancelClientRequests).Now);
    }

    [Fact]
    public async Task Cancel_judges_the_class_at_the_instant_the_session_lock_is_held()
    {
        // The class starts while the operation waits for the session row lock. The clock is read once the lock is
        // held, so the cancellation sees the already started class and is refused instead of cancelling it with a
        // stale "before start".
        var start = _clock.Now.AddSeconds(1);
        AddDropInSessionAt(start, bookingId: 500);
        _repository.WhileWaitingForLock = () => _clock.Now = start;

        var result = await CancelAsync(500);

        Assert.False(result.Succeeded);
        Assert.Equal("client_class_booking_unavailable", result.Error!.Code);
        Assert.Equal(0, _repository.CancelClientCallCount);
        Assert.Equal(BookingStatus.Pending, Assert.Single(_repository.DropInBookings).State.Status);
    }

    [Theory]
    [InlineData(0, 42, 500)]
    [InlineData(1, 0, 500)]
    [InlineData(1, 42, 0)]
    public async Task Cancel_rejects_an_invalid_request_without_reading_storage(int businessId, int clientId, int bookingId)
    {
        var result = await _service.CancelOwnDropInAsync(businessId, clientId, bookingId);

        Assert.False(result.Succeeded);
        Assert.Equal("client_class_booking_not_found", result.Error!.Code);
        Assert.Equal(0, _repository.FindOwnProbeCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    /// <summary>A clock that advances one second per read, so a second read changes the outcome and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }

        public DateTime Now => first.AddSeconds(ReadCount++);
    }
}
