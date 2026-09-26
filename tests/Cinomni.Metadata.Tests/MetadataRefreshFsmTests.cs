using Cinomni.Metadata.Persistence;

namespace Cinomni.Metadata.Tests;

/// <summary>Pure unit tests for the metadata-refresh finite-state machine (no database).</summary>
public sealed class MetadataRefreshFsmTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private static RefreshState New() => RefreshState.Create(Guid.NewGuid(), "tmdb", Now);

    [Fact]
    public void Create_starts_idle_and_should_refresh()
    {
        var state = New();

        Assert.Equal(MetadataRefreshStatus.Idle, state.Status);
        Assert.Equal(0, state.Attempts);
        Assert.True(state.ShouldRefresh(Now, Ttl));
    }

    [Fact]
    public void Happy_path_reaches_fresh_and_is_not_eligible_until_stale()
    {
        var state = New();
        var snapshotId = Guid.NewGuid();

        state.BeginRefresh(Now);
        Assert.Equal(MetadataRefreshStatus.Refreshing, state.Status);
        Assert.Equal(1, state.Attempts);
        Assert.False(state.ShouldRefresh(Now, Ttl)); // in progress

        state.MarkFresh(snapshotId, Now);
        Assert.Equal(MetadataRefreshStatus.Fresh, state.Status);
        Assert.Equal(snapshotId, state.SnapshotId);
        Assert.False(state.ShouldRefresh(Now, Ttl));                 // fresh within TTL
        Assert.True(state.ShouldRefresh(Now + Ttl, Ttl));            // stale past TTL
    }

    [Fact]
    public void A_success_resets_the_attempt_counter_so_failures_are_counted_consecutively()
    {
        var state = New();

        // A first attempt fails (backoff counts attempt 1)...
        state.BeginRefresh(Now);
        state.MarkFailed(Now.AddMinutes(5), Now);
        Assert.Equal(1, state.Attempts);

        // ...then a later attempt succeeds: the counter resets, so a future blip starts fresh.
        state.BeginRefresh(Now.AddMinutes(5));
        Assert.Equal(2, state.Attempts);
        state.MarkFresh(Guid.NewGuid(), Now.AddMinutes(5));
        Assert.Equal(0, state.Attempts);
    }

    [Fact]
    public void A_failure_backs_off_then_becomes_eligible_again()
    {
        var state = New();
        var nextEligible = Now.AddMinutes(30);

        state.BeginRefresh(Now);
        state.MarkFailed(nextEligible, Now);

        Assert.Equal(MetadataRefreshStatus.Failed, state.Status);
        Assert.Null(state.SnapshotId);
        Assert.False(state.ShouldRefresh(Now, Ttl));                 // still backing off
        Assert.True(state.ShouldRefresh(nextEligible, Ttl));         // backoff elapsed
    }

    [Fact]
    public void Illegal_transition_is_rejected_and_leaves_state_untouched()
    {
        var state = New(); // Idle

        Assert.Throws<InvalidOperationException>(() => state.MarkFresh(Guid.NewGuid(), Now));
        Assert.Equal(MetadataRefreshStatus.Idle, state.Status);
    }
}
