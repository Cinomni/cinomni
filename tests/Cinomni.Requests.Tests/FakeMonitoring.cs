using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Contracts;

namespace Cinomni.Requests.Tests;

/// <summary>
/// Stands in for Monitoring: remembers the root of each work and every policy applied to it, so a test
/// can seed a title that is catalogued but unwatched and see whether fulfilling a request switches it on.
/// </summary>
internal sealed class FakeMonitoring : IMonitoringQuery, IMonitoringCommands
{
    private readonly Dictionary<Guid, MonitoredTargetSummary> _roots = [];

    public List<(Guid WorkId, MonitoringMode Mode)> Applied { get; } = [];

    public void SeedRoot(Guid workId, bool monitored) =>
        _roots[workId] = Root(workId, monitored ? MonitoringMode.All : MonitoringMode.None);

    public Task<Result<MonitoredTargetId>> ApplyMonitoringPolicyAsync(
        WorkId workId, MonitoringMode mode, CancellationToken cancellationToken = default)
    {
        Applied.Add((workId.Value, mode));
        var root = Root(workId.Value, mode);
        _roots[workId.Value] = root;
        return Task.FromResult(Result<MonitoredTargetId>.Success(root.Id));
    }

    public Task<MonitoredTargetSummary?> GetByWorkAsync(WorkId workId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_roots.GetValueOrDefault(workId.Value));

    public Task<bool> IsMonitoredAsync(WorkId workId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_roots.TryGetValue(workId.Value, out var root) && root.Monitored);

    public Task<Result> SetTargetMonitoredAsync(MonitoredTargetId targetId, bool monitored, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> SetSubtreeMonitoredAsync(MonitoredTargetId targetId, bool monitored, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> ClearSearchCooldownAsync(WorkId workId, int? seasonNumber = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> ClearTargetSearchCooldownAsync(MonitoredTargetId targetId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MonitoredTargetSummary>> ListByWorkAsync(WorkId workId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MonitoredTargetSummary>> ListSeasonsAsync(WorkId workId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MonitoredTargetSummary>> ListMissingAsync(
        int limit = MonitoringPaging.DefaultPageSize, int offset = 0, WorkId? workId = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MonitoredTargetSummary>> ListAsync(
        int limit = MonitoringPaging.DefaultPageSize, int offset = 0, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MonitoredTargetSummary>> ResolveTargetsForUnitsAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    private static MonitoredTargetSummary Root(Guid workId, MonitoringMode mode) => new(
        new MonitoredTargetId(Guid.CreateVersion7()),
        new WorkId(workId),
        TargetKind.Movie,
        Monitored: mode != MonitoringMode.None,
        mode,
        IsMissing: true,
        TargetRef: workId);
}
