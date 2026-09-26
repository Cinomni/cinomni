using Cinomni.Operations.Settings;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Serializes every test that reads or writes CINOMNI_SECRET_KEY: it is process-wide state, so two
/// classes flipping it at the same time could each observe the other's value mid-composition.
/// </summary>
[CollectionDefinition(Serial, DisableParallelization = true)]
public sealed class SettingsSecretsCollection
{
    public const string Serial = "settings-secret-master-key";
}

/// <summary>
/// Pure unit tests for secret-setting encryption: no database, no environment left behind. Each test
/// proves a protection the design calls out by name — a tampered tag is rejected rather than returning
/// garbage, a value written under a different master key is detected through <c>key_id</c> rather than
/// failing obscurely, the AAD binding stops a ciphertext from decrypting under the wrong setting key,
/// and the absent-key degraded mode never throws.
/// </summary>
[Collection(SettingsSecretsCollection.Serial)]
public sealed class SettingsSecretCipherTests
{
    private static readonly byte[] KeyA = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void A_secret_round_trips_through_the_same_master_key()
    {
        var cipher = CipherFor(KeyA);

        var encrypted = cipher.Encrypt("metadata.tmdb.apiKey", "hunter2").Value;
        var read = cipher.Decrypt("metadata.tmdb.apiKey", encrypted.Cipher, encrypted.Nonce, encrypted.KeyId);

        Assert.True(read.IsReadable);
        Assert.Equal("hunter2", read.Plaintext);
    }

    [Fact]
    public void Encrypting_the_same_value_twice_produces_different_ciphertext_but_the_same_fingerprint()
    {
        var cipher = CipherFor(KeyA);

        var first = cipher.Encrypt("metadata.tmdb.apiKey", "hunter2").Value;
        var second = cipher.Encrypt("metadata.tmdb.apiKey", "hunter2").Value;

        Assert.NotEqual(Convert.ToHexString(first.Nonce), Convert.ToHexString(second.Nonce));
        Assert.NotEqual(Convert.ToHexString(first.Cipher), Convert.ToHexString(second.Cipher));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Fingerprint_differs_for_the_same_plaintext_under_two_different_setting_keys()
    {
        var cipher = CipherFor(KeyA);

        var forTmdb = cipher.Encrypt("metadata.tmdb.apiKey", "same-value").Value;
        var forTvdb = cipher.Encrypt("metadata.tvdb.apiKey", "same-value").Value;

        Assert.NotEqual(forTmdb.Fingerprint, forTvdb.Fingerprint);
    }

    [Fact]
    public void A_tampered_ciphertext_is_rejected_rather_than_returning_garbage()
    {
        var cipher = CipherFor(KeyA);
        var encrypted = cipher.Encrypt("metadata.tmdb.apiKey", "hunter2").Value;

        var tampered = (byte[])encrypted.Cipher.Clone();
        tampered[0] ^= 0xFF;

        var read = cipher.Decrypt("metadata.tmdb.apiKey", tampered, encrypted.Nonce, encrypted.KeyId);

        Assert.False(read.IsReadable);
        Assert.Equal(SecretReadOutcome.Tampered, read.Outcome);
        Assert.Null(read.Plaintext);
    }

    [Fact]
    public void A_value_written_under_a_different_master_key_is_detected_through_key_id()
    {
        var writer = CipherFor(KeyA);
        var reader = CipherFor(KeyB);

        var encrypted = writer.Encrypt("metadata.tmdb.apiKey", "hunter2").Value;
        var read = reader.Decrypt("metadata.tmdb.apiKey", encrypted.Cipher, encrypted.Nonce, encrypted.KeyId);

        // Detected from key_id before any cryptography runs, not surfaced as a generic tag failure —
        // the point being that "wrong key" and "tampered" are told apart, not conflated.
        Assert.Equal(SecretReadOutcome.KeyMismatch, read.Outcome);
        Assert.False(read.IsReadable);
    }

    [Fact]
    public void The_aad_binding_refuses_a_ciphertext_pasted_under_a_different_settings_key_row()
    {
        var cipher = CipherFor(KeyA);
        var encrypted = cipher.Encrypt("metadata.tmdb.apiKey", "hunter2").Value;

        // Same master key, same bytes — only the setting key (the AAD) the row claims to belong to
        // changes, exactly what pasting a value from one row into another would produce.
        var read = cipher.Decrypt("subtitles.provider.apiKey", encrypted.Cipher, encrypted.Nonce, encrypted.KeyId);

        Assert.Equal(SecretReadOutcome.Tampered, read.Outcome);
        Assert.False(read.IsReadable);
    }

    [Fact]
    public void An_absent_master_key_refuses_a_write_with_the_documented_code_naming_the_variable()
    {
        var cipher = new SettingsSecretCipher(masterKey: null, unavailableReason: "CINOMNI_SECRET_KEY is not set.");

        var result = cipher.Encrypt("metadata.tmdb.apiKey", "hunter2");

        Assert.True(result.IsFailure);
        Assert.Equal(SettingsSecretCipher.SecretsUnavailableErrorCode, result.Error.Code);
        Assert.Contains(SecretMasterKey.EnvironmentVariableName, result.Error.Message, StringComparison.Ordinal);
        Assert.False(cipher.IsAvailable);
    }

    [Fact]
    public void An_absent_master_key_reports_an_existing_row_as_unreadable_rather_than_throwing()
    {
        var writer = CipherFor(KeyA);
        var encrypted = writer.Encrypt("metadata.tmdb.apiKey", "hunter2").Value;

        var reader = new SettingsSecretCipher(masterKey: null, unavailableReason: "CINOMNI_SECRET_KEY is not set.");
        var read = reader.Decrypt("metadata.tmdb.apiKey", encrypted.Cipher, encrypted.Nonce, encrypted.KeyId);

        Assert.Equal(SecretReadOutcome.KeyUnavailable, read.Outcome);
        Assert.False(read.IsReadable);
        Assert.Null(read.Plaintext);
    }

    [Fact]
    public void TryLoadFromEnvironment_never_throws_and_names_the_variable_when_absent()
    {
        var previous = Environment.GetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, null);

            var key = SecretMasterKey.TryLoadFromEnvironment(out var reason);

            Assert.Null(key);
            Assert.NotNull(reason);
            Assert.Contains(SecretMasterKey.EnvironmentVariableName, reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void TryLoadFromEnvironment_rejects_a_key_of_the_wrong_length_without_leaking_it()
    {
        var previous = Environment.GetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName);
        const string TooShort = "dG9vLXNob3J0"; // base64 of "too-short" (9 bytes, not 32)
        try
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, TooShort);

            var key = SecretMasterKey.TryLoadFromEnvironment(out var reason);

            Assert.Null(key);
            Assert.NotNull(reason);
            Assert.DoesNotContain(TooShort, reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, previous);
        }
    }

    private static SettingsSecretCipher CipherFor(byte[] rawKey)
    {
        var previous = Environment.GetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, Convert.ToBase64String(rawKey));
            var masterKey = SecretMasterKey.TryLoadFromEnvironment(out var reason);
            return new SettingsSecretCipher(masterKey, reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, previous);
        }
    }
}
