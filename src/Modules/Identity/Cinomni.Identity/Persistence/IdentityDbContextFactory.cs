using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cinomni.Identity.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can build the context for migrations without
/// starting the Host. The connection string here is development-only (matches
/// docker-compose.dev.yml); production configuration comes from the Host.
/// </summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CINOMNI_DB")
            ?? "Host=localhost;Port=5442;Database=cinomni;Username=cinomni;Password=cinomni_dev";

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new IdentityDbContext(options);
    }
}
