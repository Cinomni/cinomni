using Cinomni.Import.Application;

namespace Cinomni.Import.Files;

/// <summary>Everything the organiser needs to name one landed episode file.</summary>
/// <param name="EpisodeNumbers">
/// More than one for a multi-episode file, which is named <c>S01E01-E02</c>. Empty is not valid —
/// a file with no episode number is left unresolved rather than landed under a made-up name.
/// </param>
public sealed record EpisodeNaming(
    string SeriesTitle,
    int? SeriesYear,
    int SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers,
    string? EpisodeTitle);

/// <summary>
/// Builds the library path a matched file lands at, and is the <b>only</b> place a target path is
/// composed. Two rules hold for every path it returns:
/// <list type="number">
///   <item><description>
///     <b>every</b> segment goes through <see cref="PathGuard.SanitizeName"/> — not just the leaf.
///     The series layout adds a nesting level, and <c>IImportFileSystem.EnsureDirectoryAsync</c> is
///     handed the directory part, so an unsanitised folder would be created on disk unchecked.
///   </description></item>
///   <item><description>
///     the composed path goes through <see cref="PathGuard.Confine"/> against the library root, and a
///     path that will not confine yields <see langword="null"/> rather than a best effort.
///   </description></item>
/// </list>
/// Layouts: a movie keeps the shipped <c>&lt;library&gt;/&lt;name&gt;/&lt;name&gt;.&lt;ext&gt;</c>;
/// a series lands at
/// <c>&lt;Series Title (Year)&gt;/Season NN/&lt;Series Title&gt; - S01E01 - &lt;Episode Title&gt;.&lt;ext&gt;</c>,
/// with <c>S01E01-E02</c> for a multi-episode file and <c>Season 00</c> for specials.
/// </summary>
public sealed class LibraryOrganizer(ImportOptions options)
{
    /// <summary>
    /// The cap on each segment of a movie path — <see cref="PathGuard.SanitizeName"/>'s own default,
    /// which the shipped movie layout has always used, rather than the tighter series budget.
    /// </summary>
    private const int MovieSegmentLength = 200;

    /// <summary>
    /// The shipped movie layout, unchanged: <c>&lt;library&gt;/&lt;name&gt;/&lt;name&gt;.&lt;ext&gt;</c>.
    /// <para>
    /// It is confined but deliberately <b>not</b> measured against
    /// <see cref="ImportOptions.MaxTargetPathLength"/>. The layout repeats the release name in both
    /// segments, so it spends roughly twice its length; a 2160p EXTENDED REMUX scene name — routine,
    /// and well inside <see cref="PathGuard.SanitizeName"/>'s per-segment cap — would exceed the
    /// series ceiling and fail an import that lands cleanly today.
    /// </para>
    /// </summary>
    public string? BuildMoviePath(string sourceFilePath) =>
        BuildMoviePath(sourceFilePath, MovieNamingFormat.ReleaseName, title: null, year: null);

    /// <summary>
    /// The movie layout for <paramref name="format"/>. <see cref="MovieNamingFormat.ReleaseName"/> keeps
    /// the shipped release-file name. <see cref="MovieNamingFormat.TitleYear"/> names the folder and the
    /// file from the catalog title, and falls back to the release name when that title is missing — an
    /// import must not fail because a work has no title to print.
    /// </summary>
    /// <param name="ownerTag">
    /// Set when the plain path is already another title's (<see cref="OwnerTag"/>): it is appended to the
    /// folder and to the file name, so two works that share a title and a year never share a file.
    /// </param>
    public string? BuildMoviePath(
        string sourceFilePath, MovieNamingFormat format, string? title, int? year, string? ownerTag = null)
    {
        var extension = SanitizeExtension(Path.GetExtension(sourceFilePath));
        var stem = format == MovieNamingFormat.TitleYear && !string.IsNullOrWhiteSpace(title)
            ? year is { } known ? $"{title.Trim()} ({known.ToString(System.Globalization.CultureInfo.InvariantCulture)})" : title.Trim()
            : Path.GetFileNameWithoutExtension(sourceFilePath);

        // The stem is capped before the extension is appended, as for an episode: sanitising
        // "<stem><ext>" as one segment cut the extension off a long release name, and the file then
        // played as application/octet-stream and was invisible to every extension-based rescan.
        var folder = Segment(stem, MovieSegmentLength, ownerTag);
        var fileName = Segment(stem, Math.Max(1, MovieSegmentLength - extension.Length), ownerTag) + extension;
        return PathGuard.Confine(options.LibraryRoot, Path.Combine(options.LibraryRoot, folder, fileName));
    }

