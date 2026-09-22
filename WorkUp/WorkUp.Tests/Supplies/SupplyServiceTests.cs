using WorkUp.BusinessLogic.Supplies;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Supplies;

public class SupplyServiceTests
{
    private readonly FakeSupplyRepository _supplies = new();
    private readonly FakeClock _clock = new() { Now = new DateTime(2026, 4, 10, 9, 30, 0) };
    private readonly SupplyService _service;

    public SupplyServiceTests() => _service = new SupplyService(_supplies, _clock);

    [Fact]
    public async Task Declares_current_stock_and_signals_a_low_stock_supply()
    {
        var result = await _service.CreateAsync(1, new DeclareSupplyRequest(" Tintura 7.1 ", 0.5m, 1m));

        Assert.True(result.Succeeded);
        Assert.Equal("Tintura 7.1", result.Value.Name);
        Assert.Equal(_clock.Now, result.Value.LastDeclaredAt);
        Assert.Equal(1, await _service.CountBelowMinimumAsync(1));
    }

    [Fact]
    public async Task Changes_stock_only_when_the_owner_explicitly_declares_an_edit()
    {
        var created = await _service.CreateAsync(1, new DeclareSupplyRequest("Guantes", 2m, 1m));
        _clock.Now = _clock.Now.AddDays(1);

        var updated = await _service.UpdateAsync(1, created.Value.Id, new DeclareSupplyRequest("Guantes", 8m, 2m));

        Assert.True(updated.Succeeded);
        var supply = Assert.Single(await _service.ListAsync(1));
        Assert.Equal(8m, supply.CurrentStock);
        Assert.Equal(2m, supply.MinimumStock);
        Assert.Equal(_clock.Now, supply.LastDeclaredAt);
    }

    [Theory]
    [InlineData("0.001", "0")]
    [InlineData("0", "1.001")]
    [InlineData("100000000", "0")]
    [InlineData("0", "100000000")]
    public async Task Rejects_stock_outside_SQL_decimal_10_2_before_persisting(string current, string minimum)
    {
        var result = await _service.CreateAsync(1, new DeclareSupplyRequest("Tintura",
            decimal.Parse(current, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(minimum, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal("invalid_supply_stock", result.Error!.Code);
        Assert.Empty(_supplies.All);
    }

    [Fact]
    public async Task Rejects_another_business_supply_and_keeps_low_stock_counts_isolated()
    {
        var created = await _service.CreateAsync(1, new DeclareSupplyRequest("Toallas", 0m, 2m));
        await _service.CreateAsync(2, new DeclareSupplyRequest("Toallas", 0m, 2m));

        var update = await _service.UpdateAsync(2, created.Value.Id, new DeclareSupplyRequest("Toallas", 5m, 2m));

        Assert.Equal("supply_not_found", update.Error!.Code);
        Assert.Equal(1, await _service.CountBelowMinimumAsync(1));
        Assert.Equal(1, await _service.CountBelowMinimumAsync(2));
    }
}
