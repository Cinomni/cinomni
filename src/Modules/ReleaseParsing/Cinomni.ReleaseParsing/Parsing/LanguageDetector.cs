using System.Text.RegularExpressions;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// Detects spoken languages from tag tokens. Conservative (fuller words over short codes) to
/// avoid mistaking title words for languages. Defaults to a single "Unknown" when nothing matches.
/// </summary>
internal static class LanguageDetector
{
    private static readonly (Regex Pattern, string Language)[] Languages =
    [
        (ParserRegex.Compile(@"\b(multi|multilang|multi-?audio)\b"), "Multi"),
        (ParserRegex.Compile(@"\b(english|eng)\b"), "English"),
        (ParserRegex.Compile(@"\b(spanish|castellano|espanol|latino)\b"), "Spanish"),
        (ParserRegex.Compile(@"\b(french|truefrench|vostfr|vff)\b"), "French"),
        (ParserRegex.Compile(@"\b(german|deutsch)\b"), "German"),
        (ParserRegex.Compile(@"\bitalian\b"), "Italian"),
        (ParserRegex.Compile(@"\bjapanese\b"), "Japanese"),
        (ParserRegex.Compile(@"\bkorean\b"), "Korean"),
        (ParserRegex.Compile(@"\brussian\b"), "Russian"),
        (ParserRegex.Compile(@"\b(portuguese|dublado)\b"), "Portuguese"),
        (ParserRegex.Compile(@"\bhindi\b"), "Hindi"),
        (ParserRegex.Compile(@"\b(chinese|mandarin|cantonese)\b"), "Chinese"),
    ];

    public static IReadOnlyList<string> Detect(string scope)
    {
        var found = new List<string>();
        foreach (var (pattern, language) in Languages)
        {
            if (pattern.IsMatch(scope) && !found.Contains(language))
            {
                found.Add(language);
            }
        }

        return found.Count > 0 ? found : ["Unknown"];
    }
}
