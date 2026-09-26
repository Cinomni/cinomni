using Cinomni.Operations.Settings;

namespace Cinomni.Subtitles.Application;

/// <summary>
/// Which subtitles the household wants. Read live, so a change applies to the next search without a
/// restart. An empty language list searches nothing — it does not guess a language.
/// </summary>
public sealed class SubtitlePreference
{
    internal const string LanguagesKey = "subtitles.wantedLanguages";
    internal const string HearingImpairedKey = "subtitles.hearingImpaired";
    internal const string ForcedKey = "subtitles.forced";

    public IReadOnlyList<string> WantedLanguages { get; init; } = ["en"];

    public bool HearingImpaired { get; init; }

    public bool Forced { get; init; }

    public static IEnumerable<SettingError> Check(SubtitlePreference candidate)
    {
        if (candidate.WantedLanguages.Count > SubtitlePreferenceDefinitions.MaxLanguages)
        {
            yield return new SettingError(
                [LanguagesKey],
                "settings.invalid_value",
                $"At most {SubtitlePreferenceDefinitions.MaxLanguages} languages.");
        }
    }
}

/// <summary>The settable keys backed by <see cref="SubtitlePreference"/>.</summary>
public static class SubtitlePreferenceDefinitions
{
    public const int MaxLanguages = 8;

    public static readonly SettingDefinition WantedLanguages = new(
        SubtitlePreference.LanguagesKey,
        SettingKind.List,
        IsSecret: false,
        "Subtitles:WantedLanguages",
        "en",
        new SettingValidation(Required: false, MinLength: 0, MaxLength: MaxLanguages, Pattern: "^[a-zA-Z]{2}$"));

    public static readonly SettingDefinition HearingImpaired = new(
        SubtitlePreference.HearingImpairedKey,
        SettingKind.Boolean,
        IsSecret: false,
        "Subtitles:HearingImpaired",
        "false",
        new SettingValidation(Required: true));

    public static readonly SettingDefinition Forced = new(
        SubtitlePreference.ForcedKey,
        SettingKind.Boolean,
        IsSecret: false,
        "Subtitles:Forced",
        "false",
        new SettingValidation(Required: true));

    public static readonly SettingDefinition[] All = [WantedLanguages, HearingImpaired, Forced];
}
