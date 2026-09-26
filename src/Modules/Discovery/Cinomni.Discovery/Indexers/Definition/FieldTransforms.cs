using System.Globalization;
using System.Text.RegularExpressions;
using Cinomni.Kernel.Results;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// The closed, named vocabulary of value transforms a <see cref="DefinitionFieldRule"/> may apply.
/// Pure and side-effect free, mirroring <see cref="TorznabFeedParser"/>'s discipline: a definition
/// describes extraction rules, never arbitrary code, so every transform here is a small, total-ish
/// function that fails cleanly on hostile input rather than throwing.
/// </summary>
internal static partial class FieldTransforms
{
    private const double Kibibyte = 1024;
    private const double Mebibyte = Kibibyte * 1024;
    private const double Gibibyte = Mebibyte * 1024;
    private const double Tebibyte = Gibibyte * 1024;

    /// <summary>
    /// Every spelling of a unit a result table is observed to use. The single-letter forms are not a
    /// convenience: an Apache-style directory listing and a great many tracker tables write "473K" or
    /// "54.9M" and nothing else, and in that notation the letter always denotes the binary unit — so
    /// they map to the same multipliers as their two-letter spellings rather than to powers of 1000.
    /// Rejecting them made every release from such a site report a size of zero.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> SizeUnitMultipliers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = 1,
        // Spelled out for a tiny file ("351 bytes"), as some result pages list one.
        ["BYTES"] = 1,
        ["K"] = Kibibyte,
        ["KB"] = Kibibyte,
        ["KIB"] = Kibibyte,
        ["M"] = Mebibyte,
        ["MB"] = Mebibyte,
        ["MIB"] = Mebibyte,
        ["G"] = Gibibyte,
        ["GB"] = Gibibyte,
        ["GIB"] = Gibibyte,
        ["T"] = Tebibyte,
        ["TB"] = Tebibyte,
        ["TIB"] = Tebibyte,
    };

    // Responsive result tables sometimes nest a duplicated integer metric (usually seeders) in the
    // size cell, so its TextContent is "4.8 GB1". Only an integer suffix is ignored; arbitrary text
    // remains invalid rather than letting a broad prefix match hide a broken selector.
    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]+)(?:\s*[0-9]+)?\s*$")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}")]
    public static partial Regex PlaceholderPattern();

    /// <summary>
    /// Whitespace-trimmed integer, e.g. a seeder count, with an optional thousands separator
    /// ("22,451", as some sites write large counts). Never throws.
    /// </summary>
    public static Result<int> ParseInt(string? raw)
    {
        if (int.TryParse(raw?.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
        {
            return Result<int>.Success(value);
        }

        return Result<int>.Failure(new Error(
            "discovery.definition.transform.parse_int_failed", $"'{raw}' is not a whole number."));
    }

    /// <summary>A human-readable size, e.g. <c>"1.2 GB"</c> or <c>"700MB"</c>, to a byte count.</summary>
    public static Result<long> ParseSize(string? raw)
    {
        var match = raw is null ? null : SizePattern().Match(raw);
        if (match is not { Success: true }
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude)
            || !SizeUnitMultipliers.TryGetValue(match.Groups[2].Value, out var multiplier))
        {
            return Result<long>.Failure(new Error(
                "discovery.definition.transform.parse_size_failed", $"'{raw}' is not a recognizable size."));
        }

        return Result<long>.Success((long)Math.Round(magnitude * multiplier, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// An exact, definition-declared date format — never a "best guess" parse. Always normalized to
    /// UTC (<see cref="DateTimeStyles.AdjustToUniversal"/>), never left at a site's own offset:
    /// Npgsql refuses to write a <c>DateTimeOffset</c> with a non-zero offset into
    /// <c>timestamp with time zone</c>, and <see cref="DateTimeStyles.AssumeUniversal"/> alone only
    /// covers a format with no offset token at all.
    /// </summary>
    public static Result<DateTimeOffset> ParseDate(string? raw, string format)
    {
        if (raw is not null
            && DateTimeOffset.TryParseExact(
                raw.Trim(),
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return Result<DateTimeOffset>.Success(value);
        }

        return Result<DateTimeOffset>.Failure(new Error(
            "discovery.definition.transform.parse_date_failed", $"'{raw}' does not match the format '{format}'."));
    }

    /// <summary>Never fails: an absent value trims to empty, matching how a blank match reads.</summary>
    public static string Trim(string? raw) => raw?.Trim() ?? string.Empty;

    /// <summary>Never fails: percent-encodes for safe placement inside a query string.</summary>
    public static string UrlEncode(string? raw) => Uri.EscapeDataString(raw ?? string.Empty);

    /// <summary>Resolves a possibly-relative URL against the indexer's own base address.</summary>
    public static Result<string> ResolveRelativeUrl(string? raw, Uri baseUri)
    {
        // A magnet has nothing to resolve against the site; everything else must come out as http(s).
        if (raw?.Trim() is { } magnet && magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Success(magnet);
        }

        if (!string.IsNullOrWhiteSpace(raw) && DefinitionQueryBuilder.TryResolve(baseUri, raw, out var resolved))
        {
            return Result<string>.Success(resolved.ToString());
        }

        return Result<string>.Failure(new Error(
            "discovery.definition.transform.resolve_url_failed", $"'{raw}' cannot be resolved against the indexer base URL."));
    }

    /// <summary>Every <c>{{name}}</c> placeholder referenced by a template, in order of appearance.</summary>
    public static IReadOnlyList<string> ExtractPlaceholders(string template) =>
        PlaceholderPattern().Matches(template).Select(m => m.Groups[1].Value).ToList();
}
