using System.Text;

namespace Cinomni.Downloads.Tests;

/// <summary>yEnc as a poster writes it, for the tests that need an article to decode or to serve.</summary>
internal static class YencWire
{
    /// <summary>1000 bytes that cover every value several times over and start with 4.</summary>
    public static byte[] AllByteValues() =>
        [.. Enumerable.Range(0, 1000).Select(i => (byte)((i + 4) % 256))];

    public static string Wire(IEnumerable<string> lines) => string.Join("\r\n", lines) + "\r\n";

    /// <summary>A yEnc article body as the wire carries it: one character per byte.</summary>
    public static List<string> YencArticle(byte[] plain, string? trailer = null, string? partHeader = null)
    {
        var lines = new List<string> { $"=ybegin line=128 size={plain.Length} name=Film.mkv" };
        if (partHeader is not null)
        {
            lines.Add(partHeader);
        }

        lines.AddRange(EncodeLines(plain));
        lines.Add(trailer is null ? $"=yend size={plain.Length}" : $"=yend size={plain.Length} {trailer}");
        return lines;
    }

    /// <summary>The article a well-behaved poster would send: the part, with the CRC-32 of its bytes.</summary>
    public static List<string> SignedArticle(byte[] plain) => YencArticle(plain, $"crc32={ReferenceCrc32(plain):x8}");

    private static IEnumerable<string> EncodeLines(byte[] plain)
    {
        const int lineLength = 128;
        var line = new StringBuilder();
        foreach (var value in plain)
        {
            var shifted = (byte)(value + 42);
            var critical = shifted is 0 or 10 or 13 or (byte)'=';
            if (line.Length + (critical ? 2 : 1) > lineLength)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (critical)
            {
                line.Append('=').Append((char)(byte)(shifted + 64));
            }
            else
            {
                line.Append((char)shifted);
            }
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    /// <summary>The bitwise CRC-32, written differently from the one under test so they can disagree.</summary>
    public static uint ReferenceCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
