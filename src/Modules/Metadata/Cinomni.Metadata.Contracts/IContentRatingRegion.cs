namespace Cinomni.Metadata.Contracts;

/// <summary>
/// The region whose age classification this installation reads. Empty until an administrator names
/// one. Catalog compares ceilings against it; Identity stamps it onto a ceiling at write time so a
/// later region change cannot reinterpret the certificate under a different ladder.
/// </summary>
public interface IContentRatingRegion
{
    /// <summary>The configured region, or null when the installation has not named one.</summary>
    string? Current { get; }
}

/// <summary>The shipped state: no region, so every classification reads as absent.</summary>
public sealed class UnsetContentRatingRegion : IContentRatingRegion
{
    public string? Current => null;
}

/// <summary>A fixed region, for a composition that is not the live settings store.</summary>
public sealed class FixedContentRatingRegion(string? region) : IContentRatingRegion
{
    public string? Current { get; } = string.IsNullOrWhiteSpace(region) ? null : region.Trim().ToUpperInvariant();
}
