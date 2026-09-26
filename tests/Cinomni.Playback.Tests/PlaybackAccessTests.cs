using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Playback.Tests;

/// <summary>
/// You may only play what you may see, and that is re-checked on every file served rather than only when
/// the session opened — which is what makes revoking a collection stop a stream that is already running.
/// These tests are the only thing that proves the per-segment check exists.
/// </summary>
public sealed class PlaybackAccessTests : IAsyncLifetime
{
    private static readonly Viewer Operator = new(Uuid7.New(), IsAdministrator: true);
    private static readonly Viewer Member = new(Uuid7.New(), IsAdministrator: false);

    private readonly FakeMediaEncoder _encoder = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() => _provider = await PlaybackTestHost.CreateAsync("cinomni_test_playback_access", _encoder);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_asset_you_cannot_see_does_not_play_and_says_nothing_about_why()
    {
        var shelf = await RestrictedShelfAsync();
        var assetId = await RegisterAssetAsync(await AddWorkAsync(shelf));

        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>();
        var result = await commands.RequestPlaybackAsync(Member, assetId, Client());

        // Exactly the error an asset that does not exist gives — not a distinct "forbidden".
        Assert.True(result.IsFailure);
        Assert.Equal("playback.asset_not_found", result.Error.Code);

        // The operator plays it without any grant.
        Assert.True((await commands.RequestPlaybackAsync(Operator, assetId, Client())).IsSuccess);
    }

    [Fact]
    public async Task Revoking_stops_a_stream_that_is_already_running()
    {
        var shelf = await RestrictedShelfAsync();
        var assetId = await RegisterAssetAsync(await AddWorkAsync(shelf));
        await GrantAsync(shelf, Member);

        var ticket = await StartAsync(Member, assetId);
        Assert.Equal(PlaybackMethod.DirectPlay, ticket.Method);

        // The file resolves while the grant stands...
        Assert.NotNull(await ResolveAsync(Member, ticket.SessionId));

        await RevokeAsync(shelf, Member);

        // ...and stops resolving the moment it is taken away, without the session being torn down:
        // revocation is enforced on read, at the next range request or segment.
        Assert.Null(await ResolveAsync(Member, ticket.SessionId));
    }

    [Fact]
    public async Task A_session_with_no_work_recorded_serves_nothing()
    {
        // A session from before the work was recorded (the column is nullable for exactly this) has
        // nothing to re-check access against, so it fails closed.
        var assetId = await RegisterAssetAsync(await AddWorkAsync());
        var ticket = await StartAsync(Member, assetId);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE playback.playback_sessions SET work_id = NULL WHERE id = {ticket.SessionId.Value}");
        }

        Assert.Null(await ResolveAsync(Member, ticket.SessionId));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static ClientCapability Client() => new(["matroska"], ["h264"], ["aac"], MaxHeight: null);

    private async Task<CollectionId> RestrictedShelfAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<ICollectionAdministration>()
            .CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Restricted);
        return created.Value;
    }

    private async Task<Guid> AddWorkAsync(CollectionId? collection = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync("Alien", 1979, [], collection);
        return added.Value.Value;
    }

    private async Task<Guid> RegisterAssetAsync(Guid workId)
    {
        var assetId = Uuid7.New();
        var path = Path.Combine(Path.GetTempPath(), $"cinomni-access-{assetId}.mkv");
        await File.WriteAllTextAsync(path, "not really a movie");

        var request = new RegisterMediaAssetRequest(
            assetId,
            workId,
            [Uuid7.New()],
            path,
            2_000_000_000,
            "matroska",
            [
                new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
                new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
            ]);

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        return assetId;
    }

    private async Task<PlaybackTicket> StartAsync(Viewer viewer, Guid assetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
            .RequestPlaybackAsync(viewer, assetId, Client());
        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
    }

    private async Task<ServableFile?> ResolveAsync(Viewer viewer, PlaybackSessionId sessionId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlaybackStreamer>()
            .ResolveDirectFileAsync(viewer, sessionId);
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
}
