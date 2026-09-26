using System.Security.Cryptography;

namespace Cinomni.Operations.Settings;

/// <summary>
/// The 32-byte AES-256 key that encrypts every secret setting, loaded once from
/// <see cref="EnvironmentVariableName"/> at composition time. The raw bytes never leave this type and
/// <see cref="SettingsSecretCipher"/> — never logged, never placed in an exception message, never
/// returned to a caller (SECURITY.md).
/// </summary>
public sealed class SecretMasterKey
{
    /// <summary>32 bytes, base64-encoded. Absent or invalid degrades secret settings; it never stops
    /// the process from starting (see <see cref="SettingsSecretCipher"/>).</summary>
    public const string EnvironmentVariableName = "CINOMNI_SECRET_KEY";

    private const int RequiredKeyLength = 32;

    // 8 hex characters = 4 bytes of the SHA-256 digest: enough to tell one master key apart from
    // another without exposing anything closer to the key itself than a truncated hash of it.
    private const int KeyIdHexLength = 8;

    private readonly byte[] _keyBytes;

    private SecretMasterKey(byte[] keyBytes, string keyId)
    {
        _keyBytes = keyBytes;
        KeyId = keyId;
    }

    /// <summary>Which master key encrypted a row: <c>env:&lt;8 hex of SHA-256(key)&gt;</c>.</summary>
    public string KeyId { get; }

    /// <summary>The raw key material. Internal to this assembly; only <see cref="SettingsSecretCipher"/> reads it.</summary>
    internal ReadOnlySpan<byte> KeyBytes => _keyBytes;

    /// <summary>
    /// Loads and validates <see cref="EnvironmentVariableName"/>. Never throws: returns null when the
    /// variable is absent or malformed, with <paramref name="unavailableReason"/> set to a message safe
    /// to log — it names the variable and the problem, never the raw value the operator set.
    /// </summary>
    public static SecretMasterKey? TryLoadFromEnvironment(out string? unavailableReason)
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            unavailableReason = $"{EnvironmentVariableName} is not set.";
            return null;
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(raw.Trim());
        }
        catch (FormatException)
        {
            unavailableReason = $"{EnvironmentVariableName} is not valid base64.";
            return null;
        }

        if (keyBytes.Length != RequiredKeyLength)
        {
            unavailableReason =
                $"{EnvironmentVariableName} must decode to {RequiredKeyLength} bytes (AES-256); got {keyBytes.Length}.";
            return null;
        }

        var digest = SHA256.HashData(keyBytes);
        var keyId = "env:" + Convert.ToHexStringLower(digest)[..KeyIdHexLength];

        unavailableReason = null;
        return new SecretMasterKey(keyBytes, keyId);
    }
}
