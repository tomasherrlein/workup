using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Businesses;

/// <summary>Expense type catalogue of a business (RF-01, RF-13). Never deleted, only archived.</summary>
public sealed class ExpenseTypeService(IExpenseTypeRepository expenseTypes)
{
    public Task<List<ExpenseType>> ListAsync(int businessId, CancellationToken ct = default) =>
        expenseTypes.ListByBusinessAsync(businessId, ct);

    public async Task<Result<ExpenseType>> CreateAsync(int businessId, string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return Result<ExpenseType>.Failure("expense_type_name_required", "Ingresá un nombre para el tipo de gasto.", "Name");

        if (await expenseTypes.NameExistsAsync(businessId, trimmed, ct: ct))
            return Result<ExpenseType>.Failure("expense_type_name_taken", "Ya existe un tipo de gasto con ese nombre.", "Name");

        var expenseType = new ExpenseType { BusinessId = businessId, Name = trimmed, IsActive = true };
        expenseType.Id = await expenseTypes.InsertAsync(expenseType, ct);
        return Result<ExpenseType>.Success(expenseType);
    }

    public async Task<Result> RenameAsync(int businessId, int id, string name, CancellationToken ct = default)
    {
        var expenseType = await expenseTypes.GetByIdAsync(businessId, id, ct);
        if (expenseType is null)
            return Result.Failure("expense_type_not_found", "No encontramos ese tipo de gasto.");

        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return Result.Failure("expense_type_name_required", "Ingresá un nombre para el tipo de gasto.", "Name");

        if (await expenseTypes.NameExistsAsync(businessId, trimmed, excludingId: id, ct: ct))
            return Result.Failure("expense_type_name_taken", "Ya existe un tipo de gasto con ese nombre.", "Name");

        expenseType.Name = trimmed;
        await expenseTypes.UpdateAsync(expenseType, ct);
        return Result.Success();
    }

    public async Task<Result> SetActiveAsync(int businessId, int id, bool isActive, CancellationToken ct = default)
    {
        var expenseType = await expenseTypes.GetByIdAsync(businessId, id, ct);
        if (expenseType is null)
            return Result.Failure("expense_type_not_found", "No encontramos ese tipo de gasto.");

        expenseType.IsActive = isActive;
        await expenseTypes.UpdateAsync(expenseType, ct);
        return Result.Success();
    }
}
