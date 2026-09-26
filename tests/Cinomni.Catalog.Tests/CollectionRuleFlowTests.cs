using Cinomni.Catalog.Application;
using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Rule-based placement against a real PostgreSQL instance: saving rules moves titles and records why,
/// a pin holds, a title that stops matching goes back to the default shelf, enrichment re-places, the
/// order decides ties, previews write nothing, and the queued sweep finishes what an interrupted save
/// started.
/// </summary>
public sealed class CollectionRuleFlowTests : IAsyncLifetime
{
    private static readonly CollectionRuleCondition TitleHasAlien =
        new(CollectionRuleField.Title, CollectionRuleOperator.Contains, ["alien"]);

    private static readonly CollectionRuleCondition IsHorror =
        new(CollectionRuleField.Genre, CollectionRuleOperator.Is, ["Horror"]);

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() => _provider = await CatalogTestHost.CreateAsync("cinomni_test_catalog_collection_rules");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Saving_a_rule_moves_the_titles_it_matches_and_records_which_rule_did_it()
    {
        var alien = await AddMovieAsync("Alien", "348");
        var matrix = await AddMovieAsync("The Matrix", "603");
        var shelf = await CreateCollectionAsync("Sci-fi horror");

        var applied = await SetRulesAsync(shelf, Draft("Aliens", TitleHasAlien));

        Assert.Equal(1, applied.Moved);
        var placed = await WorkAsync(alien);
        Assert.Equal(shelf.Value, placed.CollectionId);
        Assert.False(placed.CollectionPinned);
        var rule = Assert.Single(await RulesAsync(shelf));
        Assert.Equal(rule.Id, placed.PlacedByRuleId);
        var condition = Assert.Single(rule.Conditions);
        Assert.Equal((CollectionRuleField.Title, CollectionRuleOperator.Contains), (condition.Field, condition.Operator));
        Assert.Equal(["alien"], condition.Values);

        var untouched = await WorkAsync(matrix);
        Assert.Equal(DefaultCollection.Id, untouched.CollectionId);
        Assert.Null(untouched.PlacedByRuleId);
    }

    [Fact]
    public async Task A_title_that_stops_matching_goes_back_to_the_default_collection()
    {
        var alien = await AddMovieAsync("Alien", "348");
        var shelf = await CreateCollectionAsync("Sci-fi horror");
        await SetRulesAsync(shelf, Draft("Aliens", TitleHasAlien));

        var applied = await SetRulesAsync(shelf, Draft("Predators", new CollectionRuleCondition(
            CollectionRuleField.Title, CollectionRuleOperator.Contains, ["predator"])));

        Assert.Equal(1, applied.Moved);
        var work = await WorkAsync(alien);
        Assert.Equal(DefaultCollection.Id, work.CollectionId);
        Assert.Null(work.PlacedByRuleId);
    }

    [Fact]
    public async Task A_title_moved_by_hand_is_pinned_and_the_rules_leave_it_alone_until_it_is_unpinned()
    {
        var alien = await AddMovieAsync("Alien", "348");
        var shelf = await CreateCollectionAsync("Sci-fi horror");
        var elsewhere = await CreateCollectionAsync("Elsewhere");
        await Admin(admin => admin.MoveWorkAsync(alien, elsewhere));

        var preview = await PreviewRulesAsync(shelf, Draft("Aliens", TitleHasAlien));
        Assert.Equal(1, preview.Matched);
        Assert.Equal(0, preview.WouldMove);
        Assert.Equal(1, preview.PinnedSkipped);
        var listed = Assert.Single(preview.Works);
        Assert.True(listed.Pinned);
        Assert.Equal(elsewhere, listed.CurrentCollectionId);
        Assert.Equal(shelf, listed.TargetCollectionId);

        Assert.Equal(0, (await SetRulesAsync(shelf, Draft("Aliens", TitleHasAlien))).Moved);
        Assert.Equal(elsewhere.Value, (await WorkAsync(alien)).CollectionId);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var unpinned = await scope.ServiceProvider.GetRequiredService<ICollectionRules>().UnpinWorkAsync(alien);
            Assert.True(unpinned.IsSuccess);
        }

