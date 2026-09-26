namespace Cinomni.Operations.Persistence;

/// <summary>What produced a backup run.</summary>
public static class BackupTrigger
{
    /// <summary>The periodic backup worker.</summary>
    public const string Scheduled = "Scheduled";

    /// <summary>An operator running <c>backup create</c>.</summary>
    public const string Manual = "Manual";
}

/// <summary>
/// Lifecycle states of a backup run. <c>Running</c> is the only non-terminal one, and it is closed
/// either by the run itself or by the next run that acquires the advisory lock — holding the lock is
/// proof that no earlier run is still going.
/// </summary>
public static class BackupRunOutcome
{
    /// <summary>Written before the dump starts, so an interrupted run leaves a trace.</summary>
    public const string Running = "Running";

    /// <summary>A complete set — dump plus manifest — was published.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>The run reached the end without producing a backup, and says why in the reason.</summary>
    public const string Failed = "Failed";

    /// <summary>Another run held the advisory lock, so this one produced nothing on purpose.</summary>
    public const string Skipped = "Skipped";

    /// <summary>Left <c>Running</c> by a crash or a stopped process, and closed by a later run.</summary>
    public const string Interrupted = "Interrupted";
}

/// <summary>
/// One attempt to back the database up, whatever came of it.
/// <para>
/// This is what answers "when did this installation last successfully back itself up", and it is
/// deliberately a table rather than a log line: backup has no HTTP surface, so the database is the only
/// place the answer can be found later. A run that produced nothing — because it lost the advisory-lock
/// race, or because the disk was full — is recorded exactly like one that produced a dump, with the
/// reason it did not, so a quiet installation is distinguishable from a protected one.
/// </para>
/// </summary>
public sealed class BackupRun
{
    public Guid Id { get; init; }

    /// <summary>One of <see cref="BackupTrigger"/>.</summary>
    public required string TriggeredBy { get; init; }

    /// <summary>One of <see cref="BackupRunOutcome"/>.</summary>
    public required string Outcome { get; set; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Null while the run is under way, and while it stays <c>Running</c> after a crash.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The backup's name (its UTC stamp) once one exists; null for a run that produced none.</summary>
    public string? Stamp { get; set; }

    public long? DumpSizeBytes { get; set; }

    /// <summary>The dump's SHA-256, so a copy off the machine can be checked against this row.</summary>
    public string? DumpSha256 { get; set; }

    /// <summary>
    /// Why a run produced no backup, as the error code and a bounded message. Never set on success.
    /// </summary>
    public string? Reason { get; set; }
}
