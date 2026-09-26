using Cinomni.Subtitles.Files;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Pure unit tests for where a sidecar ends up when its video moves. The store composes the name from
/// the video's stem plus this module's own part — language, flags, format — so a move has to carry
/// that tail across untouched and change nothing else.
/// </summary>
public sealed class SidecarRelocationTests
{
    private static string P(params string[] segments) => Path.Combine([Path.GetFullPath("/library"), .. segments]);

    [Fact]
    public void The_language_and_flags_survive_the_move()
    {
        var to = SidecarRelocation.For(
            P("Movie_ Cut", "Movie: Cut.mkv"),
            P("Movie_ Cut", "Movie_ Cut.mkv"),
            P("Movie_ Cut", "Movie: Cut.es.forced.srt"));

        Assert.Equal(P("Movie_ Cut", "Movie_ Cut.es.forced.srt"), to);
    }

    [Fact]
    public void A_sidecar_follows_the_video_into_a_renamed_directory()
    {
        // The directory is part of what a repair fixes, so the sidecar has to change folder too.
        var to = SidecarRelocation.For(
            P("Series: One", "video.mkv"),
            P("Series_ One", "video.mkv"),
            P("Series: One", "video.en.srt"));

        Assert.Equal(P("Series_ One", "video.en.srt"), to);
    }

    [Fact]
    public void A_file_in_another_directory_is_not_this_videos_sidecar()
    {
        Assert.Null(SidecarRelocation.For(
            P("Movie", "film.mkv"),
            P("Movie_", "film.mkv"),
            P("Other", "film.en.srt")));
    }

    [Fact]
    public void A_name_that_does_not_start_with_the_stem_is_left_alone()
    {
        // Another episode's subtitle in the same season folder. Rewriting it would point its row at a
        // file that does not exist and lose the one that does.
        Assert.Null(SidecarRelocation.For(
            P("Season 01", "Show - S01E01.mkv"),
            P("Season 01", "Show - S01E01 fixed.mkv"),
            P("Season 01", "Show - S01E02.en.srt")));
    }

    [Fact]
    public void An_unchanged_video_path_yields_the_same_sidecar_path()
    {
        // What idempotency looks like here: replaying the command writes the value already stored.
        var path = P("Movie", "film.en.srt");

        Assert.Equal(path, SidecarRelocation.For(P("Movie", "film.mkv"), P("Movie", "film.mkv"), path));
    }
}
