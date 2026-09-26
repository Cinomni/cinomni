using System.Security.Cryptography;
using System.Text;

namespace Cinomni.ReleaseParsing.Persistence;

/// <summary>
/// Derives a stable id from a string, so a crash-retry of the same parse reuses the same audit
/// row and the same event idempotency key (rather than minting a fresh GUID each attempt).
/// </summary>
internal static class DeterministicId
{
    public static Guid From(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
