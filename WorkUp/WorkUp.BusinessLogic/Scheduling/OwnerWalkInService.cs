using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Owner-only at-arrival registration; businessId must come from the authenticated owner.</summary>
public sealed class OwnerWalkInService(IOwnerWalkInRepository repository, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task<Result<OwnerWalkInOutcome>> RegisterAsync(int businessId, OwnerWalkInRequest request,
        CancellationToken ct = default)
    {
        if (businessId <= 0 || request.ClientId <= 0 || request.ServiceId <= 0 || request.WorkerId <= 0)
            return Result<OwnerWalkInOutcome>.Failure("walk_in_not_found", "Revisá el cliente, servicio y trabajadora.");
        if (request.AgreedPrice is { } amount && (amount <= 0 || amount > 9999999999.99m ||
            decimal.Round(amount, 2) != amount) || request.PriceReason?.Length > 200)
            return Result<OwnerWalkInOutcome>.Failure("walk_in_invalid_price", "Revisá el precio acordado y su motivo.");
        if (request.NextVisitNote?.Length > 1000)
            return Result<OwnerWalkInOutcome>.Failure("walk_in_invalid_note", "La observación no puede superar 1000 caracteres.");

        await using var tx = await unitOfWork.BeginAsync(ct: ct);
        var context = await repository.LockContextAsync(businessId, request.ClientId, request.ServiceId, request.WorkerId, ct);
        if (context is null || !context.ClientExists || !context.WorkerActive || !context.ServiceActive ||
            context.MaxCapacity != 1 || context.DurationMinutes <= 0)
            return Result<OwnerWalkInOutcome>.Failure("walk_in_not_found", "Revisá el cliente, servicio y trabajadora.");
        if (!context.AllowsWalkIns)
            return Result<OwnerWalkInOutcome>.Failure("walk_in_disabled", "Este negocio no admite atención sin turno.");
        var now = clock.Now;
        var start = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
        DateTime end;
        try { end = start.AddMinutes(context.DurationMinutes); }
        catch (ArgumentOutOfRangeException) { return Result<OwnerWalkInOutcome>.Failure("walk_in_invalid_time", "No se puede registrar esa hora."); }
        var price = request.AgreedPrice ?? context.ListPrice;
        if (price <= 0 || price > 9999999999.99m || decimal.Round(price, 2) != price)
            return Result<OwnerWalkInOutcome>.Failure("walk_in_invalid_price", "Revisá el precio acordado y su motivo.");
        var overlap = await repository.HasOverlapAsync(businessId, request.WorkerId, start, end, ct);
        var entry = new OwnerWalkInEntry(businessId, request.ClientId, request.ServiceId, request.WorkerId,
            start, end, price, context.ListPrice, request.PriceReason, request.CollectPayment, request.NextVisitNote?.Trim());
        var ids = await repository.InsertAsync(entry, ct);
        await tx.CommitAsync(ct);
        return Result<OwnerWalkInOutcome>.Success(new(ids.AppointmentId, ids.BookingId, overlap, start));
    }
}
