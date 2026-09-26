using System.Text;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// Rewrites a .torrent so that it announces only to trackers, and fetches only from web seeds, that
/// pass the same rule a magnet's trackers do (<see cref="MagnetLink.IsAcceptableTracker"/>). The
/// sidecar relies on libtorrent's own SSRF mitigation for this; an external qBittorrent has its own,
/// which Cinomni cannot verify, so the file is filtered before it is handed over.
/// <para>
/// Only the top-level <c>announce</c>, <c>announce-list</c>, <c>url-list</c> and <c>httpseeds</c> keys
/// change. Everything else, <c>info</c> above all, is copied byte for byte, so the info-hash the
/// torrent is known by stays the same. A payload that is not a well-formed bencode dictionary yields
/// null.
/// </para>
/// <para>
/// A host <em>name</em> passes, as it does for a magnet's trackers: what it resolves to is decided at
/// announce time, by the client, which is where that residual lives.
/// </para>
/// </summary>
internal static class TorrentFileTrackers
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[]? Filter(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != (byte)'d')
        {
            return null;
        }

        using var output = new MemoryStream(payload.Length);
        output.WriteByte((byte)'d');
        var budget = MagnetLink.MaxTrackers;
        var cursor = 1;
        while (cursor < payload.Length && payload[cursor] != (byte)'e')
        {
            var keyStart = cursor;
            if (!TorrentInfoHash.TryString(payload, ref cursor, out var key))
            {
                return null;
            }

            var valueStart = cursor;
            if (!TorrentInfoHash.Skip(payload, ref cursor, depth: 0))
            {
                return null;
            }

            var rawKey = payload[keyStart..valueStart];
            var value = payload[valueStart..cursor];
            if (key.SequenceEqual("announce"u8))
            {
                if (ReadString(value) is { } url && budget > 0 && MagnetLink.IsAcceptableTracker(url))
                {
                    budget--;
                    output.Write(rawKey);
                    output.Write(value);
                }
            }
            else if (key.SequenceEqual("announce-list"u8))
            {
                WriteIfAny(output, rawKey, FilterTiers(value, ref budget));
            }
            else if (key.SequenceEqual("url-list"u8))
            {
                WriteIfAny(output, rawKey, FilterWebSeeds(value));
            }
            else if (!key.SequenceEqual("httpseeds"u8) && !key.SequenceEqual("nodes"u8))
            {
                // Dropped outright: BEP 17 seeds are obsolete and one more place a URL could hide, and
                // "nodes" is a list of host/port pairs the client's DHT pings — an address of the
                // file's choosing, internal ones included. The DHT finds its own nodes without them.
                output.Write(rawKey);
                output.Write(value);
            }
        }

        if (cursor >= payload.Length)
        {
            return null;
        }

        output.WriteByte((byte)'e');
        return output.ToArray();
    }

    /// <summary>Each tier keeps its acceptable trackers, up to the shared budget; empty tiers are gone.</summary>
    private static byte[]? FilterTiers(ReadOnlySpan<byte> value, ref int budget)
    {
        if (value[0] != (byte)'l')
        {
            return null;
        }

        using var tiers = new MemoryStream();
        var cursor = 1;
        while (value[cursor] != (byte)'e')
        {
            var tierStart = cursor;
            TorrentInfoHash.Skip(value, ref cursor, depth: 0);
            var tier = value[tierStart..cursor];
            if (tier[0] != (byte)'l')
            {
                continue;
            }

            var kept = FilterStrings(tier, url => MagnetLink.IsAcceptableTracker(url), ref budget);
            if (kept is not null)
            {
                tiers.Write(kept);
            }
        }

        return tiers.Length == 0 ? null : Wrap(tiers);
    }

    /// <summary>A web seed is fetched over HTTP by the client, so it passes the tracker rule and is http(s).</summary>
    private static byte[]? FilterWebSeeds(ReadOnlySpan<byte> value)
    {
        // Capped like the trackers: each seed is a connection the client opens.
        var budget = MagnetLink.MaxTrackers;
        if (value[0] != (byte)'l')
        {
            // A single seed may be given as a bare string; the list form is equivalent.
            return ReadString(value) is { } url && IsAcceptableWebSeed(url) ? Wrap(value) : null;
        }

        return FilterStrings(value, IsAcceptableWebSeed, ref budget);
    }

    private static bool IsAcceptableWebSeed(string url) =>
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        && MagnetLink.IsAcceptableTracker(url);

    /// <summary>The list's string elements that pass <paramref name="accept"/>, as a list, or null when none do.</summary>
    private static byte[]? FilterStrings(ReadOnlySpan<byte> list, Func<string, bool> accept, ref int budget)
    {
        using var kept = new MemoryStream();
        var cursor = 1;
        while (list[cursor] != (byte)'e')
        {
            var start = cursor;
            TorrentInfoHash.Skip(list, ref cursor, depth: 0);
            var element = list[start..cursor];
            if (budget > 0 && ReadString(element) is { } url && accept(url))
            {
                budget--;
                kept.Write(element);
            }
        }

        return kept.Length == 0 ? null : Wrap(kept);
    }

    /// <summary>A bencode string as text, or null when it is anything else or not valid UTF-8.</summary>
    private static string? ReadString(ReadOnlySpan<byte> element)
    {
        var cursor = 0;
        if (element.IsEmpty || !char.IsAsciiDigit((char)element[0])
            || !TorrentInfoHash.TryString(element, ref cursor, out var bytes))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static byte[] Wrap(MemoryStream items)
    {
        var list = new byte[items.Length + 2];
        list[0] = (byte)'l';
        items.Position = 0;
        items.ReadExactly(list, 1, (int)items.Length);
        list[^1] = (byte)'e';
        return list;
    }

    private static byte[] Wrap(ReadOnlySpan<byte> item)
    {
        var list = new byte[item.Length + 2];
        list[0] = (byte)'l';
        item.CopyTo(list.AsSpan(1));
        list[^1] = (byte)'e';
        return list;
    }

    private static void WriteIfAny(MemoryStream output, ReadOnlySpan<byte> rawKey, byte[]? value)
    {
        if (value is not null)
        {
            output.Write(rawKey);
            output.Write(value);
        }
    }
}
