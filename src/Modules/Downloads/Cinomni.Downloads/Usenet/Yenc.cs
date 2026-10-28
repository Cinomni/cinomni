using System.Globalization;

namespace Cinomni.Downloads.Usenet;

/// <summary>
/// Decodes one yEnc article body. The article is hostile input: a body that is not yEnc, whose
/// decoded size is not the size the part header states, or whose bytes do not match the checksum
/// the trailer states, is a failure rather than a file with a hole or a flipped byte.
/// <para>
/// The article is a string with one character per byte (Latin1), not text. yEnc uses all 256 byte
/// values, so reading it as ASCII or UTF-8 would corrupt it without changing its length.
/// </para>
/// </summary>
internal static class Yenc
{
    public const int MaxDecodedBytes = 8 * 1024 * 1024;

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Decode(string article)
    {
        var lines = article.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var begin = Array.FindIndex(lines, line => line.StartsWith("=ybegin ", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, line => line.StartsWith("=yend ", StringComparison.Ordinal));
        if (begin < 0 || end <= begin)
        {
            throw new InvalidOperationException("An article was not yEnc.");
        }

        var expected = PartSize(lines, begin, end);
        using var output = new MemoryStream();
        for (var i = begin + 1; i < end; i++)
        {
            if (lines[i].StartsWith("=ypart ", StringComparison.Ordinal) || lines[i].Length == 0)
            {
                continue;
            }

            DecodeLine(lines[i], output);
            if (output.Length > MaxDecodedBytes)
            {
                throw new InvalidOperationException("A yEnc part is larger than this installation will decode.");
            }
        }

        if (expected is { } size && output.Length != size)
        {
            throw new InvalidOperationException("A yEnc part did not have the size its header stated.");
        }

        var decoded = output.ToArray();
        if (StatedCrc(lines, begin, end) is { } stated && stated != Crc32(decoded))
        {
            throw new InvalidOperationException("A yEnc part did not match the checksum its trailer stated.");
        }

        return decoded;
    }

    /// <summary>
    /// The checksum of this part's bytes, when the trailer states one; some posters leave it out. A
    /// multi-part trailer carries <c>pcrc32</c> for the part and, on the last part, <c>crc32</c> for
    /// the whole file, which says nothing about this part. A single-part article carries only <c>crc32</c>.
    /// </summary>
    private static uint? StatedCrc(string[] lines, int begin, int end)
    {
        var hasPart = Array.FindIndex(
            lines, begin + 1, end - begin - 1, line => line.StartsWith("=ypart ", StringComparison.Ordinal)) >= 0;
        var field = Value(lines[end], "pcrc32") ?? (hasPart ? null : Value(lines[end], "crc32"));
        return uint.TryParse(field, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var crc) ? crc : null;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var value = n;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            }

            table[n] = value;
        }

        return table;
    }

    /// <summary>
    /// <c>=ybegin size</c> is the whole file. A multi-part article states its own span on <c>=ypart</c>,
    /// and checking it against the whole file would refuse every part but the last.
    /// </summary>
    private static int? PartSize(string[] lines, int begin, int end)
    {
        for (var i = begin + 1; i < end; i++)
        {
            if (!lines[i].StartsWith("=ypart ", StringComparison.Ordinal))
            {
                continue;
            }

            if (int.TryParse(Value(lines[i], "begin"), out var from)
                && int.TryParse(Value(lines[i], "end"), out var to)
                && to >= from)
            {
                return to - from + 1;
            }
        }

        return int.TryParse(Value(lines[begin], "size"), out var size) && size >= 0 ? size : null;
    }

    /// <summary>The value of a header field. The name is matched whole: <c>crc32</c> is not the end of <c>pcrc32</c>.</summary>
    private static string? Value(string header, string name)
    {
        var token = " " + name + "=";
        var at = header.IndexOf(token, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + token.Length;
        var end = header.IndexOf(' ', start);
        return end < 0 ? header[start..] : header[start..end];
    }

    private static void DecodeLine(string line, Stream output)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var value = AsByte(line[i]);
            if (value == (byte)'=')
            {
                if (i + 1 >= line.Length)
                {
                    throw new InvalidOperationException("A yEnc escape was cut off.");
                }

                value = (byte)(AsByte(line[++i]) - 64);
            }

            output.WriteByte((byte)(value - 42));
        }
    }

    /// <summary>A character above 0xFF means the article was read as text, and would be corrupt.</summary>
    private static byte AsByte(char value) =>
        value <= 0xFF ? (byte)value : throw new InvalidOperationException("An article was not read as bytes.");
}
