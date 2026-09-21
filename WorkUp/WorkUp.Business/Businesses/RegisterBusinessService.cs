using WorkUp.Business.Abstractions;
using WorkUp.Business.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;
// See IBusinessRepository.cs for why the entity is aliased instead of imported by its bare name.
using BusinessEntity = WorkUp.Domain.Entities.Business;

namespace WorkUp.Business.Businesses;

public sealed record RegisterBusinessRequest(
    string Name,
    string Slug,
    string ContactPhone,
    string? NotificationEmail,
    string Username,
    string Password,
    string ConfirmPassword);

/// <summary>Business registration and its single owner account, in one transaction (RF-01, RNF Seguridad).</summary>
public sealed class RegisterBusinessService(
    IBusinessRepository businesses,
    IOwnerAccountRepository ownerAccounts,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IClock clock)
{
    public async Task<Result<OwnerSession>> RegisterAsync(RegisterBusinessRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
            return Result<OwnerSession>.Failure("business_name_required", "Ingresá el nombre del negocio.", nameof(request.Name));

        var slug = request.Slug.Trim();
        if (!SlugFormat.IsValid(slug))
            return Result<OwnerSession>.Failure("invalid_slug", "El slug solo puede tener minúsculas, números y guiones, sin espacios.", nameof(request.Slug));

        if (await businesses.SlugExistsAsync(slug, ct: ct))
            return Result<OwnerSession>.Failure("slug_taken", "Ese slug ya está en uso. Elegí otro.", nameof(request.Slug));

        var phone = request.ContactPhone.Trim();
        if (phone.Length == 0)
            return Result<OwnerSession>.Failure("contact_phone_required", "Ingresá un teléfono de contacto.", nameof(request.ContactPhone));

        var username = request.Username.Trim();
        if (username.Length == 0)
            return Result<OwnerSession>.Failure("username_required", "Ingresá un usuario.", nameof(request.Username));

        if (await ownerAccounts.UsernameExistsAsync(username, ct))
            return Result<OwnerSession>.Failure("username_taken", "Ese usuario ya está en uso. Elegí otro.", nameof(request.Username));

        if (request.Password.Length < 8)
            return Result<OwnerSession>.Failure("password_too_short", "La contraseña debe tener al menos 8 caracteres.", nameof(request.Password));

        if (request.Password != request.ConfirmPassword)
            return Result<OwnerSession>.Failure("password_mismatch", "Las contraseñas no coinciden.", nameof(request.ConfirmPassword));

        var notificationEmail = string.IsNullOrWhiteSpace(request.NotificationEmail) ? null : Text.NormalizeEmail(request.NotificationEmail);
        var now = clock.Now;

        var business = new BusinessEntity
        {
            Name = name,
            Slug = slug,
            ContactPhone = phone,
            NotificationEmail = notificationEmail,
            CreatedAt = now
        };

        await using var tx = await unitOfWork.BeginAsync(ct: ct);
        business.Id = await businesses.InsertAsync(business, ct);

        var owner = new OwnerAccount
        {
            BusinessId = business.Id,
            Username = username,
            PasswordHash = passwordHasher.Hash(request.Password),
            CreatedAt = now
        };
        owner.Id = await ownerAccounts.InsertAsync(owner, ct);
        await tx.CommitAsync(ct);

        return Result<OwnerSession>.Success(new OwnerSession(business.Id, business.Name, business.Slug, owner.Id, owner.Username));
    }
}
