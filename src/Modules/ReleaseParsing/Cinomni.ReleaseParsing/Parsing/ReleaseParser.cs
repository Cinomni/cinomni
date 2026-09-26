using System.Text.RegularExpressions;
using Cinomni.Kernel.Results;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// The clean-room release-name parser. Pure and deterministic: it composes the field
/// detectors into a <see cref="ParsedRelease"/>. Series numbering is read first, because its span
/// is what bounds the title, the year search and the tag scope; quality tokens are then read from
/// the whole tag portion and the false-positive-prone markers (revision, edition, language) only
/// from it. An unparseable title is an explicit failure, never an exception.
/// </summary>
public sealed class ReleaseParser : IReleaseParser
{
    /// <summary>
    /// Parser rule-set version, recorded on every parse for explainability.
    /// </summary>
    /// <remarks>
    /// 2.0.0 introduced series numbering, which changes the canonical key of every series-shaped
    /// title. Rows already in <c>parsing.parsed_releases</c> keep the 1.0.0 key they were written
    /// with and are deliberately <b>not</b> re-parsed: <c>parse_rule_versions</c> records the
    /// boundary, and the audit discontinuity across it is accepted rather than rewritten (a
    /// re-parse would move <c>parsed_releases.canonical_key</c> out from under the key that
    /// <c>decision.release_evaluations</c> recorded at evaluation time).
    /// </remarks>
    public const string Version = "2.0.0";

    /// <summary>Upper bound on a release name (aligned with the audit column); longer input is rejected up front.</summary>
    private const int MaxTitleLength = 1000;

    public Result<ParsedRelease> Parse(string releaseTitle)
    {
        if (string.IsNullOrWhiteSpace(releaseTitle))
        {
            return Unparseable("Release title is empty.");
        }

        var sourceTitle = releaseTitle.Trim();
        if (sourceTitle.Length > MaxTitleLength)
        {
            return Unparseable("Release title is too long.");
        }

        try
        {
            // Numbering first: a date-numbered episode ("Show.2024.03.01") starts with a valid year
            // token, so the year search must run inside the title part the numbering leaves behind.
            var numbering = EpisodeNumberParser.Scan(sourceTitle);
            var (year, titlePart, tagScope) = numbering is { HasSpan: true }
                ? TitleAnalyzer.Analyze(sourceTitle, numbering.Start, numbering.Length)
                : TitleAnalyzer.Analyze(sourceTitle);

            var normalizedTitle = TitleAnalyzer.NormalizeTitle(titlePart);
            if (normalizedTitle.Length == 0)
            {
                return Unparseable("No title could be extracted.");
            }

            // Quality/revision/edition/language read from the tag scope only, so a title word
            // ("Cam", "Real", "German") is never mistaken for a tag.
            var quality = QualityDetector.Detect(tagScope);
            var revision = RevisionDetector.Detect(tagScope);
            var edition = EditionDetector.Detect(tagScope);
            var languages = LanguageDetector.Detect(tagScope);
            var releaseGroup = ReleaseGroupDetector.Detect(sourceTitle, numbering?.Numbering);
            var canonicalKey = TitleAnalyzer.CanonicalKey(normalizedTitle, year, quality, edition, numbering?.Numbering);

            var parsed = new ParsedRelease(
                SourceTitle: sourceTitle,
                ReleaseType: DetermineReleaseType(numbering?.Numbering),
                Quality: quality,
                Revision: revision,
                Languages: languages,
                ReleaseGroup: releaseGroup,
                Edition: edition,
                Year: year,
                Identity: new ReleaseIdentity(canonicalKey, InfoHash: null),
                ParserVersion: Version,
                Numbering: numbering?.Numbering);

            return Result<ParsedRelease>.Success(parsed);
        }
        catch (RegexMatchTimeoutException)
        {
            return Unparseable("Parsing timed out on a pathological title.");
        }
    }

    /// <summary>
    /// Derives the content kind from the numbering. A name with no season, episode, absolute number
    /// or air date stays a <see cref="ReleaseType.Movie"/> — which is every movie name, unchanged.
    /// </summary>
    private static ReleaseType DetermineReleaseType(EpisodeNumbering? numbering)
    {
        if (numbering is null)
        {
            return ReleaseType.Movie;
        }

        // A complete-series or multi-season marker covers whole seasons whatever else is present.
        if (numbering.IsComplete || numbering.SeasonTo is not null)
        {
            return ReleaseType.SeasonPack;
        }

        var units = numbering.Episodes.Count + numbering.AbsoluteEpisodes.Count;
        if (units > 1)
        {
            return ReleaseType.MultiEpisode;
        }

        if (units == 1 || numbering.AirDate is not null)
        {
            return ReleaseType.SingleEpisode;
        }

        return numbering.Season is not null ? ReleaseType.SeasonPack : ReleaseType.Movie;
    }

    private static Result<ParsedRelease> Unparseable(string message) =>
        Result<ParsedRelease>.Failure(new Error("parsing.unable_to_parse", message));
}
