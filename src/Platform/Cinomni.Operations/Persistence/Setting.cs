namespace Cinomni.Operations.Persistence;

/// <summary>
/// A stored override for one setting key, or nothing at all when no row exists — absence means the
/// installation still runs on whatever configuration/default already supplied it. A row is exactly one
/// of plaintext or ciphertext (<c>ck_setting_value_xor_secret</c>), so a secret can never silently land
/// in <see cref="Value"/>.
/// <para>
/// This is a raw key/value row: Operations never learns which module the key belongs to or what its
/// options type looks like (rule: keep the kernel free of module concepts). The catalogue of what a
/// key means lives in code, as a <see cref="Cinomni.Operations.Settings.SettingDefinition"/>.
/// </para>
/// </summary>
public sealed class Setting
{
    /// <summary>Dotted lower-camel identity, e.g. <c>metadata.tmdb.apiKey</c> (primary key).</summary>
    public required string Key { get; init; }

    /// <summary>Plaintext value for a non-secret kind. Null for a secret row.</summary>
    public string? Value { get; set; }

    /// <summary>AES-256-GCM ciphertext (with tag) for a secret kind. Null for a plaintext row.</summary>
    public byte[]? SecretCipher { get; set; }

    /// <summary>The 12-byte nonce used for <see cref="SecretCipher"/>. Null for a plaintext row.</summary>
    public byte[]? SecretNonce { get; set; }

    /// <summary>Which master key wrote <see cref="SecretCipher"/> (<c>env:&lt;8 hex&gt;</c>). Null for a plaintext row.</summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// Display fingerprint (8 hex of a keyed hash of the plaintext), so an operator can visually confirm
    /// a value without it ever being echoed back over HTTP.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>Optimistic concurrency token; incremented on every write.</summary>
    public long Version { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The Identity account that made this write, carried as an opaque id (no cross-schema join).</summary>
    public Guid UpdatedBy { get; set; }
}
