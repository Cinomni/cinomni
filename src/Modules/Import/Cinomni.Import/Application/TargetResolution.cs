using Cinomni.Catalog.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Application;

/// <summary>
/// The catalog units one landed file serves, with the numbers a downstream consumer needs to name it.
/// An empty <see cref="UnitIds"/> means the file resolved to nothing and carries the explanation why —
/// there is no silent drop anywhere in this path.
/// </summary>
public sealed record ResolvedUnits(
    IReadOnlyList<Guid> UnitIds,
    int? SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers,
    string? EpisodeTitle,
    string? Reason)
{
    public bool IsResolved => UnitIds.Count > 0;

    public static ResolvedUnits None(string reason) => new([], null, [], null, reason);

    /// <summary>The movie case: the work itself is the one unit, and there are no numbers to carry.</summary>
    public static ResolvedUnits Work(Guid workId) => new([workId], null, [], null, null);
}

/// <summary>
/// Maps a landed <b>file</b> to the catalog units it serves, through <see cref="ICatalogSeriesQuery"/> —
/// the single resolver Monitoring and Decision also use, so no episode-resolution rule is duplicated.
/// <para>
/// Resolution is always <b>constrained to the scope</b> the command carried (the units the acquisition
/// asked for). A season pack routinely ships episodes the platform never requested — already-present
/// ones, or a bonus episode — and linking those would create assets nobody asked for and mark
/// episodes available that were never acquired. A file outside the scope is left unresolved, with the
/// reason recorded on its match and logged.
/// </para>
/// </summary>
public sealed class TargetResolution(
    ICatalogQuery catalogQuery,
    ICatalogSeriesQuery seriesQuery,
    EpisodeFileParser fileParser,
    ILogger<TargetResolution> logger)
{
    /// <summary>The work being imported into, or null when Catalog does not know it (a bare correlation id).</summary>
    public Task<WorkSummary?> GetWorkAsync(Guid workId, CancellationToken cancellationToken = default) =>
        workId == Guid.Empty
            ? Task.FromResult<WorkSummary?>(null)
            : catalogQuery.GetByIdAsync(new WorkId(workId), cancellationToken);

    /// <summary>
    /// Resolves one file of a <b>series</b> download to its episode unit(s), restricted to
    /// <paramref name="scope"/> when the acquisition supplied one.
    /// </summary>
    /// <param name="contentRoot">
    /// Where the download starts, so the parser's extras rejection never reads the staging mount
    /// above it (a mount named <c>/mnt/media-bonus</c> is not an extras bucket).
    /// </param>
    public async Task<ResolvedUnits> ResolveFileAsync(
        Guid workId,
        string filePath,
        IReadOnlyList<Guid> scope,
        string? contentRoot = null,
        CancellationToken cancellationToken = default)
    {
        var numbering = fileParser.Parse(filePath, contentRoot);
        if (numbering is null)
        {
            return Unresolved(filePath, "the file name carries no episode numbering");
        }

        var episodes = await ResolveEpisodesAsync(new WorkId(workId), numbering, cancellationToken);
        if (episodes.Count == 0)
        {
            return Unresolved(filePath, "the numbering maps to no episode of this work");
        }

        // A date-numbered file names ONE broadcast. When the provider publishes the same air date on
        // two episodes — a two-part finale released under a single date — the date identifies
        // neither, and one file with N unit links is meant for a genuine multi-episode file, which this
        // is not. Linking both would mark an episode available that no file exists for, and it would
        // never be searched again.
        if (IsDateNumbered(numbering) && episodes.Count > 1)
        {
            return Unresolved(filePath, "its air date is shared by several episodes, so it resolves to none");
        }

        var inScope = scope.Count == 0
            ? episodes
            : [.. episodes.Where(e => scope.Contains(e.Id.Value))];
        if (inScope.Count == 0)
        {
            return Unresolved(filePath, "the episode it covers is outside the requested units");
        }

        var ordered = inScope.OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number).ToList();
        return new ResolvedUnits(
            [.. ordered.Select(e => e.Id.Value)],
            ordered[0].SeasonNumber,
            [.. ordered.Select(e => e.Number)],
            ordered[0].Title,
            Reason: null);
    }

    private async Task<IReadOnlyList<EpisodeSummary>> ResolveEpisodesAsync(
        WorkId workId,
        EpisodeFileNumbering numbering,
        CancellationToken cancellationToken)
    {
        if (numbering.SeasonNumber is { } season && numbering.EpisodeNumbers.Count > 0)
        {
            var ordered = numbering.EpisodeNumbers.Order().ToList();
            return ordered.Count == 1
                ? await seriesQuery.ResolveEpisodeAsync(workId, season, ordered[0], cancellationToken)
                : await seriesQuery.ResolveEpisodeRangeAsync(workId, season, ordered[0], ordered[^1], cancellationToken);
        }

        if (numbering.AbsoluteNumbers.Count > 0)
        {
            var resolved = new List<EpisodeSummary>();
            foreach (var absolute in numbering.AbsoluteNumbers)
            {
                resolved.AddRange(await seriesQuery.ResolveByAbsoluteNumberAsync(workId, absolute, cancellationToken));
            }

            return resolved;
        }

        if (numbering.AirDate is { } airDate)
        {
            return await seriesQuery.ResolveByAirDateAsync(workId, airDate, cancellationToken);
        }

        // A season number with no episode number (a folder-only hint) names no single file.
        return [];
    }

    /// <summary>
    /// True when the air date is the <b>only</b> numbering the file carries — exactly the branch
    /// <see cref="ResolveEpisodesAsync"/> answers with <c>ResolveByAirDateAsync</c>, whose index is
    /// not unique.
    /// </summary>
    private static bool IsDateNumbered(EpisodeFileNumbering numbering) =>
        numbering.AirDate is not null
        && numbering.EpisodeNumbers.Count == 0
        && numbering.AbsoluteNumbers.Count == 0;

    private ResolvedUnits Unresolved(string filePath, string reason)
    {
        // Every drop path is logged: a file that quietly lands nowhere is the hardest import bug to
        // diagnose, because the job still reports success for its siblings.
        logger.LogInformation("Import left {File} unresolved: {Reason}.", filePath, reason);
        return ResolvedUnits.None(reason);
    }
}
