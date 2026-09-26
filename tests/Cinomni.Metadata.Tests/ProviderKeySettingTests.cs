using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// The provider keys are settings an administrator can enter from the console, not only configuration
/// read at startup. A key saved there has to switch its provider on without a restart, and a replaced
/// key has to be the one the next request carries.
/// </summary>
public sealed class ProviderKeySettingTests
{
    private const string BaseAddress = "https://provider.example/";

    private readonly FakeHttpMessageHandler _handler = new();

    [Fact]
    public void Every_provider_key_is_a_secret_that_overrides_its_configuration_path()
    {
        Assert.All(MetadataProviderKeyDefinitions.All, definition =>
        {
            Assert.Equal(SettingKind.Secret, definition.Kind);
            Assert.True(definition.IsSecret);
        });
        Assert.Equal(
            ["Metadata:Tmdb:ApiKey", "Metadata:Tvdb:ApiKey", "Metadata:Tvdb:Pin"],
            MetadataProviderKeyDefinitions.All.Select(d => d.ConfigurationPath));
    }

    [Fact]
    public async Task A_tmdb_key_entered_as_a_setting_switches_the_provider_on_without_configuration()
    {
        _handler.Respond("search/movie", """{ "results": [] }""");
        var keys = new MutableLiveOptions<MetadataProviderKeys>(new MetadataProviderKeys { TmdbApiKey = "stored-key" });
        var source = Tmdb(configuredKey: string.Empty, keys);

        await source.SearchAsync(new MetadataProviderQuery("Interstellar", null, MetadataMediaKind.Movie));

        Assert.True(source.IsAvailable);
        Assert.Contains("api_key=stored-key", Assert.Single(_handler.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replaced_tmdb_key_is_the_one_the_next_request_carries()
    {
        _handler.Respond("search/movie", """{ "results": [] }""");
        var keys = new MutableLiveOptions<MetadataProviderKeys>(new MetadataProviderKeys { TmdbApiKey = "first-key" });
        var source = Tmdb(configuredKey: string.Empty, keys);
        var query = new MetadataProviderQuery("Interstellar", null, MetadataMediaKind.Movie);

        await source.SearchAsync(query);
        keys.Current = new MetadataProviderKeys { TmdbApiKey = "second-key" };
        await source.SearchAsync(query);

        Assert.Contains("api_key=second-key", _handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_stored_key_the_configured_one_still_applies()
    {
        var keys = new MutableLiveOptions<MetadataProviderKeys>(new MetadataProviderKeys());

        Assert.True(Tmdb(configuredKey: "configured-key", keys).IsAvailable);
    }

    [Fact]
    public async Task A_replaced_tvdb_key_signs_in_again_instead_of_reusing_the_old_token()
    {
        _handler.Respond("login", """{ "data": { "token": "a-token" } }""");
        var keys = new MutableLiveOptions<MetadataProviderKeys>(new MetadataProviderKeys { TvdbApiKey = "first-key" });
        var tokens = TvdbTokens(keys);

        await tokens.GetTokenAsync(CancellationToken.None);
        await tokens.GetTokenAsync(CancellationToken.None);
        keys.Current = new MetadataProviderKeys { TvdbApiKey = "second-key" };
        await tokens.GetTokenAsync(CancellationToken.None);

        // One sign-in per key: the cached token belongs to the key that obtained it.
        Assert.Equal(2, _handler.CountOf("login"));
    }

    [Fact]
    public async Task A_tvdb_key_removed_from_the_settings_turns_the_provider_off()
    {
        _handler.Respond("login", """{ "data": { "token": "a-token" } }""");
        var keys = new MutableLiveOptions<MetadataProviderKeys>(new MetadataProviderKeys { TvdbApiKey = "a-key" });
        var tokens = TvdbTokens(keys);
        await tokens.GetTokenAsync(CancellationToken.None);

        keys.Current = new MetadataProviderKeys();

        Assert.Null(await tokens.GetTokenAsync(CancellationToken.None));
    }

    private TmdbMetadataSource Tmdb(string configuredKey, ILiveOptions<MetadataProviderKeys> keys) => new(
        _handler.CreateClient(BaseAddress),
        new TmdbProviderOptions { ApiKey = configuredKey, BaseAddress = BaseAddress },
        new DisabledProviderNotice(NullLogger<DisabledProviderNotice>.Instance),
        NullLogger<TmdbMetadataSource>.Instance,
        keys: keys);

    private TvdbTokenProvider TvdbTokens(ILiveOptions<MetadataProviderKeys> keys) => new(
        new SingleClientFactory(_handler, BaseAddress),
        new TvdbProviderOptions { BaseAddress = BaseAddress },
        new DisabledProviderNotice(NullLogger<DisabledProviderNotice>.Instance),
        NullLogger<TvdbTokenProvider>.Instance,
        keys);

    private sealed class SingleClientFactory(FakeHttpMessageHandler handler, string baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler.CreateClient(baseAddress);
    }

    /// <summary>A live value the test moves by hand, standing in for an administrator saving a setting.</summary>
    private sealed class MutableLiveOptions<TOptions>(TOptions initial) : ILiveOptions<TOptions>
        where TOptions : class
    {
        public TOptions Current { get; set; } = initial;
    }
}
