using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinomni.Host.Health;

/// <summary>
/// Whether PostgreSQL answers. Every module's schema lives on the one connection, so this is the
/// dependency without which the installation can serve nothing at all — it is the only readiness check
/// that reports Unhealthy rather than Degraded.
/// <para>
/// Runs in a scope of its own with an explicit budget. Without one, a database that accepts a
/// connection and then stalls would leave the probe hanging until the orchestrator's own timeout, and
/// "the probe timed out" does not tell an operator which dependency was slow.
/// </para>
/// </summary>
internal sealed class PostgresHealthCheck(IServiceScopeFactory scopeFactory, TimeSpan timeout) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

            return await dbContext.Database.CanConnectAsync(budget.Token)
                ? HealthCheckResult.Healthy()
                // No description and no exception: the failure text would carry the connection string,
                // and this endpoint answers an unauthenticated caller. The reason is in the log.
                : HealthCheckResult.Unhealthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
