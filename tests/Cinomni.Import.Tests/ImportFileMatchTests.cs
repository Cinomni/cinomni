using Cinomni.Import.Contracts;
using Cinomni.Import.Persistence;

namespace Cinomni.Import.Tests;

/// <summary>
/// Pure unit tests for the per-file state machine that carries a season pack. Every value the import
/// pipeline needs per file — target path, asset id, catalog units, media info — lives on the match,
/// and each transition is guarded so a re-drive can never land the same file twice.
/// </summary>
public sealed class ImportFileMatchTests
{
    private static readonly Guid JobId = Guid.NewGuid();

    private static readonly MediaInfo Probed = new("matroska", 1400, 6_000_000,
        [new MediaStreamInfo(0, MediaStreamKind.Video, "h264", null, 1920, 1080, null, true, false)]);

    private static ImportFileMatch NewMatch(int seq = 0, string path = "/pack/S01E01.mkv") =>
        ImportFileMatch.Plan(JobId, seq, path, size: 1000, hash: "hash-1");

    [Fact]
    public void Plan_starts_planned_with_a_minted_asset_id()
    {
        var match = NewMatch();

        Assert.Equal(ImportFileMatchState.Planned, match.State);
        Assert.NotEqual(Guid.Empty, match.AssetId);
        Assert.Equal("/pack/S01E01.mkv", match.SourcePath);
        Assert.Equal(1000, match.Size);
        Assert.Null(match.TargetPath);
        Assert.False(match.IsLanded);
    }

    [Fact]
    public void Happy_path_runs_planned_to_registered()
    {
        var match = NewMatch();

        match.AttachOperation(operationSeq: 0, "/lib/The Wire/Season 01/The Wire - S01E01.mkv");
        match.MarkOperated("/lib/The Wire/Season 01/The Wire - S01E01.mkv");
        Assert.True(match.IsLanded);

        match.MarkProbed(Probed);
        Assert.Equal(ImportFileMatchState.Probed, match.State);
        Assert.Single(match.MediaInfo!.Streams);

        match.MarkRegistered();
        Assert.Equal(ImportFileMatchState.Registered, match.State);
        Assert.Equal(0, match.OperationSeq);
    }

    [Fact]
    public void An_illegal_transition_is_rejected_and_leaves_the_state_untouched()
    {
        var match = NewMatch();

        // Probing is only valid once the file actually landed.
        Assert.Throws<InvalidOperationException>(() => match.MarkProbed(Probed));
        Assert.Equal(ImportFileMatchState.Planned, match.State);
        Assert.Null(match.MediaInfoJson);
    }

    [Fact]
    public void A_landed_file_can_never_be_operated_twice()
    {
        var match = NewMatch();
        match.MarkOperated("/lib/x.mkv");

        Assert.Throws<InvalidOperationException>(() => match.MarkOperated("/lib/x.mkv"));
        Assert.Equal(ImportFileMatchState.Operated, match.State);
    }

    [Fact]
    public void A_failed_file_is_reopened_for_a_retry()
    {
        var match = NewMatch();
        match.AttachOperation(operationSeq: 3, "/lib/x.mkv");
        match.MarkFailed("verify failed");

        Assert.True(match.Reopen());
        Assert.Equal(ImportFileMatchState.Planned, match.State);
        Assert.Null(match.OperationSeq); // the next drive plans a fresh operation for it
    }

    [Fact]
    public void A_landed_file_is_never_reopened()
    {
        var match = NewMatch();
        match.MarkOperated("/lib/x.mkv");

        Assert.False(match.Reopen());
        Assert.Equal(ImportFileMatchState.Operated, match.State);
    }

    [Fact]
    public void An_unresolved_file_records_why_and_stays_unlanded()
    {
        var match = NewMatch();

        match.MarkUnresolved("S03E07 is outside the requested units");

        Assert.Equal(ImportFileMatchState.Unresolved, match.State);
        Assert.Equal("S03E07 is outside the requested units", match.Reason);
        Assert.False(match.IsLanded);
    }

    [Fact]
    public void Resolving_units_records_the_numbers_a_consumer_needs()
    {
        var match = NewMatch();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // A multi-episode file is ONE asset with N unit links; the schema makes two assets for one
        // physical file impossible, so playback of it always starts at the beginning.
        match.ResolveUnits([first, second], seasonNumber: 1, episodeNumbers: [1, 2]);

        Assert.Equal([first, second], match.UnitIds);
        Assert.Equal(1, match.SeasonNumber);
        Assert.Equal([1, 2], match.EpisodeNumbers);
    }

    [Fact]
    public void Refreshing_the_fingerprint_keeps_the_minted_asset_id()
    {
        var match = NewMatch();
        var minted = match.AssetId;

        match.RefreshFingerprint(size: 2000, hash: "hash-2");

        Assert.Equal(minted, match.AssetId);
        Assert.Equal(2000, match.Size);
        Assert.Equal("hash-2", match.Hash);
    }

    [Fact]
    public void A_probe_failure_still_leaves_the_file_registrable()
    {
        var match = NewMatch();
        match.MarkOperated("/lib/x.mkv");

        match.MarkProbed(MediaInfo.Empty);
        match.MarkRegistered();

        Assert.Equal(ImportFileMatchState.Registered, match.State);
        Assert.Empty(match.MediaInfo!.Streams);
    }
}
