namespace Cinomni.Kernel.Security;

/// <summary>
/// Names of the rate-limit policies the Host registers. They live in the kernel for the same reason
/// <see cref="AuthorizationPolicies"/> does: an endpoint has to be able to say "throttle this" without
/// depending on the composition root that decides what the throttle is. The Host owns the numbers and
/// the algorithm; a module owns only the claim that its route needs one.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// The anonymous routes that verify or establish a credential. They cannot require a session — one
    /// does not exist yet — so they are the only surface an unauthenticated caller can spend this
    /// installation's resources on, and the only one worth guessing against.
    /// </summary>
    public const string AnonymousCredentials = "anonymous-credentials";

    /// <summary>
    /// The authenticated routes that re-verify a credential on an account's behalf — the password again,
    /// or a second-factor code. A session is all they ask of a caller, so they are what a stolen session
    /// would guess against, and they are counted per account rather than per address.
    /// </summary>
    public const string AccountCredentials = "account-credentials";
}
