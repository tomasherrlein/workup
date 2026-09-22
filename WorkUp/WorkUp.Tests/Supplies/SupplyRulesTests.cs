using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Supplies;

public class SupplyRulesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 1)]
    public void Accepts_non_negative_declared_stock_and_minimum(decimal currentStock, decimal minimumStock) =>
        Assert.True(SupplyRules.IsValidStock(currentStock, minimumStock));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Rejects_negative_declared_stock_or_minimum(decimal currentStock, decimal minimumStock) =>
        Assert.False(SupplyRules.IsValidStock(currentStock, minimumStock));

    [Fact]
    public void Accepts_zero_and_the_maximum_sql_decimal_10_2_stock_values()
    {
        Assert.True(SupplyRules.IsValidStock(0m, 99999999.99m));
        Assert.True(SupplyRules.IsValidStock(99999999.99m, 0m));
    }

    [Theory]
    [InlineData("0.001", "0")]
    [InlineData("0", "1.001")]
    [InlineData("100000000", "0")]
    [InlineData("0", "100000000")]
    public void Rejects_stock_values_that_SQL_decimal_10_2_cannot_store_exactly(string current, string minimum) =>
        Assert.False(SupplyRules.IsValidStock(
            decimal.Parse(current, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(minimum, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Signals_replenishment_only_when_declared_stock_is_below_minimum()
    {
        Assert.True(SupplyRules.IsBelowMinimum(0.5m, 1));
        Assert.False(SupplyRules.IsBelowMinimum(1, 1));
    }
}
