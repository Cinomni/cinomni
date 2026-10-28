using System.Xml;
using System.Xml.Linq;

namespace Cinomni.Downloads.Usenet;

/// <summary>One article of an NZB file, in the order the file is assembled.</summary>
internal sealed record NzbSegment(int Number, long Bytes, string MessageId);

/// <summary>One file an NZB names. <see cref="Name"/> is a single path segment, never a path.</summary>
internal sealed record NzbFile(string Name, IReadOnlyList<NzbSegment> Segments);

/// <summary>An NZB, parsed and reduced to the files it actually names.</summary>
internal sealed record NzbDocument(IReadOnlyList<NzbFile> Files);

/// <summary>
/// Parses an NZB. The document is hostile input: DTDs are refused, and a file name that is a path
/// is dropped rather than trusted. A document that does not parse is a failure, not an empty release.
/// </summary>
internal static class NzbDocumentParser
{
    public static NzbDocument Parse(string xml)
    {
        var document = Load(xml) ?? throw new InvalidOperationException("The NZB could not be read.");
        var files = new List<NzbFile>();
        foreach (var file in document.Descendants("file"))
        {
            var name = FileName(file.Attribute("subject")?.Value);
            if (name is null)
            {
                continue;
            }

            var segments = file.Descendants("segment")
                .Select(Segment)
                .Where(segment => segment is not null)
                .Cast<NzbSegment>()
                .OrderBy(segment => segment.Number)
                .ToList();
            if (segments.Count == 0)
            {
                continue;
            }

            files.Add(new NzbFile(name, segments));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException("The NZB names no file.");
        }

        return new NzbDocument(files);
    }

    private static NzbSegment? Segment(XElement element)
    {
        if (!int.TryParse(element.Attribute("number")?.Value, out var number) || number < 1)
        {
            return null;
        }

        _ = long.TryParse(element.Attribute("bytes")?.Value, out var bytes);
        var id = element.Value.Trim();
        if (id.Length == 0 || id.Length > 500 || id.Any(char.IsControl))
        {
            return null;
        }

        return new NzbSegment(number, bytes < 0 ? 0 : bytes, id);
    }

    /// <summary>The quoted name in a yEnc subject, or nothing if that name is a path.</summary>
    internal static string? FileName(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var quoted = Quoted(subject);
        var raw = quoted ?? BeforeMarker(subject, " yEnc");
        if (string.IsNullOrWhiteSpace(raw) || raw.IndexOfAny(['/', '\\', ':']) >= 0 || raw.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var name = Path.GetFileName(raw.Trim());
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? Quoted(string subject)
    {
        var start = subject.IndexOf('"');
        if (start < 0)
        {
            return null;
        }

        var end = subject.IndexOf('"', start + 1);
        return end > start + 1 ? subject[(start + 1)..end] : null;
    }

    private static string? BeforeMarker(string subject, string marker)
    {
        var at = subject.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at > 0 ? subject[..at].Trim() : null;
    }

    private static XDocument? Load(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}

/// <summary>
/// What this installation can do with an NZB. Import accepts a video file and refuses an archive, and
/// nothing here unpacks or repairs, so a packed release is refused before a byte is fetched.
/// </summary>
internal static class NzbPolicy
{
    public const string Packed =
        "This release is packed (rar or par2). Unpacking is not built, so it was not downloaded.";

    public const string NoVideo =
        "The NZB has no video file this installation can import.";

    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".webm", ".mpg", ".mpeg",
    };

    /// <summary>Why the NZB cannot be downloaded, or null when its video files can.</summary>
    public static string? Refusal(NzbDocument document)
    {
        if (document.Files.Any(file => IsPacked(file.Name)))
        {
            return Packed;
        }

        return document.Files.Any(file => Video.Contains(Path.GetExtension(file.Name))) ? null : NoVideo;
    }

    public static bool IsVideo(string name) => Video.Contains(Path.GetExtension(name));

    private static bool IsPacked(string name)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
        {
            return false;
        }

        if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".par2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // .r00 volumes, and split archives named .001.
        if (extension.Length == 4 && extension[0] == '.' && extension[1] is 'r' or 'R'
            && char.IsDigit(extension[2]) && char.IsDigit(extension[3]))
        {
            return true;
        }

        return extension.Length == 4 && extension[0] == '.' && extension[1..].All(char.IsDigit);
    }
}
