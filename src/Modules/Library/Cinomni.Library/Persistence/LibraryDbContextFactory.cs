using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cinomni.Library.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can build the context for migrations without starting
/// the Host. Development-only connection string (matches docker-compose.dev.yml).
/// </summary>
public sealed class LibraryDbContextFactory : IDesignTimeDbContextFactory<LibraryDbContext>
{
    public LibraryDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CINOMNI_DB")
            ?? "Host=localhost;Port=5442;Database=cinomni;Username=cinomni;Password=cinomni_dev";

        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new LibraryDbContext(options);
    }
}
