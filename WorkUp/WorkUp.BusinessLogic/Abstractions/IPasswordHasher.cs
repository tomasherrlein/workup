namespace WorkUp.BusinessLogic.Abstractions;

/// <summary>Maintained password hashing (RNF Seguridad). Implemented with ASP.NET Core Identity's hasher.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}
