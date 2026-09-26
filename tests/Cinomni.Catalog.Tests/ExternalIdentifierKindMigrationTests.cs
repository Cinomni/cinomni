using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// The migration that makes an external id's identity include the work's kind, run against rows
/// written before it: every existing id has to take its own work's kind, or no lookup could match it.
/// </summary>
public sealed class ExternalIdentifierKindMigrationTests
{
    private const string Database = "cinomni_test_catalog_external_kind_migration";
    private const string Migration = "ExternalIdentifierKind";

    [Fact]
    public async Task Existing_ids_take_their_works_kind_and_a_film_and_a_show_can_then_share_one()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(CatalogTestHost.ConnectionStringFor(Database));
        services.AddCatalogModule();
        services.AddSingleton(new FakeMetadataQuery());
        services.AddScoped<Metadata.Contracts.IMetadataQuery>(sp => sp.GetRequiredService<FakeMetadataQuery>());
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();

            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var migrations = catalog.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith($"_{Migration}", StringComparison.Ordinal));
            Assert.True(index > 0, "the migration under test was not found");
            await catalog.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);

            // Written the way the previous schema allowed: no kind on the id.
            await catalog.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO catalog.works (id, kind, title, sort_title, status, has_asset, added_at, collection_id)
                SELECT '00000000-0000-7000-8000-000000000001', 'Movie', 'Some Film', 'some film', 'Released', false, now(), id
                FROM catalog.collections ORDER BY is_default DESC LIMIT 1;
                INSERT INTO catalog.works (id, kind, title, sort_title, status, has_asset, added_at, collection_id)
                SELECT '00000000-0000-7000-8000-000000000002', 'Series', 'Some Show', 'some show', 'Continuing', false, now(), id
                FROM catalog.collections ORDER BY is_default DESC LIMIT 1;
                INSERT INTO catalog.external_identifiers (id, work_id, provider, value)
                VALUES ('00000000-0000-7000-8000-00000000000a', '00000000-0000-7000-8000-000000000001', 'Tmdb', '603'),
                       ('00000000-0000-7000-8000-00000000000b', '00000000-0000-7000-8000-000000000002', 'Tmdb', '1399');
                """);

            await catalog.Database.MigrateAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var kinds = await catalog.ExternalIdentifiers.AsNoTracking()
                .ToDictionaryAsync(e => e.Value, e => e.Kind);
            Assert.Equal(WorkKind.Movie, kinds["603"]);
            Assert.Equal(WorkKind.Series, kinds["1399"]);

            var query = scope.ServiceProvider.GetRequiredService<ICatalogQuery>();
            Assert.Equal("Some Show", (await query.FindByExternalIdAsync(MetadataProvider.Tmdb, "1399", WorkKind.Series))!.Title);

            // And the id a show already uses can now also name a film.
            var film = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
                .AddMovieAsync("Some Film Sharing 1399", 2001, [new ExternalId(MetadataProvider.Tmdb, "1399")]);
            Assert.True(film.IsSuccess);
            Assert.Equal(WorkKind.Movie, (await query.GetByIdAsync(film.Value))!.Kind);
        }
    }
}
