using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;

namespace Cinomni.Requests.Contracts;

/// <summary>Stable internal identity of a media request (UUIDv7).</summary>
public readonly record struct MediaRequestId(Guid Value)
{
    public static MediaRequestId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// What kind of title a request is for. It decides how the title is catalogued and where its metadata
/// comes from — and it is part of what identifies the title, because a provider can number films and
/// shows independently (TMDB does), so one external id can name two unrelated titles.
/// </summary>
public enum MediaRequestKind
{
    Movie = 1,

    Series = 2,
}

/// <summary>
/// Lifecycle of a media request. A request starts <see cref="Pending"/>; an administrator either
/// approves it (the title enters the catalog and the acquisition spine takes over) or rejects it.
/// <see cref="Available"/> is reached on its own, when the work the request produced gains an asset.
/// </summary>
public enum MediaRequestStatus
{
    /// <summary>Submitted, waiting for an administrator's decision.</summary>
    Pending = 1,

    /// <summary>Approved: the title is (or is being) added to the catalog and monitored.</summary>
    Approved = 2,

    /// <summary>Rejected by an administrator; <c>DecisionNote</c> carries the reason.</summary>
    Rejected = 3,

    /// <summary>The requested title is downloaded, imported and playable.</summary>
    Available = 4,
}

/// <summary>
/// A user's request for a title, as exposed outside the module. The provider/external-id pair is what
/// the request points at — the internal <c>WorkId</c> only exists once the request is approved and the
/// title enters the catalog (the external id is never the identity).
/// </summary>
public sealed record MediaRequest(
    MediaRequestId Id,
    string Title,
    int? Year,
    string Provider,
    string ExternalId,
    MediaRequestStatus Status,
    Guid RequestedByUserId,
    string RequestedByUsername,
    Guid? WorkId,
    string? DecisionNote,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    MediaRequestKind Kind = MediaRequestKind.Movie);

/// <summary>
/// What a user submits: the metadata candidate they picked (provider + external id, plus the title and
/// year worth showing before it is catalogued) and who they are. Artwork is deliberately absent: it comes
/// from the metadata snapshot once the title is in the catalog, never from the client.
/// </summary>
/// <param name="AutoApprove">
/// The requester is trusted with approving their own (an administrator, or a member Identity granted it):
/// the request lands already approved and the title goes straight to the catalog. Comes from the session's
/// permissions, never from the request body.
/// </param>
/// <param name="Requester">
/// Who is asking, with what they may see. Only consulted to decide whether "already in your library" is
/// true <em>for them</em>: a title that exists but is hidden from the requester must answer like one
/// that does not. Absent, every catalogued title counts as hidden: the request is never refused as
/// already there, and never approved by itself — it goes to the administrator. Callers acting for a
/// person should always pass one.
/// </param>
public sealed record SubmitMediaRequest(
    string Title,
    int? Year,
    string Provider,
    string ExternalId,
    Guid RequestedByUserId,
    string RequestedByUsername,
    bool AutoApprove = false,
    int? OpenRequestLimit = null,
    MediaRequestKind Kind = MediaRequestKind.Movie,
    Viewer? Requester = null);
