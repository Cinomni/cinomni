using Cinomni.Catalog.Contracts;

namespace Cinomni.Import.Application;

/// <summary>
/// Configuration for the Import module: where the library lives, how landed files are named, and the
/// specs that decide whether a file may be imported. Video extensions are a closed allowlist
/// (every variable value is allowlisted); everything else — executables,
/// archives, scripts, samples — is refused, since downloaded content is hostile data.
/// </summary>
public sealed class ImportOptions
{
    /// <summary>Storage root the imported files land in (the library tree). Fixed by the admin.</summary>
    public string LibraryRoot { get; set; } = "/data/library";

    /// <summary>
    /// The directory completed downloads are staged in, and therefore the only place an import may
    /// read from. The composition root sets it to the same absolute path the sidecar downloads into.
    /// <para>
    /// It exists because a job's source path is composed from the torrent's own name, which is
    /// swarm-supplied, and because startup recovery replays that path out of the database long after
    /// the download that produced it. Confining it is what stops an import from scanning an arbitrary
    /// directory and hardlinking whatever passes the policy into the library under the operator's
    /// catalogue title.
    /// </para>
    /// <para>
    /// Empty — the default — means no staging root was configured and no confinement is applied, which
    /// is how an installation that predates this setting keeps behaving. It is not the packaged
    /// default: <c>Import:StagingRoot</c> falls back to <c>Downloads:Sidecar:StagingPath</c>, so a
    /// deployment gets confinement without configuring the same directory twice.
    /// </para>
    /// </summary>
    public string StagingRoot { get; set; } = string.Empty;

    /// <summary>
    /// Folder under <see cref="LibraryRoot"/> where a superseded file is set aside when its replacement
    /// lands on the same path. Inside the root on purpose: a move within one filesystem is atomic and
    /// needs no free space, while a recycle bin elsewhere would turn every upgrade into a full copy and
    /// could fail half-way on a full disk.
    /// <para>
    /// Nothing empties it. Deleting a household's only copy of something because the replacement looked
    /// better is not a decision to automate — the files stay until someone removes them.
    /// </para>
    /// </summary>
    public string RecycleFolderName { get; set; } = ".recycle";

    /// <summary>Accepted video container extensions (lower-case, with leading dot).</summary>
    public IReadOnlySet<string> VideoExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".webm", ".mpg", ".mpeg",
    };

    /// <summary>
    /// A movie file smaller than this is treated as a sample/junk and skipped (50 MiB). It is a
    /// <em>movie</em> heuristic — see <see cref="MinEpisodeBytes"/> for why episodes need their own.
    /// </summary>
    public long MinVideoBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// The same floor for a single episode (8 MiB). A 22-minute SD episode is routinely well under
    /// the 50 MiB movie floor, which would silently discard every file of a legitimate season pack.
    /// </summary>
    public long MinEpisodeBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>Folder prefix for a season, giving <c>Season 01</c> (and <c>Season 00</c> for specials).</summary>
    public string SeasonFolderPrefix { get; set; } = "Season";

    /// <summary>Whether the series folder carries the year, as <c>The Wire (2002)</c>.</summary>
    public bool SeriesFolderIncludesYear { get; set; } = true;

    /// <summary>Longest single path segment the organiser will emit (folder or file name).</summary>
    public int MaxSegmentLength { get; set; } = 150;

    /// <summary>
    /// Longest full target path the organiser will emit. The series layout adds a nesting level, so
    /// Windows' MAX_PATH is a real limit here — a path over it is rejected where it is explainable
    /// rather than surfacing as an IOException halfway through a season pack.
    /// </summary>
    public int MaxTargetPathLength { get; set; } = 240;

    /// <summary>The size floor that applies to a work of the given kind.</summary>
    public long MinBytesFor(WorkKind kind) => kind == WorkKind.Series ? MinEpisodeBytes : MinVideoBytes;
}
