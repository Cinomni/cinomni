using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// The availability half of <see cref="MonitoringBrowse"/>: a member with nothing visible must not be
/// able to force a scan of the whole <c>monitored_targets</c> table on every request. Fakes rather than
/// PostgreSQL — the loop's stopping condition is deterministic logic, and pinning it against ten thousand
/// real rows per test would make the suite the thing that is slow, not the code under test.
/// </summary>
public sealed class MonitoringBrowseScanCapTests
{
    private static readonly Viewer Member = new(Uuid7.New(), IsAdministrator: false);
    private static readonly Viewer Administrator = new(Uuid7.New(), IsAdministrator: true);

    [Fact]
    public async Task A_member_with_nothing_visible_scans_at_most_the_batch_cap_not_the_whole_table()
    {
        // Comfortably more rows than the cap allows scanning, so a loop that ignored MaxScanBatches would
        // still be going when this assertion runs.
        var totalRows = (MonitoringBrowse.MaxScanBatches + 5) * MonitoringBrowse.ScanBatchSize;
        var query = new FakeMonitoringQuery(totalRows);
        var access = new FakeContentAccess(seesEverything: false);
        var browse = new MonitoringBrowse(query, access);

        var page = await browse.ListAsync(Member, limit: 100, offset: 0);

        Assert.Empty(page);
        Assert.Equal(MonitoringBrowse.MaxScanBatches, query.ListCalls);
    }

    [Fact]
    public async Task The_missing_listing_carries_the_same_cap_for_a_member_with_nothing_visible()
    {
        var totalRows = (MonitoringBrowse.MaxScanBatches + 5) * MonitoringBrowse.ScanBatchSize;
        var query = new FakeMonitoringQuery(totalRows);
        var access = new FakeContentAccess(seesEverything: false);
        var browse = new MonitoringBrowse(query, access);

        var page = await browse.ListMissingAsync(Member, limit: 100, workId: null);

        Assert.Empty(page);
        Assert.Equal(MonitoringBrowse.MaxScanBatches, query.ListMissingCalls);
    }

    [Fact]
    public async Task A_member_whose_visible_rows_sit_past_the_cap_gets_a_short_page_rather_than_a_hang()
    {
        // Nothing is visible in the first MaxScanBatches batches; the cap must still stop the loop even
        // though rows the member could see genuinely exist further down the table.
        var totalRows = (MonitoringBrowse.MaxScanBatches + 5) * MonitoringBrowse.ScanBatchSize;
        var visibleWorkId = Guid.NewGuid();
        var query = new FakeMonitoringQuery(totalRows, visibleWorkId, visibleAtRowIndex: totalRows - 1);
        var access = new FakeContentAccess(seesEverything: false, visibleWorkId);
        var browse = new MonitoringBrowse(query, access);

        var page = await browse.ListAsync(Member, limit: 100, offset: 0);

        Assert.Empty(page);
        Assert.Equal(MonitoringBrowse.MaxScanBatches, query.ListCalls);
    }

    [Fact]
    public async Task An_administrators_unscoped_listing_makes_exactly_one_call_regardless_of_the_cap()
    {
        var totalRows = (MonitoringBrowse.MaxScanBatches + 10) * MonitoringBrowse.ScanBatchSize;
        var query = new FakeMonitoringQuery(totalRows);
        var access = new FakeContentAccess(seesEverything: false);
        var browse = new MonitoringBrowse(query, access);

        await browse.ListAsync(Administrator, limit: 100, offset: 0);

        Assert.Equal(1, query.ListCalls);
        Assert.Equal(0, access.FilterCalls);
    }

    [Fact]
    public void ScanBatchSize_never_exceeds_MonitoringPaging_MaxPageSize()
    {
        // MonitoringBrowse's static constructor is the guard that actually enforces this at runtime (it
        // throws the instant the type is first touched if the invariant breaks); this pins the value
        // itself, so a reviewer changing either constant sees a failure here too rather than only in
        // whichever test happens to construct MonitoringBrowse first.
        Assert.True(MonitoringBrowse.ScanBatchSize <= MonitoringPaging.MaxPageSize);
    }

    /// <summary>Serves a fixed number of rows, optionally one of which belongs to a known work id.</summary>
    private sealed class FakeMonitoringQuery(
        int totalRows, Guid? specialWorkId = null, int? visibleAtRowIndex = null) : IMonitoringQuery
    {
        public int ListCalls { get; private set; }

        public int ListMissingCalls { get; private set; }

        public Task<bool> IsMonitoredAsync(WorkId workId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MonitoredTargetSummary?> GetByWorkAsync(WorkId workId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MonitoredTargetSummary>> ListByWorkAsync(WorkId workId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MonitoredTargetSummary>> ListSeasonsAsync(WorkId workId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MonitoredTargetSummary>> ListMissingAsync(
            int limit = MonitoringPaging.DefaultPageSize,
            int offset = 0,
            WorkId? workId = null,
            CancellationToken cancellationToken = default)
        {
            ListMissingCalls++;
            return Task.FromResult(Batch(limit, offset));
        }

        public Task<IReadOnlyList<MonitoredTargetSummary>> ListAsync(
            int limit = MonitoringPaging.DefaultPageSize,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(Batch(limit, offset));
        }

        public Task<IReadOnlyList<MonitoredTargetSummary>> ResolveTargetsForUnitsAsync(
            IReadOnlyList<Guid> unitIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private IReadOnlyList<MonitoredTargetSummary> Batch(int limit, int offset)
        {
            var remaining = Math.Max(0, totalRows - offset);
            var count = Math.Min(limit, remaining);

            return Enumerable.Range(offset, count)
                .Select(rowIndex => new MonitoredTargetSummary(
                    new MonitoredTargetId(Guid.NewGuid()),
                    new WorkId(rowIndex == visibleAtRowIndex && specialWorkId is { } id ? id : Guid.NewGuid()),
                    TargetKind.Movie,
                    Monitored: true,
                    MonitoringMode.All,
                    IsMissing: true))
                .ToList();
        }
    }

    /// <summary>Sees nothing, or sees exactly the given set of work ids, never the whole table.</summary>
    private sealed class FakeContentAccess(bool seesEverything, params Guid[] visibleWorkIds) : IContentAccess
    {
        public int FilterCalls { get; private set; }

        public Task<IReadOnlyList<CollectionId>> VisibleCollectionsAsync(
            Viewer viewer, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CanSeeWorkAsync(Viewer viewer, Guid workId, CancellationToken cancellationToken = default) =>
            Task.FromResult(seesEverything || visibleWorkIds.Contains(workId));

        public Task<IReadOnlySet<Guid>> FilterWorksAsync(
            Viewer viewer, IReadOnlyCollection<Guid> workIds, CancellationToken cancellationToken = default)
        {
            FilterCalls++;
            IReadOnlySet<Guid> visible = seesEverything
                ? workIds.ToHashSet()
                : workIds.Where(id => visibleWorkIds.Contains(id)).ToHashSet();
            return Task.FromResult(visible);
        }
    }
}
