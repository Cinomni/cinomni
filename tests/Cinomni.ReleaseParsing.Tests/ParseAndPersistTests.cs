using System.Collections.Concurrent;
using System.Text.Json;
using Cinomni.Kernel.Messaging;
using Cinomni.ReleaseParsing.Contracts;
using Cinomni.ReleaseParsing.Messaging;
using Cinomni.ReleaseParsing.Parsing;
using Cinomni.ReleaseParsing.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.ReleaseParsing.Tests;

/// <summary>
/// Integration tests for the audit path against a real PostgreSQL instance: ParseAndPersist writes
/// the audit record and emits ReleaseParsed atomically, and an unparseable title persists nothing.
/// </summary>
public sealed class ParseAndPersistTests : IAsyncLifetime
{
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await ReleaseParsingTestHost.CreateAsync("cinomni_test_parsing", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<ReleaseParsed>, ReleaseParsedSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Parsing_and_persisting_writes_the_audit_and_emits_release_parsed()
    {
        await HandleAsync(new ParseAndPersistCommand("The.Matrix.1999.1080p.BluRay.x264-SPARKS"));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var parsingDb = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

            var record = await parsingDb.ParsedReleases.SingleAsync();
            Assert.Equal("the-matrix.1999.1080p.bluray", record.CanonicalKey);
            Assert.Equal("SPARKS", record.ReleaseGroup);
            Assert.Equal(1999, record.Year);
            Assert.Contains("Bluray", record.QualityJson);

            Assert.Equal(1, await operationsDb.Outbox
                .CountAsync(m => m.EventType == ReleaseParsingEventNames.ReleaseParsed));
        }

        await DrainOutboxAsync();

        Assert.Contains("the-matrix.1999.1080p.bluray", _sink.CanonicalKeys);
    }

    [Fact]
    public async Task A_title_at_the_parser_limit_is_persisted_with_its_derived_fields_fitted()
    {
        // The parser accepts 1000 characters; the canonical key derived from them has a 500 column.
        var title = string.Join('.', Enumerable.Repeat("Longword", 100)) + ".2010.1080p.BluRay.x264-GRP";
        Assert.True(title.Length <= 1000);

        await HandleAsync(new ParseAndPersistCommand(title));

        await using var scope = _provider.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>().ParsedReleases.SingleAsync();
        Assert.True(record.CanonicalKey.Length <= 500);
        Assert.Equal(title, record.SourceTitle);
    }

    [Fact]
    public async Task Parsing_the_same_title_twice_is_idempotent()
    {
        const string title = "Inception.2010.1080p.BluRay.x264-GRP";
        await HandleAsync(new ParseAndPersistCommand(title));
        await HandleAsync(new ParseAndPersistCommand(title));

        await using var scope = _provider.CreateAsyncScope();
        var parsingDb = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        // A retry reuses the deterministic id: one audit row, one event.
        Assert.Equal(1, await parsingDb.ParsedReleases.CountAsync());
        Assert.Equal(1, await operationsDb.Outbox
            .CountAsync(m => m.EventType == ReleaseParsingEventNames.ReleaseParsed));
    }

    [Fact]
    public async Task Persisting_an_episode_writes_the_numbering_columns_and_json()
    {
        await HandleAsync(new ParseAndPersistCommand("The.Wire.S02E05-E06.1080p.WEB-DL-GRP"));

        await using var scope = _provider.CreateAsyncScope();
        var parsingDb = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();

        var record = await parsingDb.ParsedReleases.SingleAsync();
        Assert.Equal(ReleaseType.MultiEpisode, record.ReleaseType);
        Assert.Equal(2, record.Season);
        Assert.Null(record.SeasonTo);
        Assert.Equal(5, record.EpisodeFrom);
        Assert.Equal(6, record.EpisodeTo);
        Assert.Null(record.AbsoluteEpisode);
        Assert.Null(record.AirDate);
        Assert.Null(record.Part);
        Assert.Equal("the-wire.s02e05-e06.1080p.webdl", record.CanonicalKey);

        // Read the jsonb back through a parser: PostgreSQL normalises key order and whitespace.
        Assert.NotNull(record.NumberingJson);
        using var numbering = JsonDocument.Parse(record.NumberingJson);
        Assert.Equal(2, numbering.RootElement.GetProperty("season").GetInt32());
        Assert.Equal([5, 6], numbering.RootElement.GetProperty("episodes").EnumerateArray().Select(e => e.GetInt32()));
        Assert.False(numbering.RootElement.GetProperty("isComplete").GetBoolean());

        // The scalars are what the new ix_parsed_releases_season_episode index serves.
        Assert.Equal(1, await parsingDb.ParsedReleases.CountAsync(r => r.Season == 2 && r.EpisodeFrom == 5));
    }

    [Fact]
    public async Task Reparsing_the_same_title_updates_one_row()
    {
        // The row is keyed on the source title, so bumping the parser version must not fork it.
        const string title = "The.Wire.S02E05.1080p.WEB-DL-GRP";
        await HandleAsync(new ParseAndPersistCommand(title));
        await HandleAsync(new ParseAndPersistCommand(title));

        await using var scope = _provider.CreateAsyncScope();
        var parsingDb = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        var record = await parsingDb.ParsedReleases.SingleAsync();
        Assert.Equal(DeterministicId.From(title), record.Id);
        Assert.Equal(ReleaseParser.Version, record.ParserVersion);
        Assert.Equal(1, await operationsDb.Outbox
            .CountAsync(m => m.EventType == ReleaseParsingEventNames.ReleaseParsed));
    }

    [Fact]
    public async Task The_rule_version_row_is_seeded_for_the_new_parser_version()
    {
        await _provider.MigrateReleaseParsingAsync();
        // Seeding is idempotent: a second run must not add a duplicate row.
        await _provider.MigrateReleaseParsingAsync();

        await using var scope = _provider.CreateAsyncScope();
        var parsingDb = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();

        var seeded = await parsingDb.ParseRuleVersions.SingleAsync(v => v.Version == ReleaseParser.Version);
        Assert.Equal("2.0.0", seeded.Version);
        Assert.False(string.IsNullOrWhiteSpace(seeded.Notes));
    }

    [Fact]
    public async Task An_unparseable_title_persists_nothing_and_emits_nothing()
    {
        await HandleAsync(new ParseAndPersistCommand("   "));

        await using var scope = _provider.CreateAsyncScope();
        var parsingDb = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        Assert.Empty(await parsingDb.ParsedReleases.ToListAsync());
        Assert.Equal(0, await operationsDb.Outbox
            .CountAsync(m => m.EventType == ReleaseParsingEventNames.ReleaseParsed));
    }

    private async Task HandleAsync(ParseAndPersistCommand command)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ParseAndPersistCommand>>();
        Assert.True((await handler.HandleAsync(command)).IsSuccess);
    }

    private async Task DrainOutboxAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        while (await relay.ProcessBatchAsync() > 0)
        {
        }
    }

    private sealed class EventSink
    {
        public ConcurrentBag<string> CanonicalKeys { get; } = [];
    }

    private sealed class ReleaseParsedSink(EventSink sink) : IEventHandler<ReleaseParsed>
    {
        public Task HandleAsync(ReleaseParsed domainEvent, CancellationToken cancellationToken = default)
        {
            sink.CanonicalKeys.Add(domainEvent.CanonicalKey);
            return Task.CompletedTask;
        }
    }
}
