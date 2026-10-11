using Cinomni.Library.Contracts;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Files;
using Cinomni.Subtitles.Sync;

namespace Cinomni.Subtitles.Tests;

/// <summary>Pure tests of what a pass chooses before it decodes anything: the audio, the scope, the output name.</summary>
public sealed class SyncInputsTests
{
    [Fact]
    public void The_audio_in_the_subtitles_language_wins_over_the_default_track()
    {
        IReadOnlyList<MediaStreamSummary> streams =
        [
            Stream(0, MediaStreamType.Video, null, isDefault: true),
            Stream(1, MediaStreamType.Audio, "fre", isDefault: true),
            Stream(2, MediaStreamType.Audio, "eng", isDefault: false),
        ];

        Assert.Equal(2, SyncAudioTrack.Choose(streams, "en"));
        Assert.Equal(1, SyncAudioTrack.Choose(streams, "fr"));
    }

    [Fact]
    public void Without_a_track_in_the_language_the_default_then_the_first_is_used()
    {
        IReadOnlyList<MediaStreamSummary> withDefault =
        [
            Stream(1, MediaStreamType.Audio, "jpn", isDefault: false),
            Stream(2, MediaStreamType.Audio, "ita", isDefault: true),
        ];
        IReadOnlyList<MediaStreamSummary> untagged =
        [
            Stream(3, MediaStreamType.Audio, null, isDefault: false),
            Stream(4, MediaStreamType.Audio, null, isDefault: false),
        ];

        Assert.Equal(2, SyncAudioTrack.Choose(withDefault, "es"));
        Assert.Equal(3, SyncAudioTrack.Choose(untagged, "es"));
    }

    [Fact]
    public void A_video_without_audio_has_nothing_to_compare_with()
    {
        Assert.Null(SyncAudioTrack.Choose([Stream(0, MediaStreamType.Video, null, isDefault: true)], "en"));
    }

    [Fact]
    public void Ass_forced_and_switched_off_are_out_of_scope_before_anything_is_decoded()
    {
        var on = new SubtitleSyncOptions();
        var off = new SubtitleSyncOptions { Enabled = false };

        Assert.Null(SubtitleSyncAnalyzer.Unsupported(SubtitleFormat.Srt, forced: false, on));
        Assert.Null(SubtitleSyncAnalyzer.Unsupported(SubtitleFormat.Vtt, forced: false, on));
        Assert.NotNull(SubtitleSyncAnalyzer.Unsupported(SubtitleFormat.Ass, forced: false, on));
        Assert.NotNull(SubtitleSyncAnalyzer.Unsupported(SubtitleFormat.Srt, forced: true, on));
        Assert.NotNull(SubtitleSyncAnalyzer.Unsupported(SubtitleFormat.Srt, forced: false, off));
    }

    [Fact]
    public void The_synchronized_copy_keeps_the_flags_and_the_stem_so_it_moves_with_the_video()
    {
        var original = Path.Combine("library", "Movie", "Movie.en.forced.hi.srt");

        Assert.Equal("Movie.en.forced.hi.synced.srt", SyncedSidecar.NameFor(original));
        Assert.True(SyncedSidecar.IsSyncedName("Movie.en.synced.srt"));
        Assert.False(SyncedSidecar.IsSyncedName("Movie.en.srt"));

        var moved = SidecarRelocation.For(
            Path.Combine("library", "Movie", "Movie.mkv"),
            Path.Combine("library", "Movie (1999)", "Movie (1999).mkv"),
            SyncedSidecar.PathFor(original));
        Assert.Equal(Path.Combine("library", "Movie (1999)", "Movie (1999).en.forced.hi.synced.srt"), moved);
    }

    [Fact]
    public void Options_out_of_range_are_brought_back_into_it()
    {
        var options = new SubtitleSyncOptions { Concurrency = 50, MinScore = double.NaN, MinMargin = 0.2 };

        options.Normalize();

        Assert.Equal(SubtitleSyncOptions.MaxConcurrency, options.Concurrency);
        Assert.Equal(0.25, options.MinScore);
        Assert.Equal(1, options.MinMargin);
    }

    private static MediaStreamSummary Stream(int index, MediaStreamType type, string? language, bool isDefault) =>
        new(index, type, null, language, null, null, null, null, null, isDefault, false, false);
}
