using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// T21.5 S1 core: the authenticated client panel books an individual turn from the cookie's <c>ClientId</c>. These
/// tests pin the two contracts the cookie path must never break: it resolves the persisted ficha by tenant id (no
/// email lookup, no client insert) and it reuses the same transaction, worker lock, availability and email
/// semantics as the public form.
/// </summary>
public sealed class ClientPanelBookingCoreTests
{
    private readonly FakeBookingRepository _repository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new() { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
    private readonly RecordingLinkProtector _protector = new();
    private readonly RecordingAppointmentUrlBuilder _urlBuilder = new();
    private readonly BookingService _service;
    private readonly Client _client;

    public ClientPanelBookingCoreTests()
    {
        _repository.Worker = new Worker
        {
            Id = 2, BusinessId = 1, FirstName = "Ana", LastName = "Pérez", IsActive = true
        };
        _repository.Service = new Service
        {
            Id = 3, BusinessId = 1, Name = "Corte", Price = 5000m, DurationMinutes = 30, MaxCapacity = 1, IsActive = true
        };
        _repository.Snapshot = new BookingAvailabilitySnapshot(
            Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
            {
                WorkerId = 2, DayOfWeek = day, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0)
            }).ToArray(),
            [], [], []);
        _client = new Client
        {
            Id = 8, BusinessId = 1, FirstName = "Sol", LastName = "Díaz", Email = "sol@example.com",
            HealthNotes = "private health note"
        };
        _repository.Clients.Add(_client);
        _service = new BookingService(_repository, _unitOfWork, _protector, _urlBuilder, _clock);
    }

    private static DateOnly SlotDate => new(2026, 3, 11);

    private static TimeOnly SlotTime => new(10, 0);

    [Fact]
    public async Task Books_with_the_cookie_ficha_and_never_looks_a_client_up_by_email()
    {
        var attempt = await _service.BookIndividualForClientAsync(1, 8, 3, 2, SlotDate, SlotTime);

        Assert.True(attempt.Succeeded);
        var appointment = Assert.Single(_repository.Appointments);
        var booking = Assert.Single(_repository.Bookings);
        Assert.Equal(AppointmentKind.Individual, appointment.Kind);
        Assert.Equal(new DateTime(2026, 3, 11, 10, 0, 0), appointment.StartsAt);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Equal(8, booking.ClientId);
        Assert.Equal(5000m, booking.AgreedPrice);
        Assert.Single(_repository.Clients);
        Assert.False(_repository.LookedUpClientByEmail);
        Assert.DoesNotContain("find-client", _repository.Calls);
        Assert.DoesNotContain("insert-client", _repository.Calls);
        Assert.True(_repository.Calls.IndexOf("lock-worker") < _repository.Calls.IndexOf("availability"));
        Assert.True(_repository.Calls.IndexOf("availability") < _repository.Calls.IndexOf("find-client-by-id"));
        Assert.True(_repository.Calls.IndexOf("find-client-by-id") < _repository.Calls.IndexOf("insert-appointment"));
    }

    [Fact]
    public async Task Addresses_the_persisted_email_and_name_and_never_returns_the_private_token()
    {
        _repository.Clients.Add(new Client
        {
            Id = 9, BusinessId = 1, FirstName = "Otra", LastName = "Ficha", Email = "unrelated@example.com"
        });

        var attempt = await _service.BookIndividualForClientAsync(1, 8, 3, 2, SlotDate, SlotTime);

        Assert.True(attempt.Succeeded);
        var booking = Assert.Single(_repository.Bookings);
        var link = Assert.Single(_repository.Links);
        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal("sol@example.com", notification.Recipient);
        Assert.Contains("Hola Sol,", notification.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("private health note", notification.BodyHtml, StringComparison.Ordinal);
        Assert.Equal(SecureToken.Hash(_urlBuilder.LastToken), link.TokenHash);
        Assert.Equal("protected:" + _urlBuilder.LastToken, link.ProtectedToken);
        Assert.Contains("https://workup.test/t/" + _urlBuilder.LastToken, notification.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(_urlBuilder.LastToken, attempt.Confirmation.ToString(), StringComparison.Ordinal);
        Assert.Equal(booking.Id, link.BookingId);
        Assert.Equal(1, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(24, NotificationType.AppointmentReminder)]
    [InlineData(25, NotificationType.BookingCreated)]
    public async Task Keeps_the_same_lead_window_selection_and_dedup_key(
        int hoursUntilStart, NotificationType expectedType)
    {
        var start = _clock.Now.AddHours(hoursUntilStart);

        var attempt = await _service.BookIndividualForClientAsync(
            1, 8, 3, 2, DateOnly.FromDateTime(start), TimeOnly.FromDateTime(start));

        Assert.True(attempt.Succeeded);
        var booking = Assert.Single(_repository.Bookings);
        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal(expectedType, notification.Type);
        Assert.Equal($"booking:{booking.Id}", notification.DedupKey);
        Assert.Equal("sol@example.com", notification.Recipient);
    }

    [Fact]
    public async Task Returns_the_generic_not_found_and_writes_nothing_when_the_ficha_does_not_exist()
    {
        var attempt = await _service.BookIndividualForClientAsync(1, 99, 3, 2, SlotDate, SlotTime);

        Assert.False(attempt.Succeeded);
        Assert.Equal("client_booking_not_found", attempt.Error!.Code);
        Assert.Empty(_repository.Appointments);
        Assert.Empty(_repository.Bookings);
        Assert.Empty(_repository.Links);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.Equal(1, _unitOfWork.RolledBackCount);
    }

    [Fact]
    public async Task A_ficha_from_another_tenant_is_indistinguishable_from_a_missing_one()
    {
        _client.BusinessId = 2;

        var attempt = await _service.BookIndividualForClientAsync(1, 8, 3, 2, SlotDate, SlotTime);

        Assert.False(attempt.Succeeded);
        Assert.Equal("client_booking_not_found", attempt.Error!.Code);
        Assert.Empty(_repository.Appointments);
        Assert.Empty(_repository.Bookings);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Occupied_slot_offers_the_same_worker_alternative_without_writing()
    {
        var requested = new DateTime(2026, 3, 11, 10, 0, 0);
        _repository.Snapshot = _repository.Snapshot with
        {
            Appointments =
            [
                new Appointment
                {
                    Id = 4, BusinessId = 1, WorkerId = 2, ServiceId = 3, Kind = AppointmentKind.Individual,
                    Status = AppointmentStatus.Scheduled, StartsAt = requested, EndsAt = requested.AddMinutes(30)
                }
            ]
        };

        var attempt = await _service.BookIndividualForClientAsync(
            1, 8, 3, 2, DateOnly.FromDateTime(requested), TimeOnly.FromDateTime(requested));

        Assert.False(attempt.Succeeded);
        Assert.Equal("slot_unavailable", attempt.Error!.Code);
        Assert.Equal(new DateTime(2026, 3, 11, 9, 30, 0), attempt.AlternativeStart);
        Assert.Empty(_repository.Bookings);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(10, 7, "invalid_booking_grid", false)]
    [InlineData(9, 0, "booking_start_in_past", true)]
    public async Task Rejects_times_off_the_quarter_grid_or_not_in_the_future(
        int hour, int minute, string expectedCode, bool useToday)
    {
        var date = useToday ? DateOnly.FromDateTime(_clock.Now) : SlotDate;

        var attempt = await _service.BookIndividualForClientAsync(
            1, 8, 3, 2, date, new TimeOnly(hour, minute));

        Assert.False(attempt.Succeeded);
        Assert.Equal(expectedCode, attempt.Error!.Code);
        Assert.Equal("StartTime", attempt.Error.Field);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Calls);
    }

    [Fact]
    public async Task Allows_the_last_safe_search_start_date_and_rejects_the_first_unsafe_one()
    {
        var attempt = await _service.BookIndividualForClientAsync(
            1, 8, 3, 2, DateOnly.MaxValue.AddDays(-31), SlotTime);

        Assert.True(attempt.Succeeded);
        Assert.Equal(DateOnly.MaxValue.AddDays(-1), _repository.SnapshotEndDate);

        var rejected = await _service.BookIndividualForClientAsync(
            1, 8, 3, 2, DateOnly.MaxValue.AddDays(-30), SlotTime);

        Assert.False(rejected.Succeeded);
        Assert.Equal("booking_date_out_of_range", rejected.Error!.Code);
    }

    [Fact]
    public async Task Rejects_a_worker_or_service_from_another_business_before_touching_the_ficha()
    {
        var attempt = await _service.BookIndividualForClientAsync(1, 8, 3, 99, SlotDate, SlotTime);

        Assert.False(attempt.Succeeded);
        Assert.Equal("worker_unavailable", attempt.Error!.Code);
        Assert.Empty(_repository.Appointments);
        Assert.DoesNotContain("find-client-by-id", _repository.Calls);

        _repository.Worker!.BusinessId = 2;
        var serviceAttempt = await _service.BookIndividualForClientAsync(1, 8, 3, 2, SlotDate, SlotTime);

        Assert.False(serviceAttempt.Succeeded);
        Assert.Equal("worker_unavailable", serviceAttempt.Error!.Code);
    }

    [Fact]
    public async Task Public_email_form_still_resolves_or_creates_the_ficha_by_email()
    {
        var attempt = await _service.BookIndividualAsync(1, new BookIndividualAppointmentRequest(
            3, 2, SlotDate, SlotTime, "  NEW@Example.com ", "Ana", "Nueva"));

        Assert.True(attempt.Succeeded);
        var created = Assert.Single(_repository.Clients, client => client.Email == "new@example.com");
        Assert.Equal("Ana", created.FirstName);
        Assert.Equal(created.Id, Assert.Single(_repository.Bookings).ClientId);
        Assert.Equal("new@example.com", Assert.Single(_repository.Notifications).Recipient);
        Assert.Contains("find-client", _repository.Calls);
        Assert.Contains("insert-client", _repository.Calls);
    }

    private sealed class RecordingLinkProtector : ILinkTokenProtector
    {
        public string Protect(string token) => "protected:" + token;
        public string Unprotect(string protectedToken) => protectedToken["protected:".Length..];
    }

    private sealed class RecordingAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
    {
        public string LastToken { get; private set; } = "";
        public string Build(string token)
        {
            LastToken = token;
            return "https://workup.test/t/" + token;
        }
    }
}
