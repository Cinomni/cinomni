using Cinomni.Import.Contracts;
using Cinomni.Import.Persistence;

namespace Cinomni.Import.Tests;

/// <summary>
/// Pure unit tests for the ImportJob finite-state machine and its file operations (no database).
/// The machine is a <b>batch</b> machine — Matching → Deciding → Operating → Probing → Registered is
/// traversed once for the whole set of files, with the per-file lifecycle on ImportFileMatch — so a
/// movie is simply the batch of one and a season pack does not throw on its second file.
/// </summary>
public sealed class ImportJobFsmTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly MediaInfo Probed = new("matroska", 6000, 8_000_000,
        [new MediaStreamInfo(0, MediaStreamKind.Video, "h264", null, 1920, 1080, null, true, false)]);

    private static ImportJob NewJob() =>
        ImportJob.Create(
            downloadTaskId: Guid.NewGuid(),
            intentId: Guid.NewGuid(),
            attemptId: Guid.NewGuid(),
            workId: Guid.NewGuid(),
            targetId: Guid.NewGuid(),
            sourcePath: "/data/staging/Movie.2024",
            now: Now);

    [Fact]
    public void Create_starts_pending_with_a_genesis_history_line()
    {
        var job = NewJob();

        Assert.Equal(ImportJobState.Pending, job.State);
        Assert.Single(job.History);
        Assert.Equal("ProcessCompletedDownload", job.History[0].Trigger);
        Assert.Empty(job.Matches);
    }

    [Fact]
    public void Happy_path_reaches_registered_with_asset_and_media_info()
    {
        var job = NewJob();

        job.BeginMatching(Now);
        var match = job.RecordMatch("/data/staging/Movie.2024/movie.mkv", 1000, "hash-1", Now);
        job.Approve(Now);
        var operation = job.PlanOperation(match, FileOperationType.Hardlink, "/data/library/movie/movie.mkv");
        operation.MarkExecuting();
        operation.MarkVerified();
        match.MarkOperated("/data/library/movie/movie.mkv");
        job.MarkOperated(Now);
        match.MarkProbed(Probed);
        job.Register(Now);

        Assert.Equal(ImportJobState.Registered, job.State);
        Assert.True(job.IsTerminal);
        Assert.Equal(ImportFileMatchState.Registered, match.State);
        // The root's asset/target/media-info members are first-match shims over the child.
        Assert.Equal(match.AssetId, job.AssetId);
        Assert.Equal("/data/library/movie/movie.mkv", job.TargetPath);
        Assert.NotNull(job.MediaInfo);
        Assert.Single(job.MediaInfo!.Streams);
        Assert.Equal("h264", job.MediaInfo.Streams[0].Codec);
    }

    [Fact]
    public void Illegal_transition_is_rejected_and_leaves_state_untouched()
    {
        var job = NewJob(); // Pending

        // RecordMatch is only valid from Matching.
        Assert.Throws<InvalidOperationException>(() =>
            job.RecordMatch("/x/movie.mkv", 1, "h", Now));
        Assert.Equal(ImportJobState.Pending, job.State);
        Assert.Empty(job.Matches); // the rejected transition recorded nothing
    }

    [Fact]
    public void Unmatched_when_no_file_matches()
    {
        var job = NewJob();
        job.BeginMatching(Now);

        job.MarkUnmatched("no acceptable video file", Now);

        Assert.Equal(ImportJobState.Unmatched, job.State);
        Assert.Equal("no acceptable video file", job.Reason);
        Assert.False(job.IsTerminal); // reopenable via manual import
    }

    [Fact]
    public void Rejected_when_specs_refuse()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        job.RecordMatch("/x/movie.mkv", 10, "h", Now);

        job.Reject("main file below minimum size", Now);

        Assert.Equal(ImportJobState.Rejected, job.State);
        Assert.True(job.IsTerminal);
    }

    [Fact]
    public void Operation_failure_returns_the_job_to_pending()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        var match = job.RecordMatch("/x/movie.mkv", 1000, "h", Now);
        job.Approve(Now);
        job.PlanOperation(match, FileOperationType.Hardlink, "/data/library/movie/movie.mkv");

        match.MarkFailed("verify failed");
        job.MarkOperationFailed("verify failed", Now);

        // The goal is not lost — it returns to Pending to retry.
        Assert.Equal(ImportJobState.Pending, job.State);
        Assert.Equal("verify failed", job.Reason);
        Assert.Equal(ImportFileMatchState.Failed, match.State);
    }

    [Fact]
    public void Manual_import_reopens_an_unmatched_job()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        job.MarkUnmatched("no match", Now);

        job.ManualImport("/data/staging/other.mkv", 2000, "h2", Now);

        Assert.Equal(ImportJobState.Deciding, job.State);
        Assert.Equal("/data/staging/other.mkv", Assert.Single(job.Matches).SourcePath);
    }

    [Fact]
    public void FileOperation_records_its_lifecycle()
    {
        var operation = FileOperation.Plan(Guid.NewGuid(), 0, FileOperationType.Hardlink, "/from", "/to");
        Assert.Equal(FileOperationState.Planned, operation.State);

        operation.MarkExecuting();
        Assert.Equal(FileOperationState.Executing, operation.State);

        operation.MarkVerified();
        Assert.Equal(FileOperationState.Verified, operation.State);
        Assert.True(operation.Verified);
    }

    [Fact]
    public void FileOperation_rollback_marks_failed_then_rolled_back()
    {
        var operation = FileOperation.Plan(Guid.NewGuid(), 0, FileOperationType.Hardlink, "/from", "/to");

        operation.MarkFailed();
        Assert.Equal(FileOperationState.Failed, operation.State);

        operation.MarkRolledBack();
        Assert.Equal(FileOperationState.RolledBack, operation.State);
        Assert.False(operation.Verified);
    }

    [Fact]
    public void Probe_failure_still_registers_the_asset_without_streams()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        var match = job.RecordMatch("/x/movie.mkv", 1000, "h", Now);
        job.Approve(Now);
        job.PlanOperation(match, FileOperationType.Hardlink, "/data/library/movie/movie.mkv");
        match.MarkOperated("/data/library/movie/movie.mkv");
        job.MarkOperated(Now);

        match.MarkProbed(MediaInfo.Empty);
        job.Register(Now);

        Assert.Equal(ImportJobState.Registered, job.State);
        Assert.NotNull(job.AssetId);
        Assert.Empty(job.MediaInfo!.Streams);
    }

    [Fact]
    public void A_job_may_hold_many_file_matches()
    {
        var job = NewJob();
        job.BeginMatching(Now);

        var matches = job.RecordMatches(
            [
                new ImportFileCandidate("/pack/S01E01.mkv", 1000, "h1"),
                new ImportFileCandidate("/pack/S01E02.mkv", 1100, "h2"),
                new ImportFileCandidate("/pack/S01E03.mkv", 1200, "h3"),
            ],
            Now);

        // One traversal of the batch machine for the whole set — the naive per-file loop would throw here.
        Assert.Equal(ImportJobState.Deciding, job.State);
        Assert.Equal(3, matches.Count);
        Assert.Equal([0, 1, 2], job.Matches.Select(m => m.Seq));
        Assert.Equal("/pack/S01E01.mkv", job.MatchedFilePath); // first-match shim
    }

    [Fact]
    public void Each_match_carries_its_own_asset_id()
    {
        var job = NewJob();
        job.BeginMatching(Now);

        var matches = job.RecordMatches(
            [
                new ImportFileCandidate("/pack/S01E01.mkv", 1000, "h1"),
                new ImportFileCandidate("/pack/S01E02.mkv", 1100, "h2"),
            ],
            Now);

        Assert.NotEqual(matches[0].AssetId, matches[1].AssetId);
        Assert.All(matches, m => Assert.NotEqual(Guid.Empty, m.AssetId));
    }

    [Fact]
    public void Registering_twice_reuses_the_persisted_asset_id()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        var first = job.RecordMatch("/pack/S01E01.mkv", 1000, "h1", Now);
        var mintedOnce = first.AssetId;
        job.Approve(Now);
        job.PlanOperation(first, FileOperationType.Hardlink, "/lib/S01E01.mkv");
        first.MarkOperated("/lib/S01E01.mkv");
        job.MarkOperationFailed("a sibling file failed; the batch retries", Now);

        // The re-drive re-scans the content and records the very same file again.
        job.BeginMatching(Now);
        var again = job.RecordMatch("/pack/S01E01.mkv", 1000, "h1", Now);

        Assert.Same(first, again);
        Assert.Single(job.Matches);
        // A fresh Uuid7 here would slip past Library's asset-id idempotency and then break
        // ux_media_versions_full_path on the duplicate version.
        Assert.Equal(mintedOnce, again.AssetId);
    }

    [Fact]
    public void A_match_that_already_verified_is_not_replanned()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        var matches = job.RecordMatches(
            [
                new ImportFileCandidate("/pack/S01E01.mkv", 1000, "h1"),
                new ImportFileCandidate("/pack/S01E02.mkv", 1100, "h2"),
            ],
            Now);
        job.Approve(Now);
        var landed = job.PlanOperation(matches[0], FileOperationType.Hardlink, "/lib/S01E01.mkv");
        landed.MarkVerified();
        matches[0].MarkOperated("/lib/S01E01.mkv");
        matches[1].MarkFailed("verify failed");
        job.MarkOperationFailed("1 of 2 files failed", Now);

        // Re-drive: the landed file keeps its state, the failed one is reopened for another attempt.
        job.BeginMatching(Now);
        job.RecordMatches(
            [
                new ImportFileCandidate("/pack/S01E01.mkv", 1000, "h1"),
                new ImportFileCandidate("/pack/S01E02.mkv", 1100, "h2"),
            ],
            Now);

        Assert.Equal(ImportFileMatchState.Operated, job.Matches[0].State);
        Assert.True(job.Matches[0].IsLanded);
        Assert.Equal(ImportFileMatchState.Planned, job.Matches[1].State);
        Assert.False(job.Matches[1].IsLanded);
        Assert.Single(job.Operations); // only the failed file's operation is worth planning again

        // And the machine itself refuses to re-land it, so no caller can hardlink it a second time.
        job.Approve(Now);
        Assert.Throws<InvalidOperationException>(() => job.Matches[0].MarkOperated("/lib/S01E01.mkv"));
    }

    [Fact]
    public void A_partial_batch_leaves_the_job_recoverable()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        var matches = job.RecordMatches(
            [
                new ImportFileCandidate("/pack/S01E01.mkv", 1000, "h1"),
                new ImportFileCandidate("/pack/S01E02.mkv", 1100, "h2"),
                new ImportFileCandidate("/pack/S01E03.mkv", 1200, "h3"),
            ],
            Now);
        job.Approve(Now);
        job.PlanOperation(matches[0], FileOperationType.Hardlink, "/lib/S01E01.mkv");
        matches[0].MarkOperated("/lib/S01E01.mkv");
        job.PlanOperation(matches[1], FileOperationType.Hardlink, "/lib/S01E02.mkv");
        matches[1].MarkFailed("verify failed");

        job.MarkOperationFailed("1 of 3 files failed to verify", Now);

        // Not terminal, not lost: the job is back at Pending and the trail says what happened.
        Assert.Equal(ImportJobState.Pending, job.State);
        Assert.False(job.IsTerminal);
        Assert.Equal(ImportFileMatchState.Operated, matches[0].State);
        Assert.Equal(ImportFileMatchState.Failed, matches[1].State);
        Assert.Equal(ImportFileMatchState.Planned, matches[2].State);
        Assert.Contains(job.History, h => h.Trigger == "OperationFailed");
    }

    [Fact]
    public void A_partly_landed_batch_registers_what_landed_and_stays_recoverable()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        var matches = job.RecordMatches(
            [
                new ImportFileCandidate("/pack/S01E01.mkv", 1000, "h1"),
                new ImportFileCandidate("/pack/S01E02.mkv", 1100, "h2"),
            ],
            Now);
        job.Approve(Now);
        job.PlanOperation(matches[0], FileOperationType.Hardlink, "/lib/S01E01.mkv");
        matches[0].MarkOperated("/lib/S01E01.mkv");
        job.PlanOperation(matches[1], FileOperationType.Hardlink, "/lib/S01E02.mkv");
        matches[1].MarkFailed("verify failed");
        job.MarkOperated(Now);
        matches[0].MarkProbed(Probed);

        var announced = job.RegisterProbedFiles();
        job.MarkPartiallyRegistered("1 of 2 files failed to verify", Now);

        // The file that landed is a real asset — withholding it leaves an orphan in the library
        // tree that no module knows about and no later drive can announce.
        Assert.Same(matches[0], Assert.Single(announced));
        Assert.Equal(ImportFileMatchState.Registered, matches[0].State);
        Assert.Equal(matches[0].AssetId, job.AssetId);

        // The batch is not complete, so the job is not terminal: it is back at Pending, and only the
        // file that failed is replanned on the next drive.
        Assert.Equal(ImportJobState.Pending, job.State);
        Assert.False(job.IsTerminal);
        Assert.Contains(job.History, h => h.Trigger == "PartiallyRegistered");

        job.BeginMatching(Now);
        job.RecordMatches(
            [
                new ImportFileCandidate("/pack/S01E01.mkv", 1000, "h1"),
                new ImportFileCandidate("/pack/S01E02.mkv", 1100, "h2"),
            ],
            Now);
        Assert.Equal(ImportFileMatchState.Registered, job.Matches[0].State);
        Assert.True(job.Matches[0].IsLanded);
        Assert.Equal(ImportFileMatchState.Planned, job.Matches[1].State);
    }

    [Fact]
    public void Register_reports_only_the_files_it_announced()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        var match = job.RecordMatch("/pack/S01E01.mkv", 1000, "h1", Now);
        job.Approve(Now);
        job.PlanOperation(match, FileOperationType.Hardlink, "/lib/S01E01.mkv");
        match.MarkOperated("/lib/S01E01.mkv");
        job.MarkOperated(Now);
        match.MarkProbed(Probed);
        job.RegisterProbedFiles(); // an earlier drive already announced it

        job.MarkPartiallyRegistered("a sibling failed", Now);
        job.BeginMatching(Now);
        job.RecordMatch("/pack/S01E01.mkv", 1000, "h1", Now);
        job.Approve(Now);
        job.MarkOperated(Now);

        // The outbox is at-least-once; re-announcing an already registered file would deliver a
        // second MediaAvailable for the same asset.
        Assert.Empty(job.Register(Now));
        Assert.Equal(ImportJobState.Registered, job.State);
    }

    [Fact]
    public void A_file_operation_may_only_be_planned_while_operating()
    {
        var job = NewJob();
        job.BeginMatching(Now);
        var match = job.RecordMatch("/x/movie.mkv", 1000, "h", Now); // Deciding

        Assert.Throws<InvalidOperationException>(() =>
            job.PlanOperation(match, FileOperationType.Hardlink, "/lib/movie.mkv"));
        Assert.Empty(job.Operations);
    }
}
