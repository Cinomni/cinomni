using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Messaging;
using Cinomni.Metadata.Providers;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Composition-root tests for the production adapters. Every other suite substitutes fakes, so without
/// these the real registration graph — three typed clients, the TheTVDB token singleton, the per-provider
/// options each adapter now takes, and the continuing-series job — is only ever exercised by starting the
/// Host.
/// </summary>
public sealed class MetadataAdapterRegistrationTests
{
    [Fact]
    public void Every_production_adapter_resolves()
    {
        using var provider = Build();

        var names = provider.GetServices<IMetadataSource>().Select(source => source.Name).Order().ToList();

        Assert.Equal(["tmdb", "tvdb", "tvmaze"], names);
    }

    [Fact]
    public void Tmdb_and_tvdb_cover_series_and_tvmaze_covers_only_series()
    {
        using var provider = Build();
        var sources = provider.GetServices<IMetadataSource>().ToDictionary(s => s.Name);

        // tmdb is Providers[0]: a series refresh routed to the primary provider used to find no source
        // that covered the kind and quietly do nothing.
        Assert.Contains(MetadataMediaKind.Series, sources["tmdb"].SupportedKinds);
        Assert.Contains(MetadataMediaKind.Series, sources["tvdb"].SupportedKinds);
        Assert.Equal([MetadataMediaKind.Series], sources["tvmaze"].SupportedKinds.ToList());
    }

    [Fact]
    public void An_installation_with_no_keys_has_no_provider_that_can_answer_for_a_movie()
    {
        // The state every installation starts in. TMDB is the only movie provider, so if it can report
        // itself available without a key the whole "add a movie" flow silently answers nothing.
        using var provider = BuildWithoutKeys();
        var sources = provider.GetServices<IMetadataSource>().ToDictionary(s => s.Name);

        Assert.False(sources["tmdb"].IsAvailable);
        Assert.False(sources["tvdb"].IsAvailable);
        // TVMaze needs no credential, so it is always available — and covers series only.
        Assert.True(sources["tvmaze"].IsAvailable);
        Assert.DoesNotContain(
            sources.Values.Where(source => source.IsAvailable),
            source => source.SupportedKinds.Contains(MetadataMediaKind.Movie));
    }

    [Fact]
    public void A_configured_key_makes_the_provider_available()
    {
        using var provider = Build();

        Assert.True(provider.GetServices<IMetadataSource>().Single(s => s.Name == "tmdb").IsAvailable);
    }

    [Fact]
    public void The_continuing_series_job_is_registered_with_the_configured_interval()
    {
        using var provider = Build(options => options.ContinuingSeriesSweepInterval = TimeSpan.FromMinutes(30));

        var job = Assert.Single(
            provider.GetServices<ScheduledJobRegistration>(),
            registration => registration.Name == "metadata.refresh-continuing-series");

        Assert.Equal(MetadataCommandNames.RefreshContinuingSeries, job.CommandName);
        Assert.Equal(TimeSpan.FromMinutes(30), job.Interval);
        Assert.IsType<RefreshContinuingSeriesCommand>(job.CommandFactory());
    }

    /// <summary>The production graph exactly as a fresh installation composes it: no provider keys at all.</summary>
    private static ServiceProvider BuildWithoutKeys()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddMetadataAdapters();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ServiceProvider Build(Action<Application.MetadataOptions>? configureProfile = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddMetadataAdapters(
            configureProfile,
            configureTmdb: options => options.ApiKey = "unused-in-this-test",
            configureTvdb: options => options.ApiKey = "unused-in-this-test");
        return services.BuildServiceProvider(validateScopes: true);
    }
}
