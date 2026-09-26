using System.Net;
using Cinomni.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Removing a title through the route the work pages call: the work is gone from every read afterwards,
/// and a second removal, or one of a work that never existed, is a 404 rather than a silent success.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class CatalogRemovalHttpTests
{
    [Fact]
    public async Task A_removed_work_is_gone_and_removing_it_again_is_not_found()
    {
        await using var app = await RequestsHttpTests.StartAsync("cinomni_test_host_catalog_removal");
        var client = await RequestsHttpTests.SignedInClientAsync(app);

        WorkId workId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
            workId = (await commands.AddMovieAsync("World War Z", 2013, [new ExternalId(MetadataProvider.Tmdb, "72190")])).Value;
        }

        var removed = await client.DeleteAsync($"/api/catalog/works/{workId}?deleteFiles=true");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var read = await client.GetAsync($"/api/catalog/works/{workId}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        var again = await client.DeleteAsync($"/api/catalog/works/{workId}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Contains("catalog.work_not_found", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
