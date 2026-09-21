using System.Security.Cryptography;
using System.Text;

namespace WorkUp.Business.Security;

/// <summary>A random URL-safe token and the SHA-256 hash stored to look it up.</summary>
public readonly record struct TokenPair(string Token, byte[] Hash);

/// <summary>
/// Tokens for private links, invitations, email verification and password reset. Only the hash is stored for
/// lookup; comparing hashes avoids keeping usable secrets in the database.
/// </summary>
public static class SecureToken
{
    public static TokenPair Create()
    {
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return new TokenPair(token, Hash(token));
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
