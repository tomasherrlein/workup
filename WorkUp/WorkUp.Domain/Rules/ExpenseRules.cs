namespace WorkUp.Domain.Rules;

/// <summary>Pure validation rules for owner-recorded expenses (RF-13).</summary>
public static class ExpenseRules
{
    public const decimal MaximumAmount = 9999999999.99m;

    /// <summary>Matches Expenses.Amount DECIMAL(12,2): positive, at most two fractional digits, and ten whole digits.</summary>
    public static bool IsValidAmount(decimal amount) =>
        amount > 0 && amount <= MaximumAmount && HasAtMostTwoDecimalPlaces(amount);

    public static bool HasDescription(string? description) => !string.IsNullOrWhiteSpace(description);

    private static bool HasAtMostTwoDecimalPlaces(decimal value) =>
        (decimal.GetBits(value)[3] >> 16 & 0x7F) <= 2;
}
