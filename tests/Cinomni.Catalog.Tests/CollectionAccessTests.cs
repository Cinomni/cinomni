using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Integration tests for content access against a real PostgreSQL instance. This is what actually pins
/// the feature: the API-authorization test can only see which policy a route carries, not whether a
/// filter was written, so a missing <c>WHERE</c> would sail past it and fail here.
/// </summary>
public sealed class CollectionAccessTests : IAsyncLifetime
{
    private static readonly Viewer Operator = new(Uuid7.New(), IsAdministrator: true);
    private static readonly Viewer Member = new(Uuid7.New(), IsAdministrator: false);
    private static readonly Viewer Stranger = new(Uuid7.New(), IsAdministrator: false);

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() => _provider = await CatalogTestHost.CreateAsync("cinomni_test_catalog_access");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_installation_starts_with_one_open_collection_everything_lands_in()
    {
        var workId = await AddMovieAsync("The Matrix", "603");

        var collection = Assert.Single(await CollectionsAsync(Member));
        Assert.Equal("Library", collection.Name);
        Assert.True(collection.IsDefault);
        Assert.Equal(CollectionAccessMode.Open, collection.AccessMode);
        Assert.Equal(1, collection.WorkCount);

        // Open means open: no grant exists, and everyone still sees the title.
        Assert.Equal("The Matrix", Assert.Single(await ListAsync(Member)).Title);
        Assert.NotNull(await GetAsync(Member, workId));
    }

    [Fact]
    public async Task A_restricted_collection_is_invisible_until_it_is_granted()
    {
        var (shelf, workId) = await RestrictedShelfWithWorkAsync("Grown-ups", "Alien", "348");

        // Absent from the list, and absent by id and by external id — the same answer as not existing.
        Assert.Empty(await ListAsync(Member));
        Assert.Null(await GetAsync(Member, workId));
        Assert.Null(await FindByExternalAsync(Member, "348"));
        // The default collection stays visible — it is open; the restricted shelf is what is absent.
        Assert.DoesNotContain(await CollectionsAsync(Member), c => c.Name == "Grown-ups");

        await GrantAsync(shelf, Member);

        Assert.Equal("Alien", Assert.Single(await ListAsync(Member)).Title);
        Assert.NotNull(await GetAsync(Member, workId));
        Assert.NotNull(await FindByExternalAsync(Member, "348"));
        Assert.Contains(await CollectionsAsync(Member), c => c.Name == "Grown-ups");
    }

    [Fact]
    public async Task Revoking_hides_it_again()
    {
        var (shelf, workId) = await RestrictedShelfWithWorkAsync("Grown-ups", "Alien", "348");
        await GrantAsync(shelf, Member);
        Assert.NotNull(await GetAsync(Member, workId));

        await RevokeAsync(shelf, Member);

        Assert.Null(await GetAsync(Member, workId));
        Assert.Empty(await ListAsync(Member));
    }

    [Fact]
    public async Task A_grant_on_one_collection_says_nothing_about_another()
    {
        // The negative a single-collection suite cannot catch: a filter that returns everything passes
        // "granted sees it" and only fails here.
        var (granted, grantedWork) = await RestrictedShelfWithWorkAsync("Granted", "Alien", "348");
        var (_, otherWork) = await RestrictedShelfWithWorkAsync("Other", "Solaris", "593");
        await GrantAsync(granted, Member);

        Assert.Equal("Alien", Assert.Single(await ListAsync(Member)).Title);
        Assert.NotNull(await GetAsync(Member, grantedWork));
        Assert.Null(await GetAsync(Member, otherWork));

        var visible = await CollectionsAsync(Member);
        Assert.Contains(visible, c => c.Name == "Granted");
        Assert.DoesNotContain(visible, c => c.Name == "Other");
    }

    [Fact]
    public async Task An_administrator_sees_every_collection_without_a_grant()
    {
        var (_, restricted) = await RestrictedShelfWithWorkAsync("Grown-ups", "Alien", "348");
        var open = await AddMovieAsync("The Matrix", "603");

        var titles = (await ListAsync(Operator)).Select(w => w.Title).OrderBy(t => t).ToList();
        Assert.Equal(["Alien", "The Matrix"], titles);
        Assert.NotNull(await GetAsync(Operator, restricted));
        Assert.NotNull(await GetAsync(Operator, open));
        // The restricted shelf plus the default one the other title landed in.
        Assert.Equal(2, (await CollectionsAsync(Operator)).Count);
    }

    [Fact]
    public async Task Moving_a_work_moves_who_can_see_it()
    {
        var workId = await AddMovieAsync("The Matrix", "603");
        var shelf = await CreateCollectionAsync("Grown-ups", CollectionAccessMode.Restricted);
        Assert.NotNull(await GetAsync(Member, workId));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            Assert.True((await admin.MoveWorkAsync(new WorkId(workId), shelf)).IsSuccess);
        }

