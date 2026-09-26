using System.Text.RegularExpressions;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Import.Application;

/// <summary>
/// What Import reads out of a media file's own name plus the folder it sits in. The families are
/// mutually exclusive in practice: either <see cref="EpisodeNumbers"/> with a
/// <see cref="SeasonNumber"/>, or <see cref="AbsoluteNumbers"/>, or an <see cref="AirDate"/>.
/// </summary>
public sealed record EpisodeFileNumbering(
    int? SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers,
    IReadOnlyList<int> AbsoluteNumbers,
    DateOnly? AirDate);

/// <summary>
/// Reads the numbering of one media <b>file</b> inside a download.
/// <para>
/// This is a deliberately <b>thin</b> wrapper over <see cref="IEpisodeNumberParser"/> — the platform's
/// single numbering vocabulary — and contains <b>no numbering regex of its own</b>. It adds only the
/// two things that are Import's alone to know:
/// </para>
/// <list type="number">
///   <item><description>
///     the <c>Season NN/</c> <b>folder context</b>: a file whose own name carries a bare number
///     (<c>Show - 05.mkv</c>) is an absolute number on its own, but inside <c>Season 02/</c> it is
///     episode 5 of season 2 — the layout used by scene packs and the common <c>Season NN</c> library folder convention;
///   </description></item>
///   <item><description>
///     the <b>sample / extras rejection</b>, which is about where a file sits in a download tree
///     rather than about numbering at all.
///   </description></item>
/// </list>
/// Pure and stateless, therefore safe as a DI singleton. It never throws: an unrecognised, sample or
/// extras file simply yields <see langword="null"/>.
/// </summary>
public sealed class EpisodeFileParser(IEpisodeNumberParser numberParser)
{
    /// <summary>Season number used for specials, matching the <c>Season 00</c> library convention.</summary>
    public const int SpecialsSeasonNumber = 0;

    private const int MaxPathLength = 4096;

    // FOLDER context only — never numbering. "Season 2", "Season.02", "S02" as a whole directory name.
    private static readonly Regex SeasonFolder = new(
        @"^(?:season|series|s)[. _-]?(\d{1,3})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex SpecialsFolder = new(
        @"^(?:specials?|season[. _-]?0+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Reads the numbering of <paramref name="filePath"/>, using its parent folders as context.
    /// Returns <see langword="null"/> when the file is a sample/extra or carries no numbering at all.
    /// </summary>
    /// <param name="contentRoot">
    /// Where the download starts, so the extras rejection never looks at the staging mount above it.
    /// Optional, matching <see cref="ImportFileFilter.IsExtras(string, string?)"/>.
    /// </param>
    public EpisodeFileNumbering? Parse(string filePath, string? contentRoot = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Length > MaxPathLength || IsExcluded(filePath, contentRoot))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var numbering = numberParser.Parse(fileName);
        var folderSeason = SeasonFromFolder(filePath);
        if (numbering is null)
        {
            return null;
        }

        // A bare number in the file name is absolute numbering on its own; inside a Season NN/ folder
        // it is that season's episode number instead. This is the folder context, not a new regex.
        if (folderSeason is { } season && numbering.Season is null && numbering.AbsoluteEpisodes.Count > 0)
        {
            return new EpisodeFileNumbering(season, numbering.AbsoluteEpisodes, [], numbering.AirDate);
        }

        var resolvedSeason = numbering.Season ?? (numbering.Episodes.Count > 0 ? folderSeason : null);
        return new EpisodeFileNumbering(
            resolvedSeason,
            numbering.Episodes,
            numbering.AbsoluteEpisodes,
            numbering.AirDate);
    }

    /// <summary>True when the file is a sample, or sits in an extras folder inside the download.</summary>
    public static bool IsExcluded(string filePath, string? contentRoot = null) =>
        ImportFileFilter.IsSample(filePath) || ImportFileFilter.IsExtras(filePath, contentRoot);

    private static int? SeasonFromFolder(string filePath)
    {
        // Nearest folder wins: <Series>/Season 02/<file>.
        var folders = ImportFileFilter.Segments(filePath).SkipLast(1).Reverse();
        foreach (var folder in folders)
        {
            if (SpecialsFolder.IsMatch(folder))
            {
                return SpecialsSeasonNumber;
            }

            var match = SeasonFolder.Match(folder);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var season))
            {
                return season;
            }
        }

        return null;
    }
}
