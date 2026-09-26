using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// A content ceiling hides a title the same way a missing grant does: absent from the list, and absent
/// by id. Unrated stays visible. An administrator is not filtered, so the operator can still manage
/// what a member cannot see.
/// </summary>
public sealed class ContentCeilingTests : IAsyncLifetime
{
    private static readonly Viewer Operator = new(Uuid7.New(), IsAdministrator: true);
    private static readonly Viewer Child = new(Uuid7.New(), IsAdministrator: false, ContentCeiling: "12", ContentCeilingRegion: "ES");

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await CatalogTestHost.CreateAsync(
            "cinomni_test_catalog_ceiling",
            services => services.AddSingleton<IContentRatingRegion>(new FixedContentRatingRegion("ES")));

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_ceiling_hides_a_higher_classification_and_leaves_the_rest()
    {
        var allowed = await AddRatedAsync("Inside Out", "150540", "7");
        var hidden = await AddRatedAsync("Alien", "348", "18");
        var unrated = await AddRatedAsync("Home Movie", "1", null);
        var unknown = await AddRatedAsync("Odd Label", "2", "NR");

        var visible = await ListAsync(Child);
        Assert.Contains(visible, work => work.Id.Value == allowed);
        Assert.Contains(visible, work => work.Id.Value == unrated);
        Assert.Contains(visible, work => work.Id.Value == unknown);
        Assert.DoesNotContain(visible, work => work.Id.Value == hidden);
        Assert.Null(await GetAsync(Child, hidden));
        Assert.NotNull(await GetAsync(Operator, hidden));
    }

    [Fact]
    public async Task A_ceiling_hides_the_labels_the_provider_really_publishes_whatever_their_case()
    {
        // "X" (adult film) and the television-only "13" used to be unranked, so visible to a child.
        var adultFilm = await AddRatedAsync("Adult Film", "10", "X");
        var teenSeries = await AddRatedAsync("Teen Series", "11", "13");
        var lowerCase = await AddRatedAsync("Lower Case", "12", "x");
        var spaced = await AddRatedAsync("Spaced", "13", " 18 ");
        var childrens = await AddRatedAsync("Children's", "14", "7i");

        Assert.Null(await GetAsync(Child, adultFilm));
        Assert.Null(await GetAsync(Child, teenSeries));
        Assert.Null(await GetAsync(Child, lowerCase));
        Assert.Null(await GetAsync(Child, spaced));
        Assert.NotNull(await GetAsync(Child, childrens));
    }

    [Fact]
    public async Task A_ceiling_set_for_another_region_filters_nothing()
    {
        var hidden = await AddRatedAsync("Alien", "348", "18");
        var moved = new Viewer(Child.UserId, IsAdministrator: false, ContentCeiling: "12", ContentCeilingRegion: "US");

        Assert.NotNull(await GetAsync(moved, hidden));
    }

    [Fact]
    public async Task No_ceiling_hides_nothing()
    {
        var adult = await AddRatedAsync("Alien", "349", "18");
        var open = new Viewer(Uuid7.New(), IsAdministrator: false);

        Assert.NotNull(await GetAsync(open, adult));
    }

    private async Task<Guid> AddRatedAsync(string title, string externalId, string? rating)
    {
        Guid id;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
            var added = await commands.AddMovieAsync(title, null, [new ExternalId(MetadataProvider.Tmdb, externalId)]);
            Assert.True(added.IsSuccess, added.Error.Message);
            id = added.Value.Value;
        }

        await using var update = _provider.CreateAsyncScope();
        var db = update.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var work = await db.Works.SingleAsync(row => row.Id == id);
        work.ContentRating = rating;
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<IReadOnlyList<WorkSummary>> ListAsync(Viewer viewer)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>().ListAsync(viewer);
    }

    private async Task<WorkSummary?> GetAsync(Viewer viewer, Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>().GetByIdAsync(viewer, new WorkId(workId));
    }
}
