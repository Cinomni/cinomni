using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cinomni.Requests.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can build the context for migrations without starting
/// the Host. Development-only connection string (matches docker-compose.dev.yml).
/// </summary>
public sealed class RequestsDbContextFactory : IDesignTimeDbContextFactory<RequestsDbContext>
{
    public RequestsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CINOMNI_DB")
            ?? "Host=localhost;Port=5442;Database=cinomni;Username=cinomni;Password=cinomni_dev";

        var options = new DbContextOptionsBuilder<RequestsDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new RequestsDbContext(options);
    }
}
