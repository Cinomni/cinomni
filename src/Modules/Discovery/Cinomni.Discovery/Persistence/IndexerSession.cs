namespace Cinomni.Discovery.Persistence;

/// <summary>
/// The login session of one definition-backed indexer: the encrypted cookie jar the declared login
/// sequence captured, plus what the last sign-in attempt reported. One row per indexer — the row is
/// created by the first sign-in attempt and deleted when the credential it authenticates with is
/// replaced or cleared, so a stored session can never outlive the account it belongs to.
/// <para>
/// Cookies are secrets in exactly the sense an API key is (a stolen jar is the account), so they are
/// stored encrypted with the same master key and the same discipline as the credential itself —
/// never in the clear, never logged, never serialized anywhere. Presence of the ciphertext, not its
/// contents, is all any projection reports.
/// </para>
/// </summary>
public sealed class IndexerSession
{
    public Guid IndexerId { get; init; }

    /// <summary>
    /// AES-256-GCM ciphertext (tag appended) of the cookie jar JSON, keyed to the owning indexer —
    /// the same shape <c>SettingsSecretCipher</c> produces and <see cref="Indexer"/> stores for the
    /// credential. Null when the last attempt did not produce a session.
    /// </summary>
    public byte[]? CookiesCipher { get; private set; }

    public byte[]? CookiesNonce { get; private set; }

    /// <summary>Which master key encrypted the jar, so a rotated key is reported rather than decrypted into noise.</summary>
    public string? CookiesKeyId { get; private set; }

    /// <summary>When the stored cookies were captured from the site — the honest "signed in at".</summary>
    public DateTimeOffset? CapturedAt { get; private set; }

    /// <summary>When a sign-in was last attempted, successful or not. The manager writes it on every attempt.</summary>
    public DateTimeOffset LastAttemptAt { get; private set; }

    public bool LastAttemptOk { get; private set; }

    /// <summary>
    /// How many sign-ins in a row have failed. It is what the backoff is computed from, and the only
    /// thing standing between a wrong stored password and this installation submitting it to the
    /// tracker on every scheduled search until the account is banned.
    /// <para>
    /// A sign-in the site answers with a redirect does not reset it, because a wrong password is
    /// often answered the same way. Only <see cref="Confirm"/> does: a search the stored cookies got
    /// through is the one proof that the sign-in was real.
    /// </para>
    /// </summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// When a search first got through with the stored cookies. Null while the session is unproven:
    /// the site rejecting it then is a failed sign-in, not an expiry.
    /// </summary>
    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>
    /// Replaces the stored session with a freshly signed-in, still unproven one. Writing the cookie
    /// fields together is what keeps them coherent — the database's check constraint enforces
    /// cipher-and-nonce, but only this method guarantees a stale key id never survives a
    /// re-encryption.
    /// </summary>
    public void Store(byte[] cipher, byte[] nonce, string keyId, DateTimeOffset capturedAt)
    {
        CookiesCipher = cipher;
        CookiesNonce = nonce;
        CookiesKeyId = keyId;
        CapturedAt = capturedAt;
        ConfirmedAt = null;
        LastAttemptAt = capturedAt;
        LastAttemptOk = true;
    }

    /// <summary>A search got through with the stored cookies: the sign-in was real, and the failures behind it are over.</summary>
    public void Confirm(DateTimeOffset confirmedAt)
    {
        ConfirmedAt = confirmedAt;
        ConsecutiveFailures = 0;
    }

    /// <summary>
    /// The site expired a session that had worked. Its cookies are dropped and nothing counts
    /// against the credential: signing in again right away is the normal renewal.
    /// </summary>
    public void Expire()
    {
        DropCookies();
    }

    /// <summary>
    /// Records a sign-in that did not produce a session, or produced one the site refused on first
    /// use. Any previously stored jar is dropped with it: a kept session and a failed last attempt
    /// are different states, and a row cannot honestly claim both.
    /// </summary>
    public void RecordFailedAttempt(DateTimeOffset attemptedAt)
    {
        DropCookies();
        LastAttemptAt = attemptedAt;
        LastAttemptOk = false;
        ConsecutiveFailures++;
    }

    private void DropCookies()
    {
        CookiesCipher = null;
        CookiesNonce = null;
        CookiesKeyId = null;
        CapturedAt = null;
        ConfirmedAt = null;
    }
}
