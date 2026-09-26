using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Library.Application;
using Cinomni.Library.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Library.Tests;

/// <summary>
/// An asset is visible when its work is. Library holds no access rules of its own — it asks Catalog —
/// so these tests are what prove the question is actually asked.
/// </summary>
public sealed class LibraryAccessTests : IAsyncLifetime
{
    private static readonly Viewer Operator = new(Uuid7.New(), IsAdministrator: true);
    private static readonly Viewer Member = new(Uuid7.New(), IsAdministrator: false);

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() => _provider = await LibraryTestHost.CreateAsync("cinomni_test_library_access");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_asset_of_a_hidden_work_is_absent_from_the_list_and_by_id()
    {
        var openAsset = await RegisterAssetAsync(await AddWorkAsync("The Matrix", "603"));
        var hiddenAsset = await RegisterAssetAsync(await AddWorkAsync("Alien", "348", await RestrictedShelfAsync()));

        var mine = await ListAsync(Member);
        Assert.Equal(openAsset, Assert.Single(mine).Id.Value);
        Assert.Null(await GetAsync(Member, hiddenAsset));

        // The operator sees both, without any grant.
        Assert.Equal(2, (await ListAsync(Operator)).Count);
        Assert.NotNull(await GetAsync(Operator, hiddenAsset));
    }

    [Fact]
    public async Task Asking_for_a_hidden_works_assets_returns_nothing()
    {
        var hiddenWork = await AddWorkAsync("Alien", "348", await RestrictedShelfAsync());
        await RegisterAssetAsync(hiddenWork);

        Assert.Empty(await ListAsync(Member, hiddenWork));
        Assert.Single(await ListAsync(Operator, hiddenWork));
    }

    [Fact]
    public async Task A_grant_makes_the_assets_appear_and_revoking_hides_them()
    {
        var shelf = await RestrictedShelfAsync();
        var assetId = await RegisterAssetAsync(await AddWorkAsync("Alien", "348", shelf));

        await GrantAsync(shelf, Member);
        Assert.NotNull(await GetAsync(Member, assetId));
        Assert.Single(await ListAsync(Member));

        await RevokeAsync(shelf, Member);
        Assert.Null(await GetAsync(Member, assetId));
        Assert.Empty(await ListAsync(Member));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<CollectionId> RestrictedShelfAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
        var created = await admin.CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Restricted);
        return created.Value;
    }

    private async Task<Guid> AddWorkAsync(string title, string externalId, CollectionId? collection = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync(title, null, [new ExternalId(MetadataProvider.Tmdb, externalId)], collection);
        return added.Value.Value;
    }

    private async Task<Guid> RegisterAssetAsync(Guid workId)
    {
        var assetId = Uuid7.New();
        var request = new RegisterMediaAssetRequest(
            assetId,
            workId,
            [Uuid7.New()],
            $"/library/{assetId}.mkv",
            2_000_000_000,
            "matroska",
            [new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false)]);

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        return assetId;
    }

    private async Task GrantAsync(CollectionId shelf, Viewer viewer)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICollectionAdministration>()
            .GrantAsync(shelf, viewer.UserId, Operator.UserId);
    }

    private async Task RevokeAsync(CollectionId shelf, Viewer viewer)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICollectionAdministration>().RevokeAsync(shelf, viewer.UserId);
    }

    private async Task<IReadOnlyList<MediaAssetSummary>> ListAsync(Viewer viewer, Guid? workId = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryBrowse>().ListAsync(viewer, workId);
    }

    private async Task<MediaAssetDetail?> GetAsync(Viewer viewer, Guid assetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryBrowse>()
            .GetAsync(viewer, new MediaAssetId(assetId));
    }
}
