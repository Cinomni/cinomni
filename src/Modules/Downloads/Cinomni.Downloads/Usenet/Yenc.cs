using System.Text;

namespace Cinomni.Downloads.Usenet;

/// <summary>
/// Decodes one yEnc article body. The article is hostile input: a body that is not yEnc, or whose
/// decoded size is not the size the part header states, is a failure rather than a file with a hole.
/// </summary>
internal static class Yenc
{
    public const int MaxDecodedBytes = 8 * 1024 * 1024;

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

        return output.ToArray();
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

    private static string? Value(string header, string name)
    {
        var token = name + "=";
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
        var bytes = Encoding.ASCII.GetBytes(line);
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            if (value == (byte)'=')
            {
                if (i + 1 >= bytes.Length)
                {
                    throw new InvalidOperationException("A yEnc escape was cut off.");
                }

                value = (byte)(bytes[++i] - 64);
            }

            output.WriteByte((byte)(value - 42));
        }
    }
}
