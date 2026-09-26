using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Integration tests for the Catalog flow against a real PostgreSQL instance: adding a movie
/// publishes WorkAdded atomically and the relay delivers it; external ids deduplicate without
/// minting a new identity; and a movie with no external id is valid.
/// </summary>
public sealed class CatalogFlowTests : IAsyncLifetime
{
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await CatalogTestHost.CreateAsync("cinomni_test_catalog", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<WorkAdded>, WorkAddedHandler>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Adding_a_movie_writes_work_and_event_atomically_then_relay_delivers()
    {
        WorkId workId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
            var result = await commands.AddMovieAsync("The Matrix", 1999,
                [new ExternalId(MetadataProvider.Tmdb, "603")]);
            Assert.True(result.IsSuccess);
            workId = result.Value;
        }

        // Same commit: the work row, its external id, and the outbox event.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

            var work = await catalogDb.Works.Include(w => w.ExternalIdentifiers).SingleAsync();
            Assert.Equal("The Matrix", work.Title);
            Assert.Equal("matrix", work.SortTitle);
            Assert.Single(work.ExternalIdentifiers);

            var message = await operationsDb.Outbox.SingleAsync();
            Assert.Equal(CatalogEventNames.WorkAdded, message.EventType);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
            Assert.Equal(1, await relay.ProcessBatchAsync());
        }

        Assert.Contains(workId.Value, _sink.Works);
    }

    [Fact]
    public async Task Adding_the_same_external_id_returns_the_existing_work()
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var query = scope.ServiceProvider.GetRequiredService<ICatalogQuery>();

        var first = await commands.AddMovieAsync("Inception", 2010, [new ExternalId(MetadataProvider.Tmdb, "27205")]);
        var second = await commands.AddMovieAsync("Inception (dup)", 2010, [new ExternalId(MetadataProvider.Tmdb, "27205")]);

        // Same identity, not a new one; only one work exists.
        Assert.Equal(first.Value, second.Value);
        Assert.Single(await query.ListAsync());

        var found = await query.FindByExternalIdAsync(MetadataProvider.Tmdb, "27205");
        Assert.NotNull(found);
        Assert.Equal(first.Value, found!.Id);
    }

    [Fact]
    public async Task A_film_and_a_show_that_share_a_provider_id_are_two_works()
    {
        // TMDB numbers films and shows independently: movie 1399 and tv 1399 are unrelated titles. Keyed
        // on provider and id alone, adding the show returned the film, and every lookup for the show
        // answered with a film.
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var query = scope.ServiceProvider.GetRequiredService<ICatalogQuery>();

        var film = await commands.AddMovieAsync("Some Film", 2008, [new ExternalId(MetadataProvider.Tmdb, "1399")]);
        var show = await commands.AddSeriesAsync("Some Show", 2011, [new ExternalId(MetadataProvider.Tmdb, "1399")]);

        Assert.NotEqual(film.Value, show.Value);
        Assert.Equal(show.Value, (await query.FindByExternalIdAsync(MetadataProvider.Tmdb, "1399", WorkKind.Series))!.Id);
        Assert.Equal(film.Value, (await query.FindByExternalIdAsync(MetadataProvider.Tmdb, "1399", WorkKind.Movie))!.Id);

        // Within a kind the rule-16 dedupe is unchanged.
        var again = await commands.AddSeriesAsync("Some Show (dup)", 2011, [new ExternalId(MetadataProvider.Tmdb, "1399")]);
        Assert.Equal(show.Value, again.Value);
    }

    [Fact]
    public async Task A_movie_with_no_external_id_is_valid()
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var query = scope.ServiceProvider.GetRequiredService<ICatalogQuery>();

        var result = await commands.AddMovieAsync("Homemade Film", null, []);
        Assert.True(result.IsSuccess);

        var work = await query.GetByIdAsync(result.Value);
        Assert.NotNull(work);
        Assert.Empty(work!.ExternalIds);
    }

    private sealed class EventSink
    {
        public ConcurrentBag<Guid> Works { get; } = [];
    }

    private sealed class WorkAddedHandler(EventSink sink) : IEventHandler<WorkAdded>
    {
        public Task HandleAsync(WorkAdded domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Works.Add(domainEvent.WorkId);
            return Task.CompletedTask;
        }
    }
}
