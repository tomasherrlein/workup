using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Expenses;

public class ExpenseRulesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0.01)]
    public void Accepts_a_positive_expense_amount(decimal amount) =>
        Assert.True(ExpenseRules.IsValidAmount(amount));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_zero_or_negative_expense_amount(decimal amount) =>
        Assert.False(ExpenseRules.IsValidAmount(amount));

    [Theory]
    [InlineData("9999999999.99")]
    public void Accepts_the_maximum_sql_decimal_12_2_amount(string text) =>
        Assert.True(ExpenseRules.IsValidAmount(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0.001")]
    [InlineData("1.001")]
    [InlineData("10000000000.00")]
    public void Rejects_amounts_that_SQL_decimal_12_2_cannot_store_exactly(string text) =>
        Assert.False(ExpenseRules.IsValidAmount(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Requires_a_non_blank_expense_description() =>
        Assert.True(ExpenseRules.HasDescription("Compra de tintura"));
}
