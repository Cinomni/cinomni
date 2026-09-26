using Cinomni.Decision.Application;
using Cinomni.Discovery.Application;
using Cinomni.Downloads.Application;
using Cinomni.Identity.Application;
using Cinomni.Metadata.Application;
using Cinomni.Notifications.Application;
using Cinomni.Operations.Retention;
using Cinomni.Playback.Application;
using Cinomni.ReleaseParsing.Application;

namespace Cinomni.Host;

/// <summary>
/// Binds the <c>Retention</c> configuration section onto each owner's retention options.
/// <para>
/// It lives in its own file, and every retention window in the installation is visible here in one
/// list — including the ones deliberately absent, which is the point: Acquisition, Import, Library,
/// Catalog, Monitoring, Requests and Subtitles own audit trails and current state, and none of them
/// has a window at all.
/// </para>
/// <para>
/// Every value is read explicitly rather than through a blanket <c>Bind</c>, following
/// <see cref="ModuleConfiguration"/>: a typo then leaves the default in place instead of silently
/// reshaping a retention policy. The callbacks are applied at registration time, because a window's
/// cadence becomes a scheduled-job interval and the owner validates its own values immediately —
/// an inconsistent section fails startup with a message naming the key.
/// </para>
/// </summary>
internal sealed class RetentionConfiguration(IConfiguration configuration)
{
    private const string SectionName = "Retention";

    private readonly IConfiguration _section = configuration.GetSection(SectionName);

    /// <summary>The platform kernel's own outbox and command-queue windows, plus the shared batch size.</summary>
    public void Operations(RetentionOptions options)
    {
        options.OutboxRetention = Duration(_section, "OutboxRetention", options.OutboxRetention);
        options.CompletedCommandRetention = Duration(
            _section, "CompletedCommandRetention", options.CompletedCommandRetention);
        options.FailedCommandRetention = Duration(
            _section, "FailedCommandRetention", options.FailedCommandRetention);
        options.BatchSize = Number(_section, "BatchSize", options.BatchSize);
        options.Interval = Duration(_section, "Interval", options.Interval);
    }

    public void Discovery(SearchRetentionOptions options)
    {
        var section = _section.GetSection("Discovery");
        options.SearchRetention = Duration(section, "SearchRetention", options.SearchRetention);
        options.Interval = Duration(section, "Interval", options.Interval);
    }

    public void Decision(DecisionRetentionOptions options)
    {
        var section = _section.GetSection("Decision");
        options.EvaluationRetention = Duration(section, "EvaluationRetention", options.EvaluationRetention);
        options.Interval = Duration(section, "Interval", options.Interval);
    }

    /// <summary>
    /// Metadata keeps its retention on the existing <see cref="MetadataOptions"/> profile, which is
    /// already bound in <see cref="ModuleConfiguration"/>; this is called from there.
    /// </summary>
    public void Metadata(MetadataOptions options)
    {
        var section = _section.GetSection("Metadata");
        options.SnapshotRetention = Duration(section, "SnapshotRetention", options.SnapshotRetention);
        options.SnapshotPurgeInterval = Duration(section, "Interval", options.SnapshotPurgeInterval);
    }

    public void ReleaseParsing(ReleaseParsingRetentionOptions options)
    {
        var section = _section.GetSection("ReleaseParsing");
        options.ParseRetention = Duration(section, "ParseRetention", options.ParseRetention);
        options.Interval = Duration(section, "Interval", options.Interval);
    }

    public void Notifications(NotificationRetentionOptions options)
    {
        var section = _section.GetSection("Notifications");
        options.NotificationRetention = Duration(
            section, "NotificationRetention", options.NotificationRetention);
        options.Interval = Duration(section, "Interval", options.Interval);
    }

    public void Identity(SessionRetentionOptions options)
    {
        var section = _section.GetSection("Identity");
        options.SessionGrace = Duration(section, "SessionGrace", options.SessionGrace);
        options.Interval = Duration(section, "Interval", options.Interval);
    }

    /// <summary>Playback keeps its retention on <see cref="PlaybackOptions"/>, beside the transcode root.</summary>
    public void Playback(PlaybackOptions options)
    {
        var section = _section.GetSection("Playback");
        options.SessionRetention = Duration(section, "SessionRetention", options.SessionRetention);
        options.SessionPurgeInterval = Duration(section, "Interval", options.SessionPurgeInterval);
    }

    public void Downloads(DownloadRetentionOptions options)
    {
        var section = _section.GetSection("Downloads");
        options.CheckpointRetention = Duration(section, "CheckpointRetention", options.CheckpointRetention);
        options.Interval = Duration(section, "Interval", options.Interval);
    }

    private static TimeSpan Duration(IConfiguration section, string key, TimeSpan fallback) =>
        section.GetValue<TimeSpan?>(key) ?? fallback;

    private static int Number(IConfiguration section, string key, int fallback) =>
        section.GetValue<int?>(key) ?? fallback;
}
