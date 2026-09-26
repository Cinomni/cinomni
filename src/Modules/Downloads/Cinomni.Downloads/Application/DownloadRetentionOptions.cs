namespace Cinomni.Downloads.Application;

/// <summary>
/// Retention for the Downloads module, which deliberately deletes nothing.
/// <para>
/// <c>download_tasks</c> and <c>download_history</c> are the acquisition audit trail: they answer
/// what was fetched, from where, and what happened to it. What does grow without purpose is the
/// libtorrent resume checkpoint hanging off a download that finished long ago — a <c>bytea</c> blob,
/// so this is a size problem rather than a row-count one. Only that column is trimmed.
/// </para>
/// </summary>
public sealed class DownloadRetentionOptions
{
    /// <summary>
    /// How long a finished download keeps its resume checkpoint. A seeding task is never trimmed at
    /// any age: it is still live in the sidecar. A restart of the sidecar ends its seeding rather than
    /// resuming it (see <c>DownloadTask.EndSeedingLost</c>), and the row is then trimmed like any other.
    /// </summary>
    public TimeSpan CheckpointRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often <c>downloads.retention</c> runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        if (CheckpointRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Downloads:{nameof(CheckpointRetention)} must be a positive duration "
                + $"(configured: {CheckpointRetention}).");
        }

        if (Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Downloads:{nameof(Interval)} must be a positive duration (configured: {Interval}).");
        }
    }
}
