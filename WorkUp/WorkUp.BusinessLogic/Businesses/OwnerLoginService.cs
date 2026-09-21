using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Common;

namespace WorkUp.BusinessLogic.Businesses;

/// <summary>Owner sign-in with a non-enumerating error, so a wrong username never reveals whether it exists.</summary>
public sealed class OwnerLoginService(
    IOwnerAccountRepository ownerAccounts,
    IBusinessRepository businesses,
    IPasswordHasher passwordHasher)
{
    private static readonly BusinessError InvalidCredentials = new("invalid_credentials", "Usuario o contraseña incorrectos.");

    public async Task<Result<OwnerSession>> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var account = await ownerAccounts.GetByUsernameAsync(username.Trim(), ct);
        if (account is null || !passwordHasher.Verify(account.PasswordHash, password))
            return Result<OwnerSession>.Failure(InvalidCredentials);

        var business = await businesses.GetByIdAsync(account.BusinessId, ct)
            ?? throw new InvalidOperationException($"Owner account {account.Id} has no business {account.BusinessId}.");

        return Result<OwnerSession>.Success(new OwnerSession(business.Id, business.Name, business.Slug, account.Id, account.Username));
    }
}
