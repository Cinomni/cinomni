using Cinomni.Acquisition.Contracts;

namespace Cinomni.Acquisition.Persistence;

/// <summary>
/// One immutable line of an intent's transition history (append-only). The trail makes the goal's
/// lifecycle observable — every state change, what triggered it, and when.
/// </summary>
public sealed class AcquisitionHistoryRecord
{
    public Guid IntentId { get; init; }

    /// <summary>Monotonic sequence within the intent (composite key with <see cref="IntentId"/>).</summary>
    public int Seq { get; init; }

    public IntentState FromState { get; init; }

    public IntentState ToState { get; init; }

    public required string Trigger { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string? Note { get; init; }
}