        Assert.Null(await GetAsync(Member, workId));
        Assert.NotNull(await GetAsync(Operator, workId));
    }

    [Fact]
    public async Task Access_is_per_account_not_per_role()
    {
        var (shelf, workId) = await RestrictedShelfWithWorkAsync("Grown-ups", "Alien", "348");
        await GrantAsync(shelf, Member);

        // Another member with no grant of their own sees nothing.
        Assert.NotNull(await GetAsync(Member, workId));
        Assert.Null(await GetAsync(Stranger, workId));
    }

    [Fact]
    public async Task Granting_twice_and_revoking_twice_are_both_harmless()
    {
        var (shelf, workId) = await RestrictedShelfWithWorkAsync("Grown-ups", "Alien", "348");

        await GrantAsync(shelf, Member);
        await GrantAsync(shelf, Member);
        Assert.NotNull(await GetAsync(Member, workId));
        Assert.Single(await GrantedAccountsAsync(shelf));

        await RevokeAsync(shelf, Member);
        await RevokeAsync(shelf, Member);
        Assert.Null(await GetAsync(Member, workId));
        Assert.Empty(await GrantedAccountsAsync(shelf));
    }

    [Fact]
    public async Task The_content_access_authority_agrees_with_the_read_model()
    {
        var (_, hidden) = await RestrictedShelfWithWorkAsync("Grown-ups", "Alien", "348");
        var open = await AddMovieAsync("The Matrix", "603");

        await using var scope = _provider.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<IContentAccess>();

        Assert.True(await access.CanSeeWorkAsync(Member, open));
        Assert.False(await access.CanSeeWorkAsync(Member, hidden));
        Assert.True(await access.CanSeeWorkAsync(Operator, hidden));

        // A work that does not exist answers exactly like one that is not yours.
        Assert.False(await access.CanSeeWorkAsync(Operator, Uuid7.New()));

        var filtered = await access.FilterWorksAsync(Member, [open, hidden, Uuid7.New()]);
        Assert.Equal([open], filtered);
        // The operator sees the restricted shelf and the default one; the member only the default.
        Assert.Equal(2, (await access.VisibleCollectionsAsync(Operator)).Count);
        Assert.Single(await access.VisibleCollectionsAsync(Member));
    }

    [Fact]
    public async Task A_collection_name_is_unique_and_an_unknown_one_is_reported()
    {
        await CreateCollectionAsync("Grown-ups", CollectionAccessMode.Restricted);

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();

        var duplicate = await admin.CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Open);
        Assert.Equal("catalog.collection_exists", duplicate.Error.Code);

        var unknown = new CollectionId(Uuid7.New());
        Assert.Equal(
            "catalog.collection_not_found",
            (await admin.SetAccessModeAsync(unknown, CollectionAccessMode.Open)).Error.Code);
        Assert.Equal(
            "catalog.collection_not_found",
            (await admin.GrantAsync(unknown, Member.UserId, Operator.UserId)).Error.Code);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<(CollectionId Shelf, Guid WorkId)> RestrictedShelfWithWorkAsync(
        string name,
        string title,
        string externalId)
    {
        var shelf = await CreateCollectionAsync(name, CollectionAccessMode.Restricted);
        var workId = await AddMovieAsync(title, externalId, shelf);
        return (shelf, workId);
    }

    private async Task<CollectionId> CreateCollectionAsync(string name, CollectionAccessMode mode)
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
        var created = await admin.CreateAsync(name, CollectionKind.Movies, mode);
        Assert.True(created.IsSuccess, created.Error.Message);
        return created.Value;
    }

    private async Task<Guid> AddMovieAsync(string title, string externalId, CollectionId? collection = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var added = await commands.AddMovieAsync(
            title, null, [new ExternalId(MetadataProvider.Tmdb, externalId)], collection);
        Assert.True(added.IsSuccess, added.Error.Message);
        return added.Value.Value;
    }

    private async Task GrantAsync(CollectionId shelf, Viewer viewer)
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
        Assert.True((await admin.GrantAsync(shelf, viewer.UserId, Operator.UserId)).IsSuccess);
    }

    private async Task RevokeAsync(CollectionId shelf, Viewer viewer)
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
        Assert.True((await admin.RevokeAsync(shelf, viewer.UserId)).IsSuccess);
    }

    private async Task<IReadOnlyList<Guid>> GrantedAccountsAsync(CollectionId shelf)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICollectionAdministration>().GrantedAccountsAsync(shelf);
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

    private async Task<WorkSummary?> FindByExternalAsync(Viewer viewer, string externalId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>()
            .FindByExternalIdAsync(viewer, MetadataProvider.Tmdb, externalId);
    }

    private async Task<IReadOnlyList<CollectionSummary>> CollectionsAsync(Viewer viewer)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>().CollectionsAsync(viewer);
    }
}
