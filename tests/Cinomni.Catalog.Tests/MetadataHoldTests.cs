using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// A title added while rules exist is held back from members until its metadata arrives: the rules can
/// only read kind, title and year on add, so a rule on genre or age rating that would restrict it cannot
/// fire yet, and without the hold it would sit on the open default shelf in the meantime.
/// </summary>
public sealed class MetadataHoldTests : IAsyncLifetime
{
    private static readonly Viewer Operator = new(Uuid7.New(), IsAdministrator: true);
    private static readonly Viewer Member = new(Uuid7.New(), IsAdministrator: false);

    private static readonly CollectionRuleCondition IsHorror =
        new(CollectionRuleField.Genre, CollectionRuleOperator.Is, ["Horror"]);

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() => _provider = await CatalogTestHost.CreateAsync("cinomni_test_catalog_metadata_hold");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_title_added_while_rules_exist_is_hidden_from_members_until_its_metadata_arrives()
    {
        await RestrictedHorrorShelfAsync();

        var work = await AddMovieAsync("Paddington", [new ExternalId(MetadataProvider.Tmdb, "116149")]);

        Assert.False(await CanSeeAsync(Member, work));
        var held = await GetAsync(Operator, work);
        Assert.NotNull(held);
        Assert.True(held.AwaitingMetadata);

        await EnrichAsync(work, "Paddington", ["Family", "Comedy"]);

        Assert.True(await CanSeeAsync(Member, work));
        Assert.False((await GetAsync(Operator, work))!.AwaitingMetadata);
    }

    [Fact]
    public async Task A_held_title_is_not_counted_on_a_shelf_for_a_member()
    {
        await RestrictedHorrorShelfAsync();
        await AddMovieAsync("Paddington", [new ExternalId(MetadataProvider.Tmdb, "116149")]);
        await AddMovieAsync("Home video", []);

        Assert.Equal(1, await DefaultShelfCountAsync(Member));
        Assert.Equal(2, await DefaultShelfCountAsync(Operator));
    }

    [Fact]
    public async Task A_held_title_whose_metadata_restricts_it_never_reaches_the_open_shelf()
    {
        var shelf = await RestrictedHorrorShelfAsync();
        var work = await AddMovieAsync("The Thing", [new ExternalId(MetadataProvider.Tmdb, "1091")]);

        await EnrichAsync(work, "The Thing", ["Horror"]);

        var placed = await GetAsync(Operator, work);
        Assert.Equal(shelf, placed!.Collection);
        Assert.False(placed.AwaitingMetadata);
        Assert.False(await CanSeeAsync(Member, work));
    }

    [Fact]
    public async Task Placing_a_held_title_by_hand_releases_it()
    {
        await RestrictedHorrorShelfAsync();
        var work = await AddMovieAsync("Paddington", [new ExternalId(MetadataProvider.Tmdb, "116149")]);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var moved = await scope.ServiceProvider.GetRequiredService<ICollectionAdministration>()
                .MoveWorkAsync(work, new CollectionId(Persistence.DefaultCollection.Id));
            Assert.True(moved.IsSuccess);
        }

        Assert.True(await CanSeeAsync(Member, work));
        Assert.False((await GetAsync(Operator, work))!.AwaitingMetadata);
    }

    [Fact]
    public async Task Without_rules_nothing_is_held()
    {
        var work = await AddMovieAsync("Paddington", [new ExternalId(MetadataProvider.Tmdb, "116149")]);

        Assert.True(await CanSeeAsync(Member, work));
        Assert.False((await GetAsync(Operator, work))!.AwaitingMetadata);
    }

    [Fact]
    public async Task A_title_nothing_will_enrich_is_placed_on_what_is_known_and_not_held()
    {
        // No external id means no metadata request, so a hold would hide it for good.
        await RestrictedHorrorShelfAsync();

        var work = await AddMovieAsync("Home video", []);

        Assert.True(await CanSeeAsync(Member, work));
    }

    private async Task<CollectionId> RestrictedHorrorShelfAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var shelf = (await scope.ServiceProvider.GetRequiredService<ICollectionAdministration>()
            .CreateAsync("Horror", CollectionKind.Mixed, CollectionAccessMode.Restricted)).Value;
        var saved = await scope.ServiceProvider.GetRequiredService<ICollectionRules>()
            .SetRulesAsync(shelf, [new CollectionRuleDraft(null, "Horror", [IsHorror])]);
        Assert.True(saved.IsSuccess);
        return shelf;
    }

    private async Task<WorkId> AddMovieAsync(string title, IReadOnlyList<ExternalId> ids)
    {
        await using var scope = _provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AddMovieAsync(title, 2014, ids)).Value;
    }

    private async Task EnrichAsync(WorkId work, string title, IReadOnlyList<string> genres)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AttachMetadataSnapshotAsync(
            work.Value, Uuid7.New(), title, "en", 2014, 95, null, null, genres: genres);
    }

    private async Task<bool> CanSeeAsync(Viewer viewer, WorkId work)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IContentAccess>().CanSeeWorkAsync(viewer, work.Value);
    }

    private async Task<int> DefaultShelfCountAsync(Viewer viewer)
    {
        await using var scope = _provider.CreateAsyncScope();
        var collections = await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>().CollectionsAsync(viewer);
        return collections.Single(c => c.IsDefault).WorkCount;
    }

    private async Task<WorkSummary?> GetAsync(Viewer viewer, WorkId work)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>().GetByIdAsync(viewer, work);
    }
}
