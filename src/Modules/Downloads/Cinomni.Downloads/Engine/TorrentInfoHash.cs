using System.Security.Cryptography;
using System.Text;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// The v1 info-hash of a magnet or a .torrent payload, as lowercase hex. A hostile file that is not
/// a bencode dictionary, or whose <c>info</c> dictionary cannot be bounded, yields null — never an
/// exception the caller has to treat as success.
/// </summary>
internal static class TorrentInfoHash
{
    public static string? FromMagnet(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = url["magnet:?".Length..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || !part[..eq].Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            const string prefix = "urn:btih:";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Normalize(value[prefix.Length..]);
        }

        return null;
    }

    public static string? FromTorrent(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10 || payload[0] != (byte)'d')
        {
            return null;
        }

        var info = FindInfo(payload);
        if (info.IsEmpty)
        {
            return null;
        }

        return Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
    }

    private static string? Normalize(string raw)
    {
        if (raw.Length == 40 && raw.All(Uri.IsHexDigit))
        {
            return raw.ToLowerInvariant();
        }

        if (raw.Length != 32)
        {
            return null;
        }

        try
        {
            var bytes = Base32(raw);
            return bytes is null ? null : Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[]? Base32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var buffer = 0;
        var output = new List<byte>(20);
        foreach (var ch in value.ToUpperInvariant())
        {
            var index = alphabet.IndexOf(ch);
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return output.Count == 20 ? output.ToArray() : null;
    }

    private static ReadOnlySpan<byte> FindInfo(ReadOnlySpan<byte> payload)
    {
        var cursor = 1;
        while (cursor < payload.Length && payload[cursor] != (byte)'e')
        {
            if (!TryString(payload, ref cursor, out var key))
            {
                return default;
            }

            var valueStart = cursor;
            if (!Skip(payload, ref cursor, depth: 0))
            {
                return default;
            }

            if (key.SequenceEqual("info"u8))
            {
                return payload[valueStart..cursor];
            }
        }

        return default;
    }

    internal static bool TryString(ReadOnlySpan<byte> payload, ref int cursor, out ReadOnlySpan<byte> value)
    {
        value = default;
        var colon = payload[cursor..].IndexOf((byte)':');
        if (colon <= 0 || !int.TryParse(Encoding.ASCII.GetString(payload.Slice(cursor, colon)), out var length) || length < 0)
        {
            return false;
        }

        var start = cursor + colon + 1;
        // Compared by subtraction: a declared length near int.MaxValue would overflow "start + length"
        // into a negative number that passes the bound and then throws from Slice.
        if (length > payload.Length - start)
        {
            return false;
        }

        value = payload.Slice(start, length);
        cursor = start + length;
        return true;
    }

    internal static bool Skip(ReadOnlySpan<byte> payload, ref int cursor, int depth)
    {
        if (cursor >= payload.Length || depth > 32)
        {
            return false;
        }

        return payload[cursor] switch
        {
            (byte)'d' or (byte)'l' => SkipContainer(payload, ref cursor, depth),
            (byte)'i' => SkipInt(payload, ref cursor),
            _ => TryString(payload, ref cursor, out _),
        };
    }

    private static bool SkipContainer(ReadOnlySpan<byte> payload, ref int cursor, int depth)
    {
        cursor++;
        while (cursor < payload.Length && payload[cursor] != (byte)'e')
        {
            if (!Skip(payload, ref cursor, depth + 1))
            {
                return false;
            }
        }

        if (cursor >= payload.Length)
        {
            return false;
        }

        cursor++;
        return true;
    }

    private static bool SkipInt(ReadOnlySpan<byte> payload, ref int cursor)
    {
        var end = payload[(cursor + 1)..].IndexOf((byte)'e');
        if (end < 0)
        {
            return false;
        }

        cursor += end + 2;
        return true;
    }
}
