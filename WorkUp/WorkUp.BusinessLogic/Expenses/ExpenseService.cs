using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Expenses;

public sealed record CreateExpenseRequest(int ExpenseTypeId, DateOnly SpentOn, decimal Amount, string Description);

/// <summary>Owner-recorded expenses (RF-13). Archived types remain visible in history but cannot receive new expenses.</summary>
public sealed class ExpenseService(IExpenseRepository expenses, IExpenseTypeRepository expenseTypes, IClock clock)
{
    public Task<List<ExpenseListItem>> ListAsync(int businessId, DateOnly? from, DateOnly? to, CancellationToken ct = default) =>
        expenses.ListByBusinessAsync(businessId, from, to, ct);

    public async Task<List<ExpenseType>> ListAvailableTypesAsync(int businessId, CancellationToken ct = default) =>
        (await expenseTypes.ListByBusinessAsync(businessId, ct)).Where(type => type.IsActive).ToList();

    public async Task<Result<Expense>> CreateAsync(int businessId, CreateExpenseRequest request, CancellationToken ct = default)
    {
        var expenseType = await expenseTypes.GetByIdAsync(businessId, request.ExpenseTypeId, ct);
        if (expenseType is null || !expenseType.IsActive)
            return Result<Expense>.Failure("expense_type_not_available", "Elegí un tipo de gasto activo.", nameof(request.ExpenseTypeId));

        if (!ExpenseRules.IsValidAmount(request.Amount))
            return Result<Expense>.Failure("invalid_expense_amount", "El monto debe ser mayor a cero, tener hasta dos decimales y no superar 9.999.999.999,99.", nameof(request.Amount));

        var description = request.Description.Trim();
        if (!ExpenseRules.HasDescription(description))
            return Result<Expense>.Failure("expense_description_required", "Ingresá una descripción.", nameof(request.Description));
        if (description.Length > 300)
            return Result<Expense>.Failure("expense_description_too_long", "La descripción no puede superar los 300 caracteres.", nameof(request.Description));

        var expense = new Expense
        {
            BusinessId = businessId,
            ExpenseTypeId = expenseType.Id,
            SpentOn = request.SpentOn,
            Amount = request.Amount,
            Description = description,
            CreatedAt = clock.Now
        };
        expense.Id = await expenses.InsertAsync(expense, ct);
        return Result<Expense>.Success(expense);
    }
}
