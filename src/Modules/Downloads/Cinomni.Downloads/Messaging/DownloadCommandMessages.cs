using Cinomni.Kernel.Messaging;

namespace Cinomni.Downloads.Messaging;

/// <summary>Stable registered names of the Downloads commands.</summary>
public static class DownloadCommandNames
{
    public const string AddDownload = "downloads.add-download";
    public const string SaveCheckpoints = "downloads.save-checkpoints";
    public const string TrimCheckpoints = "downloads.trim-checkpoints";
    public const string CheckTunnel = "downloads.check-tunnel";
    public const string RemoveGoalDownloads = "downloads.remove-goal-downloads";
}

/// <summary>
/// Hands a selected release off to the engine (⇠ Acquisition's <c>DownloadQueued</c>). Recoverable
/// and idempotent per attempt: re-running reuses the existing download task.
/// </summary>
/// <param name="UnitIds">
/// The catalog units this download serves, echoed from <c>DownloadQueued</c> and persisted on the
/// task. Trailing optional: the command queue persists this payload as jsonb, so an in-flight 1.x
/// row deserializes with it null and the task falls back to its work id — the movie unit.
/// </param>
public sealed record AddDownloadCommand(
    Guid AttemptId,
    Guid IntentId,
    Guid WorkId,
    Guid TargetId,
    string ReleaseGuid,
    string DownloadUrl,
    IReadOnlyList<Guid>? UnitIds = null) : ICommand;

/// <summary>
/// Periodic checkpoint: saves fresh resume data for every active task so a restart can resume
/// without a full recheck. Parameterless — driven by the platform scheduler.
/// </summary>
public sealed record SaveCheckpointsCommand : ICommand;

/// <summary>
/// The counterpart of <see cref="SaveCheckpointsCommand"/>: releases the resume blob of a download
/// that finished long enough ago that it will never be resumed. The task row itself — the audit trail
/// — is untouched. Parameterless: driven by the platform scheduler.
/// </summary>
public sealed record TrimCheckpointsCommand : ICommand;

/// <summary>
/// Asks the sidecar where its traffic is going and, when that has changed for long enough, holds or
/// releases the downloads. Parameterless — driven by the platform scheduler, and only registered when
/// a tunnel is configured. Idempotent: a re-run that observes the same thing writes a streak counter
/// and nothing else, and a re-run of the transition itself is refused by the state row.
/// </summary>
public sealed record CheckTunnelCommand : ICommand;

/// <summary>
/// Drops every torrent a cancelled acquisition goal claimed — its work was removed from the catalog.
/// Idempotent: a task already removed is skipped, so a redelivery changes nothing.
/// </summary>
/// <param name="DeleteFiles">Whether the downloaded files are deleted along with the torrent.</param>
public sealed record RemoveGoalDownloadsCommand(Guid IntentId, bool DeleteFiles) : ICommand;
