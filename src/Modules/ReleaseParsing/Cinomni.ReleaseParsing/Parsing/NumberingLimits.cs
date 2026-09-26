namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// The numeric bounds every numbering detector enforces. Release names are hostile input, so a
/// pattern is never allowed to expand unboundedly: <c>S01E01-E9999</c> and <c>- 1-9999</c> must
/// cost the same as their well-formed counterparts. These caps are the design; the 250 ms
/// <see cref="ParserRegex"/> timeout is only the backstop behind them.
/// </summary>
internal static class NumberingLimits
{
    /// <summary>No real series has more than this many seasons; anything above is a false positive.</summary>
    public const int MaxSeason = 100;

    /// <summary>Upper bound for an episode or absolute number (long-running anime sit well below it).</summary>
    public const int MaxEpisode = 2000;

    /// <summary>A range wider than this is a malformed name, not a release covering that many units.</summary>
    public const int MaxRangeSpan = 200;

    /// <summary>Upper bound on an explicit multi-episode list (<c>S01E01E02E03…</c>).</summary>
    public const int MaxEpisodesInList = 20;

    /// <summary>Longest name any detector will look at (aligned with the release-title audit column).</summary>
    public const int MaxNameLength = 1000;

    public static bool IsSeasonInRange(int season) => season is >= 0 and <= MaxSeason;

    public static bool IsEpisodeInRange(int episode) => episode is >= 0 and <= MaxEpisode;

    /// <summary>True when <paramref name="from"/>..<paramref name="to"/> is a sane, bounded, ascending range.</summary>
    public static bool IsSpanInRange(int from, int to) =>
        to >= from && to - from <= MaxRangeSpan;
}
