namespace Cinomni.Subtitles.Application;

/// <summary>
/// Whether a language named the way the subtitle preference names it (ISO 639-1, two letters: <c>en</c>)
/// and one tagged the way a container tags a track (usually ISO 639-2, three letters: <c>eng</c>, or
/// its bibliographic form: <c>ger</c> for <c>deu</c>) are the same language.
/// </summary>
public static class LanguageCodes
{
    // Languages outside this list match only when both sides use the same code.
    private static readonly Dictionary<string, string[]> ThreeLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ["eng"], ["es"] = ["spa"], ["fr"] = ["fra", "fre"], ["de"] = ["deu", "ger"],
        ["it"] = ["ita"], ["pt"] = ["por"], ["nl"] = ["nld", "dut"], ["ru"] = ["rus"],
        ["ja"] = ["jpn"], ["zh"] = ["zho", "chi"], ["ko"] = ["kor"], ["ar"] = ["ara"],
        ["sv"] = ["swe"], ["no"] = ["nor", "nob", "nno"], ["da"] = ["dan"], ["fi"] = ["fin"],
        ["pl"] = ["pol"], ["tr"] = ["tur"], ["cs"] = ["ces", "cze"], ["el"] = ["ell", "gre"],
        ["he"] = ["heb"], ["hu"] = ["hun"], ["ro"] = ["ron", "rum"], ["ca"] = ["cat"],
        ["uk"] = ["ukr"], ["hi"] = ["hin"], ["th"] = ["tha"], ["vi"] = ["vie"],
        ["id"] = ["ind"], ["bg"] = ["bul"], ["hr"] = ["hrv"], ["sr"] = ["srp"],
        ["sk"] = ["slk", "slo"], ["sl"] = ["slv"], ["eu"] = ["eus", "baq"], ["gl"] = ["glg"],
        ["fa"] = ["fas", "per"], ["ms"] = ["msa", "may"], ["et"] = ["est"], ["lv"] = ["lav"],
        ["lt"] = ["lit"], ["is"] = ["isl", "ice"],
    };

    public static bool SameLanguage(string wanted, string? tagged)
    {
        if (string.IsNullOrWhiteSpace(tagged))
        {
            return false;
        }

        var track = tagged.Trim();
        return string.Equals(wanted.Trim(), track, StringComparison.OrdinalIgnoreCase)
            || (ThreeLetter.TryGetValue(wanted.Trim(), out var codes) && codes.Contains(track, StringComparer.OrdinalIgnoreCase));
    }
}
