using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerDayActionsTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 10, 0, 0);

    [Fact]
    public async Task Finish_records_attendance_payment_and_note_in_one_transaction()
    {
        var repo = new FakeOwnerDayActionsRepository { Booking = FakeOwnerDayActionsRepository.Default() with { AgreedPrice = 90m } };
        var unit = new FakeUnitOfWork();
        var service = new OwnerDayActionsService(repo, unit, new FakeClock { Now = Now });
        var result = await service.FinishIndividualAsync(7, 12, new OwnerDayCharge(90m, "Discount"), "Next visit");
        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Attended, repo.Booking.BookingStatus);
        Assert.Equal(PaymentConcept.IndividualAppointment, repo.Payments.Single().Concept);
        Assert.Equal(100m, repo.Payments.Single().ListPrice);
        Assert.Equal("Next visit", repo.Notes.Single());
        Assert.Equal(1, unit.CommitCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Charge_can_be_recorded_before_or_after_attendance(bool first)
    {
        var repo = new FakeOwnerDayActionsRepository();
        var unit = new FakeUnitOfWork();
        var service = new OwnerDayActionsService(repo, unit, new FakeClock { Now = Now });
        if (first) Assert.True((await service.ChargeIndividualAsync(7, 12, new(100m))).Succeeded);
        Assert.True((await service.FinishIndividualAsync(7, 12)).Succeeded);
        if (!first) Assert.True((await service.ChargeIndividualAsync(7, 12, new(100m))).Succeeded);
        Assert.False((await service.ChargeIndividualAsync(7, 12, new(100m))).Succeeded);
        Assert.Single(repo.Payments);
    }

    [Fact]
    public async Task Absence_does_not_charge_or_note()
    {
        var repo = new FakeOwnerDayActionsRepository();
        var unit = new FakeUnitOfWork();
        var result = await new OwnerDayActionsService(repo, unit, new FakeClock { Now = Now }).MarkAbsentAsync(7, 12);
        Assert.True(result.Succeeded);
        Assert.Equal(BookingStatus.Absent, repo.Booking.BookingStatus);
        Assert.Equal(AbsenceSource.Owner, repo.Booking.AbsenceSource);
        Assert.Empty(repo.Payments);
        Assert.Empty(repo.Notes);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Absent)]
    [InlineData(BookingStatus.Attended)]
    public async Task Cannot_finish_non_active_booking(BookingStatus status)
    {
        var repo = new FakeOwnerDayActionsRepository { Booking = FakeOwnerDayActionsRepository.Default() with { BookingStatus = status } };
        var unit = new FakeUnitOfWork();
        var result = await new OwnerDayActionsService(repo, unit, new FakeClock { Now = Now })
            .FinishIndividualAsync(7, 12, new(100m), "note");
        Assert.False(result.Succeeded);
        Assert.Empty(repo.Payments);
        Assert.Empty(repo.Notes);
        Assert.Equal(0, unit.CommitCount);
    }

    [Fact]
    public async Task Rejects_class_and_inconsistent_tenant_without_writes()
    {
        var repo = new FakeOwnerDayActionsRepository { Booking = FakeOwnerDayActionsRepository.Default() with { AppointmentKind = AppointmentKind.ClassSession } };
        var unit = new FakeUnitOfWork();
        var service = new OwnerDayActionsService(repo, unit, new FakeClock { Now = Now });
        Assert.Equal("owner_day_individual_only", (await service.FinishIndividualAsync(7, 12)).Error!.Code);
        repo.Booking = FakeOwnerDayActionsRepository.Default() with { BusinessId = 8 };
        Assert.False((await service.ChargeIndividualAsync(7, 12, new(100m))).Succeeded);
        Assert.Equal(0, unit.CommitCount);
    }

    [Fact]
    public async Task Rejects_price_that_does_not_match_existing_agreement()
    {
        var repo = new FakeOwnerDayActionsRepository();
        var result = await new OwnerDayActionsService(repo, new FakeUnitOfWork(), new FakeClock { Now = Now })
            .ChargeIndividualAsync(7, 12, new(90m));
        Assert.Equal("owner_day_price_mismatch", result.Error!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(10000000000)]
    public async Task Rejects_invalid_amount(decimal amount)
    {
        var repo = new FakeOwnerDayActionsRepository();
        var result = await new OwnerDayActionsService(repo, new FakeUnitOfWork(), new FakeClock { Now = Now })
            .ChargeIndividualAsync(7, 12, new(amount));
        Assert.False(result.Succeeded);
        Assert.Empty(repo.Payments);
    }
}
