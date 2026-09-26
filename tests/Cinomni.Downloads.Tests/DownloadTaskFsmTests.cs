using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Persistence;

namespace Cinomni.Downloads.Tests;

/// <summary>Pure unit tests for the DownloadTask finite-state machine (no database).</summary>
public sealed class DownloadTaskFsmTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static DownloadTask NewTask() =>
        DownloadTask.Create(
            intentId: Guid.NewGuid(),
            attemptId: Guid.NewGuid(),
            workId: Guid.NewGuid(),
            targetId: Guid.NewGuid(),
            releaseGuid: "release-1",
            downloadUrl: "magnet:?xt=urn:btih:abc",
            savePath: "/data/staging",
            policy: SeedingPolicy.Unbounded,
            now: Now);

    [Fact]
    public void Create_starts_queued_with_a_genesis_history_line()
    {
        var task = NewTask();

        Assert.Equal(DownloadState.Queued, task.State);
        Assert.Single(task.History);
        Assert.Equal("AddDownload", task.History[0].Trigger);
    }

    [Fact]
    public void MarkDownloading_advances_once_and_sets_started()
    {
        var task = NewTask();

        Assert.True(task.MarkDownloading(Now));
        Assert.Equal(DownloadState.Downloading, task.State);
        Assert.NotNull(task.StartedAt);

        // A repeated "downloading" snapshot from the stream is a no-op (idempotent).
        Assert.False(task.MarkDownloading(Now));
    }

    [Fact]
    public void Observed_transitions_ignore_out_of_order_states()
    {
        var task = NewTask();
        task.MarkDownloading(Now);

        // "checking" after we already started downloading is not a valid backward move: ignored.
        Assert.False(task.MarkChecking(Now));
        Assert.Equal(DownloadState.Downloading, task.State);
    }

    [Fact]
    public void MarkCompleted_records_the_content_path()
    {
        var task = NewTask();
        task.MarkDownloading(Now);

        Assert.True(task.MarkCompleted("/data/staging/Movie", Now));
        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal("/data/staging/Movie", task.ContentPath);
    }

    [Fact]
    public void Fail_raises_error_once()
    {
        var task = NewTask();
        task.MarkDownloading(Now);

        Assert.True(task.Fail("disk full", Now));
        Assert.Equal(DownloadState.Error, task.State);
        Assert.Equal("disk full", task.LastError);

        // A terminal error is not re-raised (DownloadFailed stays single-shot).
        Assert.False(task.Fail("disk full", Now));
    }

    [Fact]
    public void Pause_then_resume_round_trips()
    {
        var task = NewTask();
        task.MarkDownloading(Now);

        task.Pause(Now);
        Assert.Equal(DownloadState.Paused, task.State);

        task.Resume(Now);
        Assert.Equal(DownloadState.Downloading, task.State);
    }

    [Fact]
    public void Pause_is_rejected_once_completed()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.MarkCompleted("/data/staging/Movie", Now);

        Assert.Throws<DownloadStateConflictException>(() => task.Pause(Now));
    }

    [Fact]
    public void Remove_is_idempotent()
    {
        var task = NewTask();
        task.MarkDownloading(Now);

        task.Remove(Now);
        var historyCount = task.History.Count;
        task.Remove(Now); // no-op

        Assert.Equal(DownloadState.Removed, task.State);
        Assert.Equal(historyCount, task.History.Count);
    }

    [Fact]
    public void ShouldStopSeeding_true_when_ratio_limit_met()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.MarkCompleted("/data/staging/Movie", Now);
        task.MarkSeeding(Now);
        task.ApplySeedingPolicy(new SeedingPolicy(RatioLimit: 1.0, SeedTimeLimitSeconds: null), Now);
        task.UpdateProgress(1.0, 0, 0, 0, 0, allTimeUpload: 200, allTimeDownload: 100, seedingSeconds: 0, Now);

        Assert.True(task.ShouldStopSeeding());
    }

    [Fact]
    public void ShouldStopSeeding_false_below_ratio_and_when_not_seeding()
    {
        var task = NewTask();
        task.MarkDownloading(Now);
        task.MarkCompleted("/data/staging/Movie", Now);
        task.MarkSeeding(Now);
        task.ApplySeedingPolicy(new SeedingPolicy(RatioLimit: 2.0, SeedTimeLimitSeconds: null), Now);
        task.UpdateProgress(1.0, 0, 0, 0, 0, allTimeUpload: 100, allTimeDownload: 100, seedingSeconds: 0, Now);

        Assert.False(task.ShouldStopSeeding());
    }

    [Fact]
    public void SetFilePriorities_updates_named_files_only()
    {
        var task = NewTask();
        task.SetFiles(new (int, string, long, FilePriorityLevel)[]
        {
            (0, "movie.mkv", 1000, FilePriorityLevel.Normal),
            (1, "sample.mkv", 50, FilePriorityLevel.Normal),
        });

        task.SetFilePriorities(new Dictionary<int, FilePriorityLevel> { [1] = FilePriorityLevel.Skip }, Now);

        Assert.Equal(FilePriorityLevel.Normal, task.Files[0].Priority);
        Assert.Equal(FilePriorityLevel.Skip, task.Files[1].Priority);
    }

    [Fact]
    public void SeedingPolicy_json_round_trips()
    {
        var policy = new SeedingPolicy(RatioLimit: 1.5, SeedTimeLimitSeconds: 7200);

        var restored = SeedingPolicy.FromJson(policy.ToJson());

        Assert.Equal(policy, restored);
    }

    [Theory]
    [InlineData("../../etc", ".._.._etc")]
    [InlineData("..", "")]
    [InlineData("/etc/cron.d", "_etc_cron.d")]
    [InlineData(@"..\..\Windows", ".._.._Windows")]
    [InlineData("C:/Windows/Temp", "C__Windows_Temp")]
    [InlineData("name\nwith\rcontrols", "name_with_controls")]
    public void OnAdded_reduces_a_hostile_torrent_name_to_one_segment(string reported, string expected)
    {
        // A torrent name is swarm-supplied and this module composes the path Import is told to scan
        // from it. A name that can carry a separator or a parent-directory token points that scan at
        // an arbitrary directory, and everything there that passes the import policy is hardlinked
        // into the household's library under the operator's own catalogue title.
        var task = NewTask();

        task.OnAdded("infohash-1", reported, Now);

        Assert.Equal(expected, task.Name);
        Assert.DoesNotContain('/', task.Name);
        Assert.DoesNotContain('\\', task.Name);
    }

    [Fact]
    public void OnAdded_keeps_an_ordinary_release_name_exactly_as_it_is()
    {
        // The other half of the same promise: every real release must compose the path it always did.
        const string ordinary = "The.Feature.2024.1080p.BluRay.x264-GROUP";
        var task = NewTask();

        task.OnAdded("infohash-1", ordinary, Now);

        Assert.Equal(ordinary, task.Name);
    }
}
