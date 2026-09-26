namespace Cinomni.Subtitles.Application;

/// <summary>
/// The subtitle profile as configuration for the movie slice: the wanted languages and the score
/// threshold a candidate must clear (completeness is separate from the score). Persisted
/// profiles/requirements are a later addition.
/// </summary>
public sealed class SubtitleOptions
{
    /// <summary>
    /// Minimum spacing between two outbound provider searches, applied process-wide by
    /// <see cref="SubtitleProviderThrottle"/>. A 24-episode season pack registers 24 assets at once and
    /// each one fires its own <c>search-subtitles:{assetId}</c>, so without a floor the platform opens a
    /// 24-fold burst against a rate-limited API — a realistic way to get an account banned. Two seconds
    /// keeps a full season inside a minute while staying far under any published provider limit.
    /// </summary>
    public static readonly TimeSpan DefaultProviderCallInterval = TimeSpan.FromSeconds(2);

    /// <summary>ISO language codes wanted for every asset (e.g. <c>en</c>, <c>es</c>).</summary>
    public IReadOnlyList<string> WantedLanguages { get; set; } = ["en"];

    /// <summary>
    /// The stagger between two provider calls (see <see cref="DefaultProviderCallInterval"/>). Injected
    /// rather than hard-coded so a test can prove the stagger in milliseconds instead of sleeping for
    /// seconds. <see cref="TimeSpan.Zero"/> disables the throttle.
    /// </summary>
    public TimeSpan ProviderCallInterval { get; set; } = DefaultProviderCallInterval;

    /// <summary>Whether to prefer a hearing-impaired subtitle.</summary>
    public bool HearingImpaired { get; set; }

    /// <summary>Whether to look for a forced subtitle.</summary>
    public bool Forced { get; set; }

    /// <summary>The minimum candidate score to accept (below it, the search waits — adaptive).</summary>
    public int MinScore { get; set; } = 5;
}
