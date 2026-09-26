using Cinomni.Metadata.Application;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// A provider with no API key answers nothing, and used to do so in silence. TMDB is the only provider
/// that covers a movie, so on a freshly installed Cinomni that silence <i>was</i> the "add a movie" flow:
/// every search came back empty and nothing anywhere said why.
/// <para>
/// These are the rules in isolation — that a provider knows it cannot answer, says so once, and spends no
/// request finding out. The same behaviour through the real composition, the real route and a real
/// database lives in <c>Cinomni.Host.Tests.MetadataSearchWithoutProviderHttpTests</c>.
/// </para>
/// </summary>
public sealed class DisabledProviderTests
{
    private const string BaseAddress = "https://provider.example/";

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly RecordingLogger _log = new();

    [Fact]
    public async Task An_unconfigured_tmdb_reports_itself_unavailable_and_asks_nobody()
    {
        var source = Tmdb(apiKey: string.Empty);

        Assert.False(source.IsAvailable);
        Assert.Empty(await source.SearchAsync(new MetadataProviderQuery("Interstellar", null, MetadataMediaKind.Movie)));
        Assert.Null(await source.FetchAsync("157336", MetadataMediaKind.Movie));

        // Not one request: a provider with no credential must not spend a call to be told so.
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task An_unconfigured_tmdb_names_the_key_that_would_switch_it_on_once()
    {
        var source = Tmdb(apiKey: string.Empty);

        source.AnnounceUnavailable();
        source.AnnounceUnavailable();
        await source.SearchAsync(new MetadataProviderQuery("Interstellar", null, MetadataMediaKind.Movie));

        var line = Assert.Single(_log.Warnings);
        Assert.Contains("TMDB is disabled", line, StringComparison.Ordinal);
        Assert.Contains("Metadata:Tmdb:ApiKey", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_tmdb_is_available_and_says_nothing()
    {
        var source = Tmdb(apiKey: "a-configured-key");

        source.AnnounceUnavailable();

        Assert.True(source.IsAvailable);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void An_unconfigured_tvdb_names_its_own_key_once()
    {
        var source = Tvdb(apiKey: string.Empty);

        source.AnnounceUnavailable();
        source.AnnounceUnavailable();

        Assert.False(source.IsAvailable);
        var line = Assert.Single(_log.Warnings);
        Assert.Contains("TheTVDB is disabled", line, StringComparison.Ordinal);
        Assert.Contains("Metadata:Tvdb:ApiKey", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_disabled_providers_each_get_their_own_line()
    {
        var notice = new DisabledProviderNotice(_log);

        notice.AnnounceOnce("TMDB", "Metadata:Tmdb:ApiKey", "Movies find nothing.");
        notice.AnnounceOnce("TheTVDB", "Metadata:Tvdb:ApiKey", "Series lose one provider.");
        notice.AnnounceOnce("TMDB", "Metadata:Tmdb:ApiKey", "Movies find nothing.");

        Assert.Equal(2, _log.Warnings.Count);
    }

    [Fact]
    public async Task A_search_with_no_available_provider_for_the_kind_fails_rather_than_returning_nothing()
    {
        var result = await SearchAsync([Unavailable("tmdb", MetadataMediaKind.Movie)], MetadataMediaKind.Movie);

        Assert.True(result.IsFailure);
        Assert.Equal(MetadataErrors.NoProvider, result.Error.Code);
        Assert.Contains("movies", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_with_no_available_provider_names_the_providers_that_lack_a_key()
    {
        var result = await SearchAsync(
            [Unavailable("tvdb", MetadataMediaKind.Series), Unavailable("tmdb", MetadataMediaKind.Series)],
            MetadataMediaKind.Series);

        // The person looking at the screen learns which provider is missing and what it lacks; the
        // configuration key itself stays in the log.
        Assert.Contains("TheTVDB", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("TMDB", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("API key", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Metadata:", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_the_providers_answered_with_nothing_is_still_an_empty_success()
    {
        var empty = new FakeMetadataSource { Candidates = [] };

        var result = await SearchAsync([empty], MetadataMediaKind.Movie);

        // The genuine miss the client is right to render as "no titles matched".
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task One_available_provider_is_enough_and_the_unavailable_one_still_explains_itself()
    {
        var unavailable = Unavailable("tvdb", MetadataMediaKind.Series);
        var available = FakeMetadataSource.ForSeries("tvmaze");

        var result = await SearchAsync([unavailable, available], MetadataMediaKind.Series);

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Value);
        // The provider nobody asked is the only one that can say why, so it is asked to say it.
        Assert.Equal(1, unavailable.Announcements);
        Assert.Empty(unavailable.Searches);
    }

    private static FakeMetadataSource Unavailable(string name, MetadataMediaKind kind) =>
        new(name, kind) { IsAvailable = false };

    private static async Task<Cinomni.Kernel.Results.Result<IReadOnlyList<MetadataCandidate>>> SearchAsync(
        IReadOnlyList<IMetadataSource> sources,
        MetadataMediaKind kind)
    {
        // A search touches no database: the service is a pure fan-out over its sources.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new MetadataOptions());
        foreach (var source in sources)
        {
            services.AddSingleton(source);
        }

        services.AddScoped<IMetadataSearch, MetadataSearchService>();

        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IMetadataSearch>().SearchAsync("frontier", year: null, kind);
    }

    private TmdbMetadataSource Tmdb(string apiKey) => new(
        _handler.CreateClient(BaseAddress),
        new TmdbProviderOptions { ApiKey = apiKey, BaseAddress = BaseAddress },
        new DisabledProviderNotice(_log),
        NullLogger<TmdbMetadataSource>.Instance);

    private TvdbMetadataSource Tvdb(string apiKey)
    {
        var options = new TvdbProviderOptions { ApiKey = apiKey, BaseAddress = BaseAddress };
        return new TvdbMetadataSource(
            _handler.CreateClient(BaseAddress),
            new TvdbTokenProvider(
                new SingleClientFactory(_handler, BaseAddress),
                options,
                new DisabledProviderNotice(_log),
                NullLogger<TvdbTokenProvider>.Instance),
            options,
            NullLogger<TvdbMetadataSource>.Instance);
    }

    /// <summary>Hands the TheTVDB login exchange the same scripted transport as the data client.</summary>
    private sealed class SingleClientFactory(FakeHttpMessageHandler handler, string baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler.CreateClient(baseAddress);
    }

    /// <summary>Keeps the warnings a provider emitted; the log line is the only evidence it never asked.</summary>
    private sealed class RecordingLogger : ILogger<DisabledProviderNotice>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
