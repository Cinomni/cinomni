using System.Security.Cryptography;
using System.Text;

namespace Cinomni.Identity.Security;

/// <summary>Creates opaque session tokens and derives the hash we persist for them.</summary>
public interface ITokenFactory
{
    /// <summary>Creates a fresh opaque token and its hash. The token is returned once; only the hash is stored.</summary>
    (string Token, string TokenHash) Create();

    /// <summary>Derives the stored hash of a presented token, for lookup during validation.</summary>
    string Hash(string token);
}

public sealed class TokenFactory : ITokenFactory
{
    // 256 bits of entropy — well above the ">=128 bits" the design requires.
    private const int TokenBytes = 32;

    public (string Token, string TokenHash) Create()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        return (token, Hash(token));
    }

    public string Hash(string token)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(digest);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
