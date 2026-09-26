using System.Globalization;
using System.Text.RegularExpressions;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// Reads the SxxEyy family of season/episode numbering out of a release or file name: explicit
/// multi-episode lists, ranges, single episodes, the <c>1x05</c> form, multi-season and season
/// packs, plus the <c>Complete</c> and <c>Part N</c> modifiers.
/// </summary>
/// <remarks>
/// Static and stateless over <see langword="readonly"/> compiled regexes — the parser is a DI
/// singleton and any per-parse mutable state would be a thread-safety bug under the parallel
/// indexer fan-out. Every repetition is bounded and every number is range-checked
/// (<see cref="NumberingLimits"/>); patterns are ordered most-specific first and a hit that fails
/// its range check falls through to the next pattern rather than being accepted.
/// </remarks>
internal static class SeriesNumberingDetector
{
    // Release names separate tokens with ".", "_", "-" or spaces. "_" is a regex *word* character,
    // so \b is unusable as a token guard here ("The_Wire_S02E05" would never match); these
    // alphanumeric look-arounds are the equivalent that treats "_" as a separator.
    private const string NotAfterAlphanumeric = "(?<![a-z0-9])";
    private const string NotBeforeAlphanumeric = "(?![a-z0-9])";
    private const string NotBeforeDigit = @"(?!\d)";

    // S01E01E02E03 — an explicit list. The outer repetition is bounded and each iteration must
    // consume a literal "E" plus a digit, so there is no nested-quantifier ambiguity.
    private static readonly Regex MultiEpisodeList = ParserRegex.Compile(
        NotAfterAlphanumeric + @"S(\d{1,3})[. _]?(E\d{1,4}(?:[. _-]?E\d{1,4}){1,19})" + NotBeforeDigit);

    // S01E01-E02 and S01E01-02.
    private static readonly Regex EpisodeRange = ParserRegex.Compile(
        NotAfterAlphanumeric + @"S(\d{1,3})[. _]?E(\d{1,4})[. _]*-[. _]*E?(\d{1,4})" + NotBeforeDigit);

    // S01E01, S1E1, S01.E01, S01 E01 — and S01E01v2, hence the digits-only guard at the end.
    private static readonly Regex SingleEpisode = ParserRegex.Compile(
        NotAfterAlphanumeric + @"S(\d{1,3})[. _]?E(\d{1,4})" + NotBeforeDigit);

    // 1x05. The leading guard keeps "1920x1080" out; the codec guard keeps "DD5.1x264" out, which
    // is the one realistic collision (x264/x265/x266 are the only common xNNN tokens).
    private static readonly Regex AlternateEpisode = ParserRegex.Compile(
        NotAfterAlphanumeric + @"(\d{1,3})x(?!26[4-6]" + NotBeforeDigit + @")(\d{1,4})" + NotBeforeDigit);

    // S01-S03 and S01-03. The trailing guard keeps "S01-1080p" out (108 would also fail the cap).
    private static readonly Regex SeasonRange = ParserRegex.Compile(
        NotAfterAlphanumeric + @"(?:S|Season[. _-]?)(\d{1,3})[. _]*-[. _]*(?:S|Season[. _-]?)?(\d{1,3})"
        + NotBeforeAlphanumeric);

    // S01 / Season 1 / Season.01 with nothing episode-shaped behind it. The digit must follow "S"
    // immediately, which is what keeps "S.W.A.T.", "Se7en" and "Ocean's.11" out, and the trailing
    // guard keeps a group token like "-S0ME" out.
    private static readonly Regex SeasonPack = ParserRegex.Compile(
        NotAfterAlphanumeric + @"(?:S|Season[. _-]?)(\d{1,3})" + NotBeforeAlphanumeric + @"(?![. _-]?E\d)");

    // "Complete Series" / "Series Complete" — specific enough to never fire on a movie edition.
    private static readonly Regex CompleteSeries = ParserRegex.Compile(
        NotAfterAlphanumeric + @"(?:the[. _-])?complete[. _-](?:series|collection|seasons?|show)" + NotBeforeAlphanumeric
        + "|" + NotAfterAlphanumeric + @"(?:series|seasons?)[. _-]complete" + NotBeforeAlphanumeric);

    private static readonly Regex BareComplete = ParserRegex.Compile(
        NotAfterAlphanumeric + "complete" + NotBeforeAlphanumeric);

    // Part 2 / Pt.2 / Part2. The trailing guard keeps "Part.1080p" out.
    private static readonly Regex PartSuffix = ParserRegex.Compile(
        NotAfterAlphanumeric + @"(?:part|pt)[. _-]?(\d{1,2})" + NotBeforeAlphanumeric);

    // Individual episode numbers inside a matched list, in order.
    private static readonly Regex EpisodeToken = ParserRegex.Compile(@"E(\d{1,4})");

