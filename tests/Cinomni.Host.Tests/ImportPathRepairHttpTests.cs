using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cinomni.Identity.Application;
using Cinomni.Import.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Drives both path-repair routes over HTTP, as a real signed-in administrator, against a real
/// PostgreSQL database holding real library rows.
/// <para>
/// This is the layer the defect was visible at and the layer no test reached: the preview answered
/// 500 and the queued run spent its attempts and died, on every installation, because the read it
/// makes was only ever served by a fake. <c>ApiAuthorizationTests</c> reads routing metadata and never
/// dispatches; <c>LibraryPathRepairTests</c> resolves the pass with a stand-in library. Only a real
/// request, over a real query, over a real database, can fail when that read stops working.
/// </para>
/// <para>Its own database, like every other PostgreSQL-backed suite here.</para>
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class ImportPathRepairHttpTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_import_path_repair_http";
    private const string AdminUsername = "operator";
    private const string Password = "correct horse battery staple";

    /// <summary>A name an earlier build wrote verbatim: the colon is invalid on the clients that mount the library.</summary>
    private const string DamagedName = "Alien: Cut (1979)";

    private const string RepairedName = "Alien_ Cut (1979)";

    private readonly PathRepairFileSystem _files = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _adminToken = null!;
    private Guid _assetId;

    private static string Under(params string[] segments) =>
        Path.Combine([ImportPathRepairHttpTestHost.LibraryRoot, .. segments]);

    private static string DamagedVideo => Under(DamagedName, DamagedName + ".mkv");

    private static string DamagedSubtitle => Under(DamagedName, DamagedName + ".es.srt");

    private static string RepairedVideo => Under(RepairedName, RepairedName + ".mkv");

    private static string RepairedSubtitle => Under(RepairedName, RepairedName + ".es.srt");

    public async Task InitializeAsync()
    {
        _app = await ImportPathRepairHttpTestHost.StartAsync(Database, _files);
        _client = _app.GetTestClient();

        await using var scope = _app.Services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var admin = await provisioning.CreateAdminAsync(AdminUsername, Password);
        Assert.True(admin.IsSuccess, admin.Error.Message);
        _adminToken = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;

        // One asset, registered through Library's own write surface, naming the damaged path.
        _assetId = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(
            new RegisterMediaAssetRequest(
                _assetId,
                WorkId: Guid.NewGuid(),
                TargetIds: [],
                DamagedVideo,
                Size: 2_000_000,
                Container: "matroska,webm",
                Streams:
                [
                    new MediaStreamInput(
                        0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null,
                        IsDefault: true, IsForced: false),
                ],
                UnitIds: [Guid.NewGuid()]));

        _files.Seed(DamagedVideo, 2_000_000);
        _files.Seed(DamagedSubtitle, 4_000);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task The_preview_answers_with_what_a_repair_would_move()
    {
        var preview = await PreviewAsync();

        var entry = Assert.Single(preview!.Entries);
        Assert.Equal(_assetId.ToString(), entry.AssetId);
        Assert.Equal(DamagedVideo, entry.From);
        Assert.Equal(RepairedVideo, entry.To);
        Assert.Equal("Repairable", entry.Outcome);
        Assert.Equal(1, entry.Sidecars);
        Assert.Equal(1, preview.Repairable);
        Assert.Equal(0, preview.Blocked);

        // A preview touches nothing: both files are exactly where they were.
        Assert.True(_files.Contains(DamagedVideo));
        Assert.True(_files.Contains(DamagedSubtitle));
        Assert.Empty(_files.Moved);
    }

    [Fact]
    public async Task A_queued_run_repairs_the_library_and_records_itself_as_completed()
    {
        using var request = Authorized(HttpMethod.Post, "/api/imports/path-repair");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<RunDto>();
        Assert.True(Guid.TryParse(accepted!.RunId, out _));

        await DrainAsync();

        // The queued command ran to completion rather than spending its attempts on a broken read.
        var command = await QueuedRepairAsync();
        Assert.Equal(CommandState.Completed, command.State);
        Assert.Null(command.Error);

        // The video moved, and the subtitle beside it moved with it.
        Assert.True(_files.Contains(RepairedVideo));
        Assert.True(_files.Contains(RepairedSubtitle));
        Assert.False(_files.Contains(DamagedVideo));
        Assert.False(_files.Contains(DamagedSubtitle));

        // The rows followed the file, through MediaFileRelocated and the command it enqueues.
        var path = Assert.Single(await ListPathsAsync());
        Assert.Equal(_assetId, path.AssetId);
        Assert.Equal(RepairedVideo, path.FullPath);

        // And the library now has nothing left to repair, which is what makes a second pass safe.
        var preview = await PreviewAsync();
        Assert.Empty(preview!.Entries);
    }

    [Fact]
    public async Task A_second_run_over_a_repaired_library_moves_nothing()
    {
        await RunRepairAsync();
        var movesAfterFirstRun = _files.Moved.Count;

        await RunRepairAsync();

        Assert.Equal(movesAfterFirstRun, _files.Moved.Count);
        Assert.Equal(RepairedVideo, Assert.Single(await ListPathsAsync()).FullPath);
    }

    [Fact]
    public async Task An_anonymous_caller_reaches_neither_route()
    {
        using var previewResponse = await _client.GetAsync("/api/imports/path-repair");
        Assert.Equal(HttpStatusCode.Unauthorized, previewResponse.StatusCode);

        using var runResponse = await _client.PostAsync("/api/imports/path-repair", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, runResponse.StatusCode);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<PreviewDto?> PreviewAsync()
    {
        using var request = Authorized(HttpMethod.Get, "/api/imports/path-repair");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<PreviewDto>();
    }

    private async Task RunRepairAsync()
    {
        using var request = Authorized(HttpMethod.Post, "/api/imports/path-repair");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await DrainAsync();
    }

    private HttpRequestMessage Authorized(HttpMethod method, string requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        return request;
    }

    private async Task<IReadOnlyList<MediaVersionPath>> ListPathsAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILibraryQuery>().ListActiveVersionPathsAsync();
    }

    private async Task<QueuedCommand> QueuedRepairAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        return await operations.Commands
            .AsNoTracking()
            .SingleAsync(c => c.CommandType == ImportCommandNames.RepairLibraryPaths);
    }

    /// <summary>Alternates the relay and the command worker until both are quiet, exactly like the Host does.</summary>
    private async Task DrainAsync()
    {
        while (true)
        {
            var commands = await DrainCommandsAsync();
            var events = await DrainOutboxAsync();
            if (commands == 0 && events == 0)
            {
                return;
            }
        }
    }

    private async Task<int> DrainCommandsAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        var total = 0;
        int processed;
        while ((processed = await processor.ProcessBatchAsync()) > 0)
        {
            total += processed;
        }

        return total;
    }

    private async Task<int> DrainOutboxAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }

    private sealed record PreviewDto(List<PreviewEntryDto> Entries, int Repairable, int Blocked);

    private sealed record PreviewEntryDto(string AssetId, string From, string To, string Outcome, int Sidecars);

    private sealed record RunDto(string RunId);
}
