using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Diagnostics;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Retention;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Cinomni.Operations;

/// <summary>
/// Registers the platform kernel (Operations): the transactional outbox and its relay,
/// the recoverable command queue and its worker, the job scheduler, and the retention purge that
/// keeps the kernel's own tables bounded.
/// </summary>
public static class OperationsModule
{
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="connectionString">The PostgreSQL connection shared by every module context.</param>
    /// <param name="configureRetention">
    /// Applied to the <see cref="RetentionOptions"/> defaults before they are validated and frozen.
    /// Read at registration time because the purge cadence becomes a scheduled-job interval, exactly
    /// like the metadata sweep. An inconsistent configuration throws here, at startup.
    /// </param>
    /// <param name="configuration">
    /// The root configuration, needed by the settings store's precedence resolver to tell an
    /// environment/command-line pin apart from a file default (<see cref="SettingsPrecedence"/>). Null
    /// in a test composition that never registers an <see cref="ILiveOptions{TOptions}"/> falls back to
    /// an empty configuration, so every existing caller keeps compiling unchanged.
    /// </param>
    public static IServiceCollection AddOperations(
        this IServiceCollection services,
        string connectionString,
        Action<RetentionOptions>? configureRetention = null,
        IConfiguration? configuration = null)
    {
        // One connection per scope, shared by every module context: this is what lets a
        // module write and its outbox events commit in a single transaction. Uses classic
        // ADO connection pooling (keyed by the connection string).
        services.AddScoped(_ => new NpgsqlConnection(connectionString));

        services.AddSingleton(configuration ?? new ConfigurationBuilder().Build());

        services.AddDbContext<OperationsDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlConnection>())
            .UseSnakeCaseNamingConvention());

        // The OperationsDbContext owns the unit-of-work transaction; module contexts enlist
        // into it (see AddModuleDbContext).
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // The platform clock. Production always gets the system one; a test that has to prove *when*
        // something runs replaces it and drives time itself, instead of sleeping and hoping.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IMessageTypeRegistry, MessageTypeRegistry>();
        services.AddSingleton<IMessageSerializer, MessageSerializer>();

        // Events: outbox + relay.
        services.AddScoped<IEventBus, OutboxEventBus>();
        services.AddScoped<IEventDispatcher, EventDispatcher>();
        services.AddScoped<OutboxRelay>();
        services.AddHostedService<OutboxHostedService>();

        // Commands: queue + worker.
        services.AddScoped<ICommandQueue, CommandQueue>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<CommandProcessor>();
        services.AddHostedService<CommandQueueHostedService>();

        // Scheduler.
        services.AddScoped<Scheduler>();
        services.AddHostedService<JobSchedulerHostedService>();

        // Backlog gauges. A hosted service, so it stays inert in a test service provider exactly like
        // the relay and the worker do; a test that wants a sample calls QueueDepthSampler directly.
        services.AddScoped<QueueDepthSampler>();
        services.AddHostedService<QueueDepthSamplerHostedService>();

        // The settings store's read path: one in-process cache every ILiveOptions<T> reads from, and
        // the drift poll that bounds staleness for a write this process did not make itself. Both are
        // inert until a module registers an ILiveOptions<T> — this increment registers none.
        services.AddSingleton<SettingsCache>();

        // Secret-setting encryption. A missing/invalid CINOMNI_SECRET_KEY produces a cipher that is
        // always unavailable rather than a startup failure: an installation that never stores a secret
        // setting must not be blocked by a missing key. Loaded once here, at composition time, so every
        // scope shares the same decision for the lifetime of the process.
        var masterKey = SecretMasterKey.TryLoadFromEnvironment(out var secretsUnavailableReason);
        services.AddSingleton(new SettingsSecretCipher(masterKey, secretsUnavailableReason));

        services.AddSingleton<SettingsRefreshWorker>();
        services.AddHostedService<SettingsRefreshHostedService>();

        // The settings store's write path (validation gates + the versioned transaction + the
        // post-commit publish). No SettingDefinition is registered by this call — a module opts a key
        // into the catalogue with AddSettingDefinition/AddSettingsCheck when it wires that key, which no
        // module does yet: this increment is the machinery, not an editable key.
        services.AddScoped<ISettingsAdministration, SettingsAdministration>();

        // The read-only /api/operations surface: projections over the spine's own tables and the
        // frozen retention options, for an operator console that has no shell access.
        services.AddScoped<OperationsQuery>();

        // The settings store's read-only listing (GET /api/operations/settings): a projection over
        // every registered SettingDefinition plus the stored rows' versions, for the console page that
        // edits them.
        services.AddScoped<SettingsQuery>();

        // Retention: the kernel's own outbox/command history is the fastest-growing data in the
        // installation, so the platform purges it on the same scheduler every module uses.
        var retention = new RetentionOptions();
        configureRetention?.Invoke(retention);
        retention.Validate();
        services.AddSingleton(retention);

        // The settings store's catalogue for RetentionOptions: all five of its properties already carry
        // a settings key and a Check() rule, so the binder rebuilds the whole type from the store rather
        // than closing over anything non-settable. AddScheduledJob below still captures retention.Interval
        // once, at composition — a value stored through the settings store is persisted and audited, but
        // the purge cadence itself only picks it up after a restart (no ScheduledJobRegistration is
        // reload-aware; see docs follow-up).
        RetentionOptions BindRetention(SettingsView view) => new()
        {
            OutboxRetention = view.GetTimeSpan(RetentionSettingDefinitions.OutboxRetention) ?? retention.OutboxRetention,
            CompletedCommandRetention =
                view.GetTimeSpan(RetentionSettingDefinitions.CompletedCommandRetention) ?? retention.CompletedCommandRetention,
            FailedCommandRetention =
                view.GetTimeSpan(RetentionSettingDefinitions.FailedCommandRetention) ?? retention.FailedCommandRetention,
            BatchSize = view.GetInt(RetentionSettingDefinitions.BatchSize) ?? retention.BatchSize,
            Interval = view.GetTimeSpan(RetentionSettingDefinitions.Interval) ?? retention.Interval,
        };

        services.AddSettingsCheck(RetentionSettingDefinitions.All, BindRetention, RetentionOptions.Check);
        services.AddLiveOptions(BindRetention);

        services.AddCommand<PurgeOperationsCommand>(OperationsCommandNames.PurgeRetention);
        services.AddScoped<ICommandHandler<PurgeOperationsCommand>, PurgeOperationsCommandHandler>();
        services.AddScheduledJob<PurgeOperationsCommand>(
            "operations.retention", OperationsCommandNames.PurgeRetention, retention.Interval);

        return services;
    }

    /// <summary>
    /// Registers a module's <see cref="DbContext"/> on the shared scoped connection and
    /// enlists it into the unit of work, so its writes can commit atomically with outbox
    /// events. Requires <see cref="AddOperations"/> to have registered the shared connection.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlConnection>())
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ITransactionEnlister, DbContextEnlister<TContext>>();
        return services;
    }

    /// <summary>
    /// Registers a domain event under a stable name so it can travel through the outbox.
    /// Every event that crosses a module boundary must be registered at startup.
    /// </summary>
    public static IServiceCollection AddIntegrationEvent<TEvent>(this IServiceCollection services, string name)
        where TEvent : IDomainEvent
    {
        services.AddSingleton(new MessageTypeRegistration(name, typeof(TEvent)));
        return services;
    }

    /// <summary>Registers a command under a stable name so it can be enqueued and executed.</summary>
    public static IServiceCollection AddCommand<TCommand>(this IServiceCollection services, string name)
        where TCommand : ICommand
    {
        services.AddSingleton(new MessageTypeRegistration(name, typeof(TCommand)));
        return services;
    }

    /// <summary>
    /// Registers a periodic job that enqueues <typeparamref name="TCommand"/> every
    /// <paramref name="interval"/>. The command must also be registered with
    /// <see cref="AddCommand{TCommand}"/> and have a handler.
    /// </summary>
    public static IServiceCollection AddScheduledJob<TCommand>(
        this IServiceCollection services,
        string jobName,
        string commandName,
        TimeSpan interval)
        where TCommand : ICommand, new()
    {
        services.AddSingleton(new ScheduledJobRegistration(jobName, commandName, interval, () => new TCommand()));
        return services;
    }

    /// <summary>Applies pending migrations for the operations schema (idempotent).</summary>
    public static async Task MigrateOperationsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Loads the settings store into memory once at startup, so every <see cref="ILiveOptions{TOptions}"/>
    /// consumer sees whatever <c>operations.setting</c> held at boot instead of starting empty and
    /// waiting for the first drift poll. Must run after <see cref="MigrateOperationsAsync"/> (the table
    /// must exist) and before the Host starts serving traffic.
    /// </summary>
    public static async Task LoadSettingsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<SettingsCache>();
        var cipher = scope.ServiceProvider.GetRequiredService<SettingsSecretCipher>();

        var snapshot = await SettingsSnapshotLoader.LoadAsync(
            dbContext, cipher, cache.Current.Generation + 1, cancellationToken);
        cache.Publish(snapshot);
    }

    /// <summary>
    /// Startup recovery: requeues commands left
    /// Running by a crash, and upserts the scheduled jobs from their registrations.
    /// </summary>
    public static async Task RecoverOperationsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        await processor.RecoverAsync(cancellationToken);

        var registrations = scope.ServiceProvider.GetServices<ScheduledJobRegistration>().ToList();
        var now = DateTimeOffset.UtcNow;

        // A row whose registration is gone — a job this build no longer has, or one a configuration
        // switch turned off — must stop claiming it is enabled and due. Nothing would enqueue it, and a
        // schedule table that asserts work is happening when none is, is worse than an absent row.
        // Disabling rather than deleting: re-registering the job re-enables it below with its history.
        var known = registrations.Select(registration => registration.Name).ToList();
        await dbContext.ScheduledJobs
            .Where(job => job.Enabled && !known.Contains(job.Name))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Enabled, false), cancellationToken);

        foreach (var registration in registrations)
        {
            var existing = await dbContext.ScheduledJobs.FindAsync([registration.Name], cancellationToken);
            if (existing is null)
            {
                dbContext.ScheduledJobs.Add(new ScheduledJob
                {
                    Name = registration.Name,
                    CommandType = registration.CommandName,
                    IntervalSeconds = (int)registration.Interval.TotalSeconds,
                    NextDue = now,
                    Enabled = true,
                });
            }
            else
            {
                // Keep LastRun/NextDue; only sync the definition.
                existing.CommandType = registration.CommandName;
                existing.IntervalSeconds = (int)registration.Interval.TotalSeconds;
                existing.Enabled = true;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
