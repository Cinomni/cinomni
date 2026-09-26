using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// The migration that introduces rules, run against a library placed before rules existed. An upgrade
/// must not change who sees what: a title an operator put on a restricted shelf stays there when the
/// first rule is saved, and the existing collections get an order with no ties.
/// </summary>
public sealed class CollectionRulesMigrationTests
{
    private const string Database = "cinomni_test_catalog_collection_rules_migration";
    private const string Migration = "AddCollectionRules";

    private static readonly Guid Restricted = Guid.Parse("00000000-0000-7000-8000-0000000000c1");
    private static readonly Guid OnDefault = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid OnRestricted = Guid.Parse("00000000-0000-7000-8000-000000000002");

    [Fact]
    public async Task Titles_already_off_the_default_shelf_are_pinned_and_collections_are_ordered_by_age()
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

            // A restricted shelf created after the default one, and a title placed on each.
            // Literal ids, matching the constants above: the analyzer refuses interpolation into raw SQL.
            await catalog.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO catalog.collections (id, name, kind, access_mode, is_default, created_at)
                VALUES ('00000000-0000-7000-8000-0000000000c1', 'Grown-ups', 'Mixed', 'Restricted', false, now() + interval '1 minute');
                INSERT INTO catalog.works (id, kind, title, sort_title, status, has_asset, added_at, collection_id)
                VALUES ('00000000-0000-7000-8000-000000000001', 'Movie', 'Alien', 'alien', 'Released', false, now(), '01000000-0000-7000-8000-000000000001'),
                       ('00000000-0000-7000-8000-000000000002', 'Movie', 'Aliens', 'aliens', 'Released', false, now(), '00000000-0000-7000-8000-0000000000c1');
                """);

            await catalog.Database.MigrateAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var works = await catalog.Works.AsNoTracking().ToDictionaryAsync(w => w.Id);
            Assert.False(works[OnDefault].CollectionPinned);
            Assert.True(works[OnRestricted].CollectionPinned);

            var priorities = await catalog.Collections.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.RulePriority);
            Assert.Equal(0, priorities[DefaultCollection.Id]);
            Assert.Equal(1, priorities[Restricted]);

            // The first rule saved matches both titles and goes on the open default shelf; the pinned one
            // stays where the operator put it.
            var rules = scope.ServiceProvider.GetRequiredService<ICollectionRules>();
            var applied = await rules.SetRulesAsync(
                new CollectionId(DefaultCollection.Id),
                [new CollectionRuleDraft(null, "Everything alien", [
                    new CollectionRuleCondition(CollectionRuleField.Title, CollectionRuleOperator.Contains, ["alien"])])]);
            Assert.Equal(0, applied.Value.Moved);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            Assert.Equal(Restricted, (await catalog.Works.AsNoTracking().SingleAsync(w => w.Id == OnRestricted)).CollectionId);
        }
    }
}
