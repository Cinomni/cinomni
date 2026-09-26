using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// Maps the production-status vocabulary of every provider we speak to onto the neutral
/// <see cref="SeriesStatus"/> — TVMaze says <c>Running</c>, TheTVDB says <c>Continuing</c> and TMDB says
/// <c>Returning Series</c> for the same thing. One table rather than three, because the status decides
/// the refresh cadence and a provider-specific spelling that falls through would quietly make a weekly
/// show refresh once a week instead of twice a day.
/// </summary>
internal static class ProviderSeriesStatus
{
    private static readonly IReadOnlyDictionary<string, SeriesStatus> Map = new Dictionary<string, SeriesStatus>(StringComparer.OrdinalIgnoreCase)
    {
        // Still producing episodes.
        ["running"] = SeriesStatus.Continuing,
        ["continuing"] = SeriesStatus.Continuing,
        ["returning series"] = SeriesStatus.Continuing,
        ["in production"] = SeriesStatus.Continuing,

        // Concluded — the episode list is final.
        ["ended"] = SeriesStatus.Ended,

        // Cancelled before concluding; treated as final, but recorded distinctly.
        ["cancelled"] = SeriesStatus.Cancelled,
        ["canceled"] = SeriesStatus.Cancelled,

        // Announced but not yet aired.
        ["upcoming"] = SeriesStatus.Upcoming,
        ["planned"] = SeriesStatus.Upcoming,
        ["pilot"] = SeriesStatus.Upcoming,
        ["in development"] = SeriesStatus.Upcoming,
        ["to be determined"] = SeriesStatus.Upcoming,
    };

    /// <summary>The neutral status for a provider's spelling; <see cref="SeriesStatus.Unknown"/> otherwise.</summary>
    public static SeriesStatus Parse(string? value) =>
        value is not null && Map.TryGetValue(value.Trim(), out var status) ? status : SeriesStatus.Unknown;
}
