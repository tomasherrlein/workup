using WorkUp.Business.Abstractions;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// Deterministic fake hasher: never a real algorithm, only good enough to prove a service hashes instead of storing
/// the plain password and later verifies it back.
/// </summary>
public sealed class FakePasswordHasher : IPasswordHasher
{
    public string Hash(string password) => $"hashed:{password}";

    public bool Verify(string hash, string password) => hash == Hash(password);
}
