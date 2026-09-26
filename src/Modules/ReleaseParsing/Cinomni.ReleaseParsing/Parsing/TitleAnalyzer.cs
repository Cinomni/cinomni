using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// Splits a release name into its title, year and tag portion, and derives the canonical key.
/// The year is the <b>last</b> 19xx/20xx token that has a real title before it, so a leading
/// number-year (e.g. "1917", "2012") is treated as the title, not the release year.
/// </summary>
internal static class TitleAnalyzer
{
    private static readonly Regex Year = ParserRegex.Compile(@"\b(19|20)\d{2}\b");

    private static readonly Regex FirstTag = ParserRegex.Compile(
        @"\b(2160p|1080[pi]|720p|576[pi]|480[pi]|bluray|web-?dl|webrip|web|hdtv|dvd|remux|x264|x265|h264|h265|hevc)\b");

    // The anime convention prefixes the fansub group: "[SubsPlease] Show Name - 12 (1080p)".
    private static readonly Regex LeadingGroup = ParserRegex.Compile(@"^\[[^\]]{1,40}\][. _]*");

    public static (int? Year, string TitlePart, string TagScope) Analyze(string title) =>
        Analyze(title, numberingStart: -1, numberingLength: 0);

    /// <summary>
    /// Numbering-aware split: everything before the numbering is the title (and the only place the
    /// year is looked for), everything after it is the tag scope.
    /// </summary>
    /// <remarks>
    /// Restricting the year search to the title part is what keeps a series year and its numbering
    /// independent — <c>The.Office.2005.S02E05.1080p</c> must yield both <c>Year = 2005</c> and
    /// <c>S02E05</c>, and a date-numbered <c>Show.2024.03.01</c> must yield no year at all.
    /// </remarks>
    public static (int? Year, string TitlePart, string TagScope) Analyze(string title, int numberingStart, int numberingLength)
    {
        if (numberingStart < 0 || numberingLength <= 0 || numberingStart + numberingLength > title.Length)
        {
            return AnalyzeWholeName(title);
        }

        var head = title[..numberingStart];
        var tagScope = title[(numberingStart + numberingLength)..];
        var (year, titleLength, _) = SplitYear(head);
        return (year, StripLeadingGroup(head[..titleLength]), tagScope);
    }

    private static (int? Year, string TitlePart, string TagScope) AnalyzeWholeName(string title)
    {
        var (year, titleLength, tagStart) = SplitYear(title);
        if (year is not null)
        {
            return (year, StripLeadingGroup(title[..titleLength]), title[tagStart..]);
        }

        // No usable year (or the only year is the title itself): cut at the first quality tag and
        // scope the tag detectors to the remainder, so a title word is never read as a scene tag.
        var tag = FirstTag.Match(title);
        return tag.Success
            ? (null, StripLeadingGroup(title[..tag.Index]), title[tag.Index..])
            : (null, StripLeadingGroup(title), title);
    }

    /// <summary>
    /// Finds the last year token that has a real title in front of it, returning the length of that
    /// title and the index just past the year (where the tag scope starts).
    /// </summary>
    private static (int? Year, int TitleLength, int TagStart) SplitYear(string scope)
    {
        var years = Year.Matches(scope);
        for (var i = years.Count - 1; i >= 0; i--)
        {
            var match = years[i];
            if (NormalizeTitle(scope[..match.Index]).Length > 0
                && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                return (year, match.Index, match.Index + match.Length);
            }
        }

        return (null, scope.Length, scope.Length);
    }

    /// <summary>Drops a leading <c>[Group]</c> prefix unless doing so would leave no title at all.</summary>
    private static string StripLeadingGroup(string titlePart)
    {
        var match = LeadingGroup.Match(titlePart);
        if (!match.Success)
        {
            return titlePart;
        }

        var stripped = titlePart[match.Length..];
        return NormalizeTitle(stripped).Length > 0 ? stripped : titlePart;
    }

    /// <summary>
    /// Lower-cased alphanumeric words separated by single spaces, with diacritics folded away
    /// (NFD + drop combining marks) so "Amélie" and "Amelie" — and NFC vs NFD forms — collapse.
    /// </summary>
    public static string NormalizeTitle(string titlePart)
    {
        var decomposed = titlePart.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Deterministic content identity: title + year + numbering + quality + edition. Release group
    /// and revision (proper/repack) are excluded so the same content collapses across indexers and
    /// repacks; edition is included so a Director's Cut is not confused with the Theatrical cut.
    /// The numbering segment sits between title+year and the quality so that S02E05, S02E06, the
    /// S02 pack and two dates of a daily show can never share a key.
    /// </summary>
    public static string CanonicalKey(
        string normalizedTitle,
        int? year,
        Quality quality,
        string? edition,
        EpisodeNumbering? numbering = null)
    {
        var parts = new List<string> { normalizedTitle.Replace(' ', '-') };

        if (year is int y)
        {
            parts.Add(y.ToString(CultureInfo.InvariantCulture));
        }

        parts.AddRange(NumberingSegments(numbering));

        if (quality.Resolution != QualityResolution.Unknown)
        {
            parts.Add($"{(int)quality.Resolution}p");
        }

        if (quality.Source != QualitySource.Unknown)
        {
            parts.Add(quality.Source.ToString().ToLowerInvariant());
        }

        if (quality.Modifier != QualityModifier.None)
        {
            parts.Add(quality.Modifier.ToString().ToLowerInvariant());
        }

        if (!string.IsNullOrEmpty(edition))
        {
            parts.Add(NormalizeTitle(edition).Replace(' ', '-'));
        }

        return string.Join('.', parts);
    }

    private static IEnumerable<string> NumberingSegments(EpisodeNumbering? numbering)
    {
        if (numbering is null)
        {
            yield break;
        }

        if (numbering.AirDate is DateOnly airDate)
        {
            yield return airDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (numbering.Season is int season)
        {
            yield return SeasonSegment(season, numbering);
        }

        if (numbering.AbsoluteEpisodes.Count > 0)
        {
            yield return string.Join('-', numbering.AbsoluteEpisodes.Select(n => $"e{n:0000}"));
        }

        if (numbering.IsComplete)
        {
            yield return "complete";
        }

        if (numbering.Part is int part)
        {
            yield return $"part{part.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    private static string SeasonSegment(int season, EpisodeNumbering numbering)
    {
        if (numbering.SeasonTo is int seasonTo && seasonTo != season)
        {
            return $"s{season:00}-s{seasonTo:00}";
        }

        return numbering.Episodes.Count == 0
            ? $"s{season:00}"
            : $"s{season:00}" + string.Join(string.Empty, numbering.Episodes.Select((e, i) => i == 0 ? $"e{e:00}" : $"-e{e:00}"));
    }
}
