using System.Text.Json;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Providers;

/// <summary>
/// Reads a SubDL search payload. Written from that service's published search API, not from another
/// product. A result that is not an unpacked subtitle file is skipped: this parser never accepts a
/// zip, and a download reference is only a path on the provider's own download host.
/// </summary>
internal static class SubdlSubtitleParser
{
    public const string DownloadHost = "https://dl.subdl.com";

    public static IReadOnlyList<ProviderCandidate> Parse(JsonElement root, SubtitleProviderQuery query)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("subtitles", out var subtitles)
            || subtitles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidates = new List<ProviderCandidate>();
        foreach (var item in subtitles.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("unpack_files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (TryFile(file, query) is { } candidate)
                    {
                        candidates.Add(candidate);
                    }
                }

                continue;
            }

            if (TryFile(item, query) is { } bare)
            {
                candidates.Add(bare);
            }
        }

        return candidates;
    }

    /// <summary>
    /// A download reference this provider will fetch, or null. Only an https path on
    /// <see cref="DownloadHost"/> is accepted; a full URL to anywhere else is refused.
    /// </summary>
    public static Uri? DownloadUri(string downloadRef)
    {
        if (string.IsNullOrWhiteSpace(downloadRef) || downloadRef.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        if (!downloadRef.StartsWith("/subtitle/", StringComparison.Ordinal))
        {
            return null;
        }

        return Uri.TryCreate(DownloadHost + downloadRef, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "dl.subdl.com", StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    public static bool IsSubtitleBytes(byte[] content)
    {
        if (content.Length < 8 || content.Length > 8 * 1024 * 1024)
        {
            return false;
        }

        // Zip, gzip, and an HTML error page are not a subtitle, whatever the URL claimed.
        if (content[0] == (byte)'P' && content[1] == (byte)'K')
        {
            return false;
        }

        if (content[0] == 0x1F && content[1] == 0x8B)
        {
            return false;
        }

        if (content[0] == (byte)'<')
        {
            return false;
        }

        return true;
    }

    private static ProviderCandidate? TryFile(JsonElement file, SubtitleProviderQuery query)
    {
        if (file.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var url = String(file, "url");
        if (DownloadUri(url ?? string.Empty) is null)
        {
            return null;
        }

        if (query.IsEpisode
            && (Int(file, "season") is { } season && season != query.SeasonNumber
                || Int(file, "episode") is { } episode && episode != query.EpisodeNumber))
        {
            return null;
        }

        var format = FormatOf(String(file, "format"), String(file, "name"));
        if (format is null)
        {
            return null;
        }

        var name = String(file, "name");
        var release = String(file, "release_name") ?? name ?? query.Release;
        var hearingImpaired = file.TryGetProperty("hi", out var hi) && hi.ValueKind == JsonValueKind.True;
        // The published search payload documents hi, not a forced flag. The file's own name is the
        // signal, the same token this module writes on disk. An explicit true is honoured if sent.
        var forced = (file.TryGetProperty("forced", out var flag) && flag.ValueKind == JsonValueKind.True)
            || HasToken(name, "forced")
            || HasToken(String(file, "release_name"), "forced");
        return new ProviderCandidate(release, ScoreOf(release, query.Release), hearingImpaired, format.Value, url!, forced);
    }

    private static int ScoreOf(string release, string wanted)
    {
        if (string.IsNullOrWhiteSpace(release) || string.IsNullOrWhiteSpace(wanted))
        {
            return 40;
        }

        return release.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? 70 : 40;
    }

    /// <summary>
    /// The format a search result declares: its <c>format</c> field when present, otherwise the
    /// extension of its file name — never a substring of either. Matching substrings read
    /// <c>The.Assassin.srt</c> as ASS and a VobSub <c>.sub</c> as SRT. A format this module cannot
    /// store is refused (null). Nothing to go on at all is assumed SRT; the download is checked again
    /// against its own bytes (<see cref="FormatOfContent"/>).
    /// </summary>
    private static SubtitleFormat? FormatOf(string? format, string? name)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            return Known(format.Trim().TrimStart('.').ToLowerInvariant());
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return SubtitleFormat.Srt;
        }

        var extension = Path.GetExtension(name.Trim()).TrimStart('.').ToLowerInvariant();
        if (Known(extension) is { } known)
        {
            return known;
        }

        // A name that ends in something else — a release name with no extension at all — says nothing
        // either way: the bytes decide on download. A format this module cannot serve is refused here.
        return Unservable.Contains(extension) ? null : SubtitleFormat.Srt;
    }

    private static SubtitleFormat? Known(string token) => token switch
    {
        "srt" => SubtitleFormat.Srt,
        "ass" or "ssa" => SubtitleFormat.Ass,
        "vtt" or "webvtt" => SubtitleFormat.Vtt,
        _ => null,
    };

    /// <summary>Subtitle and archive types this module cannot store or serve.</summary>
    private static readonly HashSet<string> Unservable =
        ["sub", "idx", "sup", "smi", "sami", "txt", "zip", "rar", "7z", "gz"];

    /// <summary>
    /// What a downloaded file actually is, read from its first lines: WebVTT opens with its signature,
    /// an ASS/SSA script with a <c>[Script Info]</c> section, and SubRip with a cue number and a
    /// <c>--&gt;</c> timing line. Null for anything else — a MicroDVD or VobSub <c>.sub</c> among them —
    /// which this module cannot serve whatever the listing called it.
    /// </summary>
    public static SubtitleFormat? FormatOfContent(byte[] content)
    {
        // Decoded by its byte-order mark: an SRT written as UTF-16 by an older Windows tool is common,
        // and read as UTF-8 its first cue number is unreadable. No mark means UTF-8.
        string head;
        using (var reader = new StreamReader(
            new MemoryStream(content, 0, Math.Min(content.Length, 4096)),
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true))
        {
            head = reader.ReadToEnd();
        }

        head = head.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        if (head.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            return SubtitleFormat.Vtt;
        }

        if (head.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFormat.Ass;
        }

        var lines = head.Split('\n', 3);
        return lines.Length >= 2
            && int.TryParse(lines[0].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            && lines[1].Contains("-->", StringComparison.Ordinal)
                ? SubtitleFormat.Srt
                : null;
    }

    private static bool HasToken(string? value, string token)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && char.IsLetter(value[i]))
            {
                continue;
            }

            if (i > start && value.AsSpan(start, i - start).Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            start = i + 1;
        }

        return false;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
