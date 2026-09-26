using System.Text.RegularExpressions;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// Extracts the release group: a bracketed <c>[GROUP]</c> or the trailing <c>-GROUP</c> token,
/// unless that token is actually a quality/codec tag (e.g. the "DL" of "WEB-DL").
/// </summary>
/// <remarks>
/// The anime convention is the mirror image of the scene one — the group leads
/// (<c>[SubsPlease] Show - 12 [A1B2C3D4].mkv</c>) and the <i>trailing</i> bracket holds the CRC32
/// checksum of the file, which is never a group name.
/// <para>
/// A leading bracket is therefore only read as a group when the name is anime-shaped, and only after
/// the scene forms have been tried. Trackers prefix their own domain the very same way
/// (<c>[ Torrent911.com ] Ad.Astra.2019.1080p.BluRay.x264-SPARKS</c>), so an unrestricted leading
/// branch would report the site instead of the scene group on ordinary movie releases — a value that
/// is persisted, carried on <c>ReleaseParsed</c> and read by custom formats.
/// </para>
/// </remarks>
internal static class ReleaseGroupDetector
{
    private static readonly Regex Bracketed = ParserRegex.Compile(@"\[([a-z0-9][a-z0-9.\-]+)\]\s*$");
    private static readonly Regex LeadingBracketed = ParserRegex.Compile(@"^\[([^\]]{1,40})\]");
    private static readonly Regex Trailing = ParserRegex.Compile(@"-([a-z0-9]{2,})\s*$");

    /// <summary>An 8-digit hexadecimal token is a CRC32 checksum, not a release group.</summary>
    private static readonly Regex Crc32 = ParserRegex.Compile(@"^[0-9a-f]{8}$");

    private static readonly Regex NonGroupTag = ParserRegex.Compile(
        @"^(x264|x265|h264|h265|hevc|av1|xvid|divx|1080p|720p|2160p|480p|576p|web|webdl|webrip|dl|rip"
        + @"|bluray|hdtv|dvd|dvdrip|aac|ac3|dts|ddp|eac3|flac|remux|hdr|hdr10|sdr|proper|repack)$");

    /// <summary>
    /// Reads the release group out of <paramref name="title"/>. <paramref name="numbering"/> is the
    /// numbering already parsed out of the same name; it is what tells an anime release (absolute
    /// numbering) apart from a scene release that merely carries a bracketed tracker prefix.
    /// </summary>
    public static string? Detect(string title, EpisodeNumbering? numbering = null)
    {
        var trimmed = title.Trim();

        var bracketed = Bracketed.Match(trimmed);
        var trailingChecksum = bracketed.Success && Crc32.IsMatch(bracketed.Groups[1].Value);
        if (bracketed.Success && !trailingChecksum)
        {
            return bracketed.Groups[1].Value;
        }

        // The scene form wins whenever it is present: it is the group, whatever leads the name.
        var trailing = Trailing.Match(trimmed);
        if (trailing.Success && !NonGroupTag.IsMatch(trailing.Groups[1].Value))
        {
            return trailing.Groups[1].Value;
        }

        if (!IsAnimeShaped(numbering, trailingChecksum))
        {
            return null;
        }

        var leading = LeadingBracketed.Match(trimmed);
        return leading.Success && !Crc32.IsMatch(leading.Groups[1].Value)
            ? leading.Groups[1].Value.Trim()
            : null;
    }

    /// <summary>
    /// Whether the name follows the fansub convention the leading bracket belongs to: absolute
    /// numbering (<c>[Group] Show - 12</c>) or a trailing CRC32 checksum, the two markers no scene
    /// release ever carries.
    /// </summary>
    private static bool IsAnimeShaped(EpisodeNumbering? numbering, bool trailingChecksum) =>
        trailingChecksum || numbering is { AbsoluteEpisodes.Count: > 0 };
}
