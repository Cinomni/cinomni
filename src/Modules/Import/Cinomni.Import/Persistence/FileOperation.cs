using Cinomni.Import.Contracts;

namespace Cinomni.Import.Persistence;

/// <summary>
/// A single recoverable file operation within an import job.
/// It is planned before it touches disk and only marked <see cref="FileOperationState.Verified"/>
/// once its result is confirmed (size compared), so a crash mid-import can resume from the last
/// confirmed step without re-duplicating. A failed verify rolls the operation back. Keyed by
/// <c>(ImportJobId, Seq)</c> like the other append-only children.
/// </summary>
public sealed class FileOperation
{
    private const int PathMaxLength = 2048;

    public Guid ImportJobId { get; init; }

    /// <summary>Ordinal within the job (a season-pack would plan several; a movie plans one).</summary>
    public int Seq { get; init; }

    /// <summary>
    /// What was done to the file. Planned as the intent and then corrected to the outcome by
    /// <see cref="RecordPerformed"/>, because a hardlink the platform refuses becomes a copy and the
    /// household's record of what happened to its file must say which one it got.
    /// </summary>
    public FileOperationType Type { get; private set; }

    public required string FromPath { get; init; }

    public required string ToPath { get; init; }

    public FileOperationState State { get; private set; } = FileOperationState.Planned;

    public bool Verified { get; private set; }

    public static FileOperation Plan(Guid importJobId, int seq, FileOperationType type, string fromPath, string toPath) =>
        new()
        {
            ImportJobId = importJobId,
            Seq = seq,
            Type = type,
            FromPath = Text.Truncate(fromPath, PathMaxLength)!,
            ToPath = Text.Truncate(toPath, PathMaxLength)!,
            State = FileOperationState.Planned,
        };

    public void MarkExecuting() => State = FileOperationState.Executing;

    /// <summary>
    /// Records the operation the filesystem actually carried out. Called before the result is verified,
    /// so even a rolled-back attempt leaves a trail that says what was tried. Nothing is persisted until
    /// the drive's unit of work, so this corrects the plan rather than rewriting a committed row.
    /// </summary>
    public void RecordPerformed(FileOperationType performed) => Type = performed;

    /// <summary>Confirms the operation landed and its result was verified on disk.</summary>
    public void MarkVerified()
    {
        State = FileOperationState.Verified;
        Verified = true;
    }

    public void MarkFailed() => State = FileOperationState.Failed;

    public void MarkRolledBack() => State = FileOperationState.RolledBack;
}
