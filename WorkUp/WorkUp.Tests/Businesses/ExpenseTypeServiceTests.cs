using WorkUp.Business.Businesses;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Businesses;

// RF-01, RF-13: expense types are unique per business and are archived, never deleted.
public class ExpenseTypeServiceTests
{
    private readonly FakeExpenseTypeRepository _repository = new();
    private readonly ExpenseTypeService _service;

    public ExpenseTypeServiceTests()
    {
        _service = new ExpenseTypeService(_repository);
    }

    [Fact]
    public async Task Creates_an_expense_type_for_the_business()
    {
        var result = await _service.CreateAsync(businessId: 1, name: "Alquiler");

        Assert.True(result.Succeeded);
        Assert.Equal("Alquiler", result.Value.Name);
        Assert.True(result.Value.IsActive);
        Assert.Equal(1, result.Value.BusinessId);
    }

    [Fact]
    public async Task Fails_when_the_name_is_already_used_by_the_same_business()
    {
        await _service.CreateAsync(1, "Alquiler");

        var result = await _service.CreateAsync(1, "Alquiler");

        Assert.False(result.Succeeded);
        Assert.Equal("expense_type_name_taken", result.Error!.Code);
    }

    [Fact]
    public async Task Allows_the_same_name_in_a_different_business()
    {
        await _service.CreateAsync(1, "Alquiler");

        var result = await _service.CreateAsync(2, "Alquiler");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Fails_when_the_name_is_blank()
    {
        var result = await _service.CreateAsync(1, "   ");

        Assert.False(result.Succeeded);
        Assert.Equal("expense_type_name_required", result.Error!.Code);
    }

    [Fact]
    public async Task Renames_an_existing_expense_type()
    {
        var created = await _service.CreateAsync(1, "Alquiler");

        var result = await _service.RenameAsync(1, created.Value.Id, "Alquiler y expensas");

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        Assert.Equal("Alquiler y expensas", list.Single().Name);
    }

    [Fact]
    public async Task Fails_to_rename_when_the_new_name_is_taken_by_another_type_of_the_same_business()
    {
        await _service.CreateAsync(1, "Alquiler");
        var second = await _service.CreateAsync(1, "Insumos");

        var result = await _service.RenameAsync(1, second.Value.Id, "Alquiler");

        Assert.False(result.Succeeded);
        Assert.Equal("expense_type_name_taken", result.Error!.Code);
    }

    [Fact]
    public async Task Archiving_never_removes_the_expense_type_from_the_list()
    {
        var created = await _service.CreateAsync(1, "Alquiler");

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        var expenseType = Assert.Single(list);
        Assert.False(expenseType.IsActive);
    }

    [Fact]
    public async Task An_archived_expense_type_can_be_reactivated()
    {
        var created = await _service.CreateAsync(1, "Alquiler");
        await _service.SetActiveAsync(1, created.Value.Id, isActive: false);

        var result = await _service.SetActiveAsync(1, created.Value.Id, isActive: true);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        Assert.True(list.Single().IsActive);
    }

    [Fact]
    public async Task Fails_when_the_expense_type_does_not_belong_to_the_business()
    {
        var created = await _service.CreateAsync(1, "Alquiler");

        var result = await _service.RenameAsync(businessId: 2, created.Value.Id, "Otro nombre");

        Assert.False(result.Succeeded);
        Assert.Equal("expense_type_not_found", result.Error!.Code);
    }
}
