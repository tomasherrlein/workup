namespace WorkUp.Domain.Rules;

/// <summary>Pure rules for declared supply stock (RF-14, D-03).</summary>
public static class SupplyRules
{
    public const decimal MaximumStock = 99999999.99m;

    /// <summary>Matches Supplies stock DECIMAL(10,2): nonnegative, at most two fractional digits, and eight whole digits.</summary>
    public static bool IsValidStock(decimal currentStock, decimal minimumStock) =>
        IsValidSingleStock(currentStock) && IsValidSingleStock(minimumStock);

    public static bool IsBelowMinimum(decimal currentStock, decimal minimumStock) => currentStock < minimumStock;

    private static bool IsValidSingleStock(decimal value) =>
        value >= 0 && value <= MaximumStock && (decimal.GetBits(value)[3] >> 16 & 0x7F) <= 2;
}
