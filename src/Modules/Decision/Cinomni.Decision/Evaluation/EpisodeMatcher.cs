using System.Globalization;
using Cinomni.Decision.Contracts;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Decision.Evaluation;

/// <summary>
/// What the search actually asked for, plus the units a candidate was resolved to cover. Built once
/// per search by <c>DecisionEngine</c> from <c>IReleaseSearchResults.GetRequestContextAsync</c> and
/// specialised per candidate with its coverage.
/// </summary>
/// <param name="ContentKind">The requested kind (<c>Movie</c>, <c>Series</c>, <c>Season</c>, <c>Episode</c>).</param>
/// <param name="Term">The series (or movie) title the search went out with.</param>
/// <param name="CoveredUnitIds">
/// The requested catalog units this candidate actually covers, resolved through
/// <c>ICatalogSeriesQuery</c>. Empty for a movie and for a release that covers nothing requested.
/// </param>
/// <param name="Year">
/// The catalog work's year, when it has one. It is the only thing that separates a series from its
/// own reboot once both carry the same title, so the identity check compares it whenever the release
/// states a year too.
/// </param>
internal sealed record EvaluationRequest(
    string ContentKind,
    string Term,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    int? AbsoluteNumber = null,
    DateOnly? AirDate = null,
    IReadOnlyList<Guid>? CoveredUnitIds = null,
    int? Year = null)
{
    private static readonly string[] SeriesKinds = ["Series", "Season", "Episode"];

    public IReadOnlyList<Guid> Covered => CoveredUnitIds ?? [];

    /// <summary>True when the request is for series content, which is what turns the matcher on.</summary>
    public bool IsSeries =>
        SeriesKinds.Any(k => string.Equals(k, ContentKind, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a season pack should outrank a single episode (a season-level goal).</summary>
    public bool PrefersCoverage =>
        string.Equals(ContentKind, "Season", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ContentKind, "Series", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The identity check the pipeline never had: is this release actually the thing that was asked for?
/// Until now the only filter was the indexer's query string, so a search for <c>S02E05</c> that came
/// back with <c>S02E06</c> — or with a different show entirely — was accepted and downloaded.
/// <para>
/// Pure and side-effect free. Every failure is <see cref="RejectionKind.Permanent"/>: a release that
/// is the wrong episode will never become the right one, so retrying it is pointless. Every reason is
/// persisted, so the explainability endpoint can say exactly which check rejected the release.
/// </para>
/// </summary>
internal static class EpisodeMatcher
{
    public const string SeriesRule = "MatchesRequestedSeries";
    public const string SeasonRule = "MatchesRequestedSeason";
    public const string EpisodeRule = "MatchesRequestedEpisode";

    /// <summary>
    /// The identity reasons for one candidate, in the order the explainability UI renders them.
    /// Empty for a movie request — a movie evaluation must look exactly as it always did.
    /// </summary>
    public static IReadOnlyList<EvaluationReason> Match(ParsedRelease parsed, EvaluationRequest request)
    {
        if (!request.IsSeries)
        {
            return [];
        }

        var reasons = new List<EvaluationReason> { MatchSeries(parsed, request) };

        // A season goal states a season and nothing else, so no episode check follows it and the
        // season check is the last thing that can judge the release.
        var episodeCheckFollows = WantsEpisodeCheck(request);

        if (request.SeasonNumber is int season)
        {
            reasons.Add(MatchSeason(
                parsed.Numbering, season, isFinalJudge: !episodeCheckFollows, coveredUnits: request.Covered.Count));
        }

        if (episodeCheckFollows)
        {
            reasons.Add(MatchEpisode(parsed.Numbering, request));
        }

        return reasons;
    }

    private static bool WantsEpisodeCheck(EvaluationRequest request) =>
        request.EpisodeNumber is not null || request.AbsoluteNumber is not null || request.AirDate is not null;

    // -- the three checks --------------------------------------------------------------------------

    /// <summary>
    /// Is this release the show that was asked for? The title is compared as a
    /// <see cref="SeriesTitleKey"/> — exactly, not as a prefix — and the year alongside it, because
    /// a reboot shares its parent's title exactly and only the year tells them apart.
    /// </summary>
    private static EvaluationReason MatchSeries(ParsedRelease parsed, EvaluationRequest request)
    {
        var release = SeriesTitleKey.Of(NormalizedTitleOf(parsed));
        var requested = SeriesTitleKey.Of(request.Term);

        // A year is evidence only when both sides state one: most series releases carry none, and
        // treating an unknown year as a mismatch would reject the ordinary case.
        var yearsComparable = request.Year is not null && parsed.Year is not null;
        var matched = SeriesTitleKey.Equal(release, requested)
            && (!yearsComparable || request.Year == parsed.Year);

        return Reason(
            SeriesRule,
            "series",
            DescribeSeries(requested, yearsComparable ? request.Year : null),
            DescribeSeries(release, yearsComparable ? parsed.Year : null),
            matched);
    }

    /// <param name="isFinalJudge">
    /// True when no episode check follows, which is the case for a season goal. It decides who
    /// adjudicates a release whose numbering states no season at all.
    /// </param>
    /// <param name="coveredUnits">
    /// How many requested catalog units the release resolved to. Only consulted when this check is
    /// the final judge; see the season-less branch for why.
    /// </param>
    private static EvaluationReason MatchSeason(
        EpisodeNumbering? numbering,
        int requestedSeason,
        bool isFinalJudge,
        int coveredUnits)
    {
        var expected = requestedSeason.ToString(CultureInfo.InvariantCulture);

        if (numbering is null)
        {
            // No numbering at all: a movie-shaped name cannot be the season that was asked for.
            return Reason(SeasonRule, "season", expected, "none", matched: false);
        }

        if (numbering.Season is not int season)
        {
            // Absolute (anime), date-based and complete-series numbering express no season. When an
            // episode check follows it adjudicates that family, and failing here as well would
            // double-count one mismatch — so defer.
            if (!isFinalJudge)
            {
                return Reason(SeasonRule, "season", expected, FamilyOf(numbering), matched: true);
            }

            // A season goal carries no episode number, absolute number or air date, so nothing
            // follows and this check is the last one that can judge the release. Deferring here is
            // what let `Show.Complete.Series` (~300 GB) satisfy a single season's pack search: it
            // resolves to no catalog unit, so the selection announces none, Import discards every
            // file as "outside the requested units", the job is rejected and the goal burns an
            // attempt — five times over, and then the season is exhausted and never acquired.
            // Coverage is exactly the line between the two: an anime batch ("- 01-12") and a daily
            // show's date do resolve to episodes of the requested season, while a complete-series
            // pack or a foreign date resolve to nothing at all.
            var covered = coveredUnits.ToString(CultureInfo.InvariantCulture);
            return Reason(
                SeasonRule, "season", expected, $"{FamilyOf(numbering)}, covers {covered} requested", coveredUnits > 0);
        }

        var lastSeason = numbering.SeasonTo is int to && to >= season ? to : season;
        var actual = lastSeason == season
            ? season.ToString(CultureInfo.InvariantCulture)
            : $"{season}-{lastSeason}";

        return Reason(SeasonRule, "season", expected, actual, requestedSeason >= season && requestedSeason <= lastSeason);
    }

    private static EvaluationReason MatchEpisode(EpisodeNumbering? numbering, EvaluationRequest request)
    {
        var expected = DescribeRequestedEpisode(request);

        if (numbering is null)
        {
            return Reason(EpisodeRule, "episode", expected, "none", matched: false);
        }

        return Reason(EpisodeRule, "episode", expected, DescribeNumbering(numbering), Covers(numbering, request));
    }

    /// <summary>
    /// Whether the release's numbering covers the requested episode. A season pack covers every
    /// episode of its season, so a pack returned for an <c>S02E05</c> search is a legitimate answer.
    /// </summary>
    private static bool Covers(EpisodeNumbering numbering, EvaluationRequest request)
    {
        if (request.AbsoluteNumber is int absolute && numbering.AbsoluteEpisodes.Contains(absolute))
        {
            return true;
        }

        if (request.AirDate is DateOnly airDate && numbering.AirDate == airDate)
        {
            return true;
        }

        if (request.EpisodeNumber is not int episode)
        {
            return false;
        }

        if (numbering.Episodes.Contains(episode))
        {
            return true;
        }

        // A pack has a season but no episode list. MatchesRequestedSeason has already confirmed the
        // season, so an empty episode list here means "the whole season", which includes this one.
        return numbering.Season is not null && numbering.Episodes.Count == 0;
    }

    /// <summary>The numbering family a release belongs to when it states no season.</summary>
    private static string FamilyOf(EpisodeNumbering numbering)
    {
        if (numbering.AbsoluteEpisodes.Count > 0)
        {
            return "absolute";
        }

        if (numbering.AirDate is not null)
        {
            return "airDate";
        }

        return numbering.IsComplete ? "complete series" : "none";
    }

    // -- descriptions (persisted verbatim as the reason's actual/expected value) ---------------------

    /// <summary>
    /// The series identity as it is persisted on the reason: the tokens that were actually compared,
    /// so "expected star trek, actual star trek picard" reads as the answer to "why was this
    /// rejected?". The year appears only when both sides stated one, which is when it was compared.
    /// </summary>
    private static string DescribeSeries(string[] titleKey, int? year)
    {
        var title = SeriesTitleKey.Describe(titleKey);
        return year is int value
            ? $"{title} ({value.ToString(CultureInfo.InvariantCulture)})"
            : title;
    }

    private static string DescribeRequestedEpisode(EvaluationRequest request)
    {
        if (request.EpisodeNumber is int episode)
        {
            return request.SeasonNumber is int season
                ? $"S{season:00}E{episode:00}"
                : $"E{episode:00}";
        }

        if (request.AbsoluteNumber is int absolute)
        {
            return $"absolute {absolute.ToString(CultureInfo.InvariantCulture)}";
        }

        return request.AirDate is DateOnly airDate
            ? airDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "any";
    }

    private static string DescribeNumbering(EpisodeNumbering numbering)
    {
        if (numbering.AirDate is DateOnly airDate)
        {
            return airDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (numbering.AbsoluteEpisodes.Count > 0)
        {
            return "absolute " + string.Join(',', numbering.AbsoluteEpisodes);
        }

        if (numbering.Season is not int season)
        {
            return "none";
        }

        return numbering.Episodes.Count == 0
            ? $"S{season:00} pack"
            : $"S{season:00}" + string.Concat(numbering.Episodes.Select(e => $"E{e:00}"));
    }

    // -- title normalisation ------------------------------------------------------------------------

    /// <summary>
    /// The parser's own normalized title, recovered from the canonical key. The key is built as
    /// <c>&lt;normalized-title-with-dashes&gt;[.year][.numbering][.quality]…</c> and a normalized
    /// title contains only letters, digits and spaces — never a '.' — so its first segment is exactly
    /// the title the parser extracted. Reading it back keeps ONE normalisation of the release side in
    /// the repository instead of a second set of rules here.
    /// </summary>
    private static string NormalizedTitleOf(ParsedRelease parsed)
    {
        var key = parsed.Identity.CanonicalKey;
        var cut = key.IndexOf('.', StringComparison.Ordinal);
        return cut < 0 ? key : key[..cut];
    }

    private static EvaluationReason Reason(string rule, string property, string expected, string actual, bool matched) =>
        matched
            ? new EvaluationReason(rule, property, expected, actual, ReasonOutcome.Pass, null)
            : new EvaluationReason(rule, property, expected, actual, ReasonOutcome.Fail, RejectionKind.Permanent);
}
