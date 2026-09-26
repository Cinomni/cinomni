using Cinomni.Import.Contracts;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Import.Application;

/// <summary>
/// Reads the structural quality of a landed file off the name it arrived under, so it can ride
/// <c>MediaAvailable</c> into the library.
/// <para>
/// It has to happen here and now. The probe result gives geometry, and geometry cannot tell a Bluray
/// rip from a WEB-DL of the same resolution — which is the whole question an upgrade asks. And the name
/// does not survive the import: a series episode lands as <c>Series - S01E01 - Title.mkv</c>, so a later
/// pass over the library would find nothing left to read.
/// </para>
/// </summary>
public sealed class ReleaseQualityReader(IReleaseParser parser)
{
    /// <summary>
    /// The quality of one file, or null when neither name says anything usable — null meaning "unknown",
    /// which downstream treats as not-judgeable rather than as poor.
    /// </summary>
    /// <param name="sourceFilePath">The file as it arrived, before the organiser renamed it.</param>
    /// <param name="contentPath">
    /// The download's own folder. A season pack often carries the quality once, on the folder, while its
    /// episode files are named bare (<c>Show - S01E03.mkv</c>); falling back to it recovers those.
    /// </param>
    public ReleaseQualityInfo? Read(string sourceFilePath, string? contentPath)
    {
        var fromFile = QualityOf(Path.GetFileNameWithoutExtension(sourceFilePath));
        if (fromFile is not null)
        {
            return fromFile;
        }

        // Only the folder's own name, never the whole path: a library root that happens to contain
        // "1080p" would otherwise label every file under it.
        var folderName = contentPath is null ? null : Path.GetFileName(contentPath.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(folderName) ? null : QualityOf(folderName);
    }

    /// <summary>
    /// Null unless the name actually carried a source: an unparseable title, or one that parsed but
    /// resolved to <see cref="QualitySource.Unknown"/>, tells us nothing worth persisting. Recording it
    /// as a known-but-empty quality would let a later comparison treat "we never knew" as "it is bad".
    /// </summary>
    private ReleaseQualityInfo? QualityOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parsed = parser.Parse(name);
        if (parsed.IsFailure || parsed.Value.Quality.Source == QualitySource.Unknown)
        {
            return null;
        }

        var quality = parsed.Value.Quality;
        return new ReleaseQualityInfo(
            quality.Source.ToString(),
            quality.Resolution.ToString(),
            quality.Modifier.ToString(),
            parsed.Value.Revision.Version);
    }
}
