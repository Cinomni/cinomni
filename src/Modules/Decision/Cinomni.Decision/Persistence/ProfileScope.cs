namespace Cinomni.Decision.Persistence;

/// <summary>
/// The content kind a profile applies to. A movie profile and a series profile have genuinely
/// different size expectations, so one shared profile is a silent-failure mode rather than a
/// simplification: with a movie's minimum size in force, every 2 GB episode is rejected on
/// <c>SizeWithinLimits</c> with a perfectly explainable but completely wrong reason.
/// </summary>
/// <remarks>
/// Persisted as text in <c>acquisition_profiles.applies_to</c>, so these strings may never change.
/// </remarks>
internal static class ProfileScope
{
    public const string Movie = "Movie";
    public const string Series = "Series";

    private static readonly string[] SeriesContentKinds = ["Series", "Season", "Episode"];

    /// <summary>Maps a search's content kind onto the profile scope that should judge it.</summary>
    public static string ForContentKind(string? contentKind) =>
        contentKind is not null
        && SeriesContentKinds.Any(k => string.Equals(k, contentKind, StringComparison.OrdinalIgnoreCase))
            ? Series
            : Movie;
}
