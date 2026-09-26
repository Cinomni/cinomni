using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cinomni.Monitoring.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can build the context for migrations without
/// starting the Host. Development-only connection string (matches docker-compose.dev.yml).
/// </summary>
public sealed class MonitoringDbContextFactory : IDesignTimeDbContextFactory<MonitoringDbContext>
{
    public MonitoringDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CINOMNI_DB")
            ?? "Host=localhost;Port=5442;Database=cinomni;Username=cinomni;Password=cinomni_dev";

        var options = new DbContextOptionsBuilder<MonitoringDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new MonitoringDbContext(options);
    }
}
