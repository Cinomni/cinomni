using System.Globalization;
using System.Text.RegularExpressions;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// Reads anime-style absolute numbering — <c>[Group] Show Name - 123 (1080p)</c>, optionally with a
/// <c>v2</c> re-release marker or a batch range <c>- 01-12</c>.
/// </summary>
/// <remarks>
/// Only consulted when no season/episode and no air date was found. The dash must be surrounded by
/// a separator on <b>both</b> sides, which is what keeps the scene group suffix out: movie names end
/// in <c>x264-GROUP</c>, where the dash is preceded by an alphanumeric, never by <c>. _</c>. The
/// batch range is bounded by <see cref="NumberingLimits.MaxRangeSpan"/> so <c>- 1-9999</c> cannot
/// expand into thousands of numbers.
/// </remarks>
internal static class AbsoluteEpisodeDetector
{
    private static readonly Regex Absolute = ParserRegex.Compile(
        @"[. _]-[. _](\d{1,4})(?:[. _]*-[. _]*(\d{1,4}))?(?:v\d{1,2})?(?![\da-z])");

    /// <summary>Reads absolute numbering, or <see langword="null"/> when the name carries none.</summary>
    public static NumberingMatch? Detect(string name)
    {
        var match = Absolute.Match(name);
        if (!match.Success
            || !TryNumber(match.Groups[1], out var from)
            || !NumberingLimits.IsEpisodeInRange(from))
        {
            return null;
        }

        var to = from;
        if (match.Groups[2].Success
            && (!TryNumber(match.Groups[2], out to)
                || !NumberingLimits.IsEpisodeInRange(to)
                || !NumberingLimits.IsSpanInRange(from, to)))
        {
            // A malformed batch range degrades to the single number it starts with.
            to = from;
        }

        var absolutes = new List<int>();
        for (var number = from; number <= to; number++)
        {
            absolutes.Add(number);
        }

        return new NumberingMatch(
            NumberingMatch.Empty with { AbsoluteEpisodes = absolutes },
            match.Index,
            match.Length);
    }

    private static bool TryNumber(Group group, out int value) =>
        int.TryParse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