    /// <summary>
    /// The folder a library path belongs to — the first one under the library root, which is the one a
    /// title owns: a movie's folder, or a series' — or <c>null</c> for a path that is not in the library.
    /// </summary>
    public string? OwnerFolderOf(string libraryPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.LibraryRoot));
        var full = Path.GetFullPath(libraryPath);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        var first = full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, 2)[0];
        return first.Length == 0 ? null : Path.Combine(root, first);
    }

    /// <summary>
    /// A sanitised path segment within <paramref name="budget"/>, with the owner tag — when there is one
    /// — appended after the cap rather than before it, so a long name can never cut the tag off and make
    /// the tagged path the plain one again.
    /// </summary>
    private static string Segment(string name, int budget, string? ownerTag)
    {
        if (ownerTag is null)
        {
            return PathGuard.SanitizeName(name, budget);
        }

        var tag = $" [{ownerTag}]";
        return PathGuard.SanitizeName(name, Math.Max(1, budget - tag.Length)) + tag;
    }

    /// <summary>
    /// The tag that tells one title's files from another's when both would land on the same path: the
    /// last eight hex digits of the work id — the random end of a UUIDv7, so works created a moment apart
    /// still differ — and always the same for the same work, so a re-drive composes the same path.
    /// </summary>
    public static string OwnerTag(Guid workId) => workId.ToString("N")[^8..];

    /// <summary>
    /// The series layout. Returns <see langword="null"/> when the naming is unusable (no episode
    /// number) or the composed path will not confine to the library root.
    /// </summary>
    /// <param name="ownerTag">
    /// Set when the plain path is already another title's (<see cref="OwnerTag"/>): it is appended to the
    /// series folder, which is what two series that share a title and a year would otherwise share.
    /// </param>
    public string? BuildEpisodePath(EpisodeNaming naming, string sourceFilePath, string? ownerTag = null)
    {
        if (naming.EpisodeNumbers.Count == 0)
        {
            return null;
        }

        var extension = SanitizeExtension(Path.GetExtension(sourceFilePath));
        var seriesFolder = Segment(SeriesFolderName(naming), options.MaxSegmentLength, ownerTag);
        var seasonFolder = PathGuard.SanitizeName(
            $"{options.SeasonFolderPrefix} {naming.SeasonNumber:D2}", options.MaxSegmentLength);

        // The stem is capped BEFORE the extension is appended. Episode titles are provider-supplied
        // and routinely over 100 characters, and sanitising "<stem><ext>" as one segment truncates
        // the container extension away — the file then plays as application/octet-stream, is
        // invisible to every extension-based rescan and breaks sidecar subtitle naming.
        var stemBudget = Math.Max(1, options.MaxSegmentLength - extension.Length);
        var fileName = PathGuard.SanitizeName(EpisodeFileName(naming), stemBudget) + extension;

        return ConfineWithinLengthLimit(Path.Combine(options.LibraryRoot, seriesFolder, seasonFolder, fileName));
    }

    /// <summary>
    /// Where a superseded file is set aside so its replacement can take its path. The stamp keeps two
    /// upgrades of the same episode from colliding in the bin — otherwise the second would overwrite
    /// the first, and the household would silently lose the copy it might have wanted back.
    /// </summary>
    /// <param name="stamp">
    /// Passed in rather than read from a clock, so the path a recovery replans is the path the
    /// interrupted attempt used, and a re-run finds its own work already done instead of duplicating it.
    /// </param>
    /// <param name="copy">
    /// Which candidate: the first is the plain stamped name, and each later one is numbered, for the rare
    /// job that has to set aside two files of the same name at the same instant — the bin never
    /// overwrites a copy it already holds.
    /// </param>
    public string? BuildRecyclePath(string existingPath, DateTimeOffset stamp, int copy = 1)
    {
        var fileName = PathGuard.SanitizeName(Path.GetFileName(existingPath), options.MaxSegmentLength);
        var folder = PathGuard.SanitizeName(options.RecycleFolderName, options.MaxSegmentLength);
        var stamped = copy <= 1
            ? $"{stamp:yyyyMMddHHmmss}-{fileName}"
            : $"{stamp:yyyyMMddHHmmss}-{copy.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{fileName}";
        return PathGuard.Confine(options.LibraryRoot, Path.Combine(options.LibraryRoot, folder, stamped));
    }

    /// <summary>The <c>S01E01</c> / <c>S01E01-E02</c> tag a landed episode file is named with.</summary>
    public static string EpisodeTag(int seasonNumber, IReadOnlyList<int> episodeNumbers)
    {
        var ordered = episodeNumbers.Order().ToList();
        var tag = $"S{seasonNumber:D2}E{ordered[0]:D2}";
        return ordered.Count == 1 ? tag : $"{tag}-E{ordered[^1]:D2}";
    }

    private string SeriesFolderName(EpisodeNaming naming) =>
        options.SeriesFolderIncludesYear && naming.SeriesYear is { } year
            ? $"{naming.SeriesTitle} ({year})"
            : naming.SeriesTitle;

    private static string EpisodeFileName(EpisodeNaming naming)
    {
        var tag = EpisodeTag(naming.SeasonNumber, naming.EpisodeNumbers);
        var stem = $"{naming.SeriesTitle} - {tag}";
        return string.IsNullOrWhiteSpace(naming.EpisodeTitle) ? stem : $"{stem} - {naming.EpisodeTitle}";
    }

    /// <summary>Keeps a leading dot and strips anything that is not a plain container extension.</summary>
    private static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var cleaned = PathGuard.SanitizeName(extension.TrimStart('.'), maxLength: 10);
        return cleaned == "unnamed" ? string.Empty : "." + cleaned;
    }

    /// <summary>
    /// Confines a <b>series</b> path and additionally holds it under
    /// <see cref="ImportOptions.MaxTargetPathLength"/>. The series layout adds a nesting level, so a
    /// path Windows would refuse at MAX_PATH is reachable; rejecting it here, where it is
    /// explainable, beats an IOException halfway through a season pack.
    /// </summary>
    private string? ConfineWithinLengthLimit(string candidate)
    {
        var confined = PathGuard.Confine(options.LibraryRoot, candidate);
        return confined is null || confined.Length > options.MaxTargetPathLength ? null : confined;
    }
}
