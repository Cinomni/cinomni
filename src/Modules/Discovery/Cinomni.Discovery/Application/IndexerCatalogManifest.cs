using System.Text.Json;
using System.Text.RegularExpressions;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Net;
using Cinomni.Kernel.Results;

namespace Cinomni.Discovery.Application;

/// <summary>One validated manifest entry, ready to be stored as a snapshot row.</summary>
internal sealed record CatalogManifestEntry(
    string Key,
    int Version,
    string Name,
    string Description,
    ReleaseProtocol ReleaseProtocol,
    IReadOnlyList<string> BaseUrls,
    bool RequiresFlareSolverr,
    int DefaultPriority,
    IndexerSettings DefaultSettings,
    string RawDefinition);

/// <summary>
/// Raw catalog-source manifest JSON to validated entries, or one named error. Pure: no HTTP, no
/// persistence. A manifest is hostile input from a URL an administrator chose, so it is held to the
/// same rules a hand-uploaded definition is, plus the catalog's own: bounded counts and lengths,
/// unique keys, and no <c>session.login</c> block in any entry.
/// <para>
/// All or nothing. One invalid entry rejects the whole manifest with
/// <see cref="InvalidManifestCode"/>, naming the entry and the rule; the caller keeps the previous
/// snapshot. A partial catalog would install a different set every refresh depending on which entry
/// the publisher broke, and silently skipping is exactly what an administrator cannot see.
/// </para>
/// </summary>
internal static partial class IndexerCatalogManifest
{
    public const int SupportedSchemaVersion = 1;

    public const int MaxEntries = 250;

    public const int MaxBaseUrls = 10;

    public const int MaxManifestNameLength = 200;

    /// <summary>Used when an entry omits <c>defaultPriority</c>: the middle of the allowed 1–50 range.</summary>
    public const int FallbackPriority = 25;

    public const string InvalidManifestCode = "discovery.catalog_source.invalid_manifest";

