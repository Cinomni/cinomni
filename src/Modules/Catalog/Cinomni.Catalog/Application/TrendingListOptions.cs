using Cinomni.Operations.Settings;

namespace Cinomni.Catalog.Application;

/// <summary>
/// Whether the scheduled job may add titles from TMDB's trending list. Off is the shipped value:
/// an installation does not grow its library until an operator asks it to.
/// </summary>
public sealed class TrendingListOptions
{
    internal const string EnabledKey = "catalog.trendingList.enabled";

    public bool Enabled { get; init; }

    public static IEnumerable<SettingError> Check(TrendingListOptions candidate)
    {
        _ = candidate;
        yield break;
    }
}

/// <summary>The one settable key backed by <see cref="TrendingListOptions"/>.</summary>
public static class TrendingListSettingDefinitions
{
    public static readonly SettingDefinition Enabled = new(
        TrendingListOptions.EnabledKey,
        SettingKind.Boolean,
        IsSecret: false,
        "Catalog:TrendingListEnabled",
        "false",
        new SettingValidation(Required: true));
}
