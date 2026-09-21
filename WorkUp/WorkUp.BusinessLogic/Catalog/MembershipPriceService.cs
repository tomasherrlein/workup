using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Catalog;

/// <summary>
/// Monthly membership price per weekly frequency (plan assumption 4). Only relevant for businesses with group
/// services; a frequency without a price is not offered when enrolling (T15).
/// </summary>
public sealed class MembershipPriceService(IMembershipPriceRepository prices)
{
    public Task<List<MembershipPrice>> ListAsync(int businessId, CancellationToken ct = default) =>
        prices.ListByBusinessAsync(businessId, ct);

    public async Task<Result<MembershipPrice>> SetPriceAsync(int businessId, int weeklyFrequency, decimal price, CancellationToken ct = default)
    {
        if (weeklyFrequency is < 1 or > 7)
            return Result<MembershipPrice>.Failure("invalid_membership_frequency", "La frecuencia semanal debe estar entre 1 y 7.", nameof(weeklyFrequency));

        if (price <= 0)
            return Result<MembershipPrice>.Failure("invalid_membership_price", "El precio debe ser mayor a cero.", nameof(price));

        var existing = await prices.GetByFrequencyAsync(businessId, weeklyFrequency, ct);
        if (existing is not null)
        {
            existing.Price = price;
            await prices.UpdateAsync(existing, ct);
            return Result<MembershipPrice>.Success(existing);
        }

        var created = new MembershipPrice { BusinessId = businessId, WeeklyFrequency = weeklyFrequency, Price = price };
        created.Id = await prices.InsertAsync(created, ct);
        return Result<MembershipPrice>.Success(created);
    }

    public async Task<Result> RemoveAsync(int businessId, int weeklyFrequency, CancellationToken ct = default)
    {
        var existing = await prices.GetByFrequencyAsync(businessId, weeklyFrequency, ct);
        if (existing is null)
            return Result.Failure("membership_price_not_found", "No encontramos ese precio de abono.");

        await prices.DeleteAsync(businessId, weeklyFrequency, ct);
        return Result.Success();
    }
}
