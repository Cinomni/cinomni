namespace Cinomni.Subtitles.Persistence;

/// <summary>
/// A subtitle waiting to be synchronized. The row is written in the same unit of work that puts its
/// search in <c>Syncing</c> and removed in the one that completes it, so pending work survives a
/// restart and can never outlive the state it belongs to. One row per subtitle: the subtitle's id is
/// the key, which is what makes a repeated request a no-op rather than a second pass.
/// <para>
/// The sync worker claims a row before it decodes anything (<see cref="ClaimedAt"/>) and counts the
/// claim (<see cref="Attempts"/>), so two workers never analyze the same subtitle and a file that keeps
/// taking the process down is given up on rather than retried for ever.
/// </para>
/// </summary>
public sealed class SubtitleSyncWork
{
    public Guid SubtitleAssetId { get; init; }

    public Guid SearchId { get; init; }

    public DateTimeOffset RequestedAt { get; init; }

    public int Attempts { get; private set; }

    public DateTimeOffset? ClaimedAt { get; private set; }

    public static SubtitleSyncWork Create(Guid subtitleAssetId, Guid searchId, DateTimeOffset now) => new()
    {
        SubtitleAssetId = subtitleAssetId,
        SearchId = searchId,
        RequestedAt = now,
    };
}
