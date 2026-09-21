using WorkUp.Business.Common;
using WorkUp.Domain.Rules;
// See IBusinessRepository.cs for why the entity is aliased instead of imported by its bare name.
using BusinessEntity = WorkUp.Domain.Entities.Business;

namespace WorkUp.Business.Businesses;

public sealed record UpdateBusinessSettingsRequest(
    string Name,
    string Slug,
    string ContactPhone,
    string? NotificationEmail,
    bool AllowsWalkIns,
    int ReminderLeadHours,
    TimeOnly DailySummaryTime,
    int StockReviewEveryDays);

/// <summary>Edits the business's own operating parameters (RF-01).</summary>
public sealed class BusinessSettingsService(IBusinessRepository businesses)
{
    /// <summary>Current settings, to pre-fill the edit form. The business always exists for a signed-in owner.</summary>
    public async Task<BusinessEntity> GetAsync(int businessId, CancellationToken ct = default) =>
        await businesses.GetByIdAsync(businessId, ct)
            ?? throw new InvalidOperationException($"Business {businessId} not found.");

    public async Task<Result<BusinessEntity>> UpdateAsync(int businessId, UpdateBusinessSettingsRequest request, CancellationToken ct = default)
    {
        var business = await businesses.GetByIdAsync(businessId, ct)
            ?? throw new InvalidOperationException($"Business {businessId} not found.");

        var name = request.Name.Trim();
        if (name.Length == 0)
            return Result<BusinessEntity>.Failure("business_name_required", "Ingresá el nombre del negocio.", nameof(request.Name));

        var slug = request.Slug.Trim();
        if (!SlugFormat.IsValid(slug))
            return Result<BusinessEntity>.Failure("invalid_slug", "El slug solo puede tener minúsculas, números y guiones, sin espacios.", nameof(request.Slug));

        if (await businesses.SlugExistsAsync(slug, excludingId: businessId, ct: ct))
            return Result<BusinessEntity>.Failure("slug_taken", "Ese slug ya está en uso. Elegí otro.", nameof(request.Slug));

        var phone = request.ContactPhone.Trim();
        if (phone.Length == 0)
            return Result<BusinessEntity>.Failure("contact_phone_required", "Ingresá un teléfono de contacto.", nameof(request.ContactPhone));

        if (request.ReminderLeadHours is < 1 or > 168)
            return Result<BusinessEntity>.Failure("invalid_reminder_lead_hours", "La anticipación del recordatorio debe estar entre 1 y 168 horas.", nameof(request.ReminderLeadHours));

        if (request.StockReviewEveryDays is < 1 or > 90)
            return Result<BusinessEntity>.Failure("invalid_stock_review_every_days", "La periodicidad de revisión debe estar entre 1 y 90 días.", nameof(request.StockReviewEveryDays));

        business.Name = name;
        business.Slug = slug;
        business.ContactPhone = phone;
        business.NotificationEmail = string.IsNullOrWhiteSpace(request.NotificationEmail) ? null : Text.NormalizeEmail(request.NotificationEmail);
        business.AllowsWalkIns = request.AllowsWalkIns;
        business.ReminderLeadHours = request.ReminderLeadHours;
        business.DailySummaryTime = request.DailySummaryTime;
        business.StockReviewEveryDays = request.StockReviewEveryDays;

        await businesses.UpdateAsync(business, ct);
        return Result<BusinessEntity>.Success(business);
    }
}