    private const int MaxDepth = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = MaxDepth,
    };

    public static Result<IReadOnlyList<CatalogManifestEntry>> Parse(string json)
    {
        ManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
        }
        catch (JsonException failure)
        {
            // Position only: the parser's own message quotes the offending content.
            return Fail($"The manifest is not valid JSON, or a value has the wrong type (line {failure.LineNumber + 1}).");
        }

        if (manifest is null)
        {
            return Fail("The manifest must be a JSON object.");
        }

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            return Fail($"Only manifest schemaVersion {SupportedSchemaVersion} is supported.");
        }

        if (manifest.Name is { Length: > MaxManifestNameLength })
        {
            return Fail($"The manifest name must be at most {MaxManifestNameLength} characters.");
        }

        if (manifest.Entries is null)
        {
            return Fail("The manifest must declare an 'entries' array.");
        }

        if (manifest.Entries.Count > MaxEntries)
        {
            return Fail($"The manifest declares more than {MaxEntries} entries.");
        }

        var entries = new List<CatalogManifestEntry>(manifest.Entries.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < manifest.Entries.Count; index++)
        {
            var entry = ValidateEntry(manifest.Entries[index]);
            if (entry.IsFailure)
            {
                return Fail($"Entry {index + 1}: {entry.Error.Message}");
            }

            if (!keys.Add(entry.Value.Key))
            {
                return Fail($"Entry {index + 1}: the key '{entry.Value.Key}' appears more than once.");
            }

            entries.Add(entry.Value);
        }

        return Result<IReadOnlyList<CatalogManifestEntry>>.Success(entries);
    }

    private static Result<CatalogManifestEntry> ValidateEntry(EntryDto? dto)
    {
        if (dto is null)
        {
            return Invalid("an entry must be a JSON object.");
        }

        if (dto.Key is null || !KeyPattern().IsMatch(dto.Key))
        {
            return Invalid(
                $"'key' must be 1 to {IndexerCatalogSourceEntry.KeyMaxLength} characters of a-z, 0-9, '.', '_' "
                + "or '-', starting with a letter or digit.");
        }

        var key = dto.Key;
        if (dto.Version is not >= 1)
        {
            return Invalid($"'{key}': 'version' must be a positive integer.");
        }

        var name = dto.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > Indexer.NameMaxLength || HasControlCharacter(name))
        {
            return Invalid($"'{key}': 'name' must be 1 to {Indexer.NameMaxLength} printable characters.");
        }

        var description = dto.Description?.Trim() ?? string.Empty;
        if (description.Length > IndexerCatalogSourceEntry.DescriptionMaxLength || HasControlCharacter(description))
        {
            return Invalid(
                $"'{key}': 'description' must be at most {IndexerCatalogSourceEntry.DescriptionMaxLength} printable characters.");
        }

        if (!string.Equals(dto.Protocol, nameof(IndexerProtocol.Definition), StringComparison.Ordinal))
        {
            return Invalid($"'{key}': 'protocol' must be 'Definition'.");
        }

        // The MVP downloads torrents only; a Usenet entry would install an indexer nothing can grab from.
        if (!string.Equals(dto.ReleaseProtocol, nameof(ReleaseProtocol.Torrent), StringComparison.Ordinal))
        {
            return Invalid($"'{key}': 'releaseProtocol' must be 'Torrent'.");
        }

        var baseUrls = ValidateBaseUrls(key, dto.BaseUrls);
        if (baseUrls.IsFailure)
        {
            return Result<CatalogManifestEntry>.Failure(baseUrls.Error);
        }

        var priority = dto.DefaultPriority ?? FallbackPriority;
        if (priority is < 1 or > 50)
        {
            return Invalid($"'{key}': 'defaultPriority' must be between 1 and 50.");
        }

        var settings = ValidateSettings(key, dto.DefaultSettings);
        if (settings.IsFailure)
        {
            return Result<CatalogManifestEntry>.Failure(settings.Error);
        }

        var requiresFlareSolverr = dto.RequiresFlareSolverr ?? false;
        if (requiresFlareSolverr && !settings.Value.UseFlareSolverr)
        {
            // Installing with its own defaults would be refused, so the entry could never be installed as published.
            return Invalid($"'{key}': it requires FlareSolverr, so 'defaultSettings.useFlareSolverr' must be true.");
        }

        var definition = ValidateDefinition(key, dto.Definition);
        if (definition.IsFailure)
        {
            return Result<CatalogManifestEntry>.Failure(definition.Error);
        }

        return Result<CatalogManifestEntry>.Success(new CatalogManifestEntry(
            key, dto.Version.Value, name, description, ReleaseProtocol.Torrent, baseUrls.Value,
            requiresFlareSolverr, priority, settings.Value, definition.Value));
    }

    private static Result<IReadOnlyList<string>> ValidateBaseUrls(string key, List<string?>? urls)
    {
        if (urls is null || urls.Count is 0 or > MaxBaseUrls)
        {
            return Result<IReadOnlyList<string>>.Failure(Problem(
                $"'{key}': 'baseUrls' must list 1 to {MaxBaseUrls} URLs."));
        }

        var normalized = new List<string>(urls.Count);
        foreach (var url in urls)
        {
            // The same rule an administrator's own base URL meets: absolute http(s), no user info, no
            // private IP literal, and measured as it will be stored.
            if (url is null || url.Length > Indexer.BaseUrlMaxLength
                || !SsrfGuard.TryValidatePublicUrl(url, out var uri)
                || uri.ToString().Length > Indexer.BaseUrlMaxLength)
            {
                return Result<IReadOnlyList<string>>.Failure(Problem(
                    $"'{key}': every base URL must be an absolute http(s) URL with a public host."));
            }

            normalized.Add(uri.ToString());
        }

        return Result<IReadOnlyList<string>>.Success(normalized);
    }

    private static Result<IndexerSettings> ValidateSettings(string key, SettingsDto? dto)
    {
        if (dto is null)
        {
            return Result<IndexerSettings>.Success(new IndexerSettings());
        }

        var unit = IndexerLimitsUnit.Day;
        if (dto.LimitsUnit is not null
            && (!Enum.TryParse(dto.LimitsUnit, ignoreCase: false, out unit) || !Enum.IsDefined(unit)
                || int.TryParse(dto.LimitsUnit, out _)))
        {
            return Result<IndexerSettings>.Failure(Problem($"'{key}': 'defaultSettings.limitsUnit' is not a known unit."));
        }

        if (dto.MinimumSeeders is < 0 || dto.QueryLimit is <= 0 || dto.GrabLimit is <= 0)
        {
            return Result<IndexerSettings>.Failure(Problem(
                $"'{key}': 'defaultSettings' are outside their allowed bounds."));
        }

        return Result<IndexerSettings>.Success(new IndexerSettings(
            dto.MinimumSeeders, dto.PreferMagnet ?? false, dto.QueryLimit, dto.GrabLimit, unit,
            dto.UseFlareSolverr ?? false));
    }

    private static Result<string> ValidateDefinition(string key, JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } definition)
        {
            return Result<string>.Failure(Problem($"'{key}': 'definition' must be a JSON object."));
        }

        var raw = definition.GetRawText();
        var parsed = IndexerDefinitionParser.Parse(raw, strictSelectors: true);
        if (parsed.IsFailure)
        {
            return Result<string>.Failure(Problem($"'{key}': {parsed.Error.Message} ({parsed.Error.Code})"));
        }

        // A catalog entry never ships a login. An account on a site is something the household holds
        // by its own choice; a subscribed list is not where that decision is made. An administrator who
        // has one uploads that definition by hand.
        if (parsed.Value.Session is not null)
        {
            return Result<string>.Failure(Problem($"'{key}': a catalog entry must not declare a 'session' login block."));
        }

        if (parsed.Value.ResultKind != ReleaseProtocol.Torrent)
        {
            return Result<string>.Failure(Problem($"'{key}': the definition's resultKind must match 'releaseProtocol'."));
        }

        return Result<string>.Success(raw);
    }

    private static bool HasControlCharacter(string value) => value.Any(char.IsControl);

    private static Error Problem(string message) => new(InvalidManifestCode, message);

    private static Result<CatalogManifestEntry> Invalid(string message) =>
        Result<CatalogManifestEntry>.Failure(Problem(message));

    private static Result<IReadOnlyList<CatalogManifestEntry>> Fail(string message) =>
        Result<IReadOnlyList<CatalogManifestEntry>>.Failure(Problem(message));

    // \z, not $: '$' also matches before a trailing newline.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,49}\z",RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyPattern();

    private sealed record ManifestDto(int? SchemaVersion, string? Name, List<EntryDto?>? Entries);

    private sealed record EntryDto(
        string? Key,
        int? Version,
        string? Name,
        string? Description,
        string? Protocol,
        string? ReleaseProtocol,
        List<string?>? BaseUrls,
        bool? RequiresFlareSolverr,
        int? DefaultPriority,
        SettingsDto? DefaultSettings,
        JsonElement? Definition);

    private sealed record SettingsDto(
        int? MinimumSeeders,
        bool? PreferMagnet,
        int? QueryLimit,
        int? GrabLimit,
        string? LimitsUnit,
        bool? UseFlareSolverr);
}
