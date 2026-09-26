using Cinomni.Downloads.Persistence;

namespace Cinomni.Downloads.Application;

/// <summary>
/// How long a transfer may go without moving, and how long a finished one keeps seeding. Both used
/// to be unbounded: a release nobody was sharing sat in <c>Downloading</c> for ever, holding its
/// acquisition goal with it, and a finished one seeded until the engine happened to restart.
/// </summary>
public sealed class TransferOptions
{
    /// <summary>
    /// How long a transfer may make no progress before it fails and its goal moves on to another
    /// release. Measured only while the engine is actually trying: a paused, network-held or
    /// engine-queued transfer is waiting, not stalled, and the clock starts again when it runs.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Upload-to-download ratio at which a new download stops seeding. Null means no bound on ratio.
    /// </summary>
    public double? SeedRatioLimit { get; set; } = 1.0;

    /// <summary>How long a new download seeds at most. Null means no bound on time.</summary>
    public TimeSpan? SeedTimeLimit { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The rule a download created now is given; a task keeps the one it was created with.</summary>
    public SeedingPolicy DefaultSeedingPolicy =>
        new(SeedRatioLimit, SeedTimeLimit is { } limit ? (int)Math.Min(limit.TotalSeconds, int.MaxValue) : null);

    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        if (StallTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Downloads:Transfers:{nameof(StallTimeout)} must be a positive duration (configured: {StallTimeout}).");
        }

        if (SeedRatioLimit is { } ratio && (double.IsNaN(ratio) || ratio <= 0))
        {
            throw new InvalidOperationException(
                $"Downloads:Transfers:{nameof(SeedRatioLimit)} must be positive or empty (configured: {ratio}).");
        }

        if (SeedTimeLimit is { } time && time <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Downloads:Transfers:{nameof(SeedTimeLimit)} must be a positive duration or empty (configured: {time}).");
        }
    }
}
