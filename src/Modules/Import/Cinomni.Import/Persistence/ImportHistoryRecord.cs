using Cinomni.Import.Contracts;

namespace Cinomni.Import.Persistence;

/// <summary>
/// One immutable line of an import job's transition history (append-only).
/// The trail makes an import explainable: every state change with its trigger.
/// </summary>
public sealed class ImportHistoryRecord
{
    public Guid ImportJobId { get; init; }

    public int Seq { get; init; }

    public ImportJobState FromState { get; init; }

    public ImportJobState ToState { get; init; }

    public required string Trigger { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string? Note { get; init; }
}
