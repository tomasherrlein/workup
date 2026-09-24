using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Classes;

// T14 business core (RF-07 cupo > 1, RF-22, D-16, D-23, D-25): booking a drop-in into an already generated class
// session. Only a `Programado` session with a future start and a free place can be booked, and the occupancy counts
// every booking that is not `Cancelado`, the fixed ones included. The last place is protected by the repository's
// guarded insert inside one transaction, and the immediate email is only queued when the session starts in less
// than 24 hours (D-22).
public class ClassDropInServiceTests
{
    private readonly FakeClassDropInRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();
    private readonly ClassDropInService _service;

    private int _nextAppointmentId = 100;

    public ClassDropInServiceTests()
    {
        _clock.Now = new DateTime(2026, 3, 10, 9, 0, 0); // Tuesday
        _service = new ClassDropInService(_repository, _unitOfWork, _clock);
    }

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    private static DropInBookingRequest Request(
        int appointmentId,
        string email = "ana@example.com",
        string firstName = "Ana",
        string lastName = "Perez",
        string? phone = null) =>
        new(appointmentId, email, firstName, lastName, phone);

    private int AddSessionAt(
        DateTime startsAt,
        int maxCapacity = 8,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        int businessId = 1,
        int serviceId = 7,
        int workerId = 3,
        params BookingStatus[] bookings)
    {
        var appointmentId = _nextAppointmentId++;
        _repository.Sessions.Add(new FakeClassDropInSession(
            businessId,
            new ClassSessionRow(appointmentId, serviceId, workerId, startsAt, maxCapacity, status, bookings)));
        return appointmentId;
    }

    private int AddSession(
        DateOnly date,
        TimeOnly time,
        int maxCapacity = 8,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        int businessId = 1,
        int serviceId = 7,
        int workerId = 3,
        params BookingStatus[] bookings) =>
        AddSessionAt(date.ToDateTime(time), maxCapacity, status, businessId, serviceId, workerId, bookings);

    private void AddClient(int businessId, int clientId, string email = "ana@example.com")
        => _repository.Clients.Add(new FakeClassDropInClient(businessId, new DropInClient(clientId, email, "Ana", "Perez")));

    // RF-07 step 2: the offer lists only scheduled classes with a future start and a free place, ordered by start.

    [Fact]
    public async Task List_excludes_suspended_started_and_full_sessions_and_orders_the_rest()
    {
        var later = AddSessionAt(new DateTime(2026, 3, 12, 18, 0, 0));
        AddSessionAt(new DateTime(2026, 3, 11, 18, 0, 0), status: AppointmentStatus.Suspended);
        AddSessionAt(new DateTime(2026, 3, 10, 8, 30, 0));
        // A fixed student keeps the only place without being cancelled, so this class is full for a drop-in too
        // (D-16, D-23).
        AddSessionAt(
            new DateTime(2026, 3, 11, 17, 0, 0), maxCapacity: 1, bookings: new[] { BookingStatus.Pending });
        var earlier = AddSessionAt(new DateTime(2026, 3, 11, 18, 0, 0), serviceId: 9, workerId: 4);

        var result = await _service.ListOptionsAsync(1, D(2026, 3, 1), D(2026, 3, 31), serviceId: null);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { earlier, later },
            result.Value.Select(option => option.AppointmentId).ToArray());

        var option = result.Value[0];
        Assert.Equal(D(2026, 3, 11), option.Date);
        Assert.Equal(T(18), option.StartTime);
        Assert.Equal(9, option.ServiceId);
        Assert.Equal(4, option.WorkerId);
        Assert.Equal(8, option.MaxCapacity);
        Assert.Equal(0, option.PlacesTaken);
        Assert.Equal(8, option.RemainingPlaces);

