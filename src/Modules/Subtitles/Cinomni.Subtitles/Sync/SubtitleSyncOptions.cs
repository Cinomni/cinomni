namespace Cinomni.Subtitles.Sync;

/// <summary>
/// How subtitle synchronization runs and how sure it must be before it changes a file. Read once at
/// registration (<c>Subtitles:Sync</c>); a test host registers its own.
/// <para>
/// The thresholds are set to keep the original rather than risk a wrong shift: a subtitle a few
/// hundred milliseconds late is an annoyance, one moved to the wrong scene is unusable, and a viewer
/// cannot tell which of the two Cinomni did until it is too late.
/// </para>
/// </summary>
public sealed class SubtitleSyncOptions
{
    public const int MaxConcurrency = 4;

    /// <summary>Whether a landed subtitle's timing is checked at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many subtitles are analyzed at once. Each runs FFmpeg over part of a film.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>The FFmpeg binary; the Host passes the one Playback is configured with.</summary>
    public string FfmpegBinaryPath { get; set; } = "ffmpeg";

    /// <summary>The longest stretch of audio decoded at each of the start, middle and end of the video.</summary>
    public TimeSpan WindowLength { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>How long decoding one stretch may take before FFmpeg is stopped and the pass fails.</summary>
    public TimeSpan WindowTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A search still synchronizing after this long is closed by the sweep, keeping the original.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many times a pass may be started on one subtitle before it is given up on.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>The least phi the best match must reach (T1).</summary>
    public double MinScore { get; set; } = 0.25;

    /// <summary>How many times better than any match more than 2 s away the best must be (T2).</summary>
    public double MinMargin { get; set; } = 1.3;

    /// <summary>How far apart the stretches' own best offsets may be and still agree.</summary>
    public TimeSpan MaxWindowDisagreement { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Puts every value back inside the range the analysis was designed for.</summary>
    public void Normalize()
    {
        Concurrency = Math.Clamp(Concurrency, 1, MaxConcurrency);
        MaxAttempts = Math.Clamp(MaxAttempts, 1, 10);
        MinScore = double.IsFinite(MinScore) ? Math.Clamp(MinScore, 0, 1) : 0.25;
        MinMargin = double.IsFinite(MinMargin) ? Math.Max(1, MinMargin) : 1.3;
        WindowLength = Clamp(WindowLength, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        WindowTimeout = Clamp(WindowTimeout, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));
        StuckAfter = Clamp(StuckAfter, TimeSpan.FromMinutes(10), TimeSpan.FromDays(1));
        MaxWindowDisagreement = Clamp(MaxWindowDisagreement, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
