using Cinomni.Discovery.Contracts;

namespace Cinomni.Discovery.Persistence;

/// <summary>
/// One validated entry of a catalog source's last successfully fetched manifest. Replaced as a whole
/// set on the next successful refresh; never edited in place. The definition is stored verbatim and
/// re-parsed at install time, the same discipline <see cref="IndexerDefinition"/> follows.
/// </summary>
public sealed class IndexerCatalogSourceEntry
{
    public const int KeyMaxLength = 50;

    public const int DescriptionMaxLength = 1000;

    public Guid SourceId { get; init; }

    /// <summary>Lower-case, unique within its source only.</summary>
    public required string Key { get; init; }

    /// <summary>Position in the manifest, so the catalog lists entries in the order the source chose.</summary>
    public int Position { get; init; }

    public int Version { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public ReleaseProtocol ReleaseProtocol { get; init; }

    /// <summary>The origins an installed indexer may point at; the first is the default.</summary>
    public required List<string> BaseUrls { get; init; }

    public bool RequiresFlareSolverr { get; init; }

    public int DefaultPriority { get; init; }

    public int? MinimumSeeders { get; init; }

    public bool PreferMagnet { get; init; }

    public int? QueryLimit { get; init; }

    public int? GrabLimit { get; init; }

    public IndexerLimitsUnit LimitsUnit { get; init; } = IndexerLimitsUnit.Day;

    public bool UseFlareSolverr { get; init; }

    /// <summary>The entry's definition document verbatim, as <c>IndexerDefinitionParser</c> accepted it.</summary>
    public required string RawDefinition { get; init; }

    public IndexerSettings ToDefaultSettings() =>
        new(MinimumSeeders, PreferMagnet, QueryLimit, GrabLimit, LimitsUnit, UseFlareSolverr);
}
