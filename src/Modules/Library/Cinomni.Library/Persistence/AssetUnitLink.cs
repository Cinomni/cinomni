namespace Cinomni.Library.Persistence;

/// <summary>
/// The N:M link between a media asset and a <b>catalog unit</b> — the work id for a movie, an episode
/// id for a series file. This is the semantically correct relation the series UI and Playback ask
/// ("which file plays episode X"), and it is deliberately <em>additive</em>: <see cref="AssetTargetLink"/>
/// keeps holding the acquiring monitored target and is left exactly as it shipped. For a movie both
/// tables hold the same value; the redundancy is accepted rather than reinterpreting a shipped table
/// and its shipped API field.
/// <para>
/// <b>A multi-episode file is one asset with several of these links.</b>
/// <c>ux_media_versions_full_path</c> is unique on the version's full path, so two assets pointing at
/// one physical file is schema-impossible. The consequence is explicit and deliberate: playback of
/// such a file always starts at the beginning, because nothing here records where episode 2 begins.
/// Per-episode offsets are deferred — <c>PlaybackTicket.ResumePositionTicks</c> cannot express
/// "play episode 2 of this file", and adding start/end offsets is a Playback-side change.
/// </para>
/// <c>unit_id</c> is an inter-schema reference to catalog (no physical FK).
/// </summary>
public sealed class AssetUnitLink
{
    public Guid AssetId { get; init; }

    public Guid UnitId { get; init; }
}
