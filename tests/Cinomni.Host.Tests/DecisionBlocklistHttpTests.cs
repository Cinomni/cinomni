using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Kernel.Results;
using Cinomni.Decision;
using Cinomni.Decision.Api;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Identity;
using Cinomni.Identity.Application;
using Cinomni.Identity.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// The block list and the interactive-search candidate are what the console reads. A forgotten
/// projection compiles, so this drives the mapped routes and reads the JSON.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class DecisionBlocklistHttpTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_decision_blocklist_http";
    private static readonly Guid SearchTargetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SearchExecution = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Guid _evaluationId;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddOperations(
            $"Host=localhost;Port=5442;Database={Database};Username=cinomni;Password=cinomni_dev");
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddDecisionModule();
        builder.Services.AddScoped<IInteractiveSearch, StubSearch>();
        builder.Services.AddScoped<IReleaseSearchResults, StubResults>();

        _app = builder.Build();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        }

        await _app.Services.MigrateDecisionAsync();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapDecisionEndpoints();
        await _app.StartAsync();

        string token;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
                .CreateAdminAsync("operator", "correct horse battery staple");
            Assert.True(admin.IsSuccess, admin.IsFailure ? admin.Error.Message : null);
            token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;

            var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
            var profileId = await db.Profiles.Select(p => p.Id).FirstAsync();
            _evaluationId = Uuid7.New();
            db.ReleaseEvaluations.Add(new ReleaseEvaluationRecord
            {
                Id = _evaluationId,
                ProfileId = profileId,
                SearchId = SearchExecution,
                TargetId = SearchTargetId,
                ReleaseGuid = "g-http",
                ReleaseTitle = "Bad.Release.1080p",
                Verdict = Verdict.Accepted,
                EvaluatorVersion = "test",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
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
    public async Task Blocking_is_what_the_list_returns_and_a_search_says_the_candidate_is_blocked()
    {
        var refused = await _client.PostAsJsonAsync(
            $"/api/decision/evaluations/{_evaluationId}/block", new { reason = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var blocked = await _client.PostAsJsonAsync(
            $"/api/decision/evaluations/{_evaluationId}/block", new { reason = "Known bad encode" });
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        var created = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("g-http", created.GetProperty("releaseGuid").GetString());
        Assert.Equal("Known bad encode", created.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(created.GetProperty("id").GetString()));

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/decision/blocks");
        var row = Assert.Single(list.GetProperty("blocks").EnumerateArray());
        Assert.Equal("Known bad encode", row.GetProperty("reason").GetString());
        Assert.False(list.GetProperty("truncated").GetBoolean());

        var search = await _client.PostAsync($"/api/decision/targets/{SearchTargetId}/search", null);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var candidate = (await search.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("candidates")[0];
        Assert.True(candidate.GetProperty("blocked").GetBoolean());
        Assert.Equal("00000000-0000-0000-0000-000000000001", candidate.GetProperty("blockId").GetString());
        Assert.Equal("Known bad encode", candidate.GetProperty("blockReason").GetString());
        Assert.False(candidate.GetProperty("grabOverridesVerdict").GetBoolean());
        Assert.Equal(7, candidate.GetProperty("leechers").GetInt32());

        using var anonymous = _app.GetTestClient();
        var denied = await anonymous.GetAsync("/api/decision/blocks");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task The_evaluation_history_says_where_each_release_came_from_and_its_swarm()
    {
        var history = await _client.GetFromJsonAsync<JsonElement>($"/api/decision/targets/{SearchTargetId}/evaluations");

        var row = Assert.Single(history.EnumerateArray());
        Assert.Equal("Example Indexer", row.GetProperty("indexerName").GetString());
        Assert.Equal(304, row.GetProperty("seeders").GetInt32());
        Assert.Equal(12, row.GetProperty("leechers").GetInt32());
    }

    /// <summary>The search that found the evaluated release, as Discovery would read it back.</summary>
    private sealed class StubResults : IReleaseSearchResults
    {
        public Task<IReadOnlyList<ReleaseCandidate>> GetResultsAsync(
            SearchExecutionId executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReleaseCandidate>>(executionId.Value == SearchExecution
                ? [new ReleaseCandidate("g-http", "Bad.Release.1080p", "magnet:?xt=urn:btih:g", ReleaseProtocol.Torrent, 1, 304, null, "Example Indexer", Leechers: 12)]
                : []);

        public Task<SearchRequestContext?> GetRequestContextAsync(
            SearchExecutionId executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SearchRequestContext?>(null);
    }

    private sealed class StubSearch : IInteractiveSearch
    {
        public Task<Result<InteractiveSearchResult>> SearchAsync(
            Guid targetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<InteractiveSearchResult>.Success(new InteractiveSearchResult(
                targetId,
                Guid.Empty,
                Guid.Empty,
                "Matrix",
                "Movie",
                [
                    new EvaluatedCandidate(
                        new ReleaseEvaluationId(Guid.Empty),
                        "g-http",
                        "Bad.Release.1080p",
                        "Idx",
                        "Torrent",
                        1,
                        1,
                        null,
                        Verdict.RejectedPermanent,
                        0,
                        1,
                        false,
                        [],
                        Blocked: true,
                        BlockId: Guid.Parse("00000000-0000-0000-0000-000000000001"),
                        BlockReason: "Known bad encode",
                        GrabOverridesVerdict: false,
                        Leechers: 7),
                ])));

        public Task<Result<ManualSelection>> GrabAsync(Guid evaluationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
