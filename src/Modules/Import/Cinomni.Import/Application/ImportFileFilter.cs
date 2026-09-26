using System.Text.RegularExpressions;

namespace Cinomni.Import.Application;

/// <summary>
/// The "this is not the feature" checks shared by the import specs and the file-name parser.
/// Downloaded content is hostile data, so both checks are conservative and purely structural: they
/// look at the file name and at the folders inside the download, never at the bytes.
/// </summary>
public static class ImportFileFilter
{
    // Whole-token markers. Matching tokens rather than substrings is what keeps "Extraction.2020"
    // (which contains "extra") and "Interstellar" (which contains "inter") importable.
    private static readonly HashSet<string> ExtraTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "sample", "samples", "extra", "extras", "featurette", "featurettes", "trailer", "trailers",
        "teaser", "bonus", "interview", "interviews", "outtake", "outtakes", "bloopers", "proof", "screens",
    };

    // Multi-word markers, checked over the whitespace-normalised token stream of one segment.
    private static readonly string[] ExtraPhrases =
    [
        "behind the scenes", "deleted scene", "deleted scenes", "making of", "bonus features",
    ];

    private static readonly Regex TokenSeparator = new(
        @"[^a-z0-9]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// The shipped sample heuristic, unchanged: a file whose stem mentions "sample" is junk. Kept as a
    /// substring test on purpose — narrowing it would change what the movie path already accepts.
    /// </summary>
    public static bool IsSample(string path) =>
        Path.GetFileNameWithoutExtension(path).Contains("sample", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the file sits in an extras/featurettes/trailer <b>folder</b> of the download. A
    /// season pack routinely ships one, and its contents are not episodes.
    /// <para>
    /// Two deliberate restrictions keep this from refusing legitimate content for good, because a
    /// refusal here is permanent (the acquisition goal exhausts on a deterministic failure):
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Folders only, never the file's own name.</b> A leaf token match refuses every episode
    ///     of "Trailer Park Boys" and every download of "The Interview". A file whose name says
    ///     "trailer" but carries no episode numbering is already left unresolved further down.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Only below <paramref name="contentRoot"/>.</b> Everything at or above the download —
    ///     the staging mount and the release folder itself — is out of scope: a mount named
    ///     <c>/mnt/media-bonus</c> would otherwise refuse every import in the installation.
    ///     Pass the download's content path; a null root falls back to every folder of the path.
    ///   </description></item>
    /// </list>
    /// </summary>
    public static bool IsExtras(string path, string? contentRoot = null)
    {
        // SkipLast(1) drops the file's own name: only the folders it sits in are a bucket marker.
        foreach (var segment in SegmentsBelow(path, contentRoot).SkipLast(1))
        {
            var tokens = TokenSeparator.Split(segment).Where(t => t.Length > 0).ToArray();
            if (tokens.Any(ExtraTokens.Contains))
            {
                return true;
            }

            var phrase = string.Join(' ', tokens);
            if (Array.Exists(ExtraPhrases, p => phrase.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Splits a path into its segments on either separator (content paths arrive in both forms).</summary>
    public static string[] Segments(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The segments of <paramref name="path"/> that lie strictly inside <paramref name="contentRoot"/>.
    /// A path that is not under the declared root keeps every segment, so a mis-declared root can
    /// never widen what is accepted.
    /// </summary>
    private static IEnumerable<string> SegmentsBelow(string path, string? contentRoot)
    {
        var segments = Segments(path);
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            return segments;
        }

        var root = Segments(contentRoot);
        if (root.Length >= segments.Length)
        {
            return [];
        }

        for (var index = 0; index < root.Length; index++)
        {
            if (!string.Equals(root[index], segments[index], StringComparison.OrdinalIgnoreCase))
            {
                return segments;
            }
        }

        return segments[root.Length..];
    }
}
