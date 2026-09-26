using Cinomni.Catalog.Contracts;
using Cinomni.Import.Application;
using Cinomni.Import.Files;

namespace Cinomni.Import.Tests;

/// <summary>
/// Pure unit tests for the import specs. The movie path must stay exactly what ships today — the
/// single largest acceptable file — while a series keeps every episode file, because collapsing a
/// season pack with <c>OrderByDescending(Size).First()</c> would import one episode out of ten.
/// </summary>
public sealed class ImportPlanTests
{
    private const long Mib = 1024 * 1024;

    private readonly ImportPolicy _policy = new(new ImportOptions
    {
        MinVideoBytes = 50 * Mib,
        MinEpisodeBytes = 8 * Mib,
    });

    [Fact]
    public void A_season_pack_plans_every_episode_file()
    {
        var plan = _policy.Plan(
            [
                File("/pack/Show.S01E01.mkv", 900 * Mib),
                File("/pack/Show.S01E02.mkv", 1100 * Mib),
                File("/pack/Show.S01E03.mkv", 950 * Mib),
                File("/pack/Show.S01.nfo", 4 * Mib),
            ],
            WorkKind.Series);

        Assert.True(plan.Accepted);
        Assert.Equal(3, plan.Files.Count);
        Assert.Equal(
            ["/pack/Show.S01E01.mkv", "/pack/Show.S01E02.mkv", "/pack/Show.S01E03.mkv"],
            plan.Files.Select(f => f.Path));
    }

    [Fact]
    public void A_movie_still_plans_exactly_one_file()
    {
        var plan = _policy.Plan(
            [
                File("/dl/Movie.2024.1080p.mkv", 8000 * Mib),
                File("/dl/Movie.2024.720p.mkv", 3000 * Mib),
            ],
            WorkKind.Movie);

        Assert.True(plan.Accepted);
        Assert.Equal("/dl/Movie.2024.1080p.mkv", Assert.Single(plan.Files).Path);
    }

    [Fact]
    public void Extras_and_featurettes_are_excluded()
    {
        var plan = _policy.Plan(
            [
                File("/pack/Show.S01E01.mkv", 900 * Mib),
                File("/pack/Extras/Show.Behind.The.Scenes.mkv", 700 * Mib),
                File("/pack/Featurettes/Making.Of.mkv", 600 * Mib),
                File("/pack/Show.S01E01.sample.mkv", 60 * Mib),
            ],
            WorkKind.Series);

        Assert.Equal("/pack/Show.S01E01.mkv", Assert.Single(plan.Files).Path);
    }

    [Fact]
    public void A_movie_whose_release_name_carries_an_extras_token_is_still_imported()
    {
        // "The.Interview.2014…" tokenises to [the, interview, 2014, …] and "interview" is an extras
        // marker, so the whole download was refused — deterministically, for every retry, until the
        // acquisition goal exhausted. The extras rejection is a season-pack rule, not a movie rule.
        const string folder = "/data/downloads/The.Interview.2014.1080p.BluRay.x264-SPARKS";

        var plan = _policy.Plan(
            [File($"{folder}/The.Interview.2014.1080p.BluRay.x264-SPARKS.mkv", 8000 * Mib)],
            WorkKind.Movie,
            folder);

        Assert.True(plan.Accepted);
        Assert.Equal($"{folder}/The.Interview.2014.1080p.BluRay.x264-SPARKS.mkv", Assert.Single(plan.Files).Path);
    }

    [Fact]
    public void A_staging_ancestor_is_not_an_extras_bucket()
    {
        // The markers describe folders INSIDE the download. An admin whose staging mount happens to
        // be /mnt/media-bonus would otherwise see every import in the installation refused.
        const string content = "/mnt/media-bonus/incomplete/Show.S01.1080p.WEB-DL";

        var plan = _policy.Plan(
            [
                File($"{content}/Show.S01E01.mkv", 900 * Mib),
                File($"{content}/Extras/Show.Deleted.Scenes.mkv", 700 * Mib),
            ],
            WorkKind.Series,
            content);

        Assert.True(plan.Accepted);
        Assert.Equal($"{content}/Show.S01E01.mkv", Assert.Single(plan.Files).Path);
    }

    [Fact]
    public void The_release_folder_of_a_season_pack_is_not_an_extras_bucket()
    {
        // The download's own folder is the release name; only what sits below it is a bucket.
        const string content = "/data/staging/Trailer.Park.Boys.S01.1080p.WEB-DL";

        var plan = _policy.Plan([File($"{content}/Trailer.Park.Boys.S01E01.mkv", 900 * Mib)], WorkKind.Series, content);

        Assert.True(plan.Accepted);
        Assert.Single(plan.Files);
    }

    [Fact]
    public void A_short_episode_above_the_series_floor_is_accepted()
    {
        // A 22-minute SD episode sits well under the 50 MiB movie floor, which would silently
        // discard every file of a perfectly legitimate season pack.
        var files = new[] { File("/pack/Show.S01E01.mkv", 20 * Mib) };

        Assert.True(_policy.Plan(files, WorkKind.Series).Accepted);
        Assert.False(_policy.Plan(files, WorkKind.Movie).Accepted);
    }

    [Fact]
    public void An_empty_content_path_is_refused_with_a_reason()
    {
        var plan = _policy.Plan([], WorkKind.Series);

        Assert.False(plan.Accepted);
        Assert.Empty(plan.Files);
        Assert.Equal("no files in the download content path", plan.Reason);
    }

    [Fact]
    public void Non_video_content_never_matches_the_allowlist()
    {
        var plan = _policy.Plan(
            [
                File("/dl/readme.txt", 900 * Mib),
                File("/dl/setup.exe", 900 * Mib),
                File("/dl/archive.rar", 900 * Mib),
            ],
            WorkKind.Series);

        Assert.False(plan.Accepted);
        Assert.Empty(plan.Files);
    }

    [Fact]
    public void Every_episode_below_the_floor_is_refused_with_a_reason()
    {
        var plan = _policy.Plan([File("/pack/Show.S01E01.mkv", Mib)], WorkKind.Series);

        Assert.False(plan.Accepted);
        Assert.Contains("below the minimum size", plan.Reason);
    }

    private static ImportFileEntry File(string path, long size) => new(path, size);
}
