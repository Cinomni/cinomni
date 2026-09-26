using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Settings;

namespace Cinomni.Discovery.Application;

/// <summary>
/// Encrypts and decrypts one indexer's stored secret, reusing the platform's
/// <see cref="SettingsSecretCipher"/> rather than growing a second cryptography surface — one
/// master key, one algorithm, one place to review. The cipher itself is settings-agnostic: it takes
/// an opaque key string as additional authenticated data, and this type supplies the indexer's own
/// identity for it.
/// <para>
/// Binding to the indexer id is load-bearing. It means ciphertext lifted from one indexer's row and
/// pasted into another's fails authentication instead of silently decrypting, so a stolen row is not
/// a portable credential.
/// </para>
/// </summary>
public sealed class IndexerCredentialProtector(SettingsSecretCipher cipher)
{
    /// <summary>The error an operator gets when this installation has no master key to encrypt with.</summary>
    public const string SecretsUnavailableErrorCode = "discovery.secrets_unavailable";

    public bool IsAvailable => cipher.IsAvailable;

    public string? UnavailableReason => cipher.UnavailableReason;

    public Result<EncryptedSecret> Encrypt(Guid indexerId, string secret)
    {
        var encrypted = cipher.Encrypt(AadFor(indexerId), secret);

        // Re-coded to this module's own prefix: the platform reports settings.secrets_unavailable,
        // and that code leaking out of /api/discovery would name a surface the caller never touched.
        // The reason text is the platform's and is safe to repeat — it names the variable, never a value.
        return encrypted.IsFailure
            ? Result<EncryptedSecret>.Failure(new Error(
                SecretsUnavailableErrorCode,
                $"This indexer's credential cannot be stored: {encrypted.Error.Message}"))
            : encrypted;
    }

    /// <summary>
    /// The stored secret, or null when there is none, the master key is gone or rotated, or the row
    /// fails authentication. Every failure reads as "no credential" on purpose: the caller's only
    /// sane response is to query the indexer unauthenticated and let it answer, which is exactly what
    /// an indexer with no credential configured already does.
    /// </summary>
    public string? TryDecrypt(Indexer indexer)
    {
        if (indexer.SecretCipher is not { } cipherBytes || indexer.SecretNonce is not { } nonce)
        {
            return null;
        }

        var read = cipher.Decrypt(AadFor(indexer.Id), cipherBytes, nonce, indexer.SecretKeyId);
        return read.IsReadable ? read.Plaintext : null;
    }

    /// <summary>
    /// The same read as <see cref="TryDecrypt"/>, reported instead of consumed: whether a credential is
    /// stored and whether this installation can actually read it. This is what an operator is shown, so
    /// it may never carry the plaintext — the decrypted value is discarded here and only the outcome
    /// class survives.
    /// <para>
    /// The search path deliberately collapses every failure to "no credential" and queries the indexer
    /// unauthenticated. That is right for a search and wrong for a console: the console has to say that
    /// the installation is degraded rather than repeat that the row is populated.
    /// </para>
    /// </summary>
    public IndexerCredentialState Classify(Indexer indexer)
    {
        if (indexer.SecretCipher is not { } cipherBytes || indexer.SecretNonce is not { } nonce)
        {
            return IndexerCredentialState.None;
        }

        return cipher.Decrypt(AadFor(indexer.Id), cipherBytes, nonce, indexer.SecretKeyId).Outcome switch
        {
            SecretReadOutcome.Ok => IndexerCredentialState.Readable,
            SecretReadOutcome.KeyUnavailable => IndexerCredentialState.MasterKeyMissing,
            SecretReadOutcome.KeyMismatch => IndexerCredentialState.MasterKeyChanged,
            _ => IndexerCredentialState.Corrupt,
        };
    }

    private static string AadFor(Guid indexerId) => $"discovery.indexer:{indexerId}";

    /// <summary>
    /// Encrypts one indexer's stored session — its cookie jar JSON — under the same master key and
    /// algorithm as the credential itself, bound to the indexer's identity with its own
    /// additional-authenticated-data domain. A jar is the account in cookie form, so it travels under
    /// exactly the discipline the password does; the distinct AAD prefix keeps a credential row and a
    /// session row non-interchangeable even when both belong to the same indexer.
    /// <para>
    /// <paramref name="credentialFingerprint"/> goes into the AAD rather than into a column, which is
    /// what makes "a session never outlives the account it signed in as" true by construction: a jar
    /// stored under one credential fails authentication when read back under another, and reads as no
    /// session at all. Keeping it out of a column matters — a stored hash of a password is something
    /// a database dump alone could be attacked, and this store's whole promise is that a dump without
    /// the master key yields nothing.
    /// </para>
    /// </summary>
    public Result<EncryptedSecret> EncryptCookies(Guid indexerId, string credentialFingerprint, string cookieJarJson) =>
        cipher.Encrypt(SessionAadFor(indexerId, credentialFingerprint), cookieJarJson);

    /// <summary>
    /// The stored cookie jar, or null when there is none, the master key is gone or rotated, the row
    /// fails authentication, or it was stored under a different credential than the one asking. Every
    /// failure reads as "no session" on purpose: the only sane response is to sign in again on the
    /// next search, which is what an indexer with no stored session already does.
    /// </summary>
    public string? TryDecryptCookies(
        Guid indexerId, string credentialFingerprint, byte[]? cookiesCipher, byte[]? cookiesNonce, string? cookiesKeyId)
    {
        if (cookiesCipher is not { } cipherBytes || cookiesNonce is not { } nonce)
        {
            return null;
        }

        var read = cipher.Decrypt(SessionAadFor(indexerId, credentialFingerprint), cipherBytes, nonce, cookiesKeyId);
        return read.IsReadable ? read.Plaintext : null;
    }

    private static string SessionAadFor(Guid indexerId, string credentialFingerprint) =>
        $"discovery.indexer-session:{indexerId}:{credentialFingerprint}";
}
