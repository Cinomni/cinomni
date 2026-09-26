using System.Text;

namespace Cinomni.Playback.Application;

/// <summary>
/// Rewrites an HLS media playlist so every segment it names carries the playlist's own query token.
/// </summary>
/// <remarks>
/// A browser that plays HLS natively (Safari) fetches the playlist from the URL it was given, with
/// <c>?access_token=</c>, and then each segment from the relative name the playlist lists — without the
/// query. Every segment was refused and the stream never started. hls.js is unaffected: it sends the
/// header. The token put here is the one the request already authenticated with, on the same route,
/// so no URL ends up carrying anything it did not already carry.
/// </remarks>
internal static class HlsPlaylist
{
    public static string CarryQueryToken(string playlist, string token)
    {
        var query = "?access_token=" + Uri.EscapeDataString(token);
        var builder = new StringBuilder(playlist.Length + 64);
        using var reader = new StringReader(playlist);
        while (reader.ReadLine() is { } line)
        {
            // A URI line is any non-blank line that is not a tag or comment. Only the bare names this
            // server writes are rewritten; anything else is passed through untouched. A fragmented-MP4
            // stream also names its init segment inside a tag, which needs the token just the same.
            builder.Append(IsSegmentName(line) ? line + query : WithTokenedMap(line, query)).Append('\n');
        }

        return builder.ToString();
    }

    private const string InitMap = "#EXT-X-MAP:URI=\"init.mp4\"";

    private static bool IsSegmentName(string line) =>
        line.Length > 0
        && !line.StartsWith('#')
        && (line.EndsWith(".ts", StringComparison.Ordinal) || line.EndsWith(".m4s", StringComparison.Ordinal))
        && line.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>The init-segment tag with the token on its URI; any other line unchanged.</summary>
    private static string WithTokenedMap(string line, string query) =>
        line == InitMap ? $"#EXT-X-MAP:URI=\"init.mp4{query}\"" : line;
}
