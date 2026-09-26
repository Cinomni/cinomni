namespace Cinomni.Operations.Persistence;

/// <summary>
/// A periodic job: on each due tick the scheduler enqueues its command. State (last/next
/// run) is persisted so schedules survive restarts and are derivable, not held in memory.
/// </summary>
public sealed class ScheduledJob
{
    /// <summary>Stable job identity (primary key).</summary>
    public required string Name { get; init; }

    /// <summary>Stable registered name of the command enqueued on each tick.</summary>
    public required string CommandType { get; set; }

    public int IntervalSeconds { get; set; }

    public DateTimeOffset? LastRun { get; set; }

    public DateTimeOffset NextDue { get; set; }

    public bool Enabled { get; set; }
}
