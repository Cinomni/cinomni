using System.Globalization;
using Cinomni.Decision.Contracts;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Decision.Evaluation;

/// <summary>
/// The identity check for a film: is this release the film that was asked for? A public tracker
/// searches by text, so "Teddy" (2021) came back with every release carrying the word, and a film was
/// judged on quality, size and format alone. A 2160p concert, "Teddy Swims - Live at Coachella 2026",
/// was accepted, downloaded and filed as the film.
/// <para>
/// The title is compared as a <see cref="SeriesTitleKey"/>, exactly as a series title is, so the same
/// tolerances hold (punctuation, diacritics, a leading article) and a longer or shorter name is a
/// different film. The year is compared when the release states one, and exactly: the same search
/// offered "Teddy.2020", a French film, for the Tamil "Teddy" (2021), and a one-year tolerance would
/// have taken it. A film whose releases are dated a year off is rejected with both years in the
/// reason, and an administrator can still take it by hand. A release numbered as an episode is never
/// a film. Every failure is <see cref="RejectionKind.Permanent"/>: the wrong film never becomes the
/// right one.
/// </para>
/// </summary>
internal static class MovieMatcher
{
    public const string MovieRule = "MatchesRequestedMovie";

    /// <summary>
    /// The identity reason for one candidate. Empty for a series request, which the episode checks
    /// judge, and for a search with no title to compare, such as one by external id alone.
    /// </summary>
    public static IReadOnlyList<EvaluationReason> Match(ParsedRelease parsed, EvaluationRequest request)
    {
        if (!request.IsMovie)
        {
            return [];
        }

        var requested = SeriesTitleKey.Of(request.Term);
        if (requested.Length == 0)
        {
            return [];
        }

        var release = SeriesTitleKey.Of(EpisodeMatcher.NormalizedTitleOf(parsed));
        var episode = IsEpisodeShaped(parsed.Numbering) ? EpisodeMatcher.DescribeNumbering(parsed.Numbering!) : null;

        // A year is evidence only when the release states one; a title-only release is judged on its title.
        var yearMatches = request.Year is null || parsed.Year is null || request.Year == parsed.Year;
        var matched = episode is null && yearMatches && SeriesTitleKey.Equal(release, requested);

        var actual = Describe(release, parsed.Year) + (episode is null ? string.Empty : " " + episode);
        return
        [
            matched
                ? new EvaluationReason(MovieRule, "movie", Describe(requested, request.Year), actual, ReasonOutcome.Pass, null)
                : new EvaluationReason(
                    MovieRule, "movie", Describe(requested, request.Year), actual, ReasonOutcome.Fail, RejectionKind.Permanent),
        ];
    }

    /// <summary>
    /// Whether a release names an episode, a season or a date rather than a film. A <c>Part 2</c> or a
    /// "complete" marker alone does not: a film can carry either. Nor does an absolute number: the
    /// parser reads one in a film's second title, as in "28 Days Later - 28 Giorni Dopo (2002)", and the
    /// title check already tells an anime episode from a film.
    /// </summary>
    private static bool IsEpisodeShaped(EpisodeNumbering? numbering) =>
        numbering is not null
        && (numbering.Season is not null || numbering.Episodes.Count > 0 || numbering.AirDate is not null);

    /// <summary>The compared title tokens, with the year when the side states one.</summary>
    private static string Describe(string[] titleKey, int? year)
    {
        var title = SeriesTitleKey.Describe(titleKey);
        return year is int value
            ? $"{title} ({value.ToString(CultureInfo.InvariantCulture)})"
            : title;
    }
}
