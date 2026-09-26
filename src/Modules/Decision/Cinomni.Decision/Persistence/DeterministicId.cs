using System.Security.Cryptography;
using System.Text;

namespace Cinomni.Decision.Persistence;

/// <summary>
/// Derives a stable id from its inputs, so re-evaluating the same search+release reuses the same
/// evaluation row and event key rather than minting a fresh GUID on a crash-retry.
/// </summary>
internal static class DeterministicId
{
    public static Guid From(Guid searchId, string releaseGuid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{searchId:N}:{releaseGuid}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
