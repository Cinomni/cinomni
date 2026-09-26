using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cinomni.Catalog.Contracts;
using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Operations.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Drives the real Monitoring routes over HTTP, as a real signed-in member, against a real PostgreSQL
/// database. <c>ApiAuthorizationTests</c> only reads routing metadata and cannot see whether the handler
/// actually filters; <c>MonitoringBrowseTests</c> resolves <see cref="Cinomni.Monitoring.Application.MonitoringBrowse"/>
/// straight from DI and would stay green even if <c>MonitoringEndpoints</c> stopped binding it. This is
/// the layer where the original defect lived — binding <c>IMonitoringQuery</c> with no viewer — so it is
/// the layer that has to fail if that binding comes back.
/// <para>Its own database, like every other PostgreSQL-backed suite here.</para>
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class MonitoringAuthorizationHttpTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_monitoring_http";
    private const string AdminUsername = "operator";
    private const string MemberUsername = "household-member";
    private const string Password = "correct horse battery staple";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _memberToken = null!;
    private Guid _openWorkId;
    private Guid _restrictedWorkId;

    public async Task InitializeAsync()
    {
        _app = await MonitoringHttpTestHost.StartAsync(Database);
        _client = _app.GetTestClient();

        await using var scope = _app.Services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var catalogCommands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();

        var admin = await provisioning.CreateAdminAsync(AdminUsername, Password);
        Assert.True(admin.IsSuccess, admin.Error.Message);

        var member = await provisioning.CreateUserAsync(MemberUsername, Password, UserRole.Member);
        Assert.True(member.IsSuccess, member.Error.Message);
        _memberToken = (await sessions.IssueAsync(member.Value)).Token;

        var shelf = await collections.CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Restricted);
        Assert.True(shelf.IsSuccess, shelf.Error.Message);

        var open = await catalogCommands.AddMovieAsync("Heat", year: null, externalIds: []);
        Assert.True(open.IsSuccess, open.Error.Message);
        _openWorkId = open.Value.Value;

        var restricted = await catalogCommands.AddMovieAsync("Alien", year: null, externalIds: [], collection: shelf.Value);
        Assert.True(restricted.IsSuccess, restricted.Error.Message);
        _restrictedWorkId = restricted.Value.Value;

        await DrainAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_member_lists_neither_targets_nor_missing_targets_for_a_work_they_cannot_see()
    {
        var targets = await GetAsAsync<List<TargetDto>>("/api/monitoring/targets?limit=100", _memberToken);
        Assert.Contains(targets!, t => t.WorkId == _openWorkId.ToString());
        Assert.DoesNotContain(targets!, t => t.WorkId == _restrictedWorkId.ToString());

        var missing = await GetAsAsync<List<TargetDto>>("/api/monitoring/targets/missing?limit=100", _memberToken);
        Assert.Contains(missing!, t => t.WorkId == _openWorkId.ToString());
        Assert.DoesNotContain(missing!, t => t.WorkId == _restrictedWorkId.ToString());
    }

    [Fact]
    public async Task A_member_gets_404_not_403_for_the_per_work_routes_of_a_hidden_work()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/monitoring/works/{_restrictedWorkId}/target");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _memberToken);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var treeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/monitoring/works/{_restrictedWorkId}/targets");
        treeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _memberToken);
        using var treeResponse = await _client.SendAsync(treeRequest);

        Assert.Equal(HttpStatusCode.NotFound, treeResponse.StatusCode);
    }

    [Fact]
    public async Task Hidden_and_genuinely_absent_answer_the_same_404_on_the_per_work_routes()
    {
        // The oracle Finding 2 closes: a hidden work (has targets, caller may not see them) must read
        // exactly like a work id nobody has ever catalogued.
        using var hiddenRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/monitoring/works/{_restrictedWorkId}/target");
        hiddenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _memberToken);
        using var hiddenResponse = await _client.SendAsync(hiddenRequest);

        using var absentRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/monitoring/works/{Guid.NewGuid()}/target");
        absentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _memberToken);
        using var absentResponse = await _client.SendAsync(absentRequest);

        Assert.Equal(absentResponse.StatusCode, hiddenResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenResponse.StatusCode);
    }

    [Fact]
    public async Task A_principal_with_no_resolvable_identity_gets_401_on_the_listings()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/monitoring/targets");
        request.Headers.Add(MonitoringHttpTestHost.BrokenPrincipalHeader, "1");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_principal_with_no_resolvable_identity_gets_401_on_a_per_work_route()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/monitoring/works/{_openWorkId}/target");
        request.Headers.Add(MonitoringHttpTestHost.BrokenPrincipalHeader, "1");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<T?> GetAsAsync<T>(string requestUri, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>();
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

    private sealed record TargetDto(string WorkId);
}
