namespace Cinomni.Subtitles.Application;

/// <summary>Whether one wanted subtitle is already on the asset, embedded or downloaded.</summary>
public static class SubtitleCoverage
{
    public static bool IsSatisfied(
        string language,
        bool forced,
        bool hearingImpaired,
        IEnumerable<(string Language, bool Forced, bool HearingImpaired)> present) =>
        present.Any(item =>
            string.Equals(item.Language, language, StringComparison.OrdinalIgnoreCase)
            && item.Forced == forced
            && item.HearingImpaired == hearingImpaired);

    /// <summary>
    /// Stable fingerprint of a preference. A catch-up uses it so a changed language list is a new
    /// pass, and a repeated pass of the same preference is not.
    /// </summary>
    public static string Stamp(SubtitlePreference preference)
    {
        var languages = string.Join(
            ',',
            preference.WantedLanguages.Select(language => language.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal));
        return $"{languages}:f{(preference.Forced ? 1 : 0)}:h{(preference.HearingImpaired ? 1 : 0)}";
    }
}
