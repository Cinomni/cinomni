namespace Cinomni.ReleaseParsing.Contracts;

/// <summary>
/// The kind of content a release carries. Persisted as text, so members may only ever be appended
/// with an explicit value — renaming or renumbering corrupts existing <c>parsed_releases</c> rows.
/// </summary>
public enum ReleaseType
{
    Movie = 1,
    SingleEpisode = 2,
    MultiEpisode = 3,
    SeasonPack = 4,
}

/// <summary>
/// Where a release came from, ordered low→high by typical fidelity. Discovery does not judge
/// quality; this is the structural value Decision scores later.
/// </summary>
public enum QualitySource
{
    Unknown = 0,
    Cam = 1,
    Telesync = 2,
    Telecine = 3,
    Screener = 4,
    Dvd = 5,
    Hdtv = 6,
    WebRip = 7,
    WebDl = 8,
    Bluray = 9,
}

/// <summary>Vertical resolution of a release (value = pixel height; 0 = unknown).</summary>
public enum QualityResolution
{
    Unknown = 0,
    R480p = 480,
    R576p = 576,
    R720p = 720,
    R1080p = 1080,
    R2160p = 2160,
}

/// <summary>A structural quality modifier layered on top of source × resolution.</summary>
public enum QualityModifier
{
    None = 0,
    Remux = 1,
}

/// <summary>
/// The closed structural quality of a release: <c>source × resolution × modifier</c>. Codec, HDR,
/// audio, edition, group and language are matched separately (as format conditions / attributes),
/// so the quality ladder stays small and closed while preferences over those axes stay open-ended.
/// </summary>
public sealed record Quality(
    QualitySource Source,
    QualityResolution Resolution,
    QualityModifier Modifier);

/// <summary>
/// Release revision markers. <c>Version</c> is the proper count (1 = original, 2 = PROPER, …);
/// <c>Real</c> and <c>IsRepack</c> are flags. By convention, equality of releases ignores
/// <see cref="IsRepack"/> (a repack is the same content), so it is excluded from the canonical key.
/// </summary>
public sealed record Revision(int Version, bool Real, bool IsRepack)
{
    public static readonly Revision Original = new(Version: 1, Real: false, IsRepack: false);
}

/// <summary>
/// Stable identity of a parsed release. <c>CanonicalKey</c> is derived deterministically from the
/// content-identifying attributes (title, year, quality) so the same release from different
/// indexers collapses; <c>InfoHash</c> is the torrent artifact hash when known (else null).
/// </summary>
public sealed record ReleaseIdentity(string CanonicalKey, string? InfoHash);

/// <summary>
/// Series numbering extracted from a release or file name. Every member is optional because the
/// numbering families are mutually exclusive in practice:
/// <list type="bullet">
///   <item><description><c>S02E05</c> → <see cref="Season"/> 2, <see cref="Episodes"/> [5].</description></item>
///   <item><description><c>S02E05-E06</c> → <see cref="Season"/> 2, <see cref="Episodes"/> [5, 6].</description></item>
///   <item><description><c>S02</c> (pack) → <see cref="Season"/> 2, empty <see cref="Episodes"/>.</description></item>
///   <item><description><c>S01-S03</c> → <see cref="Season"/> 1, <see cref="SeasonTo"/> 3.</description></item>
///   <item><description>anime <c>- 123</c> → <see cref="AbsoluteEpisodes"/> [123], no season.</description></item>
///   <item><description>a daily show <c>2024.03.01</c> → <see cref="AirDate"/>.</description></item>
/// </list>
/// <see cref="IsComplete"/> flags a complete-series pack and <see cref="Part"/> a <c>Part 2</c>
/// suffix; both are modifiers that may accompany any of the families above. Numbers are already
/// bounded and de-duplicated by the parser, so consumers never have to sanity-check them.
/// </summary>
public sealed record EpisodeNumbering(
    int? Season,
    int? SeasonTo,
    IReadOnlyList<int> Episodes,
    IReadOnlyList<int> AbsoluteEpisodes,
    DateOnly? AirDate,
    bool IsComplete,
    int? Part);

/// <summary>
/// The structured, auditable result of parsing a release name. Pure attributes only (no persisted
/// id): parsing the same title always yields an equal result (idempotency).
/// </summary>
/// <remarks>
/// <paramref name="Numbering"/> is a trailing optional member on purpose: this record is projected
/// into persisted jsonb, so members may only ever be appended with a default.
/// </remarks>
public sealed record ParsedRelease(
    string SourceTitle,
    ReleaseType ReleaseType,
    Quality Quality,
    Revision Revision,
    IReadOnlyList<string> Languages,
    string? ReleaseGroup,
    string? Edition,
    int? Year,
    ReleaseIdentity Identity,
    string ParserVersion,
    EpisodeNumbering? Numbering = null);
