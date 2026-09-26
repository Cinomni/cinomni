using Cinomni.Operations.Settings;

namespace Cinomni.Metadata.Application;

/// <summary>
/// The metadata-refresh profile as configuration for the movie slice: the providers to consult (in
/// priority order), how long a snapshot stays fresh before a re-fetch is eligible (the
/// <c>ShouldRefresh</c> TTL), the failure backoff, and when a provider is declared degraded.
/// </summary>
public sealed class MetadataOptions
{
    /// <summary>
    /// Providers to consult, most-preferred first. Search fans out across these (filtered by media kind)
    /// in this order; an unregistered name is ignored. Defaults to TMDB, then TheTVDB, then TVMaze.
    /// </summary>
    public IReadOnlyList<string> Providers { get; set; } = ["tmdb", "tvdb", "tvmaze"];

    /// <summary>How long a fresh snapshot is kept before a re-fetch is eligible.</summary>
    public TimeSpan RefreshTtl { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The much shorter TTL applied to a series whose last snapshot reports it as still producing
    /// episodes (<c>Continuing</c> or <c>Upcoming</c>). A weekly show would otherwise not learn about a
    /// new episode for up to <see cref="RefreshTtl"/>, and every downstream monitoring decision is made
    /// against the structure the last snapshot left behind.
    /// </summary>
    public TimeSpan SeriesRefreshTtl { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// How often the <c>metadata.refresh-continuing-series</c> job sweeps for continuing series that are
    /// due. Shorter than <see cref="SeriesRefreshTtl"/> so a series becomes eligible promptly; the
    /// service, not the sweep, decides whether a refresh actually runs.
    /// </summary>
    public TimeSpan ContinuingSeriesSweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Cap on how many continuing series one sweep enqueues; the remainder is picked up by the next tick.
    /// Mirrors Monitoring's <c>MaxSearchesPerSweep</c> — an unbounded sweep would fan a whole library out
    /// to the providers at once.
    /// </summary>
    public int MaxContinuingRefreshesPerSweep { get; set; } = 100;

    /// <summary>Base of the escalating backoff applied after a failed refresh.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Cap on the failure backoff.</summary>
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Consecutive failed attempts after which a provider is announced <c>ProviderDegraded</c> (in
    /// addition to the per-attempt <c>MetadataRefreshFailed</c>) for observability.
    /// </summary>
    public int DegradeAfterAttempts { get; set; } = 3;

    /// <summary>Preferred metadata/artwork language (BCP-47); seeds the artwork selection preference.</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>
    /// How long a <b>superseded</b> snapshot is kept. Every successful refresh appends a whole new
    /// snapshot with its artwork, seasons and episodes, so a continuing series writes two complete
    /// episode trees a day and nothing ever replaced the previous one. Only superseded snapshots are
    /// eligible: the one <c>refresh_states</c> currently points at is retained regardless of age,
    /// which is what keeps the artwork picker and the catalog's attached snapshot resolvable.
    /// </summary>
    public TimeSpan SnapshotRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often <c>metadata.retention</c> prunes superseded snapshots.</summary>
    public TimeSpan SnapshotPurgeInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Fails fast at composition time on retention values that would make the snapshot sweep unsafe.
    /// Called by <c>AddMetadataAdapters</c>, like every other retention owner, so a bad
    /// <c>Retention:Metadata</c> section stops the Host with a message naming the key instead of
    /// deleting a snapshot the instant it is superseded — Catalog's queued attach-metadata command
    /// resolves the snapshot by id, and a zero window can retire it before that command runs.
    /// </summary>
    /// <summary>
    /// Internal (not private) so <see cref="MetadataSettingDefinitions"/> names the exact same string
    /// rather than a duplicated literal that could drift from it.
    /// </summary>
    internal const string SnapshotRetentionKey = "metadata.snapshotRetention";
    internal const string SnapshotPurgeIntervalKey = "retention.metadata.interval";

    /// <summary>
    /// The throwing wrapper over <see cref="Check"/>: same rules, same order, same messages — see
    /// <see cref="Cinomni.Operations.Retention.RetentionOptions.Check"/>'s remarks for why the two must
    /// never drift.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        foreach (var error in Check(this))
        {
            throw new InvalidOperationException(error.Message);
        }
    }

    /// <summary>The rules themselves, as pure data — see <see cref="Validate"/>.</summary>
    public static IEnumerable<SettingError> Check(MetadataOptions candidate)
    {
        if (candidate.SnapshotRetention <= TimeSpan.Zero)
        {
            yield return new SettingError(
                [SnapshotRetentionKey],
                "settings.invalid_value",
                $"Retention:Metadata:{nameof(SnapshotRetention)} must be a positive duration "
                + $"(configured: {candidate.SnapshotRetention}).");
        }

        if (candidate.SnapshotPurgeInterval <= TimeSpan.Zero)
        {
            // A zero interval is not "run often": the scheduler would set NextDue to now on every
            // tick and enqueue a fresh purge command every few seconds, for ever.
            yield return new SettingError(
                [SnapshotPurgeIntervalKey],
                "settings.invalid_value",
                $"Retention:Metadata:Interval must be a positive duration "
                + $"(configured: {candidate.SnapshotPurgeInterval}).");
        }
    }

    /// <summary>The most-preferred (primary) provider, or <c>null</c> if none configured.</summary>
    public string? PrimaryProvider => Providers.Count > 0 ? Providers[0] : null;

    /// <summary>Priority rank of a provider (lower is better); unknown providers sort last.</summary>
    public int PriorityOf(string provider)
    {
        for (var i = 0; i < Providers.Count; i++)
        {
            if (string.Equals(Providers[i], provider, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return Providers.Count;
    }
}
