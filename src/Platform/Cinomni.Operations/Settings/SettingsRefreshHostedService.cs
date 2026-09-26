using Cinomni.Operations.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Settings;

/// <summary>
/// Runs the drift check: a cheap fingerprint, and a full (still small) table reload only when it
/// moved. A plain class rather than the hosted service itself, so a test can drive one poll
/// deterministically — hosted services do not start in the test service provider.
/// <para>
/// Registered as a singleton: <see cref="_lastFingerprint"/> must persist across ticks, unlike
/// <c>Scheduler</c> or <c>OutboxRelay</c>, whose per-tick state is rebuilt fresh from a new scope.
/// </para>
/// </summary>
public sealed class SettingsRefreshWorker(SettingsCache cache, SettingsSecretCipher cipher)
{
    /// <summary>
    /// Null until the first poll. The very first tick only baselines the fingerprint rather than
    /// reloading: the startup loader already read this same table moments before hosted services
    /// started, so treating "no prior fingerprint" as "changed" would force one redundant (harmless,
    /// but needless) reload on every process start.
    /// </summary>
    private SettingsFingerprint? _lastFingerprint;

    /// <summary>Runs one poll. Returns true when a change was detected and the cache was reloaded.</summary>
    public async Task<bool> PollOnceAsync(OperationsDbContext dbContext, CancellationToken cancellationToken)
    {
        var fingerprint = await SettingsFingerprintQuery.ComputeAsync(dbContext, cancellationToken);
        if (fingerprint == _lastFingerprint)
        {
            return false;
        }

        if (_lastFingerprint is null)
        {
            _lastFingerprint = fingerprint;
            return false;
        }

        var snapshot = await SettingsSnapshotLoader.LoadAsync(
            dbContext, cipher, cache.Current.Generation + 1, cancellationToken);
        cache.Publish(snapshot);
        _lastFingerprint = fingerprint;
        return true;
    }
}

/// <summary>
/// Polls <c>operations.setting</c> every 30 seconds for changes made outside this process (a psql
/// session, a restore, a future second node). This process's own writes publish immediately, in
/// process, right after their transaction commits (the write path); they never wait for this poll.
/// The interval only bounds staleness for the abnormal case.
/// <para>
/// LISTEN/NOTIFY was considered and rejected for MVP: it needs a dedicated connection held open
/// outside the shared scoped one <c>AddOperations</c> creates, to buy sub-second convergence for a
/// case that does not occur on a single node.
/// </para>
/// </summary>
public sealed class SettingsRefreshHostedService(
    IServiceScopeFactory scopeFactory,
    SettingsRefreshWorker worker,
    ILogger<SettingsRefreshHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);

                await using var scope = scopeFactory.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
                await worker.PollOnceAsync(dbContext, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Settings drift poll failed; will retry.");
            }
        }
    }
}
