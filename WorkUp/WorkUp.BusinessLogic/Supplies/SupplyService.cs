using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;

namespace WorkUp.BusinessLogic.Supplies;

public sealed record DeclareSupplyRequest(string Name, decimal CurrentStock, decimal MinimumStock);

/// <summary>Declared supply stock (RF-14). No appointment or service changes stock automatically (D-03).</summary>
public sealed class SupplyService(ISupplyRepository supplies, IClock clock)
{
    public Task<List<Supply>> ListAsync(int businessId, CancellationToken ct = default) =>
        supplies.ListByBusinessAsync(businessId, ct);

    public Task<int> CountBelowMinimumAsync(int businessId, CancellationToken ct = default) =>
        supplies.CountBelowMinimumAsync(businessId, ct);

    public Task<Supply?> GetAsync(int businessId, int id, CancellationToken ct = default) =>
        supplies.GetByIdAsync(businessId, id, ct);

    public async Task<Result<Supply>> CreateAsync(int businessId, DeclareSupplyRequest request, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(businessId, request, null, ct);
        if (validation.Error is not null)
            return Result<Supply>.Failure(validation.Error);

        var supply = new Supply
        {
            BusinessId = businessId,
            Name = validation.Name,
            CurrentStock = request.CurrentStock,
            MinimumStock = request.MinimumStock,
            LastDeclaredAt = clock.Now,
            IsActive = true
        };
        supply.Id = await supplies.InsertAsync(supply, ct);
        return Result<Supply>.Success(supply);
    }

    public async Task<Result> UpdateAsync(int businessId, int id, DeclareSupplyRequest request, CancellationToken ct = default)
    {
        var supply = await supplies.GetByIdAsync(businessId, id, ct);
        if (supply is null)
            return Result.Failure("supply_not_found", "No encontramos ese insumo.");

        var validation = await ValidateAsync(businessId, request, id, ct);
        if (validation.Error is not null)
            return Result.Failure(validation.Error);

        supply.Name = validation.Name;
        supply.CurrentStock = request.CurrentStock;
        supply.MinimumStock = request.MinimumStock;
        supply.LastDeclaredAt = clock.Now;
        await supplies.UpdateAsync(supply, ct);
        return Result.Success();
    }

    private async Task<(string Name, BusinessError? Error)> ValidateAsync(int businessId, DeclareSupplyRequest request, int? excludingId, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
            return (name, new BusinessError("supply_name_required", "Ingresá un nombre para el insumo.", nameof(request.Name)));
        if (name.Length > 100)
            return (name, new BusinessError("supply_name_too_long", "El nombre no puede superar los 100 caracteres.", nameof(request.Name)));
        if (!SupplyRules.IsValidStock(request.CurrentStock, request.MinimumStock))
            return (name, new BusinessError("invalid_supply_stock", "La existencia y el mínimo deben tener hasta dos decimales, no ser negativos ni superar 99.999.999,99."));
        if (await supplies.NameExistsAsync(businessId, name, excludingId, ct))
            return (name, new BusinessError("supply_name_taken", "Ya existe un insumo con ese nombre.", nameof(request.Name)));
        return (name, null);
    }
}
