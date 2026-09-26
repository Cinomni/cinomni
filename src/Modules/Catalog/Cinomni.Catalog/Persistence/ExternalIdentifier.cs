using Cinomni.Catalog.Contracts;

namespace Cinomni.Catalog.Persistence;

/// <summary>
/// Links a work to an external provider id (1:N). Unique per (provider, value, kind) so the same
/// external id can't point at two works of one kind, but it is never the work's identity. The kind is
/// part of the key because a provider can number films and shows independently: TMDB movie 1399 and
/// TMDB tv 1399 are unrelated titles.
/// </summary>
public sealed class ExternalIdentifier
{
    public Guid Id { get; init; }

    public Guid WorkId { get; init; }

    public MetadataProvider Provider { get; init; }

    public required string Value { get; init; }

    /// <summary>The kind of the work it belongs to, copied here so the unique index can include it.</summary>
    public WorkKind Kind { get; init; }
}
