using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Decision.Persistence;

/// <summary>
/// A quality (source × resolution) the profile accepts, with its rank (higher = better) and
/// optional absolute size bounds. Merges the documented QualityProfile.items + QualityDefinition +
/// SizeConstraint for the MVP; the runtime-aware MB/min model lands later.
/// </summary>
public sealed class AllowedQuality
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    public QualitySource Source { get; init; }

    public QualityResolution Resolution { get; init; }

    /// <summary>Higher wins when ranking accepted candidates.</summary>
    public int Rank { get; init; }

    public long? MinSizeBytes { get; init; }

    public long? MaxSizeBytes { get; init; }
}
