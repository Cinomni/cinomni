using System.Diagnostics;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Messaging;

/// <summary>A periodic job registered at startup: enqueues <c>CommandName</c> every <c>Interval</c>.</summary>
public sealed record ScheduledJobRegistration(
    string Name,
    string CommandName,
    TimeSpan Interval,
    Func<ICommand> CommandFactory);

/// <summary>
/// Core of the scheduler: claims the due jobs (<c>FOR UPDATE SKIP LOCKED</c>), enqueues each
/// job's command (keyed to that tick so a run is never enqueued twice), and advances
/// <c>last_run</c>/<c>next_due</c> — all in one transaction. Exposed as a callable unit so
/// tests can drive it deterministically. Schedule state lives in the DB, so it survives
/// restarts.
/// </summary>
public sealed class Scheduler(
    OperationsDbContext dbContext,
    ICommandQueue commandQueue,
    IEnumerable<ScheduledJobRegistration> registrations,
    ILogger<Scheduler> logger)
{
    private readonly IReadOnlyDictionary<string, ScheduledJobRegistration> _byName =
        registrations.ToDictionary(registration => registration.Name);

    /// <summary>Enqueues the commands for all currently-due jobs. Returns how many fired.</summary>
    public async Task<int> RunDueJobsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var due = await dbContext.ScheduledJobs
            .FromSql(
                $"""
                 SELECT * FROM operations.scheduled_job
                 WHERE enabled = true AND next_due <= {now}
                 ORDER BY next_due
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        // A scheduled tick is the ROOT of its own trace, deliberately and permanently. Trace context
        // flows forward through the outbox and the command queue, so a job that inherited an ambient
        // context — or, worse, its own previous tick — would chain a periodic sweep into a single trace
        // that grows for the lifetime of the installation and is useless to everybody. One tick, one
        // trace, and whatever that tick causes hangs off it. Do not "fix" this by propagating into it.
        var ambient = Activity.Current;
        Activity.Current = null;

        try
        {
            foreach (var job in due)
            {
                using var activity = CinomniTelemetry.Source.StartActivity(
                    $"job {job.Name}", ActivityKind.Internal);
                activity?.SetTag(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Operations);
                activity?.SetTag(CinomniTelemetry.Tags.JobName, job.Name);

                await FireAsync(job, now, activity, cancellationToken);
            }
        }
        finally
        {
            Activity.Current = ambient;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return due.Count;
    }

    /// <summary>Enqueues one due job's command and advances its schedule.</summary>
    private async Task FireAsync(
        ScheduledJob job,
        DateTimeOffset now,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        if (_byName.TryGetValue(job.Name, out var registration))
        {
            var idempotencyKey = $"{job.Name}:{job.NextDue:O}";
            var enqueued = await commandQueue.EnqueueAsync(
                registration.CommandFactory(), idempotencyKey, cancellationToken);

            // A tick whose key was already spent fired without producing work. Recorded as its own
            // outcome rather than as a success, because an operator watching "the sweep is running"
            // would otherwise be watching a number that says nothing.
            var outcome = enqueued
                ? CinomniTelemetry.Outcomes.Completed
                : CinomniTelemetry.Outcomes.Dropped;
            activity?.SetTag(CinomniTelemetry.Tags.Outcome, outcome);
            OperationsMetrics.RecordJobRun(job.Name, outcome);
        }
        else
        {
            // A row this build has no registration for: the schedule still advances, but nothing
            // runs. Startup recovery disables such rows, so seeing this means one appeared after
            // it — and a silent skip on a table that claims a job is enabled and due is exactly
            // the misleading health signal that must never be silent.
            logger.LogWarning(
                "Scheduled job '{JobName}' is enabled and due, but no module in this build registers "
                + "it. Nothing was enqueued and its schedule was advanced.",
                job.Name);

            activity?.SetTag(CinomniTelemetry.Tags.Outcome, CinomniTelemetry.Outcomes.Failed);
            activity?.SetStatus(ActivityStatusCode.Error);
            OperationsMetrics.RecordJobRun(job.Name, CinomniTelemetry.Outcomes.Failed);

            job.Enabled = false;
        }

        job.LastRun = now;
        job.NextDue = now + TimeSpan.FromSeconds(job.IntervalSeconds);
    }
}

/// <summary>Runs the <see cref="Scheduler"/> on a fixed tick.</summary>
public sealed class JobSchedulerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<JobSchedulerHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<Scheduler>();
                await scheduler.RunDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job scheduler tick failed; will retry.");
            }

            await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
