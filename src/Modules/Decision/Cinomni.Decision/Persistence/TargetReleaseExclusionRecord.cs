namespace Cinomni.Decision.Persistence;

/// <summary>
/// A release that failed one goal, and so is not offered to that goal again. Unlike a
/// <see cref="ReleaseBlockRecord"/> it is not an operator's judgement on the release and not global: the
/// same release may be perfectly good for another goal, and an operator can still grab it by hand.
/// </summary>
public sealed class TargetReleaseExclusionRecord
{
    public Guid Id { get; init; }

    public Guid TargetId { get; init; }

    /// <summary>The same identity Discovery deduplicates on, truncated to the evaluation column width.</summary>
    public required string ReleaseGuid { get; init; }

    /// <summary>What the failed attempt recorded, shown with the skip.</summary>
    public required string Reason { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
