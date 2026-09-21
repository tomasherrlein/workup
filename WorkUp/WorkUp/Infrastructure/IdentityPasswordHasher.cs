using Microsoft.AspNetCore.Identity;
using WorkUp.Business.Abstractions;

namespace WorkUp.Infrastructure;

/// <summary>ASP.NET Core Identity's maintained PBKDF2 hasher, used without Entity Framework (RNF Seguridad).</summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<Account> Hasher = new();
    private static readonly Account Subject = new();

    public string Hash(string password) => Hasher.HashPassword(Subject, password);

    public bool Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(Subject, hash, password) is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;

    private sealed class Account;
}
