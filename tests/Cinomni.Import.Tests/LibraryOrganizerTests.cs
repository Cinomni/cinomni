using Cinomni.Import.Application;
using Cinomni.Import.Files;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Import.Tests;

/// <summary>
/// Pure unit tests for the library organiser: the layouts it emits and, more importantly, that
/// <b>every</b> segment of the path it builds is sanitised and the result is confined to the library
/// root. The series layout adds a nesting level, and the directory part is handed straight to
/// EnsureDirectoryAsync — an unsanitised folder would be created on disk unchecked.
/// </summary>
public sealed class LibraryOrganizerTests
{
    private const string LibraryRoot = "/data/test-library";

    private static readonly LibraryOrganizer Organizer = new(new ImportOptions { LibraryRoot = LibraryRoot });

    [Fact]
    public void Builds_the_series_season_episode_path()
    {
        var naming = new EpisodeNaming("The Wire", 2002, 1, [1], "The Target");

        var path = Organizer.BuildEpisodePath(naming, "/staging/the.wire.s01e01.mkv");

        Assert.NotNull(path);
        Assert.Equal(
            [
                "The Wire (2002)",
                "Season 01",
                "The Wire - S01E01 - The Target.mkv",
            ],
            TailSegments(path!, 3));
    }

    [Fact]
    public void Uses_season_zero_for_specials()
    {
        var naming = new EpisodeNaming("The Wire", 2002, 0, [2], null);

        var path = Organizer.BuildEpisodePath(naming, "/staging/the.wire.s00e02.mkv");

        Assert.NotNull(path);
        Assert.Equal(["Season 00", "The Wire - S00E02.mkv"], TailSegments(path!, 2));
    }

    [Fact]
    public void Joins_a_multi_episode_range()
    {
        var naming = new EpisodeNaming("Show", 2010, 1, [1, 2], "Pilot");

        var path = Organizer.BuildEpisodePath(naming, "/staging/show.s01e01e02.mkv");

        Assert.NotNull(path);
        Assert.Equal("Show - S01E01-E02 - Pilot.mkv", TailSegments(path!, 1)[0]);
    }

    [Fact]
    public void Sanitises_every_path_segment()
    {
        // A provider title is external data: it can carry separators, invalid characters and
        // traversal tokens, and each of them must be neutralised in its own segment.
        var naming = new EpisodeNaming("../../etc/pass?wd", 2002, 1, [1], "a:b|c");

        var path = Organizer.BuildEpisodePath(naming, "/staging/x.mkv");

        Assert.NotNull(path);
        Assert.True(PathGuard.IsWithin(LibraryRoot, path!));

        // Exactly three levels below the root: the separators inside the title did NOT become
        // directory levels, so nothing was created outside the intended series/season/file shape.
        var rootDepth = ImportFileFilter.Segments(Path.GetFullPath(LibraryRoot)).Length;
        Assert.Equal(rootDepth + 3, ImportFileFilter.Segments(path!).Length);

        var segments = TailSegments(path!, 3);
        Assert.DoesNotContain('?', segments[0]);
        Assert.DoesNotContain(':', segments[2]);
        Assert.DoesNotContain('|', segments[2]);
    }

    [Fact]
    public void A_movie_path_is_unchanged()
    {
        var path = Organizer.BuildMoviePath("/staging/Movie.2024/Movie.2024.1080p.BluRay.x264.mkv");

        Assert.NotNull(path);
        Assert.Equal(
            ["Movie.2024.1080p.BluRay.x264", "Movie.2024.1080p.BluRay.x264.mkv"],
            TailSegments(path!, 2));
    }

    [Fact]
    public void A_movie_release_name_longer_than_a_segment_keeps_its_container_extension()
    {
        // Sanitising "<name><ext>" as one segment used to cut the extension off: the file then played as
        // application/octet-stream and no extension-based rescan ever saw it again.
        var release = new string('A', 260) + ".mkv";

        var path = Organizer.BuildMoviePath($"/staging/{release}");

        Assert.NotNull(path);
        Assert.EndsWith(".mkv", path!);
        Assert.True(Path.GetFileName(path).Length <= 200);
    }

    [Fact]
    public void A_title_that_would_share_another_titles_path_gets_a_tag_of_its_own()
    {
        var movie = Organizer.BuildMoviePath("/staging/Alien.1979.mkv", MovieNamingFormat.TitleYear, "Alien", 1979, ownerTag: "0badc0de");
        var episode = Organizer.BuildEpisodePath(
            new EpisodeNaming("Show", 2020, 1, [1], "Pilot"), "/staging/Show.S01E01.mkv", ownerTag: "0badc0de");

        Assert.Equal(["Alien (1979) [0badc0de]", "Alien (1979) [0badc0de].mkv"], TailSegments(movie!, 2));
        Assert.Equal("Show (2020) [0badc0de]", TailSegments(episode!, 3)[0]);
    }

