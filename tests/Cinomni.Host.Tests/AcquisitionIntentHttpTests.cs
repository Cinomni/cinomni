using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Acquisition;
using Cinomni.Acquisition.Api;
using Cinomni.Acquisition.Contracts;
using Cinomni.Identity;
using Cinomni.Identity.Application;
using Cinomni.Identity.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// The goal detail the activity view reads and the retry it sends, through the mapped routes. A forgotten
/// projection compiles, so this reads the JSON: the release an attempt tried has to arrive named.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class AcquisitionIntentHttpTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_acquisition_intent_http";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Guid _intentId;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddOperations(
            $"Host=localhost;Port=5442;Database={Database};Username=cinomni;Password=cinomni_dev");
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddAcquisitionModule();

        _app = builder.Build();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        }

        await _app.Services.MigrateAcquisitionAsync();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapAcquisitionEndpoints();
        await _app.StartAsync();

        string token;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
                .CreateAdminAsync("operator", "correct horse battery staple");
            Assert.True(admin.IsSuccess, admin.IsFailure ? admin.Error.Message : null);
            token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;

            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            var targetId = Guid.NewGuid();
            _intentId = (await commands.CreateIntentAsync(targetId, Guid.NewGuid(), "All")).Value;
            await commands.SelectCandidateAsync(
                Guid.NewGuid(),
                targetId,
                "definition:D8DE5001",
                "http://cache.example/t.torrent",
                release: new AttemptRelease("World.War.Z.2013.1080p", "Example Indexer", 304, 12));
        }

        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task An_attempt_names_the_release_it_tried_with_its_indexer_and_swarm()
    {
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/acquisition/intents/{_intentId}");

        var release = detail.GetProperty("attempts")[0].GetProperty("release");
        Assert.Equal("World.War.Z.2013.1080p", release.GetProperty("title").GetString());
        Assert.Equal("Example Indexer", release.GetProperty("indexerName").GetString());
        Assert.Equal(304, release.GetProperty("seeders").GetInt32());
        Assert.Equal(12, release.GetProperty("leechers").GetInt32());
    }

    [Fact]
    public async Task A_goal_with_a_download_in_flight_cannot_be_retried_and_says_why()
    {
        var refused = await _client.PostAsync($"/api/acquisition/intents/{_intentId}/retry", content: null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AcquisitionErrors.NotRetryable, body.GetProperty("error").GetString());

        var unknown = await _client.PostAsync($"/api/acquisition/intents/{Guid.NewGuid()}/retry", content: null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
