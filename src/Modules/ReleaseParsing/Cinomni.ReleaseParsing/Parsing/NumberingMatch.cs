using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// A numbering hit together with the span of the source name it occupies. The span is what lets
/// <see cref="TitleAnalyzer"/> cut the title before the numbering and the tag scope after it, so
/// the year search never sees <c>2024.03.01</c> and the quality detectors never see the title.
/// </summary>
/// <param name="Numbering">The numbering read out of the name.</param>
/// <param name="Start">Index of the first character of the numbering, or -1 when it has no span.</param>
/// <param name="Length">Length of the numbering span (0 when <paramref name="Start"/> is -1).</param>
internal sealed record NumberingMatch(EpisodeNumbering Numbering, int Start, int Length)
{
    /// <summary>An empty numbering with no span, used as the seed when only modifiers matched.</summary>
    public static readonly EpisodeNumbering Empty = new(
        Season: null,
        SeasonTo: null,
        Episodes: [],
        AbsoluteEpisodes: [],
        AirDate: null,
        IsComplete: false,
        Part: null);

    public bool HasSpan => Start >= 0 && Length > 0;
}
