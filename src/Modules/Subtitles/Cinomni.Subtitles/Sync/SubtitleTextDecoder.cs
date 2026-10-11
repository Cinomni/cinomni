using System.Text;

namespace Cinomni.Subtitles.Sync;

/// <summary>
/// The text of a downloaded subtitle whose encoding nobody declared: by its byte-order mark when it has
/// one, as UTF-8 when it is valid UTF-8, and otherwise as Windows-1252, which is what an unmarked
/// Western-European SubRip almost always is and a decoding that never fails.
/// <para>
/// Playback has the same rule for showing a sidecar. It is repeated here rather than shared because
/// a module never references another module's implementation, and the rule is a dozen lines.
/// </para>
/// </summary>
public static class SubtitleTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252)?.GetString(bytes) ?? Encoding.Latin1.GetString(bytes);
        }
    }
}