    /// <summary>Reads season/episode numbering, or <see langword="null"/> when the name carries none.</summary>
    public static NumberingMatch? Detect(string name) =>
        // The range runs first: a hyphen means "E05 through E08", while bare concatenation means an
        // explicit list. The list pattern also accepts the hyphen, which is what lets an out-of-range
        // range (S01E01-E9999) degrade into a list instead of being dropped entirely.
        DetectEpisodeRange(name)
        ?? DetectMultiEpisodeList(name)
        ?? DetectSingleEpisode(name)
        ?? DetectAlternateEpisode(name)
        ?? DetectSeasonRange(name)
        ?? DetectSeasonPack(name);

    /// <summary>Locates a complete-series marker, or <see langword="null"/> when there is none.</summary>
    public static (int Start, int Length)? DetectCompleteSeries(string name)
    {
        var match = CompleteSeries.Match(name);
        return match.Success ? (match.Index, match.Length) : null;
    }

    /// <summary>True when a bare <c>COMPLETE</c> token is present (only meaningful next to a season).</summary>
    public static bool HasBareComplete(string name) => BareComplete.IsMatch(name);

    /// <summary>Reads a <c>Part N</c> suffix, or <see langword="null"/> when there is none.</summary>
    public static int? DetectPart(string name)
    {
        var match = PartSuffix.Match(name);
        return match.Success && TryNumber(match.Groups[1], out var part) ? part : null;
    }

    private static NumberingMatch? DetectMultiEpisodeList(string name)
    {
        var match = MultiEpisodeList.Match(name);
        if (!match.Success || !TryNumber(match.Groups[1], out var season) || !NumberingLimits.IsSeasonInRange(season))
        {
            return null;
        }

        var episodes = new List<int>();
        foreach (Match token in EpisodeToken.Matches(match.Groups[2].Value))
        {
            if (episodes.Count >= NumberingLimits.MaxEpisodesInList)
            {
                break;
            }

            if (TryNumber(token.Groups[1], out var episode)
                && NumberingLimits.IsEpisodeInRange(episode)
                && !episodes.Contains(episode))
            {
                episodes.Add(episode);
            }
        }

        episodes.Sort();
        return episodes.Count == 0 ? null : Numbered(match, season, seasonTo: null, episodes);
    }

    private static NumberingMatch? DetectEpisodeRange(string name)
    {
        var match = EpisodeRange.Match(name);
        if (!match.Success
            || !TryNumber(match.Groups[1], out var season)
            || !TryNumber(match.Groups[2], out var from)
            || !TryNumber(match.Groups[3], out var to))
        {
            return null;
        }

        // A malformed range (S01E01-E9999) falls through to the single-episode pattern.
        if (!NumberingLimits.IsSeasonInRange(season)
            || !NumberingLimits.IsEpisodeInRange(from)
            || !NumberingLimits.IsEpisodeInRange(to)
            || !NumberingLimits.IsSpanInRange(from, to))
        {
            return null;
        }

        var episodes = new List<int>();
        for (var episode = from; episode <= to; episode++)
        {
            episodes.Add(episode);
        }

        return Numbered(match, season, seasonTo: null, episodes);
    }

    private static NumberingMatch? DetectSingleEpisode(string name)
    {
        var match = SingleEpisode.Match(name);
        return match.Success
               && TryNumber(match.Groups[1], out var season)
               && TryNumber(match.Groups[2], out var episode)
               && NumberingLimits.IsSeasonInRange(season)
               && NumberingLimits.IsEpisodeInRange(episode)
            ? Numbered(match, season, seasonTo: null, [episode])
            : null;
    }

    private static NumberingMatch? DetectAlternateEpisode(string name)
    {
        var match = AlternateEpisode.Match(name);
        return match.Success
               && TryNumber(match.Groups[1], out var season)
               && TryNumber(match.Groups[2], out var episode)
               && NumberingLimits.IsSeasonInRange(season)
               && NumberingLimits.IsEpisodeInRange(episode)
            ? Numbered(match, season, seasonTo: null, [episode])
            : null;
    }

    private static NumberingMatch? DetectSeasonRange(string name)
    {
        var match = SeasonRange.Match(name);
        return match.Success
               && TryNumber(match.Groups[1], out var from)
               && TryNumber(match.Groups[2], out var to)
               && NumberingLimits.IsSeasonInRange(from)
               && NumberingLimits.IsSeasonInRange(to)
               && to > from
            ? Numbered(match, from, to, [])
            : null;
    }

    private static NumberingMatch? DetectSeasonPack(string name)
    {
        var match = SeasonPack.Match(name);
        return match.Success
               && TryNumber(match.Groups[1], out var season)
               && NumberingLimits.IsSeasonInRange(season)
            ? Numbered(match, season, seasonTo: null, [])
            : null;
    }

    private static NumberingMatch Numbered(Match match, int season, int? seasonTo, IReadOnlyList<int> episodes) =>
        new(
            NumberingMatch.Empty with { Season = season, SeasonTo = seasonTo, Episodes = episodes },
            match.Index,
            match.Length);

    private static bool TryNumber(Group group, out int value) =>
        int.TryParse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
