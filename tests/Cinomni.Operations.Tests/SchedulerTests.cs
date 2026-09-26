using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration test for the job scheduler against a real PostgreSQL instance: a due job
/// enqueues its command and its schedule advances — and a row this build has no registration for
/// stops claiming that it does.
/// </summary>
public sealed class SchedulerTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync("cinomni_test_scheduler", services =>
        {
            services.AddCommand<Tick>("test.tick");
            services.AddScheduledJob<Tick>("test.job", "test.tick", TimeSpan.FromMinutes(10));
        });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Due_job_enqueues_its_command_and_advances_the_schedule()
    {
        // Register a job that is already due.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            dbContext.ScheduledJobs.Add(new ScheduledJob
            {
                Name = "test.job",
                CommandType = "test.tick",
                IntervalSeconds = 600,
                NextDue = DateTimeOffset.UtcNow.AddSeconds(-1),
                Enabled = true,
            });
            await dbContext.SaveChangesAsync();
        }

        int fired;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<Scheduler>();
            fired = await scheduler.RunDueJobsAsync();
        }

        Assert.Equal(1, fired);

        await using (var verifyScope = _provider.CreateAsyncScope())
        {
            var dbContext = verifyScope.ServiceProvider.GetRequiredService<OperationsDbContext>();

            var command = await dbContext.Commands.AsNoTracking().SingleAsync();
            Assert.Equal("test.tick", command.CommandType);

            var job = await dbContext.ScheduledJobs.AsNoTracking().SingleAsync();
            Assert.NotNull(job.LastRun);
            Assert.True(job.NextDue > DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// A due row whose registration is gone enqueued nothing and advanced its schedule anyway, without
    /// a word — so the table went on asserting that work was happening for ever. It is now disabled on
    /// the spot, which is the honest state and the one startup recovery would have written.
    /// </summary>
    [Fact]
    public async Task A_due_job_this_build_does_not_register_is_disabled_rather_than_skipped_silently()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            dbContext.ScheduledJobs.Add(new ScheduledJob
            {
                Name = "test.retired-job",
                CommandType = "test.retired",
                IntervalSeconds = 600,
                NextDue = DateTimeOffset.UtcNow.AddSeconds(-1),
                Enabled = true,
            });
            await dbContext.SaveChangesAsync();
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Scheduler>().RunDueJobsAsync());
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

            Assert.Empty(await dbContext.Commands.AsNoTracking().ToListAsync());

            var job = await dbContext.ScheduledJobs.AsNoTracking()
                .SingleAsync(entity => entity.Name == "test.retired-job");
            Assert.False(job.Enabled);
        }
    }

    private sealed record Tick : ICommand;
}
