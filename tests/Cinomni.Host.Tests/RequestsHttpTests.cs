using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Catalog;
using Cinomni.Catalog.Api;
using Cinomni.Identity;
using Cinomni.Identity.Api;
using Cinomni.Identity.Application;
using Cinomni.Identity.Persistence;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Requests;
using Cinomni.Requests.Api;
using Cinomni.Requests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// What kind of title a request is for, through the HTTP surface the web client binds to. The field
/// was missing entirely once — every request, series included, was catalogued as a movie — and a
/// field a projection forgets is invisible from every test that builds the record by hand.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class RequestsHttpTests
{
    private const string Database = "cinomni_test_host_requests";

    [Fact]
    public async Task A_series_request_is_recorded_and_listed_as_a_series()
    {
        await using var app = await StartAsync($"{Database}_series");
        var client = await SignedInClientAsync(app);

        var submitted = await client.PostAsJsonAsync(
            "/api/requests/",
            new { title = "Game of Thrones", year = 2011, provider = "tmdb", externalId = "1399", kind = "Series" });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/requests/"));
        var request = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal("Series", request.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_request_that_names_no_kind_is_for_a_movie()
    {
        await using var app = await StartAsync($"{Database}_default");
        var client = await SignedInClientAsync(app);

        // What every client sent before the field existed.
        var submitted = await client.PostAsJsonAsync(
            "/api/requests/", new { title = "The Matrix", year = 1999, provider = "tmdb", externalId = "603" });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/requests/"));
        Assert.Equal("Movie", Assert.Single(list.RootElement.EnumerateArray()).GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("Documentary")]
    [InlineData("7")]
    public async Task A_kind_that_is_not_a_movie_or_a_series_is_refused(string kind)
    {
        await using var app = await StartAsync($"{Database}_invalid_{kind.ToLowerInvariant()}");
        var client = await SignedInClientAsync(app);

        var submitted = await client.PostAsJsonAsync(
            "/api/requests/", new { title = "Anything", provider = "tmdb", externalId = "1", kind });

        Assert.Equal(HttpStatusCode.BadRequest, submitted.StatusCode);
        using var body = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        Assert.Equal("requests.invalid_kind", body.RootElement.GetProperty("error").GetString());
    }

    internal static async Task<HttpClient> SignedInClientAsync(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
            .CreateAdminAsync("owner", "correct-horse");
        Assert.True(admin.IsSuccess, admin.IsSuccess ? null : admin.Error.Message);
        var token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Identity, Catalog and Requests over real HTTP, against a real database.</summary>
    internal static async Task<WebApplication> StartAsync(string database)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddOperations(
            $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev");
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddCatalogModule();
        builder.Services.AddRequestsModule();
        builder.Services.AddSingleton<IMetadataRefresh, NoMetadataRefresh>();

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
            await app.Services.MigrateCatalogAsync();
            await scope.ServiceProvider.GetRequiredService<RequestsDbContext>().Database.MigrateAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIdentityEndpoints();
        app.MapCatalogEndpoints();
        app.MapRequestEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class NoMetadataRefresh : IMetadataRefresh
    {
        public Task RefreshAsync(
            Guid workId,
            string provider,
            string externalId,
            MetadataMediaKind kind = MetadataMediaKind.Movie,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
