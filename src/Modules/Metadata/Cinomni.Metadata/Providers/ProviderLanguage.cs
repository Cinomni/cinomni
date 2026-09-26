namespace Cinomni.Metadata.Providers;

/// <summary>
/// Normalises a provider's language tag to a lower-case ISO-639-1 two-letter code so the artwork
/// selector can compare languages uniformly across providers (TMDB emits 2-letter, TheTVDB 3-letter,
/// TVMaze an English name). Best-effort: an unknown tag is passed through lower-cased, and a text-less
/// image (null/empty) stays null (neutral).
/// </summary>
internal static class ProviderLanguage
{
    // The common 3-letter (ISO-639-2/T) and English-name forms Cinomni's providers actually emit.
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["english"] = "en",
        ["spa"] = "es", ["spanish"] = "es",
        ["fra"] = "fr", ["fre"] = "fr", ["french"] = "fr",
        ["deu"] = "de", ["ger"] = "de", ["german"] = "de",
        ["ita"] = "it", ["italian"] = "it",
        ["por"] = "pt", ["portuguese"] = "pt",
        ["nld"] = "nl", ["dut"] = "nl", ["dutch"] = "nl",
        ["jpn"] = "ja", ["japanese"] = "ja",
        ["kor"] = "ko", ["korean"] = "ko",
        ["zho"] = "zh", ["chi"] = "zh", ["chinese"] = "zh",
        ["rus"] = "ru", ["russian"] = "ru",
        ["ara"] = "ar", ["arabic"] = "ar",
        ["hin"] = "hi", ["hindi"] = "hi",
    };

    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var trimmed = language.Trim();
        if (Map.TryGetValue(trimmed, out var mapped))
        {
            return mapped;
        }

        // Already a 2-letter code, or a BCP-47 tag like "en-US" — take the primary subtag.
        var primary = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : trimmed;
        return primary.Length == 2 ? primary.ToLowerInvariant() : primary.ToLowerInvariant();
    }
}
