using System.Reflection;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

/// <summary>
/// T21.1 "Mis turnos y clases": the read-only agenda of the authenticated client panel. Every expectation uses the
/// fake clock, never <see cref="DateTime.Now"/>, and the fake repository is keyed by business and client so the
/// service can only see the rows of the ficha taken from the session cookie.
/// </summary>
public sealed class ClientPanelServiceTests
{
    private const int BusinessId = 1;
    private const int ClientId = 7;
    private const int OtherClientId = 8;
    private const int OtherBusinessId = 2;
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);

    private static ClientPanelBookingRecord Row(
        int bookingId,
        DateTime startsAt,
        AppointmentKind kind = AppointmentKind.Individual,
        BookingModality? modality = null,
        BookingStatus status = BookingStatus.Pending,
        AppointmentStatus appointmentStatus = AppointmentStatus.Scheduled,
        string serviceName = "Corte",
        string workerName = "Ana Pérez",
        int durationMinutes = 30,
        AppointmentOrigin? origin = null,
        int maxCapacity = 1,
        int? enrollmentId = null,
        DateTime? confirmedAt = null,
        DateTime? cancelledAt = null,
        CancelledBy? cancelledBy = null,
        DateTime? attendanceRecordedAt = null,
        AbsenceSource? absenceSource = null) =>
        new(bookingId, startsAt, startsAt.AddMinutes(durationMinutes), serviceName, workerName, kind, appointmentStatus, status, modality,
            origin, maxCapacity, enrollmentId, confirmedAt, cancelledAt, cancelledBy, attendanceRecordedAt, absenceSource);

    private static (ClientPanelService Service, FakeClientPanelRepository Repository) Built()
    {
        var repository = new FakeClientPanelRepository();
        return (new ClientPanelService(repository, new FakeClock { Now = Now }), repository);
    }

    private static ClientPanelBooking ById(ClientPanelAgenda agenda, int bookingId) =>
        agenda.Upcoming.Concat(agenda.History).Single(booking => booking.BookingId == bookingId);

    [Fact]
    public async Task Agenda_contains_only_the_authenticated_clients_bookings_of_the_business()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));
        repository.Add(BusinessId, OtherClientId, Row(2, Now.AddHours(1)));
        repository.Add(OtherBusinessId, ClientId, Row(3, Now.AddHours(1)));

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);

        Assert.Equal(new[] { 1 }, agenda.Upcoming.Select(booking => booking.BookingId));
        Assert.Empty(agenda.History);
    }

    [Fact]
    public async Task Agenda_splits_future_from_history_with_the_clock_and_keeps_past_and_cancelled_rows()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(-1)));
        repository.Add(BusinessId, ClientId, Row(3, Now.AddDays(-2), AppointmentKind.ClassSession, BookingModality.DropIn, BookingStatus.Cancelled));
        repository.Add(BusinessId, ClientId, Row(4, Now.AddHours(2), AppointmentKind.ClassSession, BookingModality.Fixed));
        repository.Add(BusinessId, ClientId, Row(5, Now, AppointmentKind.Individual, status: BookingStatus.Confirmed));

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);

        // The exact start already counts as started (RF-22), so it belongs to history, not to the upcoming list, and
        // history is ordered newest first: the row starting exactly at the clock outranks the one an hour earlier.
        Assert.Equal(new[] { 1, 4 }, agenda.Upcoming.Select(booking => booking.BookingId));
        Assert.Equal(new[] { 5, 2, 3 }, agenda.History.Select(booking => booking.BookingId));
    }

    [Fact]
    public async Task Agenda_orders_upcoming_ascending_and_history_descending_with_a_stable_tie_break()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(10, Now.AddHours(2)));
        repository.Add(BusinessId, ClientId, Row(11, Now.AddHours(1)));
        repository.Add(BusinessId, ClientId, Row(12, Now.AddHours(1)));
        repository.Add(BusinessId, ClientId, Row(20, Now.AddHours(-2), status: BookingStatus.Attended));
        repository.Add(BusinessId, ClientId, Row(21, Now.AddHours(-1), status: BookingStatus.Attended));
        repository.Add(BusinessId, ClientId, Row(22, Now.AddHours(-1), status: BookingStatus.Cancelled));

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);

        Assert.Equal(new[] { 11, 12, 10 }, agenda.Upcoming.Select(booking => booking.BookingId));
        Assert.Equal(new[] { 21, 22, 20 }, agenda.History.Select(booking => booking.BookingId));
    }

    [Fact]
    public async Task Agenda_keeps_own_individual_fixed_and_drop_in_bookings_with_their_kind_and_modality()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1), serviceName: "Corte", workerName: "Ana Pérez"));
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(2), AppointmentKind.ClassSession, BookingModality.Fixed, serviceName: "Yoga", workerName: "Lucía Gómez", durationMinutes: 60));
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(3), AppointmentKind.ClassSession, BookingModality.DropIn, serviceName: "Yoga", workerName: "Lucía Gómez", durationMinutes: 60));

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);

        var individual = ById(agenda, 1);
        Assert.Equal(AppointmentKind.Individual, individual.AppointmentKind);
        Assert.Null(individual.Modality);
        Assert.Equal("Corte", individual.ServiceName);
        Assert.Equal("Ana Pérez", individual.WorkerName);
        Assert.Equal(Now.AddHours(1), individual.StartsAt);
        Assert.Equal(Now.AddHours(1).AddMinutes(30), individual.EndsAt);

        Assert.Equal(BookingModality.Fixed, ById(agenda, 2).Modality);
        Assert.Equal(BookingModality.DropIn, ById(agenda, 3).Modality);
        Assert.Equal(AppointmentKind.ClassSession, ById(agenda, 2).AppointmentKind);
    }

    [Fact]
    public async Task Agenda_offers_confirmation_only_for_a_pending_individual_turn_before_its_start()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));                                                        // pending individual, future
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(1), status: BookingStatus.Confirmed));                       // already confirmed
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(1), status: BookingStatus.Attended));                        // already attended
        repository.Add(BusinessId, ClientId, Row(4, Now.AddHours(1), modality: null, appointmentStatus: AppointmentStatus.Suspended));
        repository.Add(BusinessId, ClientId, Row(5, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.Fixed));
        repository.Add(BusinessId, ClientId, Row(6, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.DropIn));
        repository.Add(BusinessId, ClientId, Row(7, Now.AddHours(-1)));                                                       // already started

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);

        Assert.True(ById(agenda, 1).CanConfirmIntention);
        Assert.False(ById(agenda, 2).CanConfirmIntention);
        Assert.False(ById(agenda, 3).CanConfirmIntention);
        Assert.False(ById(agenda, 4).CanConfirmIntention);
        Assert.False(ById(agenda, 5).CanConfirmIntention);
        Assert.False(ById(agenda, 6).CanConfirmIntention);
        Assert.False(ById(agenda, 7).CanConfirmIntention);
    }

    [Fact]
    public async Task Agenda_offers_cancellation_only_for_an_own_individual_or_drop_in_booking_before_its_start()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1)));                                                        // pending individual, future
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(1), status: BookingStatus.Confirmed));                       // confirmed individual, future
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.DropIn));  // pending drop-in, future
        repository.Add(BusinessId, ClientId, Row(4, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.Fixed, status: BookingStatus.Confirmed));
        repository.Add(BusinessId, ClientId, Row(5, Now.AddHours(1), status: BookingStatus.Cancelled));                       // already cancelled
        repository.Add(BusinessId, ClientId, Row(6, Now.AddHours(-1), status: BookingStatus.Confirmed));                      // already started
        repository.Add(BusinessId, ClientId, Row(7, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.DropIn, appointmentStatus: AppointmentStatus.Suspended));
        repository.Add(BusinessId, ClientId, Row(8, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.DropIn, status: BookingStatus.Attended));

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);

        Assert.True(ById(agenda, 1).CanCancel);
        Assert.True(ById(agenda, 2).CanCancel);
        Assert.True(ById(agenda, 3).CanCancel);
        Assert.False(ById(agenda, 4).CanCancel);
        Assert.False(ById(agenda, 5).CanCancel);
        Assert.False(ById(agenda, 6).CanCancel);
        Assert.False(ById(agenda, 7).CanCancel);
        Assert.False(ById(agenda, 8).CanCancel);
    }

    // --- T21.4: the typed eligibility flag for reprogramming ----------------------------------------------------

    [Fact]
    public async Task Agenda_offers_rescheduling_only_for_a_future_reserved_individual_turn_of_its_own_ficha()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1), origin: AppointmentOrigin.Reserved));
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(1), origin: AppointmentOrigin.Reserved, status: BookingStatus.Confirmed, confirmedAt: Now.AddMinutes(-30)));
        repository.Add(BusinessId, ClientId, Row(3, Now.AddHours(1), origin: AppointmentOrigin.WalkIn));
        repository.Add(BusinessId, ClientId, Row(4, Now.AddHours(1), origin: null));
        repository.Add(BusinessId, ClientId, Row(5, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.Fixed, origin: AppointmentOrigin.Reserved));
        repository.Add(BusinessId, ClientId, Row(6, Now.AddHours(1), AppointmentKind.ClassSession, BookingModality.DropIn, origin: AppointmentOrigin.Reserved));
        repository.Add(BusinessId, ClientId, Row(7, Now.AddHours(1), origin: AppointmentOrigin.Reserved, status: BookingStatus.Cancelled));
        repository.Add(BusinessId, ClientId, Row(8, Now.AddHours(1), origin: AppointmentOrigin.Reserved, appointmentStatus: AppointmentStatus.Suspended));
        repository.Add(BusinessId, ClientId, Row(9, Now.AddHours(-1), origin: AppointmentOrigin.Reserved));
        repository.Add(BusinessId, ClientId, Row(10, Now.AddHours(1), origin: AppointmentOrigin.Reserved, maxCapacity: 2));
        repository.Add(BusinessId, ClientId, Row(11, Now.AddHours(1), origin: AppointmentOrigin.Reserved, enrollmentId: 5));

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);

        // Only the own future reserved individual turn that still holds its place can be reprogrammed: a walk-in, a
        // class date (fixed or drop-in), a cancelled or suspended turn, one already started, a shared-capacity
        // appointment and an enrollment annotation are all excluded by the typed eligibility, not by a label.
        Assert.True(ById(agenda, 1).CanReschedule);
        Assert.True(ById(agenda, 2).CanReschedule);
        Assert.False(ById(agenda, 3).CanReschedule);
        Assert.False(ById(agenda, 4).CanReschedule);
        Assert.False(ById(agenda, 5).CanReschedule);
        Assert.False(ById(agenda, 6).CanReschedule);
        Assert.False(ById(agenda, 7).CanReschedule);
        Assert.False(ById(agenda, 8).CanReschedule);
        Assert.False(ById(agenda, 9).CanReschedule);
        Assert.False(ById(agenda, 10).CanReschedule);
        Assert.False(ById(agenda, 11).CanReschedule);
    }

    [Fact]
    public async Task Agenda_refuses_rescheduling_when_the_confirmation_instant_disagrees_with_the_status()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1), origin: AppointmentOrigin.Reserved, status: BookingStatus.Pending, confirmedAt: Now));
        repository.Add(BusinessId, ClientId, Row(2, Now.AddHours(1), origin: AppointmentOrigin.Reserved, status: BookingStatus.Confirmed, confirmedAt: null));

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);

        // The client core revalidates the same pair under lock; offering a link for a shape it would refuse is a dead
        // promise, so the panel projection mirrors the eligibility exactly.
        Assert.False(ById(agenda, 1).CanReschedule);
        Assert.False(ById(agenda, 2).CanReschedule);
    }

    [Fact]
    public async Task Agenda_keeps_the_cancel_and_confirmation_actions_of_a_reschedulable_turn()
    {
        var (service, repository) = Built();
        repository.Add(BusinessId, ClientId, Row(1, Now.AddHours(1), origin: AppointmentOrigin.Reserved));

        var agenda = await service.GetAgendaAsync(BusinessId, ClientId);
        var row = ById(agenda, 1);

        // Adding the reprogramming flag must not disturb the T21.2/T21.3 eligibility of the same row.
        Assert.True(row.CanConfirmIntention);
        Assert.True(row.CanCancel);
    }

    [Fact]
    public void Panel_projection_exposes_only_the_safe_agenda_fields()
    {
        var names = typeof(ClientPanelBooking).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(ClientPanelBooking.AppointmentKind),
                nameof(ClientPanelBooking.AppointmentStatus),
                nameof(ClientPanelBooking.BookingId),
                nameof(ClientPanelBooking.BookingStatus),
                nameof(ClientPanelBooking.CanCancel),
                nameof(ClientPanelBooking.CanConfirmIntention),
                nameof(ClientPanelBooking.CanReschedule),
                nameof(ClientPanelBooking.EndsAt),
                nameof(ClientPanelBooking.Modality),
                nameof(ClientPanelBooking.ServiceName),
                nameof(ClientPanelBooking.StartsAt),
                nameof(ClientPanelBooking.WorkerName)
            },
            names);

        foreach (var forbidden in new[] { "Health", "Note", "Token", "Email", "Phone", "Password", "ClientId", "BusinessId", "EnrollmentId" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }
}
