namespace Cinomni.Playback.Application;

/// <summary>
/// Configuration for the Playback module: where just-in-time HLS output is written, how many
/// transcodes the node runs at once, and when one counts as abandoned. Each session transcodes into
/// its own subdirectory under <see cref="TranscodeRoot"/>, and the segment endpoints serve only from
/// there (path-confined).
/// </summary>
public sealed class PlaybackOptions
{
    /// <summary>Root directory for per-session HLS output. Fixed by the admin.</summary>
    public string TranscodeRoot { get; set; } = "/data/transcodes";

    /// <summary>
    /// How long a playback session is kept once it is over. A session is history — one row per play,
    /// holding the on-disk path it served and the explainable plan that chose the method — whereas
    /// <c>playback_progress</c> is state keyed by (user, asset) and is never touched by retention at
    /// any age.
    /// <para>
    /// The same window ages out a session that never reached a terminal state, once it has been silent
    /// for at least a day: a client that closed the tab never tells the server, and those rows own a
    /// transcode directory that is reclaimed with them.
    /// </para>
    /// </summary>
    public TimeSpan SessionRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often <c>playback.retention</c> runs.</summary>
    public TimeSpan SessionPurgeInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How many sessions may transcode or remux on this node at once. Each one is an FFmpeg process
    /// and a converted copy of the file on disk, so this is the ceiling on both; a request past it is
    /// refused with a reason rather than started.
    /// </summary>
    public int MaxConcurrentTranscodes { get; set; } = 4;

    /// <summary>
    /// How many of those one account may hold at once, so a single member cannot take every slot the
    /// household shares.
    /// </summary>
    public int MaxTranscodesPerAccount { get; set; } = 2;

    /// <summary>
    /// How long a transcode may go without a progress report or a segment request before it is
    /// treated as abandoned: its process is stopped, its output reclaimed and its session ended. An
    /// open player reports every few seconds, and even a paused one in a throttled background tab
    /// reports about once a minute, which is why this cannot be shorter than
    /// <see cref="MinimumTranscodeIdleTimeout"/>.
    /// </summary>
    public TimeSpan TranscodeIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The floor under <see cref="TranscodeIdleTimeout"/>.</summary>
    public static readonly TimeSpan MinimumTranscodeIdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The longest one stream may hold its slot, however busy its player keeps it. Longer than any film
    /// and a long pause; what it bounds is one stream held for ever — a player left running on a screen
    /// nobody watches, or a client that keeps reporting. A viewer who does reach it retries and resumes
    /// where they were. It does not stop a member who opens stream after stream on purpose: that is what
    /// <see cref="MaxTranscodesPerAccount"/> bounds, and past it, an account matter for an administrator.
    /// </summary>
    public TimeSpan MaxTranscodeLifetime { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The floor under <see cref="MaxTranscodeLifetime"/>.</summary>
    public static readonly TimeSpan MinimumTranscodeLifetime = TimeSpan.FromHours(1);

    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        if (SessionRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Playback:{nameof(SessionRetention)} must be a positive duration "
                + $"(configured: {SessionRetention}).");
        }

        if (SessionPurgeInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Playback:{nameof(SessionPurgeInterval)} must be a positive duration "
                + $"(configured: {SessionPurgeInterval}).");
        }

        if (MaxConcurrentTranscodes < 1)
        {
            throw new InvalidOperationException(
                $"Playback:{nameof(MaxConcurrentTranscodes)} must be at least 1 (configured: {MaxConcurrentTranscodes}).");
        }

        if (MaxTranscodesPerAccount < 1)
        {
            throw new InvalidOperationException(
                $"Playback:{nameof(MaxTranscodesPerAccount)} must be at least 1 (configured: {MaxTranscodesPerAccount}).");
        }

        if (MaxTranscodeLifetime < MinimumTranscodeLifetime)
        {
            throw new InvalidOperationException(
                $"Playback:{nameof(MaxTranscodeLifetime)} must be at least {MinimumTranscodeLifetime} "
                + $"(configured: {MaxTranscodeLifetime}).");
        }

        if (TranscodeIdleTimeout < MinimumTranscodeIdleTimeout)
        {
            throw new InvalidOperationException(
                $"Playback:{nameof(TranscodeIdleTimeout)} must be at least {MinimumTranscodeIdleTimeout} — a paused "
                + $"player in a background tab reports only about once a minute (configured: {TranscodeIdleTimeout}).");
        }
    }
}
