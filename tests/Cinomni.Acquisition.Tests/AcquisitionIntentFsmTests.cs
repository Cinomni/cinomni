using Cinomni.Acquisition.Contracts;
using Cinomni.Acquisition.Persistence;

namespace Cinomni.Acquisition.Tests;

/// <summary>
/// Pure unit tests for the acquisition goal's finite-state machine — no database. These pin the
/// module's defining invariant (case #6): a download or import failure returns the goal
/// to Searching without losing it, until attempts are exhausted. Illegal transitions are rejected.
/// </summary>
public sealed class AcquisitionIntentFsmTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static AcquisitionIntent Searching(int maxAttempts = 5)
    {
        var intent = AcquisitionIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "All", T0, maxAttempts);
        intent.Plan(T0);
        return intent;
    }

    [Fact]
    public void Create_then_plan_reaches_searching()
    {
        // Arrange + Act
        var intent = Searching();

        // Assert
        Assert.Equal(IntentState.Searching, intent.State);
        Assert.Equal(0, intent.AttemptCount);
        Assert.Empty(intent.Attempts);
        // Genesis (Requested) + Requested→Planned + Planned→Searching.
        Assert.Equal(3, intent.History.Count);
        Assert.Equal(IntentState.Searching, intent.History[^1].ToState);
    }

    [Fact]
    public void Selecting_a_candidate_opens_an_attempt_and_goes_downloading()
    {
        // Arrange
        var intent = Searching();
        var evaluationId = Guid.NewGuid();

        // Act
        var attempt = intent.SelectCandidate(evaluationId, "g1", "magnet:?xt=urn:btih:g1", T0);

        // Assert
        Assert.Equal(IntentState.Downloading, intent.State);
        Assert.Equal(1, intent.AttemptCount);
        Assert.Equal("g1", intent.SelectedReleaseGuid);
        Assert.Equal(1, attempt.Ordinal);
        Assert.Equal(AttemptState.Started, attempt.State);
        Assert.Equal(evaluationId, attempt.EvaluationId);
    }

    [Fact]
    public void A_download_failure_returns_to_searching_and_keeps_the_intent()
    {
        // Arrange — a goal with a download in flight.
        var intent = Searching();
        intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);

        // Act — the download fails.
        intent.MarkDownloadFailed("tracker timeout", T0);

        // Assert — the intent is NOT lost; it returns to searching for another candidate.
        Assert.Equal(IntentState.Searching, intent.State);
        Assert.Null(intent.SelectedReleaseGuid);
        Assert.Equal(1, intent.AttemptCount); // the failed attempt is remembered
        var failed = Assert.Single(intent.Attempts);
        Assert.Equal(AttemptState.FailedDownload, failed.State);
        Assert.Equal("tracker timeout", failed.FailureReason);
        Assert.NotNull(failed.ClosedAt);
    }

    [Fact]
    public void The_goal_exhausts_only_after_its_attempts_run_out()
    {
        // Arrange — a goal that allows two attempts.
        var intent = Searching(maxAttempts: 2);

        // Act + Assert — first failure survives...
        intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);
        intent.MarkDownloadFailed("fail 1", T0);
        Assert.Equal(IntentState.Searching, intent.State);

        // ...second failure exhausts it.
        intent.SelectCandidate(Guid.NewGuid(), "g2", "magnet:g2", T0);
        intent.MarkDownloadFailed("fail 2", T0);
        Assert.Equal(IntentState.Exhausted, intent.State);
        Assert.Equal(2, intent.AttemptCount);
        Assert.All(intent.Attempts, a => Assert.Equal(AttemptState.FailedDownload, a.State));
    }

    [Fact]
    public void The_happy_path_downloads_imports_and_becomes_available()
    {
        // Arrange
        var intent = Searching();
        var attempt = intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);

        // Act
        intent.MarkDownloadStarted(T0);
        intent.MarkDownloadCompleted(T0);
        intent.MarkImported(T0);

        // Assert
        Assert.Equal(IntentState.Available, intent.State);
        Assert.Equal(AttemptState.Imported, attempt.State);
        Assert.NotNull(attempt.ClosedAt);
    }

    [Fact]
    public void An_import_failure_also_keeps_the_intent_and_retries()
    {
        // Arrange
        var intent = Searching();
        intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);
        intent.MarkDownloadCompleted(T0);

        // Act
        intent.MarkImportFailed("hardlink failed", T0);

        // Assert
        Assert.Equal(IntentState.Searching, intent.State);
        var failed = Assert.Single(intent.Attempts);
        Assert.Equal(AttemptState.FailedImport, failed.State);
    }

    [Fact]
    public void An_upgrade_reopens_an_available_goal_for_searching()
    {
        // Arrange — a landed goal.
        var intent = Searching();
        intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);
        intent.MarkDownloadCompleted(T0);
        intent.MarkImported(T0);

        // Act
        intent.Reopen(T0);

        // Assert
        Assert.Equal(IntentState.Searching, intent.State);
        Assert.True(intent.CanSelectCandidate);
    }

    [Fact]
    public void Selecting_before_planning_is_rejected_and_leaves_state_untouched()
    {
        // Arrange — a freshly created, not-yet-planned goal.
        var intent = AcquisitionIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "All", T0, 5);

        // Act + Assert — the illegal transition is rejected...
        Assert.Throws<InvalidOperationException>(() =>
            intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0));

        // ...and nothing changed.
        Assert.Equal(IntentState.Requested, intent.State);
        Assert.Empty(intent.Attempts);
    }

    [Fact]
    public void Completing_a_download_while_still_searching_is_rejected()
    {
        // Arrange
        var intent = Searching();

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => intent.MarkDownloadCompleted(T0));
        Assert.Equal(IntentState.Searching, intent.State);
    }

    [Fact]
    public void An_externally_satisfied_goal_becomes_available_without_consuming_an_attempt()
    {
        // Arrange — an episode goal that never tried anything: its season's pack is what landed.
        var intent = Searching();
        var assetId = Guid.NewGuid();

        // Act
        var satisfied = intent.MarkSatisfiedExternally(assetId, T0);

        // Assert — the goal is met, and it still has all five attempts. Charging it one would walk
        // the ten untouched episode goals of a season straight towards Exhausted.
        Assert.True(satisfied);
        Assert.Equal(IntentState.Available, intent.State);
        Assert.Equal(0, intent.AttemptCount);
        Assert.Empty(intent.Attempts);
        Assert.Equal(IntentState.Available, intent.History[^1].ToState);
        Assert.Equal("SatisfiedExternally", intent.History[^1].Trigger);
        Assert.Contains(assetId.ToString(), intent.History[^1].Note);
    }

    [Fact]
    public void Satisfying_an_already_available_goal_is_a_no_op()
    {
        // Arrange — a goal that already landed its own download.
        var intent = Searching();
        intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);
        intent.MarkDownloadCompleted(T0);
        intent.MarkImported(T0);
        var historyBefore = intent.History.Count;

        // Act — the import fan-out is at-least-once and reaches goals in every state.
        var satisfied = intent.MarkSatisfiedExternally(Guid.NewGuid(), T0);

        // Assert — no exception, no transition, no history line.
        Assert.False(satisfied);
        Assert.Equal(IntentState.Available, intent.State);
        Assert.Equal(historyBefore, intent.History.Count);
    }

    [Fact]
    public void Satisfying_a_goal_with_its_own_download_in_flight_is_a_no_op()
    {
        // Arrange — this goal is downloading its own release; the pack landing elsewhere must not
        // yank it out from under an attempt that is still open.
        var intent = Searching();
        intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);

        // Act
        var satisfied = intent.MarkSatisfiedExternally(Guid.NewGuid(), T0);

        // Assert
        Assert.False(satisfied);
        Assert.Equal(IntentState.Downloading, intent.State);
    }

    [Fact]
    public void An_attempt_records_the_units_it_claims()
    {
        // Arrange
        var intent = Searching();
        var units = new[] { Guid.NewGuid(), Guid.NewGuid() };

        // Act — a pack claims every episode it covers, and duplicates collapse.
        var attempt = intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);
        attempt.ClaimUnits([.. units, units[0]]);

        // Assert
        Assert.Equal(units.Order(), attempt.Units.Select(u => u.UnitId).Order());
        Assert.All(attempt.Units, u => Assert.Equal(attempt.Id, u.AttemptId));
    }

    [Fact]
    public void An_attempt_keeps_what_the_release_was_when_it_was_chosen()
    {
        // Arrange
        var intent = Searching();

        // Act
        var attempt = intent.SelectCandidate(
            Guid.NewGuid(), "g1", "magnet:g1", T0, new AttemptRelease("Film.2013.1080p", "Example Indexer", 304, -2));

        // Assert — a negative figure from an indexer is not a count.
        Assert.Equal("Film.2013.1080p", attempt.ReleaseTitle);
        Assert.Equal("Example Indexer", attempt.IndexerName);
        Assert.Equal(304, attempt.Seeders);
        Assert.Equal(0, attempt.Leechers);
    }

    [Fact]
    public void Retrying_an_exhausted_goal_reopens_it_with_a_fresh_budget()
    {
        // Arrange — a goal that spent its only attempt.
        var intent = Searching(maxAttempts: 1);
        intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);
        intent.MarkDownloadFailed("boom", T0);
        Assert.Equal(IntentState.Exhausted, intent.State);

        // Act
        var reopened = intent.RetryNow(T0, extraAttempts: 3);

        // Assert — searchable again, the spent attempt kept, three more to spend.
        Assert.True(reopened);
        Assert.Equal(IntentState.Searching, intent.State);
        Assert.True(intent.CanSelectCandidate);
        Assert.Equal(1, intent.AttemptCount);
        Assert.Equal(4, intent.MaxAttempts);
        Assert.Equal("ManualRetry", intent.History[^1].Trigger);
    }

    [Fact]
    public void Retrying_a_goal_that_is_already_searching_changes_nothing()
    {
        var intent = Searching();
        var historyBefore = intent.History.Count;

        var reopened = intent.RetryNow(T0, extraAttempts: 3);

        Assert.False(reopened);
        Assert.Equal(IntentState.Searching, intent.State);
        Assert.Equal(historyBefore, intent.History.Count);
    }

    [Theory]
    [InlineData(IntentState.Downloading)]
    [InlineData(IntentState.Importing)]
    [InlineData(IntentState.Available)]
    public void A_goal_that_is_busy_or_met_cannot_be_retried(IntentState state)
    {
        var intent = Searching();
        intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);
        if (state is IntentState.Importing or IntentState.Available)
        {
            intent.MarkDownloadCompleted(T0);
        }

        if (state is IntentState.Available)
        {
            intent.MarkImported(T0);
        }

        Assert.Equal(state, intent.State);
        Assert.Throws<InvalidOperationException>(() => intent.RetryNow(T0, extraAttempts: 3));
        Assert.Equal(state, intent.State);
    }

    [Fact]
    public void Cancelling_a_downloading_goal_closes_its_attempt_and_ends_it()
    {
        // Arrange
        var intent = Searching();
        var attempt = intent.SelectCandidate(Guid.NewGuid(), "g1", "magnet:g1", T0);

        // Act
        var cancelled = intent.Cancel(T0, "removed");

        // Assert — terminal, never offered a candidate again, and the attempt says why it stopped.
        Assert.True(cancelled);
        Assert.Equal(IntentState.Cancelled, intent.State);
        Assert.False(intent.CanSelectCandidate);
        Assert.True(intent.HasGivenUp);
        Assert.Equal(AttemptState.FailedDownload, attempt.State);
        Assert.Equal("removed", attempt.FailureReason);
        Assert.Throws<InvalidOperationException>(() => intent.RetryNow(T0, extraAttempts: 3));
    }

    [Fact]
    public void Cancelling_twice_changes_nothing_the_second_time()
    {
        var intent = Searching();
        intent.Cancel(T0, "removed");
        var historyBefore = intent.History.Count;

        Assert.False(intent.Cancel(T0, "removed"));
        Assert.Equal(historyBefore, intent.History.Count);
    }
}