        // The offer is read-only: it never opens a transaction, inserts or queues anything.
        Assert.Equal(1, _repository.ListCallCount);
        Assert.Equal(1, _repository.ListBusinessId);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Inserts);
        Assert.Empty(_repository.Notifications);
    }

    [Fact]
    public async Task List_reports_the_places_taken_by_every_non_cancelled_booking()
    {
        var appointmentId = AddSession(
            D(2026, 3, 11), T(18), maxCapacity: 4,
            bookings: new[] { BookingStatus.Pending, BookingStatus.Cancelled, BookingStatus.Confirmed });

        var result = await _service.ListOptionsAsync(1, D(2026, 3, 1), D(2026, 3, 31), serviceId: null);

        var option = Assert.Single(result.Value);
        Assert.Equal(appointmentId, option.AppointmentId);
        Assert.Equal(2, option.PlacesTaken);
        Assert.Equal(2, option.RemainingPlaces);
    }

    [Fact]
    public async Task List_asks_the_repository_for_the_whole_requested_range_and_the_service_filter()
    {
        var appointmentId = AddSession(D(2026, 3, 20), T(18), serviceId: 7);
        AddSession(D(2026, 3, 20), T(19), serviceId: 9);
        AddSession(D(2026, 3, 21), T(18), serviceId: 7);

        var result = await _service.ListOptionsAsync(1, D(2026, 3, 20), D(2026, 3, 20), serviceId: 7);

        Assert.Equal(appointmentId, Assert.Single(result.Value).AppointmentId);
        Assert.Equal(1, _repository.ListBusinessId);
        Assert.Equal(D(2026, 3, 20).ToDateTime(TimeOnly.MinValue), _repository.RequestedFrom);
        Assert.Equal(D(2026, 3, 20).ToDateTime(TimeOnly.MaxValue), _repository.RequestedTo);
        Assert.Equal(7, _repository.RequestedServiceId);
    }

    [Fact]
    public async Task List_reads_the_clock_once_and_judges_every_session_with_the_same_now()
    {
        // The session starts one second after the first read: a second read would put "now" exactly on the start and
        // the session would drop out of the offer, so a double read cannot pass unnoticed.
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new ClassDropInService(_repository, _unitOfWork, clock);
        var appointmentId = AddSessionAt(new DateTime(2026, 3, 10, 9, 0, 1));

        var result = await service.ListOptionsAsync(1, D(2026, 3, 10), D(2026, 3, 10), serviceId: null);

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(appointmentId, Assert.Single(result.Value).AppointmentId);
    }

    [Fact]
    public async Task List_accepts_the_last_day_of_the_calendar()
    {
        var result = await _service.ListOptionsAsync(1, D(9999, 12, 31), D(9999, 12, 31), serviceId: null);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value);
        Assert.Equal(D(9999, 12, 31).ToDateTime(TimeOnly.MaxValue), _repository.RequestedTo);
    }

    [Fact]
    public async Task List_rejects_an_invalid_business_without_reading_storage()
    {
        var result = await _service.ListOptionsAsync(0, D(2026, 3, 1), D(2026, 3, 31), serviceId: null);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.ListCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // RF-07 steps 4-7: booking an identified client into the chosen class inside one transaction.

    [Fact]
    public async Task Book_public_inserts_a_drop_in_and_commits_once()
    {
        var appointmentId = AddSession(D(2026, 3, 20), T(18), maxCapacity: 4, bookings: new[] { BookingStatus.Pending });

        var result = await _service.BookPublicAsync(1, Request(appointmentId, phone: "11 5555 5555"));

        Assert.True(result.Succeeded);
        Assert.Equal(appointmentId, result.Value.AppointmentId);
        Assert.Equal(D(2026, 3, 20), result.Value.Date);
        Assert.Equal(T(18), result.Value.StartTime);
        Assert.False(result.Value.ImmediateEmailQueued);

        var insert = Assert.Single(_repository.Inserts);
        Assert.Equal(1, insert.BusinessId);
        Assert.Equal(appointmentId, insert.AppointmentId);
        Assert.Equal(result.Value.ClientId, insert.ClientId);
        Assert.Equal(_clock.Now, insert.CreatedAt);
        Assert.Equal(500, result.Value.BookingId);

        // Beyond the 24-hour window the scheduled reminder (RF-17) owns the email, so nothing is queued now.
        Assert.Empty(_repository.Notifications);
        Assert.Equal(1, _repository.LockCallCount);
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_public_reuses_the_client_already_identified_by_email()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));
        AddClient(1, 42, email: "ANA@Example.com");

        var result = await _service.BookPublicAsync(1, Request(appointmentId, email: "  ana@example.com  "));

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Value.ClientId);
        Assert.Equal("ana@example.com", _repository.IdentifiedEmail);
        Assert.Equal(42, Assert.Single(_repository.Inserts).ClientId);
        Assert.Equal(1, _repository.IdentifyCallCount);
        Assert.Single(_repository.Clients);
    }

    [Fact]
    public async Task Book_public_creates_the_client_without_a_password_when_the_email_is_unknown()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));
        _repository.NextClientId = 77;

        var result = await _service.BookPublicAsync(1, Request(appointmentId, phone: "11 5555 5555"));

        Assert.True(result.Succeeded);
        Assert.Equal(77, result.Value.ClientId);
        var created = Assert.Single(_repository.Clients);
        Assert.Equal(1, created.BusinessId);
        Assert.Equal("ana@example.com", created.Client.Email);
        Assert.Equal("Ana", created.Client.FirstName);
        Assert.Equal("Perez", created.Client.LastName);
        Assert.Equal("11 5555 5555", _repository.IdentifiedPhone);
    }

    [Fact]
    public async Task Book_rejects_a_full_session_without_inserting_or_committing()
    {
        var appointmentId = AddSessionAt(
            _clock.Now.AddDays(1), maxCapacity: 2,
            bookings: new[] { BookingStatus.Confirmed, BookingStatus.Pending });

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_full", result.Error!.Code);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Empty(_repository.Inserts);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_rejects_a_suspended_session()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1), status: AppointmentStatus.Suspended);

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_not_available", result.Error!.Code);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_rejects_an_already_started_session()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddMinutes(-1));

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_not_available", result.Error!.Code);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_rejects_a_session_that_does_not_belong_to_the_business()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1), businessId: 2);

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_not_found", result.Error!.Code);
        Assert.Equal(1, _repository.LockCallCount);
        Assert.Equal(1, _repository.LockBusinessId);
        Assert.Equal(appointmentId, _repository.LockedAppointmentId);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Empty(_repository.Clients);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_reports_full_without_committing_when_the_guarded_insert_loses_the_race()
    {
        // The guarded insert is the real protection of the last place: when it reports that no place remained, the
        // loser learns the class is full and its transaction rolls back.
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1), maxCapacity: 1, bookings: Array.Empty<BookingStatus>());
        _repository.TryInsertReturnsNull = true;

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_full", result.Error!.Code);
        Assert.Equal(1, _repository.InsertCallCount);
        Assert.Empty(_repository.Inserts);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    // RF-17 step 4 and D-22: a class booked with less than 24 hours of notice has no future reminder to send, so the
    // immediate email is the reminder itself. The shared dedup key is what stops the scheduled reminder (T24) from
    // sending a second email for the same booking.

    [Theory]
    [InlineData(23, 59, true)]
    [InlineData(24, 0, false)]
    [InlineData(25, 0, false)]
    public async Task Book_queues_the_immediate_email_only_within_the_24_hour_window(
        int hours, int minutes, bool expected)
    {
        var startsAt = _clock.Now.AddHours(hours).AddMinutes(minutes);
        var appointmentId = AddSessionAt(startsAt);

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Value.ImmediateEmailQueued);

        if (!expected)
        {
            Assert.Empty(_repository.Notifications);
            Assert.Equal(0, _repository.EnqueueCallCount);
            return;
        }

        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal(1, notification.BusinessId);
        Assert.Equal(result.Value.BookingId, notification.BookingId);
        Assert.Equal(NotificationType.DropInReminder, notification.Type);
        Assert.Equal($"reminder:booking:{result.Value.BookingId}", notification.DedupKey);
        Assert.Equal(_clock.Now, notification.CreatedAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Book_reads_the_clock_once_for_the_bookability_check_the_window_and_the_timestamps()
    {
        // The session starts one second after the first read: a second read would put "now" exactly on the start, the
        // class would no longer be bookable and the email would fall outside the window, so a double read cannot pass.
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new ClassDropInService(_repository, _unitOfWork, clock);
        var appointmentId = AddSessionAt(new DateTime(2026, 3, 10, 9, 0, 1));

        var result = await service.BookPublicAsync(1, Request(appointmentId));

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.True(result.Value.ImmediateEmailQueued);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0), Assert.Single(_repository.Inserts).CreatedAt);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0), Assert.Single(_repository.Notifications).CreatedAt);
    }

    // RF-07 step 5 and RF-11: the request carries the contact data. An invalid one fails before any storage call.

    [Theory]
    [InlineData("ana[at]example.com")]
    [InlineData("ana@example")]
    [InlineData("")]
    public async Task Book_rejects_an_invalid_email_before_any_storage_call(string email)
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));

        var result = await _service.BookPublicAsync(1, Request(appointmentId, email: email));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_invalid_email", result.Error!.Code);
        Assert.Equal(0, _repository.ListCallCount);
        Assert.Equal(0, _repository.LockCallCount);
        Assert.Equal(0, _repository.IdentifyCallCount);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Empty(_repository.Clients);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Book_rejects_a_request_without_a_name_before_any_storage_call()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));

        var result = await _service.BookPublicAsync(1, Request(appointmentId, firstName: "   "));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.LockCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Book_rejects_an_invalid_request_before_any_storage_call()
    {
        var result = await _service.BookPublicAsync(0, Request(100));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.LockCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // RF-11: the owner-assisted path books for a client that already belongs to the business; it never creates one.

    [Fact]
    public async Task Book_for_client_inserts_for_a_client_of_the_business()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));
        AddClient(1, 42);

        var result = await _service.BookForClientAsync(1, appointmentId, 42);

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Value.ClientId);
        Assert.Equal(42, Assert.Single(_repository.Inserts).ClientId);
        Assert.Equal(1, _repository.ClientExistsCallCount);
        Assert.Equal(0, _repository.IdentifyCallCount);
        Assert.Single(_repository.Clients);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_for_client_rejects_a_client_that_does_not_belong_to_the_business()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));
        AddClient(2, 42);

        var result = await _service.BookForClientAsync(1, appointmentId, 42);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_client_not_found", result.Error!.Code);
        Assert.Equal(1, _repository.ClientExistsCallCount);
        Assert.Equal(0, _repository.LockCallCount);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_for_client_rejects_an_unknown_client()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));

        var result = await _service.BookForClientAsync(1, appointmentId, 42);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_client_not_found", result.Error!.Code);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(0, 100, 42)]
    [InlineData(1, 0, 42)]
    [InlineData(1, 100, 0)]
    public async Task Book_for_client_rejects_an_invalid_request_without_reading_storage(
        int businessId, int appointmentId, int clientId)
    {
        var result = await _service.BookForClientAsync(businessId, appointmentId, clientId);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.ClientExistsCallCount);
        Assert.Equal(0, _repository.LockCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    /// <summary>A clock that advances one second per read, so a second read changes the outcome and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }
        public DateTime Now => first.AddSeconds(ReadCount++);
    }
}
