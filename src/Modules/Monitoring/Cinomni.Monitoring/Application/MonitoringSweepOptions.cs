using Cinomni.Operations.Settings;

namespace Cinomni.Monitoring.Application;

/// <summary>
/// How long the unprompted sweep waits after a known air instant before it asks an indexer.
/// <para>
/// Zero is the shipped value, and it is the behaviour every installation has today: a title is
/// searched as soon as it has aired. Waiting is an operator's choice, the same reasoning that ships
/// upgrades switched off. An episode with no air instant is not delayed — only a known time can be
/// waited past, and inventing one would hide a movie forever.
/// </para>
/// <para>
/// It does not apply to an upgrade of something already on disk, and it does not apply to a search
/// an operator runs by hand. Those are not the first grab after a premiere.
/// </para>
/// </summary>
public sealed class MonitoringSweepOptions
{
    internal const string SearchDelayKey = "monitoring.searchDelay";

    /// <summary>Fourteen days. Long enough to wait for a proper release, short of a value that breaks clock arithmetic.</summary>
    public static readonly TimeSpan MaxSearchDelay = TimeSpan.FromDays(14);

    public TimeSpan SearchDelay { get; init; }

    public static IEnumerable<SettingError> Check(MonitoringSweepOptions candidate)
    {
        if (candidate.SearchDelay < TimeSpan.Zero || candidate.SearchDelay > MaxSearchDelay)
        {
            yield return new SettingError(
                [SearchDelayKey],
                "settings.invalid_value",
                $"{nameof(SearchDelay)} must be between zero and {MaxSearchDelay} (configured: {candidate.SearchDelay}).");
        }
    }
}

/// <summary>The one settable key backed by <see cref="MonitoringSweepOptions"/>.</summary>
public static class MonitoringSweepSettingDefinitions
{
    public static readonly SettingDefinition SearchDelay = new(
        MonitoringSweepOptions.SearchDelayKey,
        SettingKind.Duration,
        IsSecret: false,
        "Monitoring:SearchDelay",
        "00:00:00",
        // Zero is the off position. The shared duration preset starts at one minute, which would make
        // "search as soon as it airs" unreachable from the console.
        new SettingValidation(Required: true, MinValue: 0, MaxValue: 14 * 24 * 60 * 60, MaxLength: 32));
}
