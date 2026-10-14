using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Expenses;
using WorkUp.Domain.Entities;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Expenses;

public class ExpenseServiceTests
{
    private readonly FakeExpenseRepository _expenses = new();
    private readonly FakeExpenseTypeRepository _types = new();
    private readonly FakeClock _clock = new() { Now = new DateTime(2026, 4, 10, 9, 30, 0) };
    private readonly ExpenseService _service;

    public ExpenseServiceTests() => _service = new ExpenseService(_expenses, _types, _clock);

    [Fact]
    public async Task Creates_an_expense_only_for_an_active_type_of_the_owner_business()
    {
        var type = new ExpenseType { BusinessId = 1, Name = "Insumos" };
        await _types.InsertAsync(type);

        var result = await _service.CreateAsync(1, new CreateExpenseRequest(type.Id, new DateOnly(2026, 4, 9), 12500m, " Compra de tintura "));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value.BusinessId);
        Assert.Equal("Compra de tintura", result.Value.Description);
        Assert.Equal(_clock.Now, result.Value.CreatedAt);
    }

    [Fact]
    public async Task Rejects_an_archived_or_another_business_expense_type_for_new_expenses()
    {
        var archived = new ExpenseType { BusinessId = 1, Name = "Alquiler", IsActive = false };
        var otherBusiness = new ExpenseType { BusinessId = 2, Name = "Insumos" };
        await _types.InsertAsync(archived);
        await _types.InsertAsync(otherBusiness);

        var archivedResult = await _service.CreateAsync(1, new CreateExpenseRequest(archived.Id, new DateOnly(2026, 4, 9), 1m, "Alquiler"));
        var crossBusinessResult = await _service.CreateAsync(1, new CreateExpenseRequest(otherBusiness.Id, new DateOnly(2026, 4, 9), 1m, "Compra"));

        Assert.Equal("expense_type_not_available", archivedResult.Error!.Code);
        Assert.Equal("expense_type_not_available", crossBusinessResult.Error!.Code);
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("1.001")]
    [InlineData("10000000000.00")]
    public async Task Rejects_amounts_outside_SQL_decimal_12_2_before_persisting(string text)
    {
        var type = new ExpenseType { BusinessId = 1, Name = "Insumos" };
        await _types.InsertAsync(type);

        var result = await _service.CreateAsync(1, new CreateExpenseRequest(type.Id, new DateOnly(2026, 4, 9),
            decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), "Compra"));

        Assert.Equal("invalid_expense_amount", result.Error!.Code);
        Assert.Empty(_expenses.All);
    }

    [Fact]
    public async Task Lists_expenses_only_for_the_owner_business_and_selected_period()
    {
        var type = new ExpenseType { BusinessId = 1, Name = "Insumos" };
        await _types.InsertAsync(type);
        await _service.CreateAsync(1, new CreateExpenseRequest(type.Id, new DateOnly(2026, 4, 1), 10m, "A"));
        await _service.CreateAsync(1, new CreateExpenseRequest(type.Id, new DateOnly(2026, 5, 1), 20m, "B"));

        var items = await _service.ListAsync(1, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));

        var item = Assert.Single(items);
        Assert.Equal("A", item.Expense.Description);
    }
}
