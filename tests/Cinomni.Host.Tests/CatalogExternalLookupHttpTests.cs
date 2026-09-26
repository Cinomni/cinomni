using System.Net;
using System.Text.Json;
using Cinomni.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// The "is this title already in the library" lookup the add pages make, through the route they call.
/// A provider can number films and shows independently, so the answer depends on which one is asked.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class CatalogExternalLookupHttpTests
{
    [Fact]
    public async Task The_lookup_answers_for_the_kind_it_is_asked_about()
    {
        await using var app = await RequestsHttpTests.StartAsync("cinomni_test_host_catalog_external_kind");
        var client = await RequestsHttpTests.SignedInClientAsync(app);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
            Assert.True((await commands.AddMovieAsync("Some Film", 2008, [new ExternalId(MetadataProvider.Tmdb, "1399")])).IsSuccess);
        }

        using (var film = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/works/by-external/Tmdb/1399?kind=Movie")))
        {
            Assert.Equal("Some Film", film.RootElement.GetProperty("title").GetString());
        }

        // The show with the same number is not in the library, whatever the film says.
        var show = await client.GetAsync("/api/catalog/works/by-external/Tmdb/1399?kind=Series");
        Assert.Equal(HttpStatusCode.NotFound, show.StatusCode);

        // A caller that names no kind is answered as before.
        var unqualified = await client.GetAsync("/api/catalog/works/by-external/Tmdb/1399");
        Assert.Equal(HttpStatusCode.OK, unqualified.StatusCode);
    }
}
