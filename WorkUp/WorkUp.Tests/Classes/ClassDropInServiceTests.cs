using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Classes;

// T14 business core (RF-07 cupo > 1, RF-22, D-16, D-23, D-25): booking a drop-in into an already generated class
// session. Only a `ClassSession` that is `Programado` with a future start and a free place can be booked, and the
// occupancy counts every booking that is not `Cancelado`, the fixed ones included. The last place is protected by the
// repository's guarded insert inside one transaction, and the immediate email is only queued when the session starts
// in less than 24 hours (D-22).
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

    /// <summary>Bookings of a session with synthetic client ids, enough when the test only needs the occupancy.</summary>
    private static ClassSessionBooking[] Statuses(params BookingStatus[] statuses) =>
        statuses.Select((status, index) => new ClassSessionBooking(index + 1, status)).ToArray();

    /// <summary>Bookings of a session all held by one client, to exercise the active-client unique key.</summary>
    private static ClassSessionBooking[] ForClient(int clientId, params BookingStatus[] statuses) =>
        statuses.Select(status => new ClassSessionBooking(clientId, status)).ToArray();

    private int AddSessionAt(
        DateTime startsAt,
        int maxCapacity = 8,
        AppointmentStatus status = AppointmentStatus.Scheduled,
        int businessId = 1,
        int serviceId = 7,
        int workerId = 3,
        AppointmentKind kind = AppointmentKind.ClassSession,
        IReadOnlyList<ClassSessionBooking>? bookings = null,
        int reminderLeadHours = 24)
    {
        var appointmentId = _nextAppointmentId++;
        _repository.Sessions.Add(new FakeClassDropInSession(
            businessId,
            new ClassSessionRow(
                appointmentId, serviceId, workerId, startsAt, maxCapacity, kind, status, bookings ?? [],
                ReminderLeadHours: reminderLeadHours)));
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
        AppointmentKind kind = AppointmentKind.ClassSession,
        IReadOnlyList<ClassSessionBooking>? bookings = null) =>
        AddSessionAt(date.ToDateTime(time), maxCapacity, status, businessId, serviceId, workerId, kind, bookings);

    private void AddClient(
        int businessId,
        int clientId,
        string email = "ana@example.com",
        string firstName = "Ana",
        string lastName = "Perez")
        => _repository.Clients.Add(
            new FakeClassDropInClient(businessId, new DropInClient(clientId, email, firstName, lastName)));

    /// <summary>One annotation of a session with the full booking shape the roster and the cancellation read.</summary>
    private static ClassSessionBooking Annotation(
        int bookingId,
        int clientId,
        string clientName,
        BookingStatus status = BookingStatus.Pending,
        BookingModality? modality = BookingModality.DropIn) =>
        new(clientId, status, bookingId, modality, clientName);

    /// <summary>
    /// An upcoming session with one drop-in annotation of <paramref name="clientId"/>, registered in the fake as a
    /// cancellable booking too, so a cancellation test can load it and cancel it.
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
        var appointmentId = AddSessionAt(
            startsAt,
            maxCapacity,
            status,
            businessId,
            kind: kind,
            bookings: [Annotation(bookingId, clientId, clientName, bookingStatus, modality)]);

        _repository.DropInBookings.Add(new FakeClassDropInBooking(
            businessId,
            new DropInCancellationState(
                bookingId, appointmentId, startsAt, kind, status, bookingStatus, modality)));
        return appointmentId;
    }

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
            new DateTime(2026, 3, 11, 17, 0, 0), maxCapacity: 1, bookings: Statuses(BookingStatus.Pending));
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
            bookings: Statuses(BookingStatus.Pending, BookingStatus.Cancelled, BookingStatus.Confirmed));

        var result = await _service.ListOptionsAsync(1, D(2026, 3, 1), D(2026, 3, 31), serviceId: null);

        var option = Assert.Single(result.Value);
        Assert.Equal(appointmentId, option.AppointmentId);
        Assert.Equal(2, option.PlacesTaken);
        Assert.Equal(2, option.RemainingPlaces);
    }

    [Fact]
    public async Task List_excludes_individual_appointments()
    {
        // An individual turn is also `Programado` and has capacity 1, so without the kind discriminant it would be
        // offered as a class and bookable as a drop-in.
        AddSessionAt(_clock.Now.AddDays(1), maxCapacity: 1, kind: AppointmentKind.Individual);

        var result = await _service.ListOptionsAsync(1, D(2026, 3, 1), D(2026, 3, 31), serviceId: null);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value);
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
        // The port takes dates, never an inclusive maximum instant that no DATETIME2(0) column can store.
        Assert.Equal(D(2026, 3, 20), _repository.RequestedFrom);
        Assert.Equal(D(2026, 3, 20), _repository.RequestedTo);
        Assert.Equal(7, _repository.RequestedServiceId);
    }

    [Fact]
    public async Task List_includes_a_session_on_the_last_inclusive_day_and_excludes_the_next()
    {
        var onLastDay = AddSession(D(2026, 3, 20), T(18));
        AddSession(D(2026, 3, 21), T(18));

        var result = await _service.ListOptionsAsync(1, D(2026, 3, 20), D(2026, 3, 20), serviceId: null);

        Assert.Equal(onLastDay, Assert.Single(result.Value).AppointmentId);
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
    public async Task List_accepts_the_day_before_the_last_of_the_calendar()
    {
        // 9999-12-30 is the last day whose exclusive bound, (toInclusive + 1 day) 00:00, is still representable.
        var appointmentId = AddSession(D(9999, 12, 30), T(18));

        var result = await _service.ListOptionsAsync(1, D(9999, 12, 30), D(9999, 12, 30), serviceId: null);

        Assert.Equal(appointmentId, Assert.Single(result.Value).AppointmentId);
        Assert.Equal(D(9999, 12, 30), _repository.RequestedTo);
    }

    [Fact]
    public async Task List_rejects_a_range_ending_on_the_last_day_of_the_calendar()
    {
        // The port bounds the range as `StartsAt < (toInclusive + 1 day) 00:00`, and 9999-12-31 + 1 day is not
        // representable, so the service refuses that range before any storage call instead of handing the repository
        // a bound it cannot compute. The date filtering of the fake is an emulation of the query shape, not proof of
        // the SQL bound: this test pins the rejection the service owes every implementation.
        AddSession(D(9999, 12, 30), T(18));

        var result = await _service.ListOptionsAsync(1, D(9999, 12, 30), DateOnly.MaxValue, serviceId: null);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.ListCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
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

    // RF-22: the owner screen reads the upcoming classes with their drop-in annotations. The read is tenant-scoped,
    // never presents a fixed annotation as a drop-in, and keeps an empty class in the answer.

    [Fact]
    public async Task Roster_returns_the_upcoming_sessions_with_only_their_drop_in_annotations()
    {
        var appointmentId = AddSession(
            D(2026, 3, 20), T(18), maxCapacity: 8,
            bookings:
            [
                Annotation(501, 11, "Fija Estudiante", BookingStatus.Confirmed, BookingModality.Fixed),
                Annotation(502, 42, "Ana Perez"),
                Annotation(503, 43, "Bella Cancelada", BookingStatus.Cancelled)
            ]);
        AddSessionAt(_clock.Now.AddDays(-1));                              // already started
        AddSessionAt(_clock.Now.AddDays(2), businessId: 2);                // another business
        AddSessionAt(_clock.Now.AddDays(3), kind: AppointmentKind.Individual);

        var result = await _service.ListRosterAsync(1);

        Assert.True(result.Succeeded);
        var session = Assert.Single(result.Value);
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.Equal(7, session.ServiceId);
        Assert.Equal(3, session.WorkerId);
        Assert.Equal(8, session.MaxCapacity);
        Assert.Equal(AppointmentStatus.Scheduled, session.Status);
        Assert.Equal(D(2026, 3, 20), session.Date);
        Assert.Equal(T(18), session.StartTime);

        // The fixed annotation is never part of the roster; the cancelled drop-in is, with its status, so the owner
        // can see that the place was released and who released it.
        Assert.Equal(new[] { 502, 503 }, session.DropIns.Select(entry => entry.BookingId).ToArray());
        Assert.Equal(new[] { 42, 43 }, session.DropIns.Select(entry => entry.ClientId).ToArray());
        Assert.Equal(new[] { "Ana Perez", "Bella Cancelada" }, session.DropIns.Select(entry => entry.ClientName).ToArray());
        Assert.Equal(BookingStatus.Pending, session.DropIns[0].Status);
        Assert.Equal(BookingStatus.Cancelled, session.DropIns[1].Status);

        // The tenant and the instant are the ones the caller named, through the single clock read.
        Assert.Equal(1, _repository.RosterCallCount);
        Assert.Equal(1, _repository.RosterBusinessId);
        Assert.Equal(_clock.Now, _repository.RosterFromExclusive);

        // The roster is read-only: it opens no transaction and writes nothing.
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Inserts);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _repository.CancelCallCount);
    }

    [Fact]
    public async Task Roster_keeps_a_session_without_annotations_with_an_empty_list_and_orders_by_start()
    {
        var later = AddSession(D(2026, 3, 21), T(9));
        var earlier = AddSession(
            D(2026, 3, 20), T(18), bookings: [Annotation(502, 42, "Ana Perez")]);

        var result = await _service.ListRosterAsync(1);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { earlier, later },
            result.Value.Select(session => session.AppointmentId).ToArray());
        Assert.Empty(result.Value[1].DropIns);
    }

    [Fact]
    public async Task Roster_reads_the_clock_once_and_judges_every_session_with_the_same_now()
    {
        // The session starts one second after the first read: a second read would move "now" past its start and the
        // session would drop out of the roster, so a double read cannot pass unnoticed.
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new ClassDropInService(_repository, _unitOfWork, clock);
        var appointmentId = AddDropInSessionAt(new DateTime(2026, 3, 10, 9, 0, 1));

        var result = await service.ListRosterAsync(1);

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(appointmentId, Assert.Single(result.Value).AppointmentId);
    }

    [Fact]
    public async Task Roster_accepts_a_suspended_session_because_the_owner_still_needs_to_see_who_is_booked()
    {
        // Suspending a class does not cancel its annotations, so the owner screen must show them with the session
        // status; filtering the session out would hide exactly the students that need to hear about the suspension.
        var appointmentId = AddDropInSessionAt(
            _clock.Now.AddDays(1), status: AppointmentStatus.Suspended);

        var result = await _service.ListRosterAsync(1);

        var session = Assert.Single(result.Value);
        Assert.Equal(appointmentId, session.AppointmentId);
        Assert.Equal(AppointmentStatus.Suspended, session.Status);
        Assert.Equal(500, Assert.Single(session.DropIns).BookingId);
    }

    [Fact]
    public async Task Roster_rejects_an_invalid_business_without_reading_storage()
    {
        var result = await _service.ListRosterAsync(0);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_invalid_request", result.Error!.Code);
        Assert.Equal(0, _repository.RosterCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // RF-07 steps 4-7: booking an identified client into the chosen class inside one transaction.

    [Fact]
    public async Task Book_public_inserts_a_drop_in_and_commits_once()
    {
        var appointmentId = AddSession(D(2026, 3, 20), T(18), maxCapacity: 4, bookings: Statuses(BookingStatus.Pending));

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
            bookings: Statuses(BookingStatus.Confirmed, BookingStatus.Pending));

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
    public async Task Book_rejects_an_individual_appointment_as_a_class_not_found()
    {
        // Capacity 1 and no bookings: without the kind discriminant this individual turn would be booked as a class.
        var appointmentId = AddSessionAt(
            _clock.Now.AddDays(1), maxCapacity: 1, kind: AppointmentKind.Individual);

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_not_found", result.Error!.Code);
        Assert.Equal(0, _repository.IdentifyCallCount);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Empty(_repository.Inserts);
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
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1), maxCapacity: 1);
        _repository.TryInsertReturnsNoPlaceLeft = true;

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_full", result.Error!.Code);
        Assert.Equal(1, _repository.InsertCallCount);
        Assert.Empty(_repository.Inserts);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_fails_loudly_when_the_guarded_insert_reports_an_undefined_result()
    {
        // The switch over DropInInsertResultType knows three outcomes and the enum default 0 is not one of them.
        // Treating it as a successful insert would commit a booking that does not exist and queue
        // `reminder:booking:0` for it, so the service must not fall through: it fails loudly and the unit rolls back.
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));
        _repository.TryInsertResultOverride = new DropInInsertResult((DropInInsertResultType)0, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.BookPublicAsync(1, Request(appointmentId)));

        Assert.Equal(1, _repository.InsertCallCount);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    // UX_Bookings_ActiveClient (AppointmentId, ClientId) WHERE Status <> 5: a double submit or a fixed student
    // booking their own session must be answered with a stable code, never an unhandled unique-key exception.

    [Fact]
    public async Task Book_rejects_a_client_that_already_holds_a_non_cancelled_booking()
    {
        var appointmentId = AddSessionAt(
            _clock.Now.AddDays(1), maxCapacity: 4, bookings: ForClient(42, BookingStatus.Confirmed));
        AddClient(1, 42);

        var result = await _service.BookForClientAsync(1, appointmentId, 42);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_already_booked", result.Error!.Code);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Empty(_repository.Inserts);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_maps_the_already_booked_race_to_the_stable_code()
    {
        var appointmentId = AddSessionAt(_clock.Now.AddDays(1));
        _repository.TryInsertReturnsAlreadyBooked = true;

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_already_booked", result.Error!.Code);
        Assert.Equal(1, _repository.InsertCallCount);
        Assert.Empty(_repository.Inserts);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_reports_the_clients_own_booking_when_the_full_session_is_hers_already()
    {
        // The client's own booking is checked before the occupancy: a client holding the only place of a class that is
        // therefore full must hear that she is already booked, not that the class is full. Both reject without a write.
        var appointmentId = AddSessionAt(
            _clock.Now.AddDays(1), maxCapacity: 1, bookings: ForClient(42, BookingStatus.Confirmed));
        AddClient(1, 42);

        var result = await _service.BookPublicAsync(1, Request(appointmentId));

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_already_booked", result.Error!.Code);
        Assert.Equal(0, _repository.InsertCallCount);
        Assert.Empty(_repository.Inserts);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_allows_a_client_whose_only_booking_is_cancelled()
    {
        var appointmentId = AddSessionAt(
            _clock.Now.AddDays(1), maxCapacity: 4, bookings: ForClient(42, BookingStatus.Cancelled));
        AddClient(1, 42);

        var result = await _service.BookForClientAsync(1, appointmentId, 42);

        Assert.True(result.Succeeded);
        Assert.Equal(42, Assert.Single(_repository.Inserts).ClientId);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    // RF-17 step 4 and D-22: a class booked inside the configured anticipation has no future reminder to send, so the
    // immediate email is the reminder itself. The window is inclusive at its boundary: a booking made exactly on the
    // lead instant is already inside it, which is what makes the scheduled reminder (T24) its strict complement — the
    // boundary belongs to the immediate email and to nothing else. The shared dedup key is what stops the scheduled
    // reminder from sending a second email for the same booking. The outbox columns are not nullable, so the service
    // composes the full notice and the data layer only stores it.

    [Theory]
    [InlineData(23, 59, true)]
    [InlineData(24, 0, true)]
    [InlineData(25, 0, false)]
    public async Task Book_queues_the_immediate_email_only_within_the_default_24_hour_lead(
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
        Assert.Equal(NotificationType.DropInReminder, notification.Type);
        Assert.Equal("ana@example.com", notification.Recipient);
        Assert.False(string.IsNullOrWhiteSpace(notification.Subject));
        Assert.False(string.IsNullOrWhiteSpace(notification.BodyHtml));
        Assert.Equal($"reminder:booking:{result.Value.BookingId}", notification.DedupKey);
        Assert.Equal(_clock.Now, notification.CreatedAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    // RF-17 and D-22: the anticipation is the one the business configured, not a constant of the service. The window
    // is inclusive at its boundary, so the booking made exactly on the lead instant queues the immediate email and the
    // scheduled reminder (T24) is its strict complement: the two windows are disjoint and cover the class. Both are
    // checked with a shorter and a longer lead than the schema default, so a service that kept its own 24 h constant
    // cannot pass either group.
    [Theory]
    [InlineData(12, 11, true)]
    [InlineData(12, 12, true)]
    [InlineData(12, 13, false)]
    [InlineData(48, 47, true)]
    [InlineData(48, 48, true)]
    [InlineData(48, 49, false)]
    public async Task Book_uses_the_configured_reminder_lead_with_its_boundary_inside_the_window(
        int reminderLeadHours, int hoursOfNotice, bool expected)
    {
        var appointmentId = AddSessionAt(
            _clock.Now.AddHours(hoursOfNotice), reminderLeadHours: reminderLeadHours);

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
        Assert.Equal(NotificationType.DropInReminder, notification.Type);
        Assert.Equal($"reminder:booking:{result.Value.BookingId}", notification.DedupKey);
        Assert.Equal(_clock.Now, notification.CreatedAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Book_queues_the_email_stored_on_the_client_not_the_one_typed_in_the_request()
    {
        // The identifier matches the client by email ignoring case, and the notice recipient is the email stored on
        // the client, the value the outbox column will carry, never the string the form sent.
        var appointmentId = AddSessionAt(_clock.Now.AddHours(2));
        AddClient(1, 42, email: "ANA@Example.com");

        var result = await _service.BookPublicAsync(1, Request(appointmentId, email: "ana@example.com"));

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Value.ClientId);
        Assert.True(result.Value.ImmediateEmailQueued);
        Assert.Equal("ANA@Example.com", Assert.Single(_repository.Notifications).Recipient);
    }

    [Fact]
    public async Task Book_for_client_queues_the_notice_to_the_stored_email_inside_the_window()
    {
        // The owner-assisted path carries no contact form, so the notice recipient and name come from the stored
        // client: a class starting in less than 24 hours still gets its immediate email, with the shared dedup key.
        var appointmentId = AddSessionAt(_clock.Now.AddHours(2));
        AddClient(1, 42, email: "ana.stored@example.com", firstName: "Anabella");

        var result = await _service.BookForClientAsync(1, appointmentId, 42);

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Value.ClientId);
        Assert.True(result.Value.ImmediateEmailQueued);

        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal(1, notification.BusinessId);
        Assert.Equal(NotificationType.DropInReminder, notification.Type);
        Assert.Equal("ana.stored@example.com", notification.Recipient);
        Assert.False(string.IsNullOrWhiteSpace(notification.Subject));
        Assert.Contains("Anabella", notification.BodyHtml);
        Assert.Equal($"reminder:booking:{result.Value.BookingId}", notification.DedupKey);
        Assert.Equal(_clock.Now, notification.CreatedAt);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Book_for_client_does_not_queue_outside_the_24_hour_window()
    {
        // With more notice the scheduled reminder (RF-17) owns the email, on the assisted path too.
        var appointmentId = AddSessionAt(_clock.Now.AddDays(3));
        AddClient(1, 42);

        var result = await _service.BookForClientAsync(1, appointmentId, 42);

        Assert.True(result.Succeeded);
        Assert.False(result.Value.ImmediateEmailQueued);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _repository.EnqueueCallCount);
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
        Assert.Equal(1, _repository.FindClientCallCount);
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
        Assert.Equal(1, _repository.FindClientCallCount);
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
        Assert.Equal(0, _repository.FindClientCallCount);
        Assert.Equal(0, _repository.LockCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    // RF-22: the owner cancels a drop-in before its class starts. The booking row is locked inside one transaction,
    // the cancellation releases the place, and every rejection reports a stable code without writing.

    [Fact]
    public async Task Cancel_marks_the_annotation_cancelled_by_the_owner_and_commits_once()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1), bookingId: 500, clientId: 42);

        var result = await _service.CancelDropInAsync(1, 500);

        Assert.True(result.Succeeded);
        var stored = Assert.Single(_repository.DropInBookings);
        Assert.Equal(BookingStatus.Cancelled, stored.State.Status);
        Assert.Equal(CancelledBy.Owner, stored.CancelledBy);
        Assert.Equal(_clock.Now, stored.CancelledAt);

        Assert.Equal(1, _repository.LockDropInCallCount);
        Assert.Equal(1, _repository.CancelCallCount);
        Assert.Equal(1, _unitOfWork.BeginCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(0, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_releases_the_place_so_the_same_client_can_book_again()
    {
        var appointmentId = AddDropInSessionAt(
            _clock.Now.AddDays(1), bookingId: 500, clientId: 42, maxCapacity: 1);
        AddClient(1, 42);

        var cancelled = await _service.CancelDropInAsync(1, 500);
        var rebooked = await _service.BookForClientAsync(1, appointmentId, 42);

        Assert.True(cancelled.Succeeded);
        Assert.True(rebooked.Succeeded);
        Assert.Equal(42, Assert.Single(_repository.Inserts).ClientId);
        Assert.Equal(2, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Cancel_reads_the_clock_once_for_the_window_and_the_cancellation_instant()
    {
        // The class starts one second after the first read: a second read would put "now" exactly on the start, the
        // cancellation would be rejected and the instant written would differ, so a double read cannot pass.
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 9, 0, 0));
        var service = new ClassDropInService(_repository, _unitOfWork, clock);
        AddDropInSessionAt(new DateTime(2026, 3, 10, 9, 0, 1), bookingId: 500);

        var result = await service.CancelDropInAsync(1, 500);

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(1, _repository.CancelCallCount);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0), Assert.Single(_repository.CancelRequests).Now);
    }

    [Fact]
    public async Task Cancel_rejects_a_suspended_session_without_writing()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1), status: AppointmentStatus.Suspended);

        var result = await _service.CancelDropInAsync(1, 500);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_not_available", result.Error!.Code);
        Assert.Equal(BookingStatus.Pending, Assert.Single(_repository.DropInBookings).State.Status);
        Assert.Equal(0, _repository.CancelCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_rejects_an_already_started_session_without_writing()
    {
        AddDropInSessionAt(_clock.Now.AddMinutes(-1));

        var result = await _service.CancelDropInAsync(1, 500);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_not_available", result.Error!.Code);
        Assert.Equal(BookingStatus.Pending, Assert.Single(_repository.DropInBookings).State.Status);
        Assert.Equal(0, _repository.CancelCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_rejects_a_second_cancellation_without_writing_again()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1));

        var first = await _service.CancelDropInAsync(1, 500);
        var second = await _service.CancelDropInAsync(1, 500);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal("drop_in_already_cancelled", second.Error!.Code);
        Assert.Equal(1, _repository.CancelCallCount);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_treats_a_booking_of_another_business_exactly_like_a_missing_one()
    {
        AddDropInSessionAt(_clock.Now.AddDays(1), businessId: 2);

        var foreign = await _service.CancelDropInAsync(1, 500);
        var missing = await _service.CancelDropInAsync(1, 999);

        Assert.False(foreign.Succeeded);
        Assert.False(missing.Succeeded);
        Assert.Equal("drop_in_not_found", foreign.Error!.Code);
        Assert.Equal(foreign.Error!.Code, missing.Error!.Code);
        Assert.Equal(foreign.Error!.Message, missing.Error!.Message);
        Assert.Equal(2, _repository.LockDropInCallCount);
        Assert.Equal(0, _repository.CancelCallCount);
        Assert.Equal(1, _repository.LockDropInBusinessId);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(2, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task Cancel_rejects_a_fixed_annotation_and_an_individual_turn_as_not_found()
    {
        // A fixed annotation is not a drop-in and an individual turn is not a class: neither can be cancelled here,
        // and both are answered exactly like a booking of another business.
        var fixedSession = AddSession(
            D(2026, 3, 20), T(18),
            bookings: [Annotation(700, 11, "Fija Estudiante", BookingStatus.Confirmed, BookingModality.Fixed)]);
        _repository.DropInBookings.Add(new FakeClassDropInBooking(
            1,
            new DropInCancellationState(
                700, fixedSession, D(2026, 3, 20).ToDateTime(T(18)),
                AppointmentKind.ClassSession, AppointmentStatus.Scheduled, BookingStatus.Confirmed,
                BookingModality.Fixed)));
        var individualSession = AddDropInSessionAt(
            _clock.Now.AddDays(1), bookingId: 701, kind: AppointmentKind.Individual);

        var fixedResult = await _service.CancelDropInAsync(1, 700);
        var individualResult = await _service.CancelDropInAsync(1, 701);

        Assert.False(fixedResult.Succeeded);
        Assert.False(individualResult.Succeeded);
        Assert.Equal("drop_in_not_found", fixedResult.Error!.Code);
        Assert.Equal("drop_in_not_found", individualResult.Error!.Code);
        Assert.Equal(0, _repository.CancelCallCount);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(2, _unitOfWork.RolledBackCount);
        Assert.Equal(individualSession, _repository.DropInBookings[1].State.AppointmentId);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1, 0)]
    public async Task Cancel_rejects_an_invalid_request_without_reading_storage(int businessId, int bookingId)
    {
        var result = await _service.CancelDropInAsync(businessId, bookingId);

        Assert.False(result.Succeeded);
        Assert.Equal("drop_in_not_found", result.Error!.Code);
        Assert.Equal(0, _repository.LockDropInCallCount);
        Assert.Equal(0, _unitOfWork.BeginCount);
    }

    /// <summary>A clock that advances one second per read, so a second read changes the outcome and cannot go unnoticed.</summary>
    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }
        public DateTime Now => first.AddSeconds(ReadCount++);
    }
}
