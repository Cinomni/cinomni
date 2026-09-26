using System.Text;
using System.Text.RegularExpressions;
using TextEncoding = System.Text.Encoding;

namespace Cinomni.Playback.Application;

/// <summary>
/// Pure text handling for subtitles on their way to a browser, which reads WebVTT and nothing else:
/// decoding a sidecar whose encoding nobody declared, and turning SubRip into WebVTT. Everything here
/// is hostile input — a downloaded file — so it is decoded leniently and converted line by line,
/// never evaluated.
/// </summary>
public static partial class WebVtt
{
    private const string Header = "WEBVTT";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>SubRip's <c>00:01:02,345</c>; WebVTT wants a dot before the milliseconds.</summary>
    [GeneratedRegex(@"(\d{1,2}:\d{2}:\d{2}),(\d{1,3})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex SrtTimestamp();

    /// <summary>An ASS override block (<c>{\an8}</c>, <c>{\i1}</c>) some SubRip files carry; WebVTT would print it.</summary>
    [GeneratedRegex(@"\{\\[^}]*\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex AssOverride();

    /// <summary>
    /// The text of a subtitle file: by its byte-order mark when it has one, as UTF-8 when it is valid
    /// UTF-8, and otherwise as Windows-1252 — what an unmarked Western-European SubRip almost always is,
    /// and a decoding that never fails, so a file is shown with an odd character rather than not at all.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return TextEncoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return TextEncoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return TextEncoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252)?.GetString(bytes) ?? TextEncoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>SubRip as WebVTT: the header, dotted timestamps on the timing lines, and no ASS overrides.</summary>
    public static string FromSrt(string srt)
    {
        var builder = new StringBuilder(srt.Length + 16);
        builder.Append(Header).Append("\n\n");
        foreach (var raw in Normalize(srt).TrimStart('﻿').Split('\n'))
        {
            var line = raw.Contains("-->", StringComparison.Ordinal)
                ? SrtTimestamp().Replace(raw, "$1.$2")
                : AssOverride().Replace(raw, string.Empty);
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>A file that says it is WebVTT, with its line endings evened out; null when it is not WebVTT at all.</summary>
    public static string? FromVtt(string vtt)
    {
        var text = Normalize(vtt).TrimStart('﻿');
        return text.StartsWith(Header, StringComparison.Ordinal) ? text : null;
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
