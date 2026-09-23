using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WorkUp.Areas.Owner.Controllers;
using WorkUp.Areas.Owner.Models.Day;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.Infrastructure.Auth;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerDayTests
{
    [Fact]
    public async Task Day_exposes_agreed_price_for_owner_charge_without_exposing_private_notes()
    {
        var date = new DateOnly(2026, 3, 10);
        var repository = new FakeOwnerDayRepository();
        repository.Rows.Add(Row(1, 42, date, new TimeOnly(9, 0), new TimeOnly(10, 0),
            BookingStatus.Pending, OwnerDayPaymentState.Pending) with { AgreedPrice = 120m });
        var day = await new OwnerDayService(repository, new FakeClock { Now = date.ToDateTime(new TimeOnly(11, 0)) })
            .GetDayAsync(7, date);
        Assert.Equal(120m, Assert.Single(Assert.Single(day.Finished).Attendees).AgreedPrice);
    }

    [Fact]
    public async Task Today_groups_ongoing_earliest_ties_later_and_finished_items()
    {
        var today = new DateOnly(2026, 3, 10);
        var clock = new FakeClock { Now = today.ToDateTime(new TimeOnly(10, 0)) };
        var repository = new FakeOwnerDayRepository { PaidTotal = 18000m };
        repository.Rows.AddRange(
        [
            Row(1, 1, today, new TimeOnly(9, 0), new TimeOnly(10, 0), BookingStatus.Attended),
            Row(2, 2, today, new TimeOnly(9, 30), new TimeOnly(10, 15), BookingStatus.Pending, OwnerDayPaymentState.Pending),
            Row(7, 8, today, new TimeOnly(10, 0), new TimeOnly(10, 30), BookingStatus.Pending, OwnerDayPaymentState.Pending),
            Row(3, 3, today, new TimeOnly(10, 30), new TimeOnly(11, 0), BookingStatus.Confirmed, OwnerDayPaymentState.Pending),
            Row(3, 4, today, new TimeOnly(10, 30), new TimeOnly(11, 0), BookingStatus.Pending, OwnerDayPaymentState.Paid),
            Row(4, 5, today, new TimeOnly(10, 30), new TimeOnly(11, 30), BookingStatus.Pending,
                kind: AppointmentKind.ClassSession),
            Row(5, 6, today, new TimeOnly(11, 0), new TimeOnly(12, 0), BookingStatus.Pending),
            Row(6, 7, today, new TimeOnly(12, 0), new TimeOnly(13, 0), BookingStatus.Cancelled)
        ]);
        var service = new OwnerDayService(repository, clock);

        var result = await service.GetDayAsync(7, today);

        Assert.Equal([2, 7], result.Now.Select(item => item.AppointmentId));
        Assert.Equal([3, 4], result.Next.Select(item => item.AppointmentId));
        Assert.Equal([5], result.Later.Select(item => item.AppointmentId));
        Assert.Equal([1, 6], result.Finished.Select(item => item.AppointmentId));
        Assert.Equal(3, result.PendingPaymentCount);
        Assert.Equal(18000m, result.TotalPaid);
        Assert.Equal(2, Assert.Single(result.Next, item => item.AppointmentId == 3).Attendees.Count);
        Assert.DoesNotContain(typeof(OwnerDayAttendee).GetProperties(), property =>
            property.Name.Contains("Health", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Duplicate_booking_rows_do_not_multiply_pending_count_or_attendees()
    {
        var today = new DateOnly(2026, 3, 10);
        var repository = new FakeOwnerDayRepository();
        var booking = Row(1, 42, today, new TimeOnly(11, 0), new TimeOnly(12, 0),
            BookingStatus.Pending, OwnerDayPaymentState.Pending);
        repository.Rows.AddRange([booking, booking]);
        var service = new OwnerDayService(repository, new FakeClock { Now = today.ToDateTime(new TimeOnly(9, 0)) });

        var result = await service.GetDayAsync(7, today);

        Assert.Single(Assert.Single(result.Next).Attendees);
        Assert.Equal(1, result.PendingPaymentCount);
    }

    [Fact]
    public async Task Suspended_bookings_do_not_create_payment_debt_even_if_projection_is_stale()
    {
        var day = new DateOnly(2026, 3, 10);
        var repository = new FakeOwnerDayRepository();
        repository.Rows.AddRange([
            Row(1, 11, day, new TimeOnly(11, 0), new TimeOnly(12, 0), BookingStatus.Pending,
                OwnerDayPaymentState.Pending, AppointmentKind.ClassSession) with { AppointmentStatus = AppointmentStatus.Suspended },
            Row(2, 12, day, new TimeOnly(12, 0), new TimeOnly(13, 0), BookingStatus.Pending,
                OwnerDayPaymentState.Pending, AppointmentKind.ClassSession)
        ]);
        var result = await new OwnerDayService(repository, new FakeClock { Now = day.ToDateTime(new TimeOnly(9, 0)) })
            .GetDayAsync(7, day);

        Assert.Equal(1, result.PendingPaymentCount);
        Assert.Equal(OwnerDayPaymentState.NotApplicable, Assert.Single(result.Finished[0].Attendees).PaymentState);
        Assert.Equal(OwnerDayPaymentState.Pending, Assert.Single(result.Next[0].Attendees).PaymentState);
    }

    [Fact]
    public void Sql_day_projection_does_not_turn_fixed_enrollments_or_suspended_classes_into_debt()
    {
        // SQL integration requires an isolated LocalDB; this guard covers the CASE ordering without opening a database.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var sql = File.ReadAllText(Path.Combine(root, "WorkUp.Data", "Scheduling", "SqlOwnerDayRepository.cs"));
        var caseStart = sql.IndexOf("CASE\n                       WHEN b.Id IS NULL", StringComparison.Ordinal);
        var caseEnd = sql.IndexOf("END AS PaymentState", caseStart, StringComparison.Ordinal);
        Assert.True(caseStart >= 0 && caseEnd > caseStart);
        var paymentCase = sql[caseStart..caseEnd];
        Assert.Contains("a.Status = @suspended THEN @notApplicable", paymentCase);
        Assert.Contains("b.Modality = @fixed THEN @notApplicable", paymentCase);
        Assert.DoesNotContain("@pendingEnrollment THEN @pending", paymentCase);
        Assert.True(paymentCase.IndexOf("a.Status = @suspended", StringComparison.Ordinal) <
                    paymentCase.IndexOf("WHEN EXISTS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Future_day_has_no_now_and_all_earliest_start_ties_are_next()
    {
        var today = new DateOnly(2026, 3, 10);
        var selected = today.AddDays(1);
        var repository = new FakeOwnerDayRepository();
        repository.Rows.AddRange(
        [
            Row(1, 1, selected, new TimeOnly(9, 0), new TimeOnly(10, 0), BookingStatus.Pending),
            Row(2, 2, selected, new TimeOnly(9, 0), new TimeOnly(9, 45), BookingStatus.Confirmed),
            Row(3, 3, selected, new TimeOnly(10, 0), new TimeOnly(11, 0), BookingStatus.Pending)
        ]);
        var service = new OwnerDayService(repository, new FakeClock { Now = today.ToDateTime(new TimeOnly(15, 0)) });

        var result = await service.GetDayAsync(7, selected);

        Assert.Empty(result.Now);
        Assert.Equal([1, 2], result.Next.Select(item => item.AppointmentId));
        Assert.Equal([3], result.Later.Select(item => item.AppointmentId));
        Assert.Empty(result.Finished);
    }

    [Fact]
    public async Task Past_day_does_not_mislabel_a_later_clock_time_as_now_or_upcoming()
    {
        var today = new DateOnly(2026, 3, 10);
        var past = today.AddDays(-1);
        var repository = new FakeOwnerDayRepository();
        repository.Rows.Add(Row(1, 1, past, new TimeOnly(17, 0), new TimeOnly(18, 0), BookingStatus.Pending));
        var service = new OwnerDayService(repository, new FakeClock { Now = today.ToDateTime(new TimeOnly(9, 0)) });

        var result = await service.GetDayAsync(7, past);

        Assert.Empty(result.Now);
        Assert.Empty(result.Next);
        Assert.Empty(result.Later);
        Assert.Equal([1], result.Finished.Select(item => item.AppointmentId));
    }

    [Fact]
    public async Task Suspended_and_fully_closed_attendance_are_finished_even_before_their_end_time()
    {
        var today = new DateOnly(2026, 3, 10);
        var repository = new FakeOwnerDayRepository();
        repository.Rows.AddRange(
        [
            Row(1, 1, today, new TimeOnly(11, 0), new TimeOnly(12, 0), BookingStatus.Cancelled),
            Row(2, 2, today, new TimeOnly(11, 0), new TimeOnly(12, 0), BookingStatus.Attended) with
                { AppointmentStatus = AppointmentStatus.Suspended }
        ]);
        var service = new OwnerDayService(repository, new FakeClock { Now = today.ToDateTime(new TimeOnly(9, 0)) });

        var result = await service.GetDayAsync(7, today);

        Assert.Equal([1, 2], result.Finished.Select(item => item.AppointmentId));
        Assert.Empty(result.Next);
    }

    [Fact]
    public async Task Invalid_business_does_not_query_day_or_payment_totals()
    {
        var repository = new FakeOwnerDayRepository();
        var service = new OwnerDayService(repository, new FakeClock());

        var result = await service.GetDayAsync(0, new DateOnly(2026, 3, 10));

        Assert.Empty(result.Now);
        Assert.Empty(result.Next);
        Assert.Empty(repository.DayRequests);
        Assert.Empty(repository.PaidTotalRequests);
    }

    [Fact]
    public async Task Day_controller_uses_authenticated_tenant_and_selected_date()
    {
        var selected = new DateOnly(2026, 3, 12);
        var repository = new FakeOwnerDayRepository();
        var service = new OwnerDayService(repository, new FakeClock());
        var controller = new DayController(service,
            new OwnerDayActionsService(new FakeOwnerDayActionsRepository(), new FakeUnitOfWork(), new FakeClock()),
            new FakeClock())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(WorkUpClaims.BusinessId, "7")], "test"))
                }
            }
        };

        var result = await controller.Index(selected);

        Assert.IsType<ViewResult>(result);
        var request = Assert.Single(repository.DayRequests);
        Assert.Equal(7, request.BusinessId);
        Assert.Equal(selected, request.Date);
        var model = Assert.IsType<DayIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(selected, model.Day.SelectedDate);
    }

    private static OwnerDayRow Row(
        int appointmentId,
        int? bookingId,
        DateOnly date,
        TimeOnly startsAt,
        TimeOnly endsAt,
        BookingStatus status,
        OwnerDayPaymentState paymentState = OwnerDayPaymentState.NotApplicable,
        AppointmentKind kind = AppointmentKind.Individual) => new(
        appointmentId,
        date.ToDateTime(startsAt),
        date.ToDateTime(endsAt),
        10,
        kind,
        AppointmentStatus.Scheduled,
        kind == AppointmentKind.Individual ? AppointmentOrigin.Reserved : null,
        "Corte",
        "Ana Pérez",
        bookingId,
        bookingId is null ? null : "Marina Gómez",
        bookingId is null ? null : status,
        paymentState);
}
