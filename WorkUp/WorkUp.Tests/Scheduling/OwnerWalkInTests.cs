using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerWalkInTests
{
    [Fact]
    public async Task OwnerWalkIn_uses_clock_and_persists_overlap_warning()
    {
        var repo = new FakeOwnerWalkInRepository { Overlap = true };
        var uow = new FakeUnitOfWork();
        var result = await new OwnerWalkInService(repo, uow, new FakeClock { Now = new DateTime(2026, 3, 10, 10, 5, 7, 900) })
            .RegisterAsync(7, new OwnerWalkInRequest(20, 30, 40, 90m, "Discount", true, "Next time"));
        Assert.True(result.Succeeded);
        Assert.True(result.Value.HasOverlap);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 5, 7), repo.Inserted!.StartsAt);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 35, 7), repo.Inserted.EndsAt);
        Assert.Equal(1, uow.CommitCount);
        Assert.Equal(90m, repo.Inserted.AgreedPrice);
        Assert.True(repo.Inserted.CollectPayment);
        Assert.Equal("Next time", repo.Inserted.NextVisitNote);
    }

    [Fact]
    public async Task OwnerWalkIn_reads_clock_once_when_truncating_across_second_boundary()
    {
        var repo = new FakeOwnerWalkInRepository();
        var clock = new AdvancingClock(new DateTime(2026, 3, 10, 10, 5, 7, 900));

        var result = await new OwnerWalkInService(repo, new FakeUnitOfWork(), clock)
            .RegisterAsync(7, new(20, 30, 40));

        Assert.True(result.Succeeded);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 5, 7), repo.Inserted!.StartsAt);
    }

    private sealed class AdvancingClock(DateTime first) : IClock
    {
        public int ReadCount { get; private set; }
        public DateTime Now => first.AddSeconds(ReadCount++);
    }

    [Fact]
    public async Task OwnerWalkIn_without_overlap_uses_tariff_and_does_not_charge()
    {
        var repo = new FakeOwnerWalkInRepository();
        var result = await Run(repo, new(20, 30, 40));
        Assert.True(result.Succeeded);
        Assert.False(result.Value.HasOverlap);
        Assert.Equal(100m, repo.Inserted!.AgreedPrice);
        Assert.False(repo.Inserted.CollectPayment);
    }

    [Theory]
    [InlineData(8, 20, 30, 40)]
    [InlineData(7, 21, 30, 40)]
    [InlineData(7, 20, 31, 40)]
    [InlineData(7, 20, 30, 41)]
    public async Task OwnerWalkIn_rejects_cross_tenant_or_missing_relationship(int business, int client, int service, int worker)
    {
        var repo = new FakeOwnerWalkInRepository();
        var result = await Run(repo, new(client, service, worker), business);
        Assert.False(result.Succeeded);
        Assert.Null(repo.Inserted);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("client")]
    [InlineData("worker")]
    [InlineData("service")]
    [InlineData("class")]
    public async Task OwnerWalkIn_rejects_disabled_or_inactive_selection(string condition)
    {
        var repo = new FakeOwnerWalkInRepository();
        repo.Context = condition switch
        {
            "disabled" => repo.Context! with { AllowsWalkIns = false },
            "client" => repo.Context! with { ClientExists = false },
            "worker" => repo.Context! with { WorkerActive = false },
            "service" => repo.Context! with { ServiceActive = false },
            _ => repo.Context! with { MaxCapacity = 2 }
        };
        Assert.False((await Run(repo, new(20, 30, 40))).Succeeded);
        Assert.Null(repo.Inserted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.001)]
    [InlineData(10000000000)]
    public async Task OwnerWalkIn_rejects_invalid_agreed_price(decimal amount)
    {
        var repo = new FakeOwnerWalkInRepository();
        Assert.False((await Run(repo, new(20, 30, 40, amount))).Succeeded);
        Assert.Null(repo.Inserted);
    }

    [Fact]
    public async Task OwnerWalkIn_failed_insert_never_commits()
    {
        var repo = new FakeOwnerWalkInRepository { FailOnInsert = true };
        var unit = new FakeUnitOfWork();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OwnerWalkInService(repo, unit, new FakeClock { Now = new DateTime(2026, 3, 10, 10, 0, 0) })
                .RegisterAsync(7, new(20, 30, 40)));
        Assert.Equal(0, unit.CommitCount);
    }

    private static Task<WorkUp.BusinessLogic.Common.Result<OwnerWalkInOutcome>> Run(
        FakeOwnerWalkInRepository repo, OwnerWalkInRequest request, int businessId = 7) =>
        new OwnerWalkInService(repo, new FakeUnitOfWork(), new FakeClock { Now = new DateTime(2026, 3, 10, 10, 0, 0) })
            .RegisterAsync(businessId, request);
}
