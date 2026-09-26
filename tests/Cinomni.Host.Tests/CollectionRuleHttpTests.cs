using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cinomni.Catalog;
using Cinomni.Catalog.Api;
using Cinomni.Catalog.Contracts;
using Cinomni.Identity;
using Cinomni.Identity.Api;
using Cinomni.Identity.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// Rule-based placement through the routes the web client calls, asserting the fields it reads: the
/// rule shape, a preview's counts and per-title target, the applied count, a collection's rule
/// priority, and the error envelope for a refused rule or order.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class CollectionRuleHttpTests
{
    private static readonly object AlienRule = new
    {
        // An unsaved rule arrives with an empty id from the web client; it means "new".
        id = "",
        name = "Aliens",
        conditions = new[] { new { field = "Title", @operator = "Contains", values = new[] { "alien" } } },
    };

    [Fact]
    public async Task A_rule_is_previewed_saved_read_back_and_a_pin_is_released()
    {
        await using var app = await StartAsync("cinomni_test_host_collection_rules");
        var client = await RequestsHttpTests.SignedInClientAsync(app);

        WorkId alien;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            alien = (await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
                .AddMovieAsync("Alien", 1979, [new ExternalId(MetadataProvider.Tmdb, "348")])).Value;
        }

        var created = await client.PostAsJsonAsync(
            "/api/catalog/collections", new { name = "Sci-fi horror", kind = "Mixed", accessMode = "Restricted" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string shelf;
        using (var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            shelf = body.RootElement.GetProperty("collectionId").GetString()!;
        }

        using (var collections = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/collections")))
        {
            var byName = collections.RootElement.EnumerateArray()
                .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("rulePriority").GetInt32());
            Assert.Equal(0, byName["Library"]);
            Assert.Equal(1, byName["Sci-fi horror"]);
        }

        var previewed = await client.PostAsJsonAsync($"/api/catalog/collections/{shelf}/rules/preview", new[] { AlienRule });
        Assert.Equal(HttpStatusCode.OK, previewed.StatusCode);
        using (var preview = JsonDocument.Parse(await previewed.Content.ReadAsStringAsync()))
        {
            var root = preview.RootElement;
            Assert.Equal(1, root.GetProperty("matched").GetInt32());
            Assert.Equal(1, root.GetProperty("wouldMove").GetInt32());
            Assert.Equal(0, root.GetProperty("pinnedSkipped").GetInt32());
            var work = Assert.Single(root.GetProperty("works").EnumerateArray().ToList());
            Assert.Equal(alien.ToString(), work.GetProperty("id").GetString());
            Assert.Equal("Alien", work.GetProperty("title").GetString());
            Assert.Equal(1979, work.GetProperty("year").GetInt32());
            Assert.Equal("Movie", work.GetProperty("kind").GetString());
            Assert.Equal("Library", work.GetProperty("currentCollectionName").GetString());
            Assert.Equal(shelf, work.GetProperty("targetCollectionId").GetString());
            Assert.Equal("Sci-fi horror", work.GetProperty("targetCollectionName").GetString());
            Assert.False(work.GetProperty("pinned").GetBoolean());
        }

        var saved = await client.PutAsJsonAsync($"/api/catalog/collections/{shelf}/rules", new[] { AlienRule });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var applied = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, applied.RootElement.GetProperty("moved").GetInt32());
        }

        using (var rules = JsonDocument.Parse(await client.GetStringAsync($"/api/catalog/collections/{shelf}/rules")))
        {
            var rule = Assert.Single(rules.RootElement.EnumerateArray().ToList());
            Assert.True(Guid.TryParse(rule.GetProperty("id").GetString(), out _));
            Assert.Equal(shelf, rule.GetProperty("collectionId").GetString());
            Assert.Equal("Aliens", rule.GetProperty("name").GetString());
            var condition = Assert.Single(rule.GetProperty("conditions").EnumerateArray().ToList());
            Assert.Equal("Title", condition.GetProperty("field").GetString());
            Assert.Equal("Contains", condition.GetProperty("operator").GetString());
            Assert.Equal("alien", Assert.Single(condition.GetProperty("values").EnumerateArray().ToList()).GetString());
        }

        Assert.Equal(shelf, await CollectionOfAsync(client, alien));

        // A move by hand pins; releasing the pin hands the title straight back to the rules.
        var library = await DefaultCollectionIdAsync(client);
        var moved = await client.PutAsJsonAsync($"/api/catalog/works/{alien}/collection", new { collectionId = library });
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        Assert.Equal(library, await CollectionOfAsync(client, alien));

        var unpinned = await client.DeleteAsync($"/api/catalog/works/{alien}/collection-pin");
        Assert.Equal(HttpStatusCode.NoContent, unpinned.StatusCode);
        Assert.Equal(shelf, await CollectionOfAsync(client, alien));
    }

    [Fact]
    public async Task A_refused_rule_or_order_answers_in_the_error_envelope()
    {
        await using var app = await StartAsync("cinomni_test_host_collection_rules_refused");
        var client = await RequestsHttpTests.SignedInClientAsync(app);
        var library = await DefaultCollectionIdAsync(client);

        var empty = await client.PutAsJsonAsync(
            $"/api/catalog/collections/{library}/rules",
            new[] { new { id = (string?)null, name = "Everything", conditions = Array.Empty<object>() } });
        await AssertErrorAsync(empty, HttpStatusCode.BadRequest, "catalog.rule.no_conditions");

        var unknown = await client.PostAsJsonAsync($"/api/catalog/collections/{Guid.NewGuid()}/rules/preview", new[] { AlienRule });
        await AssertErrorAsync(unknown, HttpStatusCode.NotFound, "catalog.collection_not_found");

        var incomplete = await client.PutAsJsonAsync(
            "/api/catalog/collections/rule-priority", new { collectionIds = Array.Empty<Guid>() });
        await AssertErrorAsync(incomplete, HttpStatusCode.BadRequest, "catalog.rule_priority.incomplete");

        var noWork = await client.DeleteAsync($"/api/catalog/works/{Guid.NewGuid()}/collection-pin");
        await AssertErrorAsync(noWork, HttpStatusCode.NotFound, "catalog.work_not_found");
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("message").GetString()));
    }

    private static async Task<string> DefaultCollectionIdAsync(HttpClient client)
    {
        using var collections = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/collections"));
        return collections.RootElement.EnumerateArray()
            .Single(c => c.GetProperty("isDefault").GetBoolean())
            .GetProperty("id").GetString()!;
    }

    private static async Task<string?> CollectionOfAsync(HttpClient client, WorkId work)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync($"/api/catalog/works/{work}"));
        return body.RootElement.GetProperty("collectionId").GetString();
    }

    /// <summary>Identity and Catalog over real HTTP, with enums on the wire by name as the Host sends them.</summary>
    private static async Task<WebApplication> StartAsync(string database)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddOperations(
            $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev");
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddCatalogModule();
        builder.Services.Configure<JsonOptions>(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
            await app.Services.MigrateCatalogAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIdentityEndpoints();
        app.MapCatalogEndpoints();
        await app.StartAsync();
        return app;
    }
}
