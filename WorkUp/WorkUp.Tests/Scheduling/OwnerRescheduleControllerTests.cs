using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.Areas.Owner.Models.Agenda;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerRescheduleControllerTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0);
    private static readonly DateTime OriginalStart = new(2026, 3, 11, 10, 0, 0);
    private static readonly DateTime NewStart = new(2026, 3, 12, 10, 15, 0);

    [Fact]
    public void Reschedule_post_is_owner_only_and_antiforgery_protected()
    {
        var authorize = Assert.Single(typeof(AgendaController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true))
            as AuthorizeAttribute;
        Assert.Equal(AuthSchemes.Owner, authorize!.AuthenticationSchemes);

        var post = typeof(AgendaController).GetMethods()
            .Single(method => method.Name == nameof(AgendaController.Reschedule) &&
                              method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Length == 1);
        Assert.NotNull(post.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: false).SingleOrDefault());
        var get = typeof(AgendaController).GetMethods()
            .Single(method => method.Name == nameof(AgendaController.Reschedule) &&
                              method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false).Length == 1);
        Assert.NotNull(get.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public async Task Owner_detail_and_get_form_offer_reprogramming_without_a_worker_choice()
    {
        var fixture = CreateFixture();
        var controller = fixture.CreateController(businessId: 7);

        var detailResult = await controller.Detail(40);
        var detail = Assert.IsType<AgendaBookingDetailViewModel>(Assert.IsType<ViewResult>(detailResult).Model);
        Assert.True(detail.CanReschedule);
        var formResult = Assert.IsType<ViewResult>(await controller.Reschedule(40));

        Assert.Equal("Reschedule", formResult.ViewName);
        Assert.IsType<AgendaBookingRescheduleViewModel>(formResult.Model);
        Assert.DoesNotContain("WorkerId", typeof(OwnerRescheduleInput).GetProperties().Select(property => property.Name));

        fixture.AgendaRepository.BookingDetails[0] = (7, detail.Booking with { Origin = AppointmentOrigin.WalkIn });
        Assert.IsType<NotFoundResult>(await controller.Reschedule(40));
    }

    [Fact]
    public async Task Reschedule_get_offers_the_available_options_of_the_selected_day()
    {
        var fixture = CreateFixture();
        var controller = fixture.CreateController(businessId: 7);

        var result = Assert.IsType<ViewResult>(await controller.Reschedule(40));
        var model = Assert.IsType<AgendaBookingRescheduleViewModel>(result.Model);

        Assert.Equal(DateOnly.FromDateTime(OriginalStart), model.Input.Date);
        Assert.NotNull(model.Options);
        Assert.Equal(DateOnly.FromDateTime(OriginalStart), model.Options!.Date);
        Assert.DoesNotContain(TimeOnly.FromDateTime(OriginalStart), model.Options.AvailableStartTimes);
        Assert.Contains(new TimeOnly(9, 0), model.Options.AvailableStartTimes);
    }

    [Fact]
    public async Task Reschedule_get_navigates_to_a_requested_day_and_keeps_it_on_the_form()
    {
        var fixture = CreateFixture();
        var controller = fixture.CreateController(businessId: 7);
        var requestedDate = DateOnly.FromDateTime(NewStart);

        var result = Assert.IsType<ViewResult>(await controller.Reschedule(40, requestedDate));
        var model = Assert.IsType<AgendaBookingRescheduleViewModel>(result.Model);

        Assert.Equal(requestedDate, model.Input.Date);
        Assert.Equal(requestedDate, model.Options!.Date);
        Assert.Contains(TimeOnly.FromDateTime(NewStart), model.Options.AvailableStartTimes);
    }

    [Fact]
    public async Task Successful_owner_reschedule_uses_business_claim_and_redirects_to_detail()
    {
        var fixture = CreateFixture();
        var controller = fixture.CreateController(businessId: 7);

        var result = await controller.Reschedule(40, ValidInput());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AgendaController.Detail), redirect.ActionName);
        Assert.Equal(40, redirect.RouteValues!["id"]);
        Assert.Equal(7, fixture.RescheduleRepository.Booking!.BusinessId);
        Assert.Contains("apply-reschedule", fixture.RescheduleRepository.Calls);
        Assert.Equal(NewStart, fixture.RescheduleRepository.Booking!.StartsAt);
        Assert.Equal(BookingStatus.Confirmed, fixture.RescheduleRepository.Booking.BookingStatus);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
        Assert.DoesNotContain("BusinessId", typeof(OwnerRescheduleInput).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("WorkerId", typeof(OwnerRescheduleInput).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task Business_scope_comes_from_the_owner_claim_and_rejects_another_tenant()
    {
        var fixture = CreateFixture();
        var controller = fixture.CreateController(businessId: 8);

        var result = await controller.Reschedule(40, ValidInput());

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(fixture.RescheduleRepository.Calls);
        Assert.Equal(OriginalStart, fixture.RescheduleRepository.Booking!.StartsAt);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Stale_form_keeps_the_original_booking_and_renders_a_generic_error()
    {
        var fixture = CreateFixture();
        fixture.AgendaRepository.BookingDetails[0] = (7, fixture.AgendaRepository.BookingDetails[0].Detail with
        {
            BookingStatus = BookingStatus.Cancelled,
            CancelledAt = Now,
            CancelledBy = CancelledBy.Owner
        });
        var controller = fixture.CreateController(businessId: 7);
        var submitted = ValidInput();

        var result = await controller.Reschedule(40, submitted);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Reschedule", view.ViewName);
        var model = Assert.IsType<AgendaBookingRescheduleViewModel>(view.Model);
        Assert.Same(submitted, model.Input);
        Assert.Contains(view.ViewData.ModelState.Values.SelectMany(value => value.Errors), error =>
            error.ErrorMessage.Contains("No se pudo reprogramar", StringComparison.Ordinal));
        Assert.Equal(OriginalStart, fixture.RescheduleRepository.Booking!.StartsAt);
        Assert.Equal(BookingStatus.Pending, fixture.RescheduleRepository.Booking.BookingStatus);
        Assert.DoesNotContain("apply-reschedule", fixture.RescheduleRepository.Calls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Occupied_slot_keeps_the_form_and_original_reservation_with_the_specific_business_error()
    {
        var fixture = CreateFixture();
        fixture.RescheduleRepository.Snapshot = fixture.RescheduleRepository.Snapshot with
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
        var controller = fixture.CreateController(businessId: 7);
        var submitted = ValidInput();

        var result = await controller.Reschedule(40, submitted);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AgendaBookingRescheduleViewModel>(view.Model);
        Assert.Same(submitted, model.Input);
        // "slot_unavailable" is a business-rule outcome on the owner's own booking, not an existence leak, so it
        // shows the specific message instead of the generic reschedule error (finding 5).
        Assert.Contains(view.ViewData.ModelState.Values.SelectMany(value => value.Errors), error =>
            error.ErrorMessage.Contains("Ese horario no está disponible", StringComparison.Ordinal));
        Assert.DoesNotContain(view.ViewData.ModelState.Values.SelectMany(value => value.Errors), error =>
            error.ErrorMessage.Contains("No se pudo reprogramar", StringComparison.Ordinal));
        Assert.Equal(OriginalStart, fixture.RescheduleRepository.Booking!.StartsAt);
        Assert.Equal(BookingStatus.Pending, fixture.RescheduleRepository.Booking.BookingStatus);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Choosing_the_same_slot_shows_its_specific_business_message()
    {
        var fixture = CreateFixture();
        var controller = fixture.CreateController(businessId: 7);
        var sameSlot = new OwnerRescheduleInput
        {
            Date = DateOnly.FromDateTime(OriginalStart),
            StartTime = TimeOnly.FromDateTime(OriginalStart)
        };

        var result = await controller.Reschedule(40, sameSlot);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Contains(view.ViewData.ModelState.Values.SelectMany(value => value.Errors), error =>
            error.ErrorMessage == "Elegí un horario distinto al actual.");
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task A_reschedule_that_no_longer_finds_the_booking_keeps_the_generic_error()
    {
        var fixture = CreateFixture();
        fixture.RescheduleRepository.Booking = null;
        var controller = fixture.CreateController(businessId: 7);
        var submitted = ValidInput();

        var result = await controller.Reschedule(40, submitted);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Contains(view.ViewData.ModelState.Values.SelectMany(value => value.Errors), error =>
            error.ErrorMessage.Contains("No se pudo reprogramar", StringComparison.Ordinal));
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    private static OwnerRescheduleInput ValidInput() => new()
    {
        Date = DateOnly.FromDateTime(NewStart),
        StartTime = TimeOnly.FromDateTime(NewStart)
    };

    private static Fixture CreateFixture()
    {
        var rescheduleRepository = new FakeOwnerRescheduleRepository
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
                BusinessName: "Demo",
                ClientFirstName: "Sol",
                ClientEmail: "sol@example.invalid",
                ServiceName: "Corte",
                WorkerName: "Ana"),
            Snapshot = new BookingAvailabilitySnapshot(
                Enum.GetValues<DayOfWeek>().Select(day => new WorkerSchedule
                {
                    WorkerId = 2,
                    DayOfWeek = day,
                    StartTime = new TimeOnly(9, 0),
                    EndTime = new TimeOnly(17, 0)
                }).ToArray(), [], [], [])
        };
        var unitOfWork = new CommittingUnitOfWork(rescheduleRepository);
        var detail = new OwnerAgendaBookingDetail(
            40, 30, OriginalStart, OriginalStart.AddMinutes(45), AppointmentStatus.Scheduled,
            AppointmentOrigin.Reserved, BookingStatus.Pending, "Corte", "Ana", "Sol Gómez",
            "sol@example.invalid", null, 12000m, AppointmentKind.Individual, 1,
            WorkerId: 2);
        var agendaRepository = new FakeOwnerAgendaRepository();
        agendaRepository.BookingDetails.Add((7, detail));
        agendaRepository.Entries.Add((7, new OwnerAgendaRow(
            30, 40, OriginalStart, OriginalStart.AddMinutes(45), 2, AppointmentKind.Individual,
            AppointmentStatus.Scheduled, AppointmentOrigin.Reserved, BookingStatus.Pending,
            "Corte", "Ana", "Sol Gómez", 12000m)));

        return new Fixture(
            agendaRepository,
            rescheduleRepository,
            unitOfWork,
            new OwnerRescheduleService(rescheduleRepository, unitOfWork, new FakeClock { Now = Now }),
            new OwnerAgendaService(agendaRepository, new FakeClock { Now = Now }));
    }

    private sealed record Fixture(
        FakeOwnerAgendaRepository AgendaRepository,
        FakeOwnerRescheduleRepository RescheduleRepository,
        CommittingUnitOfWork UnitOfWork,
        OwnerRescheduleService RescheduleService,
        OwnerAgendaService AgendaService)
    {
        public AgendaController CreateController(int businessId)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(WorkUpClaims.BusinessId, businessId.ToString())], "test"));
            var controller = new AgendaController(AgendaService, null!, RescheduleService, new FakeClock { Now = Now })
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
            controller.TempData = new TempDataDictionary(context, new FakeTempDataProvider());
            return controller;
        }
    }

    private sealed class CommittingUnitOfWork(FakeOwnerRescheduleRepository repository) : IUnitOfWork
    {
        public int CommitCount { get; private set; }

        public Task<ITransaction> BeginAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken ct = default) => Task.FromResult<ITransaction>(new Transaction(this, repository));

        private sealed class Transaction(CommittingUnitOfWork owner, FakeOwnerRescheduleRepository repository) : ITransaction
        {
            private bool _committed;

            public Task CommitAsync(CancellationToken ct = default)
            {
                repository.CommitPendingChanges();
                owner.CommitCount++;
                _committed = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!_committed)
                    repository.DiscardPendingChanges();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
