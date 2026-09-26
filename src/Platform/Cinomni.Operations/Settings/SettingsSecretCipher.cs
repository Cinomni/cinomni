using System.Security.Cryptography;
using System.Text;
using Cinomni.Kernel.Results;

namespace Cinomni.Operations.Settings;

/// <summary>
/// Encrypts and decrypts secret-kind setting values with AES-256-GCM, the setting's own key as
/// additional authenticated data, and a fresh 12-byte nonce per write. Registered as a singleton built
/// once from <see cref="SecretMasterKey.TryLoadFromEnvironment"/>.
/// <para>
/// THE DEGRADED MODE IS THE POINT: a missing or invalid <see cref="SecretMasterKey.EnvironmentVariableName"/>
/// never stops the process from starting. <see cref="Encrypt"/> refuses with a documented failure code
/// naming the variable; <see cref="Decrypt"/> reports every stored secret as unreadable rather than
/// throwing. An installation that never uses a secret setting is unaffected either way.
/// </para>
/// </summary>
public sealed class SettingsSecretCipher
{
    /// <summary>The failure code <see cref="Encrypt"/> returns when no master key is loaded.</summary>
    public const string SecretsUnavailableErrorCode = "settings.secrets_unavailable";

    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly SecretMasterKey? _masterKey;

    public SettingsSecretCipher(SecretMasterKey? masterKey, string? unavailableReason)
    {
        _masterKey = masterKey;
        UnavailableReason = unavailableReason;
    }

    /// <summary>False when no master key was loaded from the environment at startup.</summary>
    public bool IsAvailable => _masterKey is not null;

    /// <summary>
    /// Set only when <see cref="IsAvailable"/> is false. Safe to log: names the environment variable and
    /// why it was rejected, never a raw value (SECURITY.md).
    /// </summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> for storage under <paramref name="settingKey"/>. Pure CPU,
    /// meant to run before a write transaction opens so the transaction stays short.
    /// </summary>
    public Result<EncryptedSecret> Encrypt(string settingKey, string plaintext)
    {
        if (_masterKey is not { } key)
        {
            return Result<EncryptedSecret>.Failure(new Error(
                SecretsUnavailableErrorCode,
                $"Secret settings cannot be written: {SecretMasterKey.EnvironmentVariableName} is not set."));
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var aad = Encoding.UTF8.GetBytes(settingKey);
        var cipherBytes = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];

        using (var aesGcm = new AesGcm(key.KeyBytes, TagLength))
        {
            aesGcm.Encrypt(nonce, plaintextBytes, cipherBytes, tag, aad);
        }

        // secret_cipher is one bytea column: the tag travels appended to the ciphertext rather than in
        // a column of its own.
        var cipherWithTag = new byte[cipherBytes.Length + TagLength];
        cipherBytes.CopyTo(cipherWithTag, 0);
        tag.CopyTo(cipherWithTag, cipherBytes.Length);

        return Result<EncryptedSecret>.Success(new EncryptedSecret(
            cipherWithTag, nonce, key.KeyId, ComputeFingerprint(settingKey, plaintext)));
    }

    /// <summary>
    /// Decrypts one stored secret row. Never throws: every failure — an absent key, a rotated key, or a
    /// tampered/misplaced ciphertext — comes back as a <see cref="SecretReadOutcome"/> a caller can act
    /// on (a missing/mismatched key degrades the row to "not in effect"; the store never crashes on it).
    /// </summary>
    public SecretReadResult Decrypt(string settingKey, byte[] cipherWithTag, byte[] nonce, string? storedKeyId)
    {
        if (_masterKey is not { } key)
        {
            return SecretReadResult.Failed(SecretReadOutcome.KeyUnavailable);
        }

        // Checked before any cryptography runs, so a rotated/lost key is reported for what it is rather
        // than surfacing as an indistinguishable authentication failure.
        if (!string.Equals(storedKeyId, key.KeyId, StringComparison.Ordinal))
        {
            return SecretReadResult.Failed(SecretReadOutcome.KeyMismatch);
        }

        if (nonce.Length != NonceLength || cipherWithTag.Length < TagLength)
        {
            return SecretReadResult.Failed(SecretReadOutcome.Tampered);
        }

        var cipherLength = cipherWithTag.Length - TagLength;
        var plaintextBytes = new byte[cipherLength];
        var aad = Encoding.UTF8.GetBytes(settingKey);

        try
        {
            using var aesGcm = new AesGcm(key.KeyBytes, TagLength);
            aesGcm.Decrypt(
                nonce,
                cipherWithTag.AsSpan(0, cipherLength),
                cipherWithTag.AsSpan(cipherLength, TagLength),
                plaintextBytes,
                aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            // Covers both a corrupted row and the AAD-binding case: the same ciphertext/nonce pasted
            // under a different setting key's row fails authentication here, because settingKey feeds
            // the AAD and no longer matches what was used to encrypt it.
            return SecretReadResult.Failed(SecretReadOutcome.Tampered);
        }
        catch (CryptographicException)
        {
            return SecretReadResult.Failed(SecretReadOutcome.Tampered);
        }

        return SecretReadResult.Ok(Encoding.UTF8.GetString(plaintextBytes));
    }

    /// <summary>
    /// 8 hex of SHA-256(settingKey || 0x00 || plaintext) — domain-separated by the setting's own key so
    /// the same value under two different keys never displays the same fingerprint, and a one-way hash
    /// so it can be shown to an operator without disclosing the value itself (SECURITY.md).
    /// </summary>
    private static string ComputeFingerprint(string settingKey, string plaintext)
    {
        var keyBytes = Encoding.UTF8.GetBytes(settingKey);
        var valueBytes = Encoding.UTF8.GetBytes(plaintext);
        var buffer = new byte[keyBytes.Length + 1 + valueBytes.Length];
        Buffer.BlockCopy(keyBytes, 0, buffer, 0, keyBytes.Length);
        buffer[keyBytes.Length] = 0;
        Buffer.BlockCopy(valueBytes, 0, buffer, keyBytes.Length + 1, valueBytes.Length);

        return Convert.ToHexStringLower(SHA256.HashData(buffer))[..8];
    }
}
