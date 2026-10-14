using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Catalog;

public sealed record CreateServiceRequest(string Name, decimal Price, int DurationMinutes, int MaxCapacity);

public sealed record UpdateServiceRequest(string Name, decimal Price, int DurationMinutes, int MaxCapacity);

/// <summary>
/// Tarifario: services and class types (RF-02). Max capacity 1 is an individual turn; greater than 1 is a class.
/// A service is archived, never deleted, so its history is kept and it stops being offered for new bookings.
/// T27: a service still used by an active grid class cannot be archived, because that would leave the class without
/// a valid class type; the class has to be archived first.
/// </summary>
public sealed class CatalogService(IServiceRepository services, IUnitOfWork unitOfWork)
{
    public Task<List<Service>> ListAsync(int businessId, CancellationToken ct = default) =>
        services.ListByBusinessAsync(businessId, ct);

    public Task<Service?> GetServiceAsync(int businessId, int id, CancellationToken ct = default) =>
        services.GetByIdAsync(businessId, id, ct);

    /// <summary>Drives whether the owner navigation shows Clases and Abonos (T02 acceptance).</summary>
    public Task<bool> HasGroupServicesAsync(int businessId, CancellationToken ct = default) =>
        services.HasActiveGroupServiceAsync(businessId, ct);

    public async Task<Result<Service>> CreateAsync(int businessId, CreateServiceRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
            return Result<Service>.Failure("service_name_required", "Ingresá un nombre para el servicio.", nameof(request.Name));

        if (await services.NameExistsAsync(businessId, name, ct: ct))
            return Result<Service>.Failure("service_name_taken", "Ya existe un servicio con ese nombre.", nameof(request.Name));

        if (request.Price <= 0)
            return Result<Service>.Failure("invalid_service_price", "El precio debe ser mayor a cero.", nameof(request.Price));

        if (request.DurationMinutes <= 0)
            return Result<Service>.Failure("invalid_service_duration", "La duración debe ser mayor a cero.", nameof(request.DurationMinutes));

        if (request.MaxCapacity < 1)
            return Result<Service>.Failure("invalid_service_max_capacity", "El cupo máximo debe ser 1 o más.", nameof(request.MaxCapacity));

        var service = new Service
        {
            BusinessId = businessId,
            Name = name,
            Price = request.Price,
            DurationMinutes = request.DurationMinutes,
            MaxCapacity = request.MaxCapacity,
            IsActive = true
        };
        service.Id = await services.InsertAsync(service, ct);
        return Result<Service>.Success(service);
    }

    public async Task<Result> UpdateAsync(int businessId, int id, UpdateServiceRequest request, CancellationToken ct = default)
    {
        var service = await services.GetByIdAsync(businessId, id, ct);
        if (service is null)
            return Result.Failure("service_not_found", "No encontramos ese servicio.");

        var name = request.Name.Trim();
        if (name.Length == 0)
            return Result.Failure("service_name_required", "Ingresá un nombre para el servicio.", nameof(request.Name));

        if (await services.NameExistsAsync(businessId, name, excludingId: id, ct: ct))
            return Result.Failure("service_name_taken", "Ya existe un servicio con ese nombre.", nameof(request.Name));

        if (request.Price <= 0)
            return Result.Failure("invalid_service_price", "El precio debe ser mayor a cero.", nameof(request.Price));

        if (request.DurationMinutes <= 0)
            return Result.Failure("invalid_service_duration", "La duración debe ser mayor a cero.", nameof(request.DurationMinutes));

        if (request.MaxCapacity < 1)
            return Result.Failure("invalid_service_max_capacity", "El cupo máximo debe ser 1 o más.", nameof(request.MaxCapacity));

        // Once a service has appointments or grid classes, switching it between individual (1) and group (>1)
        // would change what kind of service it is under history that already assumed the old kind.
        var wasIndividual = service.MaxCapacity == 1;
        var willBeIndividual = request.MaxCapacity == 1;
        if (wasIndividual != willBeIndividual && await services.HasBookingHistoryAsync(businessId, id, ct))
            return Result.Failure(
                "service_capacity_kind_locked",
                "Este servicio ya tiene turnos o clases registradas: no se puede cambiar entre individual y grupal.",
                nameof(request.MaxCapacity));

        service.Name = name;
        service.Price = request.Price;
        service.DurationMinutes = request.DurationMinutes;
        service.MaxCapacity = request.MaxCapacity;
        await services.UpdateAsync(service, ct);
        return Result.Success();
    }

    public async Task<Result> SetActiveAsync(int businessId, int id, bool isActive, CancellationToken ct = default)
    {
        // T27: an active class type cannot break a class, so reactivation stays free and opens no transaction.
        if (isActive)
            return await ReactivateAsync(businessId, id, ct);

        return await ArchiveAsync(businessId, id, ct);
    }

    private async Task<Result> ReactivateAsync(int businessId, int id, CancellationToken ct)
    {
        var service = await services.GetByIdAsync(businessId, id, ct);
        if (service is null)
            return Result.Failure("service_not_found", "No encontramos ese servicio.");

        service.IsActive = true;
        await services.UpdateAsync(service, ct);
        return Result.Success();
    }

    /// <summary>
    /// T27: an active grid class must keep an active class type, so archiving the type it depends on is rejected and
    /// the owner archives the class first. The archive takes the service row lock inside one transaction before the
    /// guard and the write: a grid-class create or reactivation that committed just before the lock is seen by the
    /// guard, and one that arrives after this commit finds the archived service and is rejected. A rejection leaves
    /// <c>IsActive</c> untouched and rolls the transaction back.
    /// </summary>
    private async Task<Result> ArchiveAsync(int businessId, int id, CancellationToken ct)
    {
        await using var tx = await unitOfWork.BeginAsync(ct: ct);
        await services.LockForGridClassWriteAsync(businessId, id, ct);

        var service = await services.GetByIdAsync(businessId, id, ct);
        if (service is null)
            return Result.Failure("service_not_found", "No encontramos ese servicio.");

        if (await services.HasActiveGridClassAsync(businessId, id, ct))
            return Result.Failure(
                "service_has_active_grid_class",
                "Este servicio lo usa una clase activa de la grilla: para archivarlo, primero archivá esa clase.");

        service.IsActive = false;
        await services.UpdateAsync(service, ct);
        await tx.CommitAsync(ct);
        return Result.Success();
    }
}
