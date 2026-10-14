using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Data;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerAgendaTests
{
    [Fact]
    public void DateOnly_minimum_and_regular_range_midnights_are_bound_as_datetime2()
    {
        using var command = new SqlCommand();
        var minimumMidnight = DateOnly.MinValue.ToDateTime(TimeOnly.MinValue);
        var regularMidnight = new DateOnly(2026, 9, 22).ToDateTime(TimeOnly.MinValue);

        command.Parameters.AddDateTime2("@rangeStart", minimumMidnight)
            .AddDateTime2("@rangeEndExclusive", regularMidnight);

        var parameters = command.Parameters.Cast<SqlParameter>().ToArray();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(SqlDbType.DateTime2, parameters[0].SqlDbType);
        Assert.Equal(minimumMidnight, parameters[0].Value);
        Assert.Equal(SqlDbType.DateTime2, parameters[1].SqlDbType);
        Assert.Equal(regularMidnight, parameters[1].Value);
    }

    [Fact]
    public void Agenda_controller_is_available_for_owner_navigation()
    {
        var controller = typeof(OwnerControllerBase).Assembly.GetType(
            "WorkUp.Areas.Owner.Controllers.AgendaController");

        Assert.NotNull(controller);
        Assert.True(typeof(OwnerControllerBase).IsAssignableFrom(controller));
    }

    [Fact]
    public async Task Week_agenda_passes_a_bounded_tenant_and_filter_projection_to_the_repository()
    {
        var repository = new FakeOwnerAgendaRepository();
        var selectedDate = new DateOnly(2026, 9, 22);
        var expected = Row(1, 10, selectedDate.ToDateTime(new TimeOnly(9, 0)), 4,
            AppointmentKind.Individual, AppointmentOrigin.Reserved, BookingStatus.Pending);
        repository.Workers.Add((7, new OwnerAgendaWorkerOption(4, "Ana Pérez")));
        repository.Workers.Add((8, new OwnerAgendaWorkerOption(5, "Otra trabajadora")));
        repository.Entries.Add((7, expected));
        repository.Entries.Add((7, Row(2, 11, selectedDate.ToDateTime(new TimeOnly(10, 0)), 9,
            AppointmentKind.Individual, AppointmentOrigin.Reserved, BookingStatus.Pending)));
        repository.Entries.Add((7, Row(3, 12, selectedDate.ToDateTime(new TimeOnly(11, 0)), 4,
            AppointmentKind.Individual, AppointmentOrigin.WalkIn, BookingStatus.Pending)));
        repository.Entries.Add((8, Row(4, 13, selectedDate.ToDateTime(new TimeOnly(9, 0)), 4,
            AppointmentKind.Individual, AppointmentOrigin.Reserved, BookingStatus.Pending)));
        var service = new OwnerAgendaService(repository, new FakeClock());

        var result = await service.GetAgendaAsync(7, new OwnerAgendaFilter(
            selectedDate, OwnerAgendaPeriod.Week, 4, BookingStatus.Pending, AppointmentOrigin.Reserved));

        Assert.Equal(new DateOnly(2026, 9, 21), result.RangeStart);
        Assert.Equal(new DateOnly(2026, 9, 28), result.RangeEndExclusive);
        Assert.Equal([expected], result.Entries);
        Assert.Equal([new OwnerAgendaWorkerOption(4, "Ana Pérez")], result.Workers);
        var request = Assert.Single(repository.AgendaRequests);
        Assert.Equal(7, request.BusinessId);
        Assert.Equal(new DateOnly(2026, 9, 21), request.RangeStart);
        Assert.Equal(new DateOnly(2026, 9, 28), request.RangeEndExclusive);
        Assert.Equal(4, request.WorkerId);
        Assert.Equal(BookingStatus.Pending, request.BookingStatus);
        Assert.Equal(AppointmentOrigin.Reserved, request.Origin);
        Assert.Equal([7], repository.RequestedWorkerBusinessIds);
    }

    [Fact]
    public async Task Day_agenda_uses_one_day_and_invalid_or_oversized_filters_are_safely_normalized()
    {
        var repository = new FakeOwnerAgendaRepository();
        var today = new DateOnly(2026, 3, 10);
        var service = new OwnerAgendaService(repository, new FakeClock { Now = today.ToDateTime(new TimeOnly(9, 0)) });

        var day = await service.GetAgendaAsync(7, new OwnerAgendaFilter(today));
        await service.GetAgendaAsync(7, new OwnerAgendaFilter(DateOnly.MaxValue, (OwnerAgendaPeriod)99,
            WorkerId: 0, BookingStatus: (BookingStatus)99, Origin: (AppointmentOrigin)99));

        Assert.Equal(today, day.RangeStart);
        Assert.Equal(today.AddDays(1), day.RangeEndExclusive);
        var normalized = repository.AgendaRequests[1];
        Assert.Equal(today, normalized.RangeStart);
        Assert.Equal(today.AddDays(1), normalized.RangeEndExclusive);
        Assert.Null(normalized.WorkerId);
        Assert.Null(normalized.BookingStatus);
        Assert.Null(normalized.Origin);
        Assert.True(normalized.RangeEndExclusive.DayNumber - normalized.RangeStart.DayNumber <= 7);
    }

    [Fact]
    public async Task Invalid_business_does_not_query_the_tenant_repository()
    {
        var repository = new FakeOwnerAgendaRepository();
        var service = new OwnerAgendaService(repository, new FakeClock());

        var result = await service.GetAgendaAsync(0, new OwnerAgendaFilter(new DateOnly(2026, 9, 22)));

        Assert.Empty(result.Workers);
        Assert.Empty(result.Entries);
        Assert.Empty(repository.RequestedWorkerBusinessIds);
        Assert.Empty(repository.AgendaRequests);
    }

    [Fact]
    public async Task Individual_booking_detail_is_tenant_scoped_and_excludes_health_notes_and_link_tokens()
    {
        var detail = Detail(10);
        var repository = new FakeOwnerAgendaRepository();
        repository.BookingDetails.Add((7, detail));
        var service = new OwnerAgendaService(repository, new FakeClock());

        var found = await service.GetIndividualBookingAsync(7, 10);
        var crossTenant = await service.GetIndividualBookingAsync(8, 10);

        Assert.Equal(detail with { CanCancel = true, CanReschedule = true }, found);
        Assert.Null(crossTenant);
        Assert.Equal([(7, 10), (8, 10)], repository.BookingRequests);
        var fields = typeof(OwnerAgendaBookingDetail).GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(fields, name => name.Contains("Health", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(BookingStatus.Pending, true)]
    [InlineData(BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.Attended, false)]
    [InlineData(BookingStatus.Absent, false)]
    [InlineData(BookingStatus.Cancelled, false)]
    public async Task Booking_detail_only_offers_owner_cancellation_for_active_individual_bookings_before_start(
        BookingStatus status, bool canCancel)
    {
        var detail = Detail(10) with
        {
            BookingStatus = status,
            ConfirmedAt = status == BookingStatus.Confirmed ? new DateTime(2026, 9, 21, 12, 0, 0) : null,
            CancelledAt = status == BookingStatus.Cancelled ? new DateTime(2026, 9, 21, 12, 0, 0) : null,
            CancelledBy = status == BookingStatus.Cancelled ? CancelledBy.Owner : null
        };
        var repository = new FakeOwnerAgendaRepository();
        repository.BookingDetails.Add((7, detail));
        var service = new OwnerAgendaService(repository, new FakeClock { Now = new DateTime(2026, 9, 21, 12, 0, 0) });

        var found = await service.GetIndividualBookingAsync(7, 10);

        Assert.Equal(canCancel, found!.CanCancel);
    }

    [Fact]
    public async Task Booking_detail_hides_cancellation_and_reschedule_at_start_and_for_non_reserved_or_non_scheduled_appointments()
    {
        var now = new DateTime(2026, 9, 22, 9, 0, 0);
        foreach (var detail in new[]
                 {
                     Detail(10) with { StartsAt = now },
                     Detail(11) with { Origin = AppointmentOrigin.WalkIn },
                     Detail(12) with { AppointmentStatus = AppointmentStatus.Suspended }
                 })
        {
            var repository = new FakeOwnerAgendaRepository();
            repository.BookingDetails.Add((7, detail));
            var service = new OwnerAgendaService(repository, new FakeClock { Now = now });

            var found = await service.GetIndividualBookingAsync(7, detail.BookingId);

            Assert.False(found!.CanCancel);
            Assert.False(found.CanReschedule);
        }
    }

    [Theory]
    [InlineData(BookingStatus.Pending, true)]
    [InlineData(BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.Attended, false)]
    [InlineData(BookingStatus.Absent, false)]
    [InlineData(BookingStatus.Cancelled, false)]
    public async Task Booking_detail_only_offers_rescheduling_for_active_individual_bookings_before_start(
        BookingStatus status, bool canReschedule)
    {
        var detail = Detail(10) with
        {
            BookingStatus = status,
            ConfirmedAt = status == BookingStatus.Confirmed ? new DateTime(2026, 9, 21, 12, 0, 0) : null,
            CancelledAt = status == BookingStatus.Cancelled ? new DateTime(2026, 9, 21, 12, 0, 0) : null,
            CancelledBy = status == BookingStatus.Cancelled ? CancelledBy.Owner : null
        };
        var repository = new FakeOwnerAgendaRepository();
        repository.BookingDetails.Add((7, detail));
        var service = new OwnerAgendaService(repository, new FakeClock { Now = new DateTime(2026, 9, 21, 12, 0, 0) });

        var found = await service.GetIndividualBookingAsync(7, 10);

        Assert.Equal(canReschedule, found!.CanReschedule);
    }

    [Fact]
    public async Task Agenda_controller_uses_the_authenticated_business_claim_for_filters()
    {
        var repository = new FakeOwnerAgendaRepository();
        var service = new OwnerAgendaService(repository, new FakeClock());
        var controller = CreateController(service, 7);

        await controller.Index(new DateOnly(2026, 9, 22), "week", 4, "Pending", "Reserved");

        var request = Assert.Single(repository.AgendaRequests);
        Assert.Equal(7, request.BusinessId);
        Assert.Equal(4, request.WorkerId);
        Assert.Equal(BookingStatus.Pending, request.BookingStatus);
        Assert.Equal(AppointmentOrigin.Reserved, request.Origin);
    }

    [Fact]
    public async Task Owner_cancel_post_is_antiforgery_protected_and_uses_post_redirect_get()
    {
        var state = new OwnerBookingCancellationState(
            10, 20, new DateTime(2026, 9, 22, 10, 0, 0), AppointmentKind.Individual,
            AppointmentStatus.Scheduled, AppointmentOrigin.Reserved, 1, BookingStatus.Pending,
            null, null, null, null, null, null, null);
        var actionsRepository = new FakeOwnerBookingActionsRepository { Booking = state, BusinessId = 7 };
        var actionsService = new OwnerBookingActionsService(
            null!, null!, actionsRepository, new FakeUnitOfWork(), new FakeClock { Now = new DateTime(2026, 9, 22, 9, 0, 0) });
        var agendaService = new OwnerAgendaService(new FakeOwnerAgendaRepository(), new FakeClock());
        var controller = CreateController(agendaService, 7, actionsService);

        var result = await controller.Cancel(10);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AgendaController.Detail), redirect.ActionName);
        Assert.Equal(10, redirect.RouteValues!["id"]);
        Assert.Equal(CancelledBy.Owner, actionsRepository.Booking!.CancelledBy);
        var method = typeof(AgendaController).GetMethod(nameof(AgendaController.Cancel))!;
        Assert.NotNull(method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).SingleOrDefault());
        Assert.NotNull(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: false).SingleOrDefault());
        var bookingMethod = typeof(AgendaController).GetMethod(nameof(AgendaController.CreateBooking))!;
        Assert.NotNull(bookingMethod.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).SingleOrDefault());
        Assert.NotNull(bookingMethod.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public async Task Agenda_controller_returns_generic_not_found_for_cross_tenant_booking_detail()
    {
        var repository = new FakeOwnerAgendaRepository();
        repository.BookingDetails.Add((8, Detail(10)));
        var service = new OwnerAgendaService(repository, new FakeClock());
        var controller = CreateController(service, 7);

        var result = await controller.Detail(10);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal([(7, 10)], repository.BookingRequests);
    }

    private static AgendaController CreateController(
        OwnerAgendaService service, int businessId, OwnerBookingActionsService? bookingActionsService = null)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(WorkUpClaims.BusinessId, businessId.ToString())], "test"));
        var controller = new AgendaController(service, bookingActionsService!, null!, new FakeClock())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        controller.TempData = new TempDataDictionary(context, new FakeTempDataProvider());
        return controller;
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static OwnerAgendaRow Row(
        int appointmentId,
        int? bookingId,
        DateTime startsAt,
        int workerId,
        AppointmentKind kind,
        AppointmentOrigin? origin,
        BookingStatus? bookingStatus) => new(
        appointmentId, bookingId, startsAt, startsAt.AddMinutes(30), workerId, kind,
        AppointmentStatus.Scheduled, origin, bookingStatus, "Corte", "Ana Pérez", "Marina Gómez", 12000m);

    private static OwnerAgendaBookingDetail Detail(int bookingId) => new(
        bookingId, 20, new DateTime(2026, 9, 22, 9, 0, 0), new DateTime(2026, 9, 22, 9, 30, 0),
        AppointmentStatus.Scheduled, AppointmentOrigin.Reserved, BookingStatus.Pending,
        "Corte", "Ana Pérez", "Marina Gómez", "marina@example.invalid", "••••••1234", 12000m);
}
