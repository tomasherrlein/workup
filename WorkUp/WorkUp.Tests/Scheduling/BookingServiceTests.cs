using System.Data;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class BookingServiceTests
{
    private readonly FakeBookingRepository _repository = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new() { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
    private readonly RecordingLinkProtector _protector = new();
    private readonly RecordingAppointmentUrlBuilder _urlBuilder = new();
    private readonly BookingService _service;

    public BookingServiceTests()
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
        _service = new BookingService(_repository, _unitOfWork, _protector, _urlBuilder, _clock);
    }

    [Fact]
    public async Task Creates_pending_individual_booking_and_stores_only_hashed_and_protected_token()
    {
        var existingClient = new Client
        {
            Id = 8, BusinessId = 1, FirstName = "Sol", LastName = "Díaz", Email = "sol@example.com",
            HealthNotes = "private health note"
        };
        _repository.Clients.Add(existingClient);

        var attempt = await _service.BookIndividualAsync(1, ValidRequest());

        Assert.True(attempt.Succeeded);
        var confirmation = attempt.Confirmation;
        var appointment = Assert.Single(_repository.Appointments);
        var booking = Assert.Single(_repository.Bookings);
        var link = Assert.Single(_repository.Links);
        var notification = Assert.Single(_repository.Notifications);
        Assert.Equal(AppointmentKind.Individual, appointment.Kind);
        Assert.Equal(AppointmentOrigin.Reserved, appointment.Origin);
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
        Assert.Equal(new DateTime(2026, 3, 11, 10, 0, 0), appointment.StartsAt);
        Assert.Equal(appointment.StartsAt.AddMinutes(30), appointment.EndsAt);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Null(booking.Modality);
        Assert.Equal(8, booking.ClientId);
        Assert.Equal(5000m, booking.AgreedPrice);
        Assert.Equal(appointment.Id, booking.AppointmentId);
        Assert.Equal(booking.Id, link.BookingId);
        Assert.Equal(SecureToken.Hash(_urlBuilder.LastToken), link.TokenHash);
        Assert.Equal("protected:" + _urlBuilder.LastToken, link.ProtectedToken);
        Assert.Equal(appointment.StartsAt.AddHours(24), link.ExpiresAt);
        Assert.Equal(_clock.Now, link.CreatedAt);
        Assert.Equal("sol@example.com", notification.Recipient);
        Assert.Equal(NotificationType.BookingCreated, notification.Type);
        Assert.Single(_repository.Notifications);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.DoesNotContain("private health note", notification.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("https://workup.test/t/" + _urlBuilder.LastToken, notification.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(_urlBuilder.LastToken, confirmation.ToString(), StringComparison.Ordinal);
        Assert.Equal(appointment.Id, confirmation.AppointmentId);
        Assert.Equal(booking.Id, confirmation.BookingId);
        Assert.Equal("Corte", confirmation.ServiceName);
        Assert.Equal("Ana Pérez", confirmation.WorkerName);
        Assert.Equal(5000m, confirmation.AgreedPrice);
        Assert.True(_repository.Calls.IndexOf("lock-worker") < _repository.Calls.IndexOf("availability"));
        Assert.True(_repository.Calls.IndexOf("availability") < _repository.Calls.IndexOf("insert-appointment"));
    }

    [Theory]
    [InlineData(24, NotificationType.AppointmentReminder)]
    [InlineData(25, NotificationType.BookingCreated)]
    public async Task Enqueues_one_immediate_email_with_the_content_for_its_reminder_window(
        int hoursUntilStart, NotificationType expectedType)
    {
        var start = _clock.Now.AddHours(hoursUntilStart);
        var attempt = await _service.BookIndividualAsync(1, ValidRequest(start));

        Assert.True(attempt.Succeeded);
        Assert.Equal(expectedType, Assert.Single(_repository.Notifications).Type);
        Assert.Contains(expectedType == NotificationType.AppointmentReminder ? "Confirmá" : "Tu reserva quedó pendiente", _repository.Notifications[0].BodyHtml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-an-email", "Sol", "Díaz", 10, 0)]
    [InlineData("sol@example.com", " ", "Díaz", 10, 0)]
    [InlineData("sol@example.com", "Sol", " ", 10, 0)]
    [InlineData("sol@example.com", "Sol", "Díaz", 10, 7)]
    public async Task Rejects_invalid_public_contact_data_or_non_quarter_hour_time(
        string email, string firstName, string lastName, int hour, int minute)
    {
        var result = await _service.BookIndividualAsync(1, ValidRequest() with
        {
            Email = email, FirstName = firstName, LastName = lastName, StartTime = new TimeOnly(hour, minute)
        });

        Assert.False(result.Succeeded);
        Assert.Empty(_repository.Appointments);
        Assert.Empty(_repository.Bookings);
        Assert.Empty(_repository.Links);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _unitOfWork.CommitCount);
        Assert.DoesNotContain("lock-worker", _repository.Calls);
    }

    [Fact]
    public async Task Rejects_names_that_exceed_the_client_schema_length()
    {
        var request = ValidRequest() with { FirstName = new string('A', 61) };

        var result = await _service.BookIndividualAsync(1, request);

        Assert.False(result.Succeeded);
        Assert.Equal("client_first_name_too_long", result.Error!.Code);
        Assert.DoesNotContain("lock-worker", _repository.Calls);
    }

    [Fact]
    public async Task Rejects_seconds_even_when_the_minute_is_on_the_quarter_hour_grid()
    {
        var request = ValidRequest() with { StartTime = new TimeOnly(10, 0, 30) };

        var result = await _service.BookIndividualAsync(1, request);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_booking_grid", result.Error!.Code);
        Assert.DoesNotContain("lock-worker", _repository.Calls);
    }

    [Fact]
    public async Task Allows_the_last_safe_search_start_date()
    {
        var lastSafeStart = DateOnly.MaxValue.AddDays(-31);
        var request = ValidRequest() with { Date = lastSafeStart };

        var result = await _service.BookIndividualAsync(1, request);

        Assert.True(result.Succeeded);
        Assert.Equal(DateOnly.MaxValue.AddDays(-1), _repository.SnapshotEndDate);
        Assert.Equal(1, _unitOfWork.BeginCount);
    }

    [Fact]
    public async Task Rejects_exactly_thirty_days_before_max_date_before_transaction_or_repository_access()
    {
        var firstUnsafeStart = DateOnly.MaxValue.AddDays(-30);
        var request = ValidRequest() with { Date = firstUnsafeStart };

        var result = await _service.BookIndividualAsync(1, request);

        Assert.False(result.Succeeded);
        Assert.Equal("booking_date_out_of_range", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.BeginCount);
        Assert.Empty(_repository.Calls);
        Assert.Empty(_repository.Appointments);
        Assert.Empty(_repository.Bookings);
    }

    [Fact]
    public async Task Rejects_a_past_start_without_opening_a_transaction()
    {
        var result = await _service.BookIndividualAsync(1, ValidRequest(new DateTime(2026, 3, 10, 8, 45, 0)));

        Assert.False(result.Succeeded);
        Assert.Equal("booking_start_in_past", result.Error!.Code);
        Assert.DoesNotContain("lock-worker", _repository.Calls);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Returns_nearest_same_worker_alternative_with_a_thirty_day_inclusive_search_horizon()
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

        var result = await _service.BookIndividualAsync(1, ValidRequest(requested));

        Assert.False(result.Succeeded);
        Assert.Equal("slot_unavailable", result.Error!.Code);
        Assert.Equal(new DateTime(2026, 3, 11, 9, 30, 0), result.AlternativeStart);
        Assert.Equal(new DateOnly(2026, 3, 11), _repository.SnapshotStartDate);
        Assert.Equal(new DateOnly(2026, 4, 10), _repository.SnapshotEndDate);
        Assert.Empty(_repository.Appointments);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Does_not_partially_book_when_client_email_insert_loses_a_concurrent_unique_key_race()
    {
        _repository.LoseNextClientInsertRace = true;

        var result = await _service.BookIndividualAsync(1, ValidRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("booking_retry", result.Error!.Code);
        Assert.DoesNotContain("email", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_repository.Appointments);
        Assert.Empty(_repository.Bookings);
        Assert.Empty(_repository.Links);
        Assert.Empty(_repository.Notifications);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Tenant_scopes_active_service_and_worker_before_writing()
    {
        var result = await _service.BookIndividualAsync(2, ValidRequest());

        Assert.False(result.Succeeded);
        Assert.Empty(_repository.Appointments);
        Assert.Empty(_repository.Bookings);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Rejects_a_service_from_another_business()
    {
        _repository.Service!.BusinessId = 2;

        var result = await _service.BookIndividualAsync(1, ValidRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("service_unavailable", result.Error!.Code);
        Assert.Empty(_repository.Appointments);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Rejects_archived_or_group_services(bool isActive, int capacity)
    {
        _repository.Service!.IsActive = isActive;
        _repository.Service.MaxCapacity = capacity;

        var result = await _service.BookIndividualAsync(1, ValidRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("service_unavailable", result.Error!.Code);
        Assert.Empty(_repository.Appointments);
    }

    [Fact]
    public async Task Finds_or_creates_the_client_by_normalized_business_email_without_health_notes()
    {
        var result = await _service.BookIndividualAsync(1, ValidRequest() with { Email = "  NEW@Example.com " });

        Assert.True(result.Succeeded);
        var client = Assert.Single(_repository.Clients);
        Assert.Equal("new@example.com", client.Email);
        Assert.Equal("Sol", client.FirstName);
        Assert.Equal("Díaz", client.LastName);
        Assert.Null(client.HealthNotes);
        Assert.Null(client.Phone);
        Assert.Equal(client.Id, Assert.Single(_repository.Bookings).ClientId);
    }

    [Fact]
    public async Task Does_not_block_on_an_appointment_for_another_worker()
    {
        var requested = new DateTime(2026, 3, 11, 10, 0, 0);
        _repository.Snapshot = _repository.Snapshot with
        {
            Appointments =
            [
                new Appointment
                {
                    Id = 4, BusinessId = 1, WorkerId = 99, ServiceId = 3, Kind = AppointmentKind.Individual,
                    Status = AppointmentStatus.Scheduled, StartsAt = requested, EndsAt = requested.AddMinutes(30)
                }
            ]
        };

        var result = await _service.BookIndividualAsync(1, ValidRequest(requested));

        Assert.True(result.Succeeded);
        Assert.Single(_repository.Appointments);
        Assert.Equal(2, _repository.Appointments[0].WorkerId);
    }

    [Fact]
    public async Task Client_email_identity_is_scoped_to_the_business()
    {
        _repository.Clients.Add(new Client { Id = 40, BusinessId = 2, Email = "sol@example.com", FirstName = "Otra", LastName = "Ficha" });

        var result = await _service.BookIndividualAsync(1, ValidRequest());

        Assert.True(result.Succeeded);
        Assert.Equal(2, _repository.Clients.Count);
        Assert.Equal(1, _repository.Clients.Single(client => client.BusinessId == 1).BusinessId);
        Assert.Equal(_repository.Clients.Single(client => client.BusinessId == 1).Id, Assert.Single(_repository.Bookings).ClientId);
    }

    private static BookIndividualAppointmentRequest ValidRequest(DateTime? startsAt = null)
    {
        var start = startsAt ?? new DateTime(2026, 3, 11, 10, 0, 0);
        return new BookIndividualAppointmentRequest(
            3, 2, DateOnly.FromDateTime(start), TimeOnly.FromDateTime(start), "sol@example.com", "Sol", "Díaz");
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

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int BeginCount { get; private set; }
        public int CommitCount { get; private set; }

        public Task<ITransaction> BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default)
        {
            BeginCount++;
            return Task.FromResult<ITransaction>(new RecordingTransaction(this));
        }

        private sealed class RecordingTransaction(RecordingUnitOfWork owner) : ITransaction
        {
            public Task CommitAsync(CancellationToken ct = default)
            {
                owner.CommitCount++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
