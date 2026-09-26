using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Cinomni.Discovery.Contracts;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// A Torznab/Newznab answer that is not a feed: unreadable, or the protocol's own error document. The
/// message names the indexer and a bounded code, never text the indexer sent.
/// </summary>
internal sealed class TorznabFeedException(string indexerName, string code)
    : InvalidOperationException($"Indexer '{indexerName}' did not answer with a feed ({code}).")
{
    public const string Unreadable = "unreadable";

    public const string UnknownCode = "error";

    public string Code { get; } = code;
}

/// <summary>
/// Parses a Torznab/Newznab XML feed (RSS 2.0 + torznab/newznab attributes) into raw release
/// candidates. Pure and side-effect free so the fiddly bits are unit-tested; the transport is a
/// thin wrapper around it. Hardened against XXE — DTDs are prohibited and no external entities
/// are resolved (indexer responses are hostile input).
/// </summary>
internal static class TorznabFeedParser
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";
    private static readonly XNamespace Newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    public static IReadOnlyList<ReleaseCandidate> Parse(
        string xml,
        string indexerName,
        ReleaseProtocol defaultProtocol,
        bool preferMagnet = false)
    {
        // Neither of these is "no results". A feed that does not parse, or the error document the
        // protocol answers with HTTP 200 (bad API key, request limit reached, unsupported function), is
        // a broken indexer; read as an empty feed it looked exactly like a title nobody seeds, and the
        // indexer test reported success.
        var document = TryLoad(xml)
            ?? throw new TorznabFeedException(indexerName, TorznabFeedException.Unreadable);
        if (document.Root is { Name.LocalName: "error" } error)
        {
            throw new TorznabFeedException(indexerName, ErrorCode(error));
        }

        var candidates = new List<ReleaseCandidate>();
        foreach (var item in document.Descendants("item"))
        {
            var candidate = ParseItem(item, indexerName, defaultProtocol, preferMagnet);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static ReleaseCandidate? ParseItem(
        XElement item, string indexerName, ReleaseProtocol defaultProtocol, bool preferMagnet)
    {
        var title = item.Element("title")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var enclosure = item.Element("enclosure");
        var enclosureUrl = enclosure?.Attribute("url")?.Value;
        var magnetUrl = Attr(item, "magneturl");
        var downloadUrl = (preferMagnet ? magnetUrl : enclosureUrl)
            ?? (preferMagnet ? enclosureUrl : magnetUrl)
            ?? item.Element("link")?.Value;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        // The guid is the dedup identity; fall back to the download URL when the feed omits it.
        var releaseGuid = item.Element("guid")?.Value;
        if (string.IsNullOrWhiteSpace(releaseGuid))
        {
            releaseGuid = downloadUrl;
        }

        var size = ParseLong(Attr(item, "size"))
            ?? ParseLong(enclosure?.Attribute("length")?.Value)
            ?? ParseLong(item.Element("size")?.Value)
            ?? 0L;

        var seeders = ParseInt(Attr(item, "seeders"));
        var leechers = Leechers(seeders, ParseInt(Attr(item, "leechers")), ParseInt(Attr(item, "peers")));
        var publishedAt = ParseDate(item.Element("pubDate")?.Value);

        var isTorrent = downloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(enclosure?.Attribute("type")?.Value, "application/x-bittorrent", StringComparison.OrdinalIgnoreCase);
        var protocol = isTorrent ? ReleaseProtocol.Torrent : defaultProtocol;

        // Indexer-attributed numbering. Advisory (most feeds omit it), but when present it is a far
        // more reliable signal than re-parsing the title. "episode" is the documented attribute name;
        // "ep" mirrors the request parameter and some endpoints echo that instead.
        var season = ParseInt(Attr(item, "season"));
        var episode = ParseInt(Attr(item, "episode")) ?? ParseInt(Attr(item, "ep"));
        var tvdbId = Attr(item, "tvdbid");
        var category = Attr(item, "category") ?? item.Element("category")?.Value.Trim();

        return new ReleaseCandidate(
            releaseGuid, title, downloadUrl, protocol, size, seeders, publishedAt, indexerName,
            season, episode, NullIfBlank(tvdbId), NullIfBlank(category), leechers);
    }

    /// <summary>
    /// Torznab names the swarm two ways: <c>leechers</c> when a feed says so outright, otherwise <c>peers</c>,
    /// which is everybody in the swarm — seeders included. Derived from peers only when the arithmetic
    /// makes sense; a peers figure below the seeders is a feed that means something else by it.
    /// </summary>
    private static int? Leechers(int? seeders, int? leechers, int? peers) =>
        leechers ?? (peers is { } all && seeders is { } seeding && all >= seeding ? all - seeding : null);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Attr(XElement item, string name) =>
        item.Elements(Torznab + "attr").Concat(item.Elements(Newznab + "attr"))
            .FirstOrDefault(a => string.Equals(a.Attribute("name")?.Value, name, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>
    /// Normalized to UTC, never left at the feed's own offset: Npgsql refuses to write a
    /// <c>DateTimeOffset</c> with a non-zero offset into <c>timestamp with time zone</c>, and a real
    /// indexer's <c>pubDate</c> commonly carries its own local offset (a feed from an Australian host
    /// sends <c>+1000</c>, for instance). <see cref="DateTimeStyles.AssumeUniversal"/> alone only
    /// covers a feed that omits the offset entirely; <see cref="DateTimeStyles.AdjustToUniversal"/> is
    /// what converts one that is present.
    /// </summary>
    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// The protocol's numeric error code, and nothing else of it: the description is the indexer's own
    /// text, hostile input that has no place in a log line or an exception message.
    /// </summary>
    private static string ErrorCode(XElement error) =>
        int.TryParse(error.Attribute("code")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? code.ToString(CultureInfo.InvariantCulture)
            : TorznabFeedException.UnknownCode;

    private static XDocument? TryLoad(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
