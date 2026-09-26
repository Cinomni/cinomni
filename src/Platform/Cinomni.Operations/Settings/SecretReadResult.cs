namespace Cinomni.Operations.Settings;

/// <summary>Why a stored secret could, or could not, be decrypted. Never guessed from the failure mode
/// of the underlying cryptography — the caller-visible distinctions below are established by explicit
/// checks so a mismatch is reported for what it is, not surfaced as a generic decrypt failure.</summary>
public enum SecretReadOutcome
{
    /// <summary>Decrypted successfully; <see cref="SecretReadResult.Plaintext"/> is set.</summary>
    Ok,

    /// <summary>No master key is loaded (<see cref="SecretMasterKey.EnvironmentVariableName"/> absent or
    /// invalid) — decryption was never attempted.</summary>
    KeyUnavailable,

    /// <summary>The row's <c>key_id</c> names a master key different from the one currently loaded — the
    /// installation lost or rotated <see cref="SecretMasterKey.EnvironmentVariableName"/> since this row
    /// was written. Detected from <c>key_id</c> before any cryptography runs, not inferred from an
    /// authentication failure.</summary>
    KeyMismatch,

    /// <summary>The ciphertext failed authentication under the matching key: a corrupted row, or a value
    /// stored under one setting key's row and read back under another (the additional-authenticated-data
    /// binding doing its job).</summary>
    Tampered,
}

/// <summary>The outcome of decrypting one stored secret. Never carries a value on failure.</summary>
public readonly record struct SecretReadResult
{
    private SecretReadResult(SecretReadOutcome outcome, string? plaintext)
    {
        Outcome = outcome;
        Plaintext = plaintext;
    }

    public SecretReadOutcome Outcome { get; }

    /// <summary>Set only when <see cref="Outcome"/> is <see cref="SecretReadOutcome.Ok"/>.</summary>
    public string? Plaintext { get; }

    public bool IsReadable => Outcome == SecretReadOutcome.Ok;

    public static SecretReadResult Ok(string plaintext) => new(SecretReadOutcome.Ok, plaintext);

    public static SecretReadResult Failed(SecretReadOutcome outcome)
    {
        if (outcome == SecretReadOutcome.Ok)
        {
            throw new ArgumentException("Use Ok(plaintext) to report a successful read.", nameof(outcome));
        }

        return new SecretReadResult(outcome, null);
    }
}
