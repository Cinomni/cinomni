using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Persistence;

namespace Cinomni.Subtitles.Tests;

/// <summary>Pure unit tests for the SubtitleSearch finite-state machine (no database).</summary>
public sealed class SubtitleSearchFsmTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static SubtitleSearch NewSearch() =>
        SubtitleSearch.Create(Guid.NewGuid(), "en", forced: false, hearingImpaired: false, Now);

    private static readonly (string, string, int, bool, string, string)[] Candidates =
        [("fake", "Movie.2024", 90, false, "file-1", "Same film by title.")];

    [Fact]
    public void Create_starts_requested()
    {
        var search = NewSearch();

        Assert.Equal(SubtitleSearchState.Requested, search.State);
        Assert.Equal(0, search.Attempts);
    }

    [Fact]
    public void Happy_path_reaches_available()
    {
        var search = NewSearch();
        var subtitleId = Guid.NewGuid();

        search.BeginSearch(Now);
        Assert.Equal(1, search.Attempts);
        search.RecordCandidates(Candidates, Now);
        search.BeginDownload(Now);
        search.MarkAvailable(subtitleId, Now);

        Assert.Equal(SubtitleSearchState.Available, search.State);
        Assert.True(search.IsSatisfied);
        Assert.Equal(subtitleId, search.SubtitleAssetId);
        Assert.Single(search.Candidates);
    }

    [Fact]
    public void Not_found_can_be_reopened_by_an_adaptive_re_search()
    {
        var search = NewSearch();
        search.BeginSearch(Now);
        search.RecordCandidates([], Now);
        search.MarkNotFound(Now);
        Assert.Equal(SubtitleSearchState.NotFound, search.State);

        // Adaptive: NotFound → Searching again, bumping the attempt counter.
        search.BeginSearch(Now);
        Assert.Equal(SubtitleSearchState.Searching, search.State);
        Assert.Equal(2, search.Attempts);
    }

    [Fact]
    public void A_download_that_did_not_land_waits_like_a_search_that_found_nothing()
    {
        var search = NewSearch();
        search.BeginSearch(Now);
        search.RecordCandidates(Candidates, Now);
        search.BeginDownload(Now);

        search.MarkNotFound(Now);

        Assert.Equal(SubtitleSearchState.NotFound, search.State);
        search.BeginSearch(Now.AddHours(6));
        Assert.Equal(2, search.Attempts);
    }

    [Fact]
    public void A_landed_subtitle_is_synchronized_and_then_available()
    {
        var search = Downloading();
        var subtitleId = Guid.NewGuid();

        search.BeginSync(subtitleId, Now);
        Assert.Equal(SubtitleSearchState.Syncing, search.State);
        Assert.Equal(subtitleId, search.SubtitleAssetId);

        search.CompleteSync(Now);
        Assert.Equal(SubtitleSearchState.Available, search.State);
        Assert.True(search.IsSatisfied);
    }

    [Fact]
    public void An_available_subtitle_can_be_synchronized_again()
    {
        var search = Downloading();
        search.MarkAvailable(Guid.NewGuid(), Now);

        search.BeginResync(Now);

        Assert.Equal(SubtitleSearchState.Syncing, search.State);
        search.CompleteSync(Now);
        Assert.Equal(SubtitleSearchState.Available, search.State);
    }

    [Fact]
    public void A_synchronizing_search_is_not_reopened_searched_or_synchronized_twice()
    {
        var search = Downloading();
        search.BeginSync(Guid.NewGuid(), Now);

        Assert.Throws<InvalidOperationException>(() => search.BeginSearch(Now));
        Assert.Throws<InvalidOperationException>(() => search.BeginResync(Now));
        Assert.Throws<InvalidOperationException>(() => search.MarkNotFound(Now));
        Assert.Throws<InvalidOperationException>(() => search.MarkAvailable(Guid.NewGuid(), Now));
        Assert.Equal(SubtitleSearchState.Syncing, search.State);
        Assert.Equal(1, search.Attempts);
    }

    [Fact]
    public void Sync_transitions_from_the_wrong_state_are_rejected()
    {
        var requested = NewSearch();
        Assert.Throws<InvalidOperationException>(() => requested.BeginSync(Guid.NewGuid(), Now));
        Assert.Throws<InvalidOperationException>(() => requested.CompleteSync(Now));
        Assert.Throws<InvalidOperationException>(() => requested.BeginResync(Now));
        Assert.Equal(SubtitleSearchState.Requested, requested.State);

        var available = Downloading();
        available.MarkAvailable(Guid.NewGuid(), Now);
        Assert.Throws<InvalidOperationException>(() => available.CompleteSync(Now));
        Assert.Throws<InvalidOperationException>(() => available.BeginSync(Guid.NewGuid(), Now));
        Assert.Equal(SubtitleSearchState.Available, available.State);
    }

    [Fact]
    public void A_language_found_already_present_has_no_download_to_synchronize()
    {
        var search = NewSearch();
        search.MarkAlreadyPresent(Now);

        Assert.Throws<InvalidOperationException>(() => search.BeginResync(Now));
        Assert.Equal(SubtitleSearchState.Available, search.State);
    }

    private static SubtitleSearch Downloading()
    {
        var search = NewSearch();
        search.BeginSearch(Now);
        search.RecordCandidates(Candidates, Now);
        search.BeginDownload(Now);
        return search;
    }

    [Fact]
    public void Illegal_transition_is_rejected_and_leaves_state_untouched()
    {
        var search = NewSearch(); // Requested

        Assert.Throws<InvalidOperationException>(() => search.MarkAvailable(Guid.NewGuid(), Now));
        Assert.Equal(SubtitleSearchState.Requested, search.State);
    }
}
