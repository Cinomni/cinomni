namespace Cinomni.Decision.Persistence;

/// <summary>
/// The Decision aggregate root: a profile that decides what quality is acceptable, how custom
/// formats score, and when a title is good enough to stop looking for a better one.
/// </summary>
public sealed class AcquisitionProfile
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    /// <summary>
    /// The content kind this profile judges (<see cref="ProfileScope.Movie"/> or
    /// <see cref="ProfileScope.Series"/>). Without this scope the engine would keep picking the
    /// oldest profile for everything and reject every episode on size — explainably, and wrongly.
    /// </summary>
    public string AppliesTo { get; set; } = ProfileScope.Movie;

    /// <summary>A release with a custom-format score below this is rejected.</summary>
    public int MinFormatScore { get; set; }

    /// <summary>
    /// The quality rank at which a title is good enough: once what is on disk reaches it, nothing is
    /// searched for it again and no candidate is accepted for it. Compared against
    /// <see cref="AllowedQuality.Rank"/>, so it speaks the same scale the allowed set already uses.
    /// </summary>
    public int CutoffRank { get; set; }

    /// <summary>
    /// Whether a title that is already present may be replaced by a better release at all. False turns
    /// the whole upgrade path off for this profile, whatever <see cref="CutoffRank"/> says — an explicit
    /// switch rather than the trick of setting the cutoff below the lowest allowed rank.
    /// </summary>
    public bool UpgradesAllowed { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; }

    public List<AllowedQuality> AllowedQualities { get; } = [];

    public List<FormatRule> FormatRules { get; } = [];
}
