namespace Cinomni.Host.RateLimiting;

/// <summary>
/// How hard the anonymous credential routes are throttled: how many requests one client may make, and
/// over what window. Both are live — an operator changes them from the console without a restart —
/// because the right numbers depend on the installation, and finding out that the default is wrong
/// should not cost a redeploy.
/// <para>
/// A fixed window rather than a sliding one, deliberately. It is the algorithm whose remaining budget
/// and reset time can be stated exactly, which is what makes the <c>Retry-After</c> on a refusal a
/// fact rather than an estimate; and against password guessing the difference between the two is
/// noise, since either bounds attempts per unit time to the same order.
/// </para>
/// </summary>
public sealed class AnonymousRateLimitOptions
{
    internal const string PermitsKey = "security.anonymousRateLimitPermits";
    internal const string WindowKey = "security.anonymousRateLimitWindow";

    /// <summary>
    /// Requests one client may make per <see cref="Window"/>. Ten a minute leaves a household
    /// mistyping a password entirely unaffected and makes guessing pointless: at this rate an attacker
    /// gets fewer attempts in a day than a modern wordlist spends in a second.
    /// </summary>
    public int Permits { get; init; } = 10;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}
