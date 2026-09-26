using Cinomni.Catalog.Contracts;
using Cinomni.Import.Files;

namespace Cinomni.Import.Application;

/// <summary>
/// The set of files the import specs accepted out of a download, or the explanation why none
/// qualified. A movie plans exactly one file; a season pack plans every episode it carries.
/// </summary>
public sealed record ImportPlan(bool Accepted, IReadOnlyList<ImportFileEntry> Files, string Reason)
{
    public static ImportPlan Refused(string reason) => new(false, [], reason);
}

/// <summary>
/// The import specs (a pure function of the file list and the work kind). Common to both kinds: the
/// closed video-extension allowlist and the sample filter — executables, archives and scripts never
/// match the allowlist at all.
/// <para>
/// The kinds then diverge, and they must. A <b>movie</b> collapses to the single largest acceptable
/// file over <see cref="ImportOptions.MinVideoBytes"/>, exactly as it always has — including the set
/// of filters, because a movie download is one release whose name routinely carries a word the
/// extras rejection treats as a marker ("The Interview", "Proof"). A <b>series</b> keeps
/// <em>every</em> acceptable file — collapsing a season pack to its largest file would import one
/// episode out of ten — is measured against <see cref="ImportOptions.MinEpisodeBytes"/> because the
/// 50 MiB movie floor silently discards short episodes, and additionally drops the extras/
/// featurettes folders a pack ships alongside the episodes.
/// </para>
/// </summary>
public sealed class ImportPolicy(ImportOptions options)
{
    private const string NoAcceptableFile = "no acceptable video file (extension allowlist / sample filter)";

    /// <summary>
    /// Plans the files to land, or explains why nothing qualified.
    /// </summary>
    /// <param name="contentPath">
    /// Where the download starts, so the extras rejection can tell a folder <em>inside</em> the pack
    /// from the staging mount above it. Optional so a caller that only has a file list keeps the
    /// conservative whole-path behaviour.
    /// </param>
    public ImportPlan Plan(IReadOnlyList<ImportFileEntry> files, WorkKind kind, string? contentPath = null)
    {
        if (files.Count == 0)
        {
            return ImportPlan.Refused("no files in the download content path");
        }

        var candidates = files
            .Where(f => options.VideoExtensions.Contains(Path.GetExtension(f.Path)))
            .Where(f => !ImportFileFilter.IsSample(f.Path))
            .OrderByDescending(f => f.Size)
            .ToList();

        if (candidates.Count == 0)
        {
            return ImportPlan.Refused(NoAcceptableFile);
        }

        var floor = options.MinBytesFor(kind);
        if (kind != WorkKind.Series)
        {
            return PlanMovie(candidates[0], floor);
        }

        var episodes = candidates.Where(f => !ImportFileFilter.IsExtras(f.Path, contentPath)).ToList();
        return episodes.Count == 0 ? ImportPlan.Refused(NoAcceptableFile) : PlanSeries(episodes, floor);
    }

    /// <summary>Single-file convenience for the movie path (the batch of one).</summary>
    public ImportPlan Decide(IReadOnlyList<ImportFileEntry> files) => Plan(files, WorkKind.Movie);

    private static ImportPlan PlanMovie(ImportFileEntry main, long floor) =>
        main.Size < floor
            ? ImportPlan.Refused($"main file below minimum size ({main.Size} bytes)")
            : new ImportPlan(true, [main], "accepted");

    private static ImportPlan PlanSeries(IReadOnlyList<ImportFileEntry> candidates, long floor)
    {
        var accepted = candidates
            .Where(f => f.Size >= floor)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        return accepted.Count == 0
            ? ImportPlan.Refused($"every episode file is below the minimum size ({floor} bytes)")
            : new ImportPlan(true, accepted, "accepted");
    }
}
