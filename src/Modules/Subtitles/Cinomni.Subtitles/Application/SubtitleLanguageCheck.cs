using System.Text.RegularExpressions;

namespace Cinomni.Subtitles.Application;

/// <summary>
/// Whether a downloaded subtitle is plausibly written in the language it was asked for, judged by the
/// alphabet its text uses. Providers label files by hand, and the label is wrong often enough to matter:
/// a Hebrew subtitle filed as English was downloaded as the English one and played as such.
/// <para>
/// Only the writing system is checked, never the language itself: English and Spanish both pass for
/// each other, Russian and Hebrew do not pass for English. A language whose alphabet is not listed, or a
/// file with too little text to tell, always passes — the check refuses only what it is sure of.
/// </para>
/// </summary>
public static partial class SubtitleLanguageCheck
{
    /// <summary>Below this many letters a file is not judged.</summary>
    public const int MinLetters = 100;

    /// <summary>At least this share of the letters must be in the language's own alphabet.</summary>
    public const double MinShare = 0.6;

    private enum Script
    {
        Latin,
        Cyrillic,
        Greek,
        Hebrew,
        Arabic,
        Cjk,
        Hangul,
        Thai,
    }

    private static readonly Dictionary<string, Script> Scripts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = Script.Latin, ["es"] = Script.Latin, ["fr"] = Script.Latin, ["de"] = Script.Latin,
        ["it"] = Script.Latin, ["pt"] = Script.Latin, ["nl"] = Script.Latin, ["sv"] = Script.Latin,
        ["da"] = Script.Latin, ["no"] = Script.Latin, ["nb"] = Script.Latin, ["nn"] = Script.Latin,
        ["fi"] = Script.Latin, ["pl"] = Script.Latin, ["cs"] = Script.Latin, ["sk"] = Script.Latin,
        ["ro"] = Script.Latin, ["hu"] = Script.Latin, ["tr"] = Script.Latin, ["hr"] = Script.Latin,
        ["sl"] = Script.Latin, ["et"] = Script.Latin, ["lv"] = Script.Latin, ["lt"] = Script.Latin,
        ["id"] = Script.Latin, ["ms"] = Script.Latin, ["vi"] = Script.Latin, ["ca"] = Script.Latin,
        ["eu"] = Script.Latin, ["gl"] = Script.Latin, ["is"] = Script.Latin, ["sq"] = Script.Latin,
        ["ru"] = Script.Cyrillic, ["uk"] = Script.Cyrillic, ["bg"] = Script.Cyrillic, ["mk"] = Script.Cyrillic,
        ["be"] = Script.Cyrillic,
        ["el"] = Script.Greek,
        ["he"] = Script.Hebrew,
        ["ar"] = Script.Arabic, ["fa"] = Script.Arabic, ["ur"] = Script.Arabic,
        ["zh"] = Script.Cjk, ["ja"] = Script.Cjk,
        ["ko"] = Script.Hangul,
        ["th"] = Script.Thai,
    };

    public static bool IsPlausible(string language, string text)
    {
        var primary = language.Split('-', '_')[0];
        if (!Scripts.TryGetValue(primary, out var expected))
        {
            return true;
        }

        // Markup such as <i> or {\an8} is letters too, and in the Latin alphabet whatever the language.
        string spoken;
        try
        {
            spoken = Markup().Replace(text, " ");
        }
        catch (RegexMatchTimeoutException)
        {
            // Too much markup to strip in time: judge the raw text, tags and all.
            spoken = text;
        }

        var letters = 0;
        var inScript = 0;
        foreach (var c in spoken)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            letters++;
            if (ScriptOf(c) == expected)
            {
                inScript++;
            }
        }

        return letters < MinLetters || (double)inScript / letters >= MinShare;
    }

    private static Script? ScriptOf(char c) => c switch
    {
        (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= 'À' and <= 'ɏ') or (>= 'Ḁ' and <= 'ỿ') => Script.Latin,
        >= 'Ѐ' and <= 'ԯ' => Script.Cyrillic,
        >= 'Ͱ' and <= 'Ͽ' => Script.Greek,
        >= '֐' and <= '׿' => Script.Hebrew,
        (>= '؀' and <= 'ۿ') or (>= 'ݐ' and <= 'ݿ') => Script.Arabic,
        (>= '぀' and <= 'ヿ') or (>= '㐀' and <= '䶿') or (>= '一' and <= '鿿') => Script.Cjk,
        (>= '가' and <= '힯') or (>= 'ᄀ' and <= 'ᇿ') => Script.Hangul,
        >= '฀' and <= '๿' => Script.Thai,
        _ => null,
    };

    [GeneratedRegex(@"<[^>]{0,200}>|\{[^}]{0,200}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Markup();
}
