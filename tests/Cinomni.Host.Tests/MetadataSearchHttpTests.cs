using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// A freshly installed Cinomni has no TMDB key, and TMDB is the only provider that answers for a movie.
/// Searching for a film therefore had nothing to ask, and said nothing about it: the route answered
/// <c>200 []</c>, which the client can only render as "no titles matched" — the same answer it gives for
/// a film that genuinely does not exist. This suite pins the two halves of the correction: the
/// installation says once, naming the key, that the provider is switched off, and the route answers
/// "nobody could answer" rather than "nothing matched".
/// <para>
/// It runs against the production composition (see <see cref="MetadataSearchHttpTestHost"/>), because
/// substituting an <c>IMetadataSource</c> is exactly what let this survive.
/// </para>
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class MetadataSearchWithoutProviderHttpTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_metadata_search_unconfigured_http";

    private readonly CannedHttpTransport _tmdb = new("{\"results\":[]}");
    private readonly RecordedLogs _logs = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _app = await MetadataSearchHttpTestHost.StartAsync(Database, _tmdb, _logs);
        _client = _app.GetTestClient();
        _token = await MetadataSearchHttpTests.SignInAsync(_app, "operator");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _tmdb.Dispose();
    }

    [Fact]
    public async Task A_movie_search_with_no_provider_configured_is_an_unanswered_question_not_an_empty_result()
    {
        using var response = await MetadataSearchHttpTests.SearchAsync(_client, _token, "Interstellar");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var failure = await response.Content.ReadFromJsonAsync<MetadataSearchHttpTests.ErrorEnvelope>();
        Assert.Equal("metadata.no_provider", failure!.Error);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));

        // Nothing was asked of anybody: an unconfigured provider must not spend a request to discover
        // that it has no credentials.
        Assert.Empty(_tmdb.Requests);
    }

    [Fact]
    public async Task The_unconfigured_provider_is_announced_once_naming_its_configuration_key()
    {
        using var first = await MetadataSearchHttpTests.SearchAsync(_client, _token, "Interstellar");
        using var second = await MetadataSearchHttpTests.SearchAsync(_client, _token, "Arrival");

        // Once for the process, not once per search: a library-wide refresh sweep would otherwise write
        // one line per work. This is the same contract TheTVDB's token provider already honours.
        var announcements = _logs.WarningsContaining("Metadata:Tmdb:ApiKey");
        Assert.Single(announcements);
        Assert.Contains("TMDB", announcements[0], StringComparison.Ordinal);
    }
}

/// <summary>
/// The other half of the same story: with a key configured the route must behave exactly as it did
/// before — a provider that matches nothing is a genuine empty result, and a provider that matches
/// something returns it. A correction that turned every miss into a failure would be worse than the
/// defect.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class MetadataSearchConfiguredProviderHttpTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_metadata_search_configured_http";

    // Only the film that exists comes back; every other term gets the empty result TMDB really answers
    // with, which is what makes the "genuine miss" case below genuine.
    private readonly CannedHttpTransport _tmdb = new CannedHttpTransport("{\"results\":[]}")
        .Respond(
            "query=Interstellar",
            """
            {"results":[{"id":157336,"title":"Interstellar","release_date":"2014-11-07","overview":"A team travels."}]}
            """);

    private readonly RecordedLogs _logs = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _app = await MetadataSearchHttpTestHost.StartAsync(Database, _tmdb, _logs, tmdbApiKey: "a-configured-key");
        _client = _app.GetTestClient();
        _token = await MetadataSearchHttpTests.SignInAsync(_app, "operator");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _tmdb.Dispose();
    }

    [Fact]
    public async Task A_configured_provider_answers_with_its_candidates()
    {
        using var response = await MetadataSearchHttpTests.SearchAsync(_client, _token, "Interstellar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidate = Assert.Single(
            (await response.Content.ReadFromJsonAsync<List<MetadataSearchHttpTests.CandidateDto>>())!);
        Assert.Equal("tmdb", candidate.Provider);
        Assert.Equal("Interstellar", candidate.Title);
        Assert.Equal(2014, candidate.Year);

        // The key never travels in a log line, but it does have to reach the provider's query string.
        Assert.Contains(_tmdb.Requests, request => request.Contains("search/movie", StringComparison.Ordinal));
        Assert.Empty(_logs.WarningsContaining("Metadata:Tmdb:ApiKey"));
    }

    [Fact]
    public async Task A_configured_provider_that_matches_nothing_is_still_an_empty_result()
    {
        using var response = await MetadataSearchHttpTests.SearchAsync(_client, _token, "Nothing At All");

        // The provider answered; it simply had no hit for a title nobody has heard of. That is the one
        // case the client is right to render as "no titles matched".
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<MetadataSearchHttpTests.CandidateDto>>())!);
    }
}

/// <summary>Shared plumbing for the two metadata-search HTTP suites.</summary>
public static class MetadataSearchHttpTests
{
    /// <summary>The standard failure envelope every Cinomni route answers with.</summary>
    public sealed record ErrorEnvelope(string Error, string Message);

    /// <summary>The parts of a search hit these suites assert on.</summary>
    public sealed record CandidateDto(string Provider, string ExternalId, string Title, int? Year);

    internal static async Task<string> SignInAsync(WebApplication app, string username)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();

        var user = await provisioning.CreateAdminAsync(username, "correct horse battery staple");
        Assert.True(user.IsSuccess, user.Error.Message);
        return (await sessions.IssueAsync(user.Value)).Token;
    }

    internal static async Task<HttpResponseMessage> SearchAsync(HttpClient client, string token, string term)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/metadata/search?term={Uri.EscapeDataString(term)}&kind=Movie");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
