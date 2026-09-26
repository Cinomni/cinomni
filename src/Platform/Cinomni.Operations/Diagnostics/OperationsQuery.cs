using Cinomni.Operations.Persistence;
using Cinomni.Operations.Retention;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Operations.Diagnostics;

/// <summary>
/// Read-only projections behind <c>/api/operations</c>. The routes themselves live in the Host,
/// which is where a platform-wide HTTP surface belongs: every module references this project, so an
/// ASP.NET dependency here would have reached the whole backend. Keeping the queries separate also
/// lets them be proven against a real database without a routing pipeline.
/// <para>
/// Every read is a projection over the <c>operations</c> schema this platform kernel owns; nothing
/// here touches a module's own schema, and nothing here writes.
/// </para>
/// </summary>
public sealed class OperationsQuery(
    OperationsDbContext dbContext,
    QueueDepthSampler queueDepthSampler,
    ILiveOptions<RetentionOptions> retention)
{
    /// <summary>
    /// The outbox backlog and command-queue depth. Reuses <see cref="QueueDepthSampler"/> — the same
    /// snapshot the metrics gauges publish — rather than running a second query over the same tables.
    /// </summary>
    public Task<QueueDepthSnapshot> GetQueueSnapshotAsync(CancellationToken cancellationToken = default) =>
        queueDepthSampler.SampleAsync(cancellationToken);

    /// <summary>Every scheduled job as currently persisted. Ordered by next due, soonest first.</summary>
    public async Task<IReadOnlyList<ScheduledJobSummary>> GetJobsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.ScheduledJobs
            .AsNoTracking()
            .OrderBy(job => job.NextDue)
            .ThenBy(job => job.Name)
            .Select(job => new ScheduledJobSummary(
                job.Name,
                job.CommandType,
                job.IntervalSeconds,
                job.LastRun,
                job.NextDue,
                job.Enabled))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Failed commands, newest attempt first, capped server-side to <see cref="FailedCommandsPaging.MaxLimit"/>
    /// regardless of what the caller asked for. Never selects <c>Payload</c>: a queued command's payload can
    /// carry infrastructure detail (rule: never serialize it to an operator console).
    /// </summary>
    public async Task<IReadOnlyList<FailedCommandSummary>> GetFailedCommandsAsync(
        int? limit, CancellationToken cancellationToken = default)
    {
        var capped = FailedCommandsPaging.Clamp(limit);

        return await dbContext.Commands
            .AsNoTracking()
            .Where(command => command.State == CommandState.Failed)
            .OrderByDescending(command => command.LastAttemptAt ?? command.QueuedAt)
            .Take(capped)
            .Select(command => new FailedCommandSummary(
                command.Id,
                command.CommandType,
                command.Attempts,
                command.MaxAttempts,
                command.LastAttemptAt,
                command.Error))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The retention windows currently in effect, through the same store-backed read path the settings
    /// panel edits — so this echoes a value a PUT just accepted rather than the frozen value the Host
    /// booted with. No database access of its own: <see cref="ILiveOptions{TOptions}"/> reads an
    /// in-process cache.
    /// </summary>
    public RetentionSummary GetRetention()
    {
        var current = retention.Current;
        return new RetentionSummary(
            current.OutboxRetention.TotalSeconds,
            current.CompletedCommandRetention.TotalSeconds,
            current.FailedCommandRetention.TotalSeconds,
            current.BatchSize,
            current.Interval.TotalSeconds);
    }
}

/// <summary>Bounds for <c>GET /api/operations/commands/failed</c>. The list was unbounded otherwise.</summary>
public static class FailedCommandsPaging
{
    /// <summary>Rows returned when the caller does not ask for a specific count.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Hard ceiling: a caller asking for more gets this instead. Never trust the query string.</summary>
    public const int MaxLimit = 200;

    /// <summary>Clamps a requested limit into <c>[1, <see cref="MaxLimit"/>]</c>.</summary>
    public static int Clamp(int? limit) => limit switch
    {
        null or < 1 => DefaultLimit,
        > MaxLimit => MaxLimit,
        _ => limit.Value,
    };
}

/// <summary>One scheduled job's definition and last-known run timestamps.</summary>
/// <param name="Name">Stable job identity.</param>
/// <param name="CommandType">Stable registered name of the command it enqueues on each tick.</param>
/// <param name="IntervalSeconds">How often the job is due.</param>
/// <param name="LastRun">
/// When the job last ran; null if it has never fired. Whether that run <b>succeeded</b> is not persisted
/// anywhere — it lives only as an OTLP metric tag — so this deliberately carries no outcome field.
/// </param>
/// <param name="NextDue">When the job is next due to run.</param>
/// <param name="Enabled">Whether the job is currently active.</param>
public sealed record ScheduledJobSummary(
    string Name,
    string CommandType,
    int IntervalSeconds,
    DateTimeOffset? LastRun,
    DateTimeOffset NextDue,
    bool Enabled);

/// <summary>
/// One failed command, without its payload. <see cref="Error"/> is the operator's evidence of what went
/// wrong; the payload that produced it is deliberately never part of this projection.
/// </summary>
public sealed record FailedCommandSummary(
    Guid Id,
    string CommandType,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset? LastAttemptAt,
    string? Error);

/// <summary>The retention windows as configured, in seconds, plus the shared purge batch size.</summary>
public sealed record RetentionSummary(
    double OutboxRetentionSeconds,
    double CompletedCommandRetentionSeconds,
    double FailedCommandRetentionSeconds,
    int BatchSize,
    double IntervalSeconds);
