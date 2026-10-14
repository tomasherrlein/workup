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
        // The precheck above is only a courtesy check: between it and the insert a competing create can commit the same
        // name. Losing the UQ_ExpenseTypes_Name race is the same business outcome, never a second type.
        var inserted = await expenseTypes.InsertAsync(expenseType, ct);
        if (!inserted.IsInserted)
            return Result<ExpenseType>.Failure("expense_type_name_taken", "Ya existe un tipo de gasto con ese nombre.", "Name");

        expenseType.Id = inserted.ExpenseTypeId;
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
        // Same lost window as the create: the rename must report the existing field error and leave the stored row as it
        // was, never silently repoint the request at another type.
        if (!(await expenseTypes.UpdateAsync(expenseType, ct)).IsUpdated)
            return Result.Failure("expense_type_name_taken", "Ya existe un tipo de gasto con ese nombre.", "Name");

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
