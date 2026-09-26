using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration tests for the unit of work against a real PostgreSQL instance: a shared
/// transaction commits or rolls back the outbox write as a whole.
/// </summary>
public sealed class UnitOfWorkTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_uow",
            services =>
            {
                services.AddIntegrationEvent<Marker>("test.marker");
                services.AddModuleDbContext<ModuleDbContext>();
            });

        // The module context stands in for a real module's tables; only Operations has migrations.
        await using var scope = _provider.CreateAsyncScope();
        var moduleDb = scope.ServiceProvider.GetRequiredService<ModuleDbContext>();
        await moduleDb.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS widgets (
                id uuid PRIMARY KEY,
                name text NOT NULL,
                CONSTRAINT ux_widgets_name UNIQUE (name)
            )
            """);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Commit_persists_the_outbox_write()
    {
        await using var scope = _provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();

        await unitOfWork.ExecuteAsync(token => eventBus.PublishAsync(new Marker(Guid.NewGuid()), token));

        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(1, await dbContext.Outbox.CountAsync());
    }

    [Fact]
    public async Task Rollback_discards_the_outbox_write_on_failure()
    {
        await using var scope = _provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteAsync(async token =>
            {
                await eventBus.PublishAsync(new Marker(Guid.NewGuid()), token);
                throw new InvalidOperationException("boom");
            }));

        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Outbox.CountAsync());
    }

    [Fact]
    public async Task An_enlisted_module_context_is_usable_again_once_the_unit_of_work_completes()
    {
        await using var scope = _provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var moduleDb = scope.ServiceProvider.GetRequiredService<ModuleDbContext>();

        // Several units of work in ONE scope is not an edge case: it is what a command batch is. The
        // outbox relay routinely queues several commands from a single event, and the worker then runs
        // all of them in one scope.
        for (var round = 1; round <= 3; round++)
        {
            await unitOfWork.ExecuteAsync(token => eventBus.PublishAsync(new Marker(Guid.NewGuid()), token));

            // Enlisting is not self-reversing. Left attached, the module context binds its next
            // statement to a transaction that has already finished, and the command that issued it
            // fails with "Transaction is already completed" — a command handler that merely reads its
            // own tables before opening its unit of work.
            Assert.Null(moduleDb.Database.CurrentTransaction);
            var probe = await moduleDb.Database.SqlQuery<int>($"""SELECT {round} AS "Value" """).SingleAsync();
            Assert.Equal(round, probe);
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(3, await dbContext.Outbox.CountAsync());
    }

    [Fact]
    public async Task A_failed_unit_of_work_does_not_leak_its_writes_into_the_next_one()
    {
        // Arrange — ONE scope, therefore ONE module context, running several units of work: exactly
        // what CommandQueueHostedService does with a claimed batch of up to 20 commands.
        await using var scope = _provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var moduleDb = scope.ServiceProvider.GetRequiredService<ModuleDbContext>();

        await unitOfWork.ExecuteAsync(async token =>
        {
            moduleDb.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "shared" });
            await moduleDb.SaveChangesAsync(token);
        });

        // Act — the second command writes a duplicate and fails on the unique index; the third is an
        // innocent bystander that only writes its own row.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            unitOfWork.ExecuteAsync(async token =>
            {
                moduleDb.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "shared" });
                await moduleDb.SaveChangesAsync(token);
            }));

        await unitOfWork.ExecuteAsync(async token =>
        {
            moduleDb.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "innocent" });
            await moduleDb.SaveChangesAsync(token);
        });

        // Assert — the rolled-back duplicate must not have been re-issued by the third unit of work
        // (which would have failed it too, and every command after it in the batch).
        var names = await moduleDb.Widgets.AsNoTracking().Select(w => w.Name).OrderBy(n => n).ToListAsync();
        Assert.Equal(["innocent", "shared"], names);
    }

    [Fact]
    public async Task A_failed_unit_of_work_does_not_leave_its_modifications_readable_by_the_next_one()
    {
        // Arrange — a committed row the next command will read back, the way a "was it already
        // available?" guard reads its aggregate before deciding whether to publish a first-transition
        // event.
        await using var scope = _provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var moduleDb = scope.ServiceProvider.GetRequiredService<ModuleDbContext>();
        var id = Guid.NewGuid();

        await unitOfWork.ExecuteAsync(async token =>
        {
            moduleDb.Widgets.Add(new Widget { Id = id, Name = "before" });
            await moduleDb.SaveChangesAsync(token);
        });

        // Act — a unit of work that mutates the aggregate and then rolls back.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteAsync(async token =>
            {
                var tracked = await moduleDb.Widgets.SingleAsync(w => w.Id == id, token);
                tracked.Name = "after";
                await moduleDb.SaveChangesAsync(token);
                throw new InvalidOperationException("boom");
            }));

        // Assert — the next command in the batch must see what the database holds, not the value the
        // rollback undid. Identity resolution otherwise hands it the stale tracked instance.
        var reloaded = await moduleDb.Widgets.SingleAsync(w => w.Id == id);
        Assert.Equal("before", reloaded.Name);
    }

    [Fact]
    public async Task A_successful_unit_of_work_keeps_tracking_the_entities_it_saved()
    {
        // The failure path clears the change tracker; the success path must NOT, because callers
        // legitimately go on using the aggregate they just committed (a second unit of work in the
        // same command frequently updates it again).
        await using var scope = _provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var moduleDb = scope.ServiceProvider.GetRequiredService<ModuleDbContext>();
        var widget = new Widget { Id = Guid.NewGuid(), Name = "tracked" };

        await unitOfWork.ExecuteAsync(async token =>
        {
            moduleDb.Widgets.Add(widget);
            await moduleDb.SaveChangesAsync(token);
        });

        Assert.Equal(EntityState.Unchanged, moduleDb.Entry(widget).State);

        // Mutating the still-tracked instance and saving again must reach the row.
        widget.Name = "tracked-and-renamed";
        await unitOfWork.ExecuteAsync(moduleDb.SaveChangesAsync);

        var stored = await moduleDb.Widgets.AsNoTracking().SingleAsync(w => w.Id == widget.Id);
        Assert.Equal("tracked-and-renamed", stored.Name);
    }

    private sealed record Marker(Guid Id) : DomainEvent
    {
        public override string IdempotencyKey => $"marker:{Id}";
    }
}

/// <summary>A stand-in for a module's own table, created by the test host rather than a migration.</summary>
internal sealed class Widget
{
    public Guid Id { get; init; }

    public required string Name { get; set; }
}

/// <summary>
/// A stand-in for a module's context: one table is enough, because what is under test is the
/// enlistment lifecycle on the shared connection rather than any mapping.
/// </summary>
internal sealed class ModuleDbContext(DbContextOptions<ModuleDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Widget>().ToTable("widgets").HasIndex(w => w.Name).IsUnique();
}
