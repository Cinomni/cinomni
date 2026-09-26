namespace Cinomni.Identity.Application;

/// <summary>
/// The bounds on what may be submitted as a credential, shared by the two paths that accept one so
/// they cannot drift apart on what they accept.
/// <para>
/// A maximum matters more here than it looks. Hashing is deliberately expensive — Argon2id holds
/// 19 MiB while it runs — and the cost of the input on top of that is the caller's to choose:
/// unbounded, a single permitted request can carry a password the size of the request-body limit and
/// spend several times the memory the throttle was sized against, without exceeding any limit. The
/// ceiling is far above any passphrase a person types and far below anything worth allocating.
/// </para>
/// </summary>
internal static class CredentialLimits
{
    public const int MinimumPasswordLength = 8;

    public const int MaximumPasswordLength = 1024;

    /// <summary>
    /// The column's width, and taken from here by the schema, so the two cannot disagree again: the bound
    /// was 256 against a 100-character column, and a name between the two passed validation and failed
    /// the insert with a 500.
    /// </summary>
    public const int MaximumUsernameLength = 100;
}
