using Cinomni.Operations.Settings;

namespace Cinomni.Host.RateLimiting;

/// <summary>The settable-key catalogue backed by <see cref="AnonymousRateLimitOptions"/>: its two properties.</summary>
public static class AnonymousRateLimitSettingDefinitions
{
    public static readonly SettingDefinition Permits = new(
        AnonymousRateLimitOptions.PermitsKey,
        SettingKind.Number,
        IsSecret: false,
        "Security:AnonymousRateLimit:Permits",
        "10",
        // Narrower than the shared Count preset on purpose. That one allows 100,000, which would turn
        // the throttle off through a setting whose name says it is on; and this is the one control
        // standing between an anonymous caller and unbounded password guessing.
        new SettingValidation(Required: true, MinValue: 1, MaxValue: 1_000, MaxLength: 6, Pattern: "^[0-9]+$"));

    /// <summary>
    /// The shared duration preset, whose floor of one minute is also the floor that makes sense here:
    /// a window shorter than the round trip it is meant to bound turns an ordinary retry into a
    /// refusal, and buys nothing against an attacker who is not waiting anyway.
    /// </summary>
    public static readonly SettingDefinition Window = new(
        AnonymousRateLimitOptions.WindowKey,
        SettingKind.Duration,
        IsSecret: false,
        "Security:AnonymousRateLimit:Window",
        "00:01:00",
        // The shared Duration preset reaches 365 days. A window that long is not a rate limit, it is a
        // year-long lockout of the whole household, since a fixed window holds for its full length.
        new SettingValidation(Required: true, MinValue: 60, MaxValue: 3_600, MaxLength: 16));
}