    [Fact]
    public void A_long_name_keeps_its_owner_tag_whole()
    {
        // Appending the tag before the cap let a long release name cut it off, and the "tagged" path
        // came out as the plain one again.
        var movie = Organizer.BuildMoviePath($"/staging/{new string('A', 260)}.mkv", MovieNamingFormat.ReleaseName, null, null, ownerTag: "0badc0de");

        var segments = TailSegments(movie!, 2);
        Assert.EndsWith(" [0badc0de]", segments[0], StringComparison.Ordinal);
        Assert.EndsWith(" [0badc0de].mkv", segments[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_owner_tag_is_the_random_end_of_the_work_id_and_the_same_every_time()
    {
        // A UUIDv7 starts with its timestamp: two works created a moment apart share their first digits
        // and must not share a tag.
        var first = Uuid7.New();
        var second = Uuid7.New();

        Assert.Equal(first.ToString("N")[^8..], LibraryOrganizer.OwnerTag(first));
        Assert.Equal(LibraryOrganizer.OwnerTag(first), LibraryOrganizer.OwnerTag(first));
        Assert.NotEqual(LibraryOrganizer.OwnerTag(first), LibraryOrganizer.OwnerTag(second));
    }

    [Fact]
    public void A_long_movie_release_name_still_lands()
    {
        // The shipped movie layout repeats the release name in both segments, so it burns roughly
        // twice its length; applying the series MaxTargetPathLength ceiling to it refuses 2160p /
        // EXTENDED / REMUX scene names that imported cleanly before the series layout existed.
        const string release = "The.Lord.of.the.Rings.The.Fellowship.of.the.Ring.2001.EXTENDED." +
            "REMASTERED.2160p.UHD.BluRay.x265.10bit.HDR.TrueHD.7.1.Atmos-SWTYBLZ.mkv";

        var path = Organizer.BuildMoviePath($"/staging/{release}");

        Assert.NotNull(path);
        Assert.EndsWith(release, path!);
        Assert.True(path!.Length > new ImportOptions().MaxTargetPathLength);
    }

    [Fact]
    public void A_movie_path_is_still_sanitised_and_confined()
    {
        // Dropping the length ceiling must not drop the guards: a release name is hostile data.
        var path = Organizer.BuildMoviePath("/staging/Movie?2024|<CON>:x.mkv");

        Assert.NotNull(path);
        Assert.True(PathGuard.IsWithin(LibraryRoot, path!));
        var rootDepth = ImportFileFilter.Segments(Path.GetFullPath(LibraryRoot)).Length;
        Assert.Equal(rootDepth + 2, ImportFileFilter.Segments(path!).Length);
        var leaf = TailSegments(path!, 1)[0];
        Assert.DoesNotContain('?', leaf);
        Assert.DoesNotContain('|', leaf);
    }

    [Fact]
    public void A_long_episode_title_keeps_its_container_extension()
    {
        // Episode titles come from the catalog, which is provider-supplied and community editable:
        // 100+ character titles are routine. Appending ".mkv" and only then capping the leaf cuts
        // the container extension off, leaving a file that plays as octet-stream and is invisible
        // to every extension-based rescan.
        var naming = new EpisodeNaming(
            "Show",
            2020,
            1,
            [1],
            string.Join(' ', Enumerable.Repeat("an unusually verbose provider supplied episode title", 4)));

        var path = Organizer.BuildEpisodePath(naming, "/staging/x.mkv");

        Assert.NotNull(path);
        var leaf = TailSegments(path!, 1)[0];
        Assert.Equal(".mkv", Path.GetExtension(leaf));
        Assert.True(leaf.Length <= new ImportOptions().MaxSegmentLength);
    }

    [Fact]
    public void Title_year_names_a_movie_from_the_catalog_and_keeps_the_extension()
    {
        var path = Organizer.BuildMoviePath(
            "/staging/Inception.2010.1080p.mkv", MovieNamingFormat.TitleYear, "Inception", 2010);

        Assert.NotNull(path);
        Assert.Equal(["Inception (2010)", "Inception (2010).mkv"], TailSegments(path!, 2));
    }

    [Fact]
    public void Title_year_falls_back_to_the_release_name_when_the_catalog_title_is_missing()
    {
        var path = Organizer.BuildMoviePath("/staging/Inception.2010.mkv", MovieNamingFormat.TitleYear, "  ", null);

        Assert.NotNull(path);
        Assert.Equal(["Inception.2010", "Inception.2010.mkv"], TailSegments(path!, 2));
    }

    [Fact]
    public void A_file_with_no_episode_number_gets_no_path()
    {
        var naming = new EpisodeNaming("The Wire", 2002, 1, [], null);

        Assert.Null(Organizer.BuildEpisodePath(naming, "/staging/x.mkv"));
    }

    [Fact]
    public void An_over_long_path_is_refused_rather_than_attempted()
    {
        // Windows resolves against MAX_PATH; the extra nesting level makes this reachable in dev, and
        // an IOException halfway through a season pack is far worse than an explained refusal.
        var organizer = new LibraryOrganizer(new ImportOptions { LibraryRoot = LibraryRoot, MaxTargetPathLength = 40 });

        Assert.Null(organizer.BuildEpisodePath(new EpisodeNaming("The Wire", 2002, 1, [1], "The Target"), "/staging/x.mkv"));
    }

    [Theory]
    [InlineData(1, new[] { 1 }, "S01E01")]
    [InlineData(0, new[] { 12 }, "S00E12")]
    [InlineData(12, new[] { 3, 4, 5 }, "S12E03-E05")]
    public void Episode_tag_is_zero_padded_and_ranged(int season, int[] episodes, string expected)
    {
        Assert.Equal(expected, LibraryOrganizer.EpisodeTag(season, episodes));
    }

    private static string[] TailSegments(string path, int count) =>
        [.. ImportFileFilter.Segments(path).TakeLast(count)];
}
