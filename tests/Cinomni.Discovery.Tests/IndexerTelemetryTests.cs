using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Search.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// A federated search swallows a failing indexer by design — one broken endpoint must not fail the
/// whole search — which means an indexer that has been returning nothing for a week is
/// indistinguishable downstream from a title nobody seeds. These pin the numbers that tell them apart,
/// and pin just as hard what must never appear beside them.
/// </summary>
public sealed class IndexerTelemetryTests : IAsyncLifetime
{
    private const string SecretTerm = "a title this household is looking for";

    private readonly SelectiveIndexerCatalog _catalog = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_telemetry", services =>
        {
            services.AddSingleton(_catalog);
            services.AddSingleton<IIndexerClient, SelectiveIndexerClient>();
        });

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        Assert.True((await admin.AddIndexerAsync(
            "Working", IndexerProtocol.Torznab, "https://working.example/torznab", 1)).IsSuccess);
        Assert.True((await admin.AddIndexerAsync(
            "Broken", IndexerProtocol.Torznab, "https://broken.example/torznab", 2)).IsSuccess);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_failing_indexer_is_counted_by_name_while_the_search_still_succeeds()
    {
        _catalog.FailFor("Broken");

        var recorder = new Recorder();
        using var listener = recorder.Listen();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
            var outcome = await search.SearchAsync(new SearchCriterion(SecretTerm, 2024, null, null, "Movie"));

            // The point of the whole design: one broken endpoint does not fail the search.
            Assert.Single(outcome.Candidates);
        }

        // Both indexers are timed, so a slow one is visible even when it eventually answers.
        Assert.Equal(2, recorder.Count("cinomni.indexer.search.duration"));

        var failure = Assert.Single(recorder.Longs("cinomni.indexer.search.failures"));
        Assert.Equal("Broken", failure[CinomniTelemetry.Tags.IndexerName]);
    }

    [Fact]
    public async Task No_measurement_carries_the_search_term_or_the_indexer_url()
    {
        var recorder = new Recorder();
        using var listener = recorder.Listen();

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IReleaseSearch>()
                .SearchAsync(new SearchCriterion(SecretTerm, 2024, null, null, "Movie"));
        }

        // What the household is looking for is nobody's business, a base URL carries the API key, and
        // both would make the cardinality of these series unbounded. Only the operator-chosen name and
        // a bounded outcome word are allowed.
        foreach (var tags in recorder.AllTags())
        {
            Assert.All(tags.Values, value =>
            {
                var text = value as string ?? string.Empty;
                Assert.DoesNotContain("title", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("example", text, StringComparison.OrdinalIgnoreCase);
            });

            Assert.All(tags.Keys, key => Assert.StartsWith("cinomni.", key, StringComparison.Ordinal));
        }
    }

    /// <summary>Answers for one indexer and throws for another, so both paths run in one search.</summary>
    private sealed class SelectiveIndexerCatalog
    {
        private readonly ConcurrentDictionary<string, bool> _failing = new(StringComparer.Ordinal);

        public void FailFor(string indexerName) => _failing[indexerName] = true;

        public bool ShouldFail(string indexerName) => _failing.ContainsKey(indexerName);
    }

    private sealed class SelectiveIndexerClient(SelectiveIndexerCatalog catalog) : IIndexerClient
    {
        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
            IndexerSummary indexer,
            IndexerCredential? credential,
            string? definitionContent,
            SearchCriterion criterion,
            CancellationToken cancellationToken = default)
        {
            if (catalog.ShouldFail(indexer.Name))
            {
                // The shape of a real failure: a message quoting the URL, which is why the recording
                // sites are given a name and never an exception.
                throw new HttpRequestException($"500 from {indexer.BaseUrl}?apikey=secret&q={criterion.Term}");
            }

            IReadOnlyList<ReleaseCandidate> candidates =
            [
                new ReleaseCandidate(
                    Guid.NewGuid().ToString(), "Something.2024.1080p", "magnet:?xt=urn:btih:0",
                    ReleaseProtocol.Torrent, 1_000_000L, 10, null, indexer.Name),
            ];

            return Task.FromResult(candidates);
        }
    }

    /// <summary>Plain BCL capture, filtered to this test's own indexers so a parallel class cannot leak in.</summary>
    private sealed class Recorder
    {
        private static readonly string[] OwnIndexers = ["Working", "Broken"];

        private readonly ConcurrentBag<(string Instrument, Dictionary<string, object?> Tags)> _seen = [];

        public MeterListener Listen()
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, meterListener) =>
                {
                    if (instrument.Meter.Name == CinomniTelemetry.MeterName
                        && instrument.Name.StartsWith("cinomni.indexer.", StringComparison.Ordinal))
                    {
                        meterListener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Capture(instrument.Name, tags));
            listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Capture(instrument.Name, tags));
            listener.Start();
            return listener;
        }

        public int Count(string instrument) => _seen.Count(entry => entry.Instrument == instrument);

        public IReadOnlyList<Dictionary<string, object?>> Longs(string instrument) =>
            _seen.Where(entry => entry.Instrument == instrument).Select(entry => entry.Tags).ToList();

        public IEnumerable<Dictionary<string, object?>> AllTags() => _seen.Select(entry => entry.Tags);

        private void Capture(string instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copied = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copied[tag.Key] = tag.Value;
            }

            if (copied.TryGetValue(CinomniTelemetry.Tags.IndexerName, out var name)
                && name is string indexerName
                && OwnIndexers.Contains(indexerName, StringComparer.Ordinal))
            {
                _seen.Add((instrument, copied));
            }
        }
    }
}
