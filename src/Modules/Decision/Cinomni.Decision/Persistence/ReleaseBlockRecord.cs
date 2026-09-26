namespace Cinomni.Decision.Persistence;

/// <summary>
/// An operator's decision that a release must not be taken by the sweep. It outlives the evaluation
/// that prompted it: rejected evaluations are aged out, and this is the explanation that has to remain.
/// </summary>
public sealed class ReleaseBlockRecord
{
    public Guid Id { get; init; }

    /// <summary>The same identity Discovery deduplicates on, truncated to the evaluation column width.</summary>
    public required string ReleaseGuid { get; init; }

    public required string ReleaseTitle { get; set; }

    public required string Reason { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