        var work = await WorkAsync(alien);
        Assert.False(work.CollectionPinned);
        Assert.Equal(shelf.Value, work.CollectionId);
        Assert.NotNull(work.PlacedByRuleId);
    }

    [Fact]
    public async Task A_title_added_onto_a_named_collection_is_pinned_there()
    {
        var shelf = await CreateCollectionAsync("Sci-fi horror");
        var elsewhere = await CreateCollectionAsync("Elsewhere");
        await SetRulesAsync(shelf, Draft("Aliens", TitleHasAlien));

        WorkId alien;
        await using (var scope = _provider.CreateAsyncScope())
        {
            alien = (await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
                .AddMovieAsync("Alien", 1979, [new ExternalId(MetadataProvider.Tmdb, "348")], elsewhere)).Value;
        }

        var work = await WorkAsync(alien);
        Assert.Equal(elsewhere.Value, work.CollectionId);
        Assert.True(work.CollectionPinned);
    }

    [Fact]
    public async Task A_new_title_is_placed_by_the_rules_as_it_is_added()
    {
        var shelf = await CreateCollectionAsync("Sci-fi horror");
        await SetRulesAsync(shelf, Draft("Aliens", TitleHasAlien));

        var alien = await AddMovieAsync("Aliens", "679");

        Assert.Equal(shelf.Value, (await WorkAsync(alien)).CollectionId);
    }

    [Fact]
    public async Task Enrichment_re_places_a_title_once_its_genres_arrive()
    {
        var shelf = await CreateCollectionAsync("Horror");
        await SetRulesAsync(shelf, Draft("Horror", IsHorror));
        var work = await AddMovieAsync("The Thing", "1091");
        Assert.Equal(DefaultCollection.Id, (await WorkAsync(work)).CollectionId);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AttachMetadataSnapshotAsync(
                work.Value, Uuid7.New(), "The Thing", "en", 1982, 109, null, null, genres: ["Horror", "Science Fiction"]);
        }

        Assert.Equal(shelf.Value, (await WorkAsync(work)).CollectionId);
    }

    [Fact]
    public async Task Enrichment_leaves_a_pinned_title_where_it_was_pinned()
    {
        var shelf = await CreateCollectionAsync("Horror");
        var elsewhere = await CreateCollectionAsync("Elsewhere");
        await SetRulesAsync(shelf, Draft("Horror", IsHorror));
        var work = await AddMovieAsync("The Thing", "1091");
        await Admin(admin => admin.MoveWorkAsync(work, elsewhere));

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AttachMetadataSnapshotAsync(
                work.Value, Uuid7.New(), "The Thing", "en", 1982, 109, null, null, genres: ["Horror"]);
        }

        var placed = await WorkAsync(work);
        Assert.Equal(elsewhere.Value, placed.CollectionId);
        Assert.True(placed.CollectionPinned);
        Assert.Equal(["Horror"], placed.Genres);
    }

    [Fact]
    public async Task Moving_a_title_by_hand_onto_the_shelf_a_rule_put_it_on_pins_it_there()
    {
        var alien = await AddMovieAsync("Alien", "348");
        var shelf = await CreateCollectionAsync("Sci-fi horror");
        await SetRulesAsync(shelf, Draft("Aliens", TitleHasAlien));

        await Admin(admin => admin.MoveWorkAsync(alien, shelf));

        var work = await WorkAsync(alien);
        Assert.Equal(shelf.Value, work.CollectionId);
        Assert.True(work.CollectionPinned);
        Assert.Null(work.PlacedByRuleId);

        // Changing the shelf's rules so they no longer match does not send it to the open default one.
        await SetRulesAsync(shelf, Draft("Predators", new CollectionRuleCondition(
            CollectionRuleField.Title, CollectionRuleOperator.Contains, ["predator"])));
        Assert.Equal(shelf.Value, (await WorkAsync(alien)).CollectionId);
    }

    [Fact]
    public async Task The_order_decides_which_collection_claims_a_title_both_would_match()
    {
        var alien = await AddMovieAsync("Alien", "348");
        var first = await CreateCollectionAsync("First");
        var second = await CreateCollectionAsync("Second");
        await SetRulesAsync(first, Draft("Aliens", TitleHasAlien));
        await SetRulesAsync(second, Draft("Aliens too", TitleHasAlien));
        Assert.Equal(first.Value, (await WorkAsync(alien)).CollectionId);

        var order = new[] { new CollectionId(DefaultCollection.Id), second, first };
        await using (var scope = _provider.CreateAsyncScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<ICollectionRules>();
            var preview = (await rules.PreviewRulePriorityAsync(order)).Value;
            Assert.Equal(1, preview.WouldMove);
            Assert.Equal(second, Assert.Single(preview.Works).TargetCollectionId);

            Assert.Equal(1, (await rules.SetRulePriorityAsync(order)).Value.Moved);
        }

        Assert.Equal(second.Value, (await WorkAsync(alien)).CollectionId);
        var priorities = (await Admin(admin => admin.ListAsync())).ToDictionary(c => c.Id, c => c.RulePriority);
        Assert.Equal(0, priorities[new CollectionId(DefaultCollection.Id)]);
        Assert.Equal(1, priorities[second]);
        Assert.Equal(2, priorities[first]);
    }

    [Fact]
    public async Task An_order_that_does_not_name_every_collection_exactly_once_is_refused()
    {
        var shelf = await CreateCollectionAsync("Shelf");
        await using var scope = _provider.CreateAsyncScope();
        var rules = scope.ServiceProvider.GetRequiredService<ICollectionRules>();

        var missing = await rules.SetRulePriorityAsync([shelf]);
        var twice = await rules.SetRulePriorityAsync([shelf, shelf, new CollectionId(DefaultCollection.Id)]);

        Assert.Equal("catalog.rule_priority.incomplete", missing.Error.Code);
        Assert.Equal("catalog.rule_priority.incomplete", twice.Error.Code);
    }

    [Fact]
    public async Task A_preview_writes_nothing()
    {
        var alien = await AddMovieAsync("Alien", "348");
        var shelf = await CreateCollectionAsync("Sci-fi horror");

        var preview = await PreviewRulesAsync(shelf, Draft("Aliens", TitleHasAlien));

        Assert.Equal(1, preview.WouldMove);
        Assert.Equal(DefaultCollection.Id, (await WorkAsync(alien)).CollectionId);
        Assert.Empty(await RulesAsync(shelf));
    }

    [Fact]
    public async Task An_invalid_rule_is_refused_before_anything_is_saved()
    {
        var shelf = await CreateCollectionAsync("Shelf");
        await using var scope = _provider.CreateAsyncScope();
        var rules = scope.ServiceProvider.GetRequiredService<ICollectionRules>();

        var empty = await rules.SetRulesAsync(shelf, [new CollectionRuleDraft(null, "Everything", [])]);
        var nameless = await rules.SetRulesAsync(shelf, [Draft(" ", TitleHasAlien)]);
        var unknown = await rules.SetRulesAsync(new CollectionId(Uuid7.New()), [Draft("Aliens", TitleHasAlien)]);

        Assert.Equal("catalog.rule.no_conditions", empty.Error.Code);
        Assert.Equal("catalog.rule.invalid_name", nameless.Error.Code);
        Assert.Equal("catalog.collection_not_found", unknown.Error.Code);
        Assert.Empty(await RulesAsync(shelf));
    }

    [Fact]
    public async Task A_rule_named_by_id_is_kept_and_one_left_out_is_deleted()
    {
        var shelf = await CreateCollectionAsync("Shelf");
        await SetRulesAsync(shelf, Draft("Aliens", TitleHasAlien), Draft("Horror", IsHorror));
        var saved = await RulesAsync(shelf);

        await SetRulesAsync(shelf, new CollectionRuleDraft(saved[1].Id, "Horror, renamed", [IsHorror]));

        var rule = Assert.Single(await RulesAsync(shelf));
        Assert.Equal(saved[1].Id, rule.Id);
        Assert.Equal("Horror, renamed", rule.Name);
    }

    [Fact]
    public async Task The_queued_sweep_finishes_a_save_that_stopped_before_sweeping_across_batches()
    {
        var shelf = await CreateCollectionAsync("Sci-fi horror");
        var count = CollectionPlacementService.BatchSize + 20;
        await SeedMoviesAsync(count, "Alien");

        // The rule is committed with its queued sweep, and the process "stops" before the inline sweep.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var placement = scope.ServiceProvider.GetRequiredService<CollectionPlacementService>();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async token =>
            {
                db.CollectionRules.Add(StoredCollectionRule.Create(shelf.Value, "Aliens", 0, [TitleHasAlien], DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(token);
                await placement.QueueSweepAsync(Uuid7.New(), token);
            });
        }

        Assert.Equal(count, await CountOnAsync(DefaultCollection.Id));

        await MessageDriver.DrainAsync(_provider);

        Assert.Equal(count, await CountOnAsync(shelf.Value));
        Assert.Equal(0, await CountOnAsync(DefaultCollection.Id));
    }

    [Fact]
    public async Task A_sweep_batch_run_twice_moves_nothing_the_second_time()
    {
        await AddMovieAsync("Alien", "348");
        var shelf = await CreateCollectionAsync("Sci-fi horror");
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            db.CollectionRules.Add(StoredCollectionRule.Create(shelf.Value, "Aliens", 0, [TitleHasAlien], DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var runId = Uuid7.New();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var placement = scope.ServiceProvider.GetRequiredService<CollectionPlacementService>();
            Assert.Equal(1, (await placement.SweepBatchAsync(runId, null, queueNext: true)).Moved);
            Assert.Equal(0, (await placement.SweepBatchAsync(runId, null, queueNext: true)).Moved);
        }
    }

    private static CollectionRuleDraft Draft(string name, params CollectionRuleCondition[] conditions) =>
        new(null, name, conditions);

    private async Task<WorkId> AddMovieAsync(string title, string tmdbId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync(title, 1979, [new ExternalId(MetadataProvider.Tmdb, tmdbId)]);
        return result.Value;
    }

    /// <summary>Inserts works straight into the table: enough of them to span two sweep batches, quickly.</summary>
    private async Task SeedMoviesAsync(int count, string titlePrefix)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        for (var i = 0; i < count; i++)
        {
            var title = $"{titlePrefix} {i}";
            db.Works.Add(new Work
            {
                Id = Uuid7.New(),
                Kind = WorkKind.Movie,
                Title = title,
                SortTitle = title.ToLowerInvariant(),
                Status = WorkStatus.Released,
                AddedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<CollectionId> CreateCollectionAsync(string name) =>
        (await Admin(admin => admin.CreateAsync(name, CollectionKind.Mixed, CollectionAccessMode.Restricted))).Value;

    private async Task<T> Admin<T>(Func<ICollectionAdministration, Task<T>> act)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await act(scope.ServiceProvider.GetRequiredService<ICollectionAdministration>());
    }

    private async Task<RulesApplied> SetRulesAsync(CollectionId collection, params CollectionRuleDraft[] rules)
    {
        await using var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICollectionRules>().SetRulesAsync(collection, rules);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }

    private async Task<RulePreview> PreviewRulesAsync(CollectionId collection, params CollectionRuleDraft[] rules)
    {
        await using var scope = _provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICollectionRules>().PreviewRulesAsync(collection, rules)).Value;
    }

    private async Task<IReadOnlyList<CollectionRule>> RulesAsync(CollectionId collection)
    {
        await using var scope = _provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICollectionRules>().RulesAsync(collection)).Value;
    }

    private async Task<Work> WorkAsync(WorkId id)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Works
            .AsNoTracking()
            .SingleAsync(w => w.Id == id.Value);
    }

    private async Task<int> CountOnAsync(Guid collectionId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Works
            .CountAsync(w => w.CollectionId == collectionId);
    }
}
