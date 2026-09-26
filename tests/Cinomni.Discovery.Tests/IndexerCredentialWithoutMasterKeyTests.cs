using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Search.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// The same store on an installation that never set <c>CINOMNI_SECRET_KEY</c> — the default, since
/// nothing forces one. Writing a credential is refused with a named reason; everything else about
/// Discovery keeps working, because an indexer with no credential is an ordinary indexer.
/// <para>
/// A separate class from <see cref="IndexerCredentialTests"/> because the cipher is composed once,
/// from the environment, when the host is built: the two cannot share one.
/// </para>
/// </summary>
[Collection(IndexerSecretsCollection.Serial)]
[Trait("Category", "RequiresDatabase")]
public sealed class IndexerCredentialWithoutMasterKeyTests : IAsyncLifetime
{
    private string? _previousKey;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _previousKey = Environment.GetEnvironmentVariable("CINOMNI_SECRET_KEY");
        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", null);

        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_no_master_key", services =>
        {
            services.AddSingleton<FakeIndexerCatalog>();
            services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
        });
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", _previousKey);
    }

    [Fact]
    public async Task Storing_a_credential_is_refused_with_a_code_this_module_owns()
    {
        var indexerId = await AddIndexerAsync("Unkeyed");

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var result = await admin.SetCredentialAsync(indexerId, null, "s3cr3t");

        Assert.True(result.IsFailure);
        // Not the platform's settings.* code: this failure surfaces on /api/discovery, and a caller
        // reading it should not be sent looking at a settings surface they never touched.
        Assert.Equal(IndexerCredentialProtector.SecretsUnavailableErrorCode, result.Error.Code);
        // The reason names the variable an operator has to set, and never a value.
        Assert.Contains("CINOMNI_SECRET_KEY", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_persisted_by_a_refused_write()
    {
        var indexerId = await AddIndexerAsync("Untouched");

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        await admin.SetCredentialAsync(indexerId, "operator", "s3cr3t");

        // Encryption happens before the transaction opens, so a refusal leaves the row exactly as it
        // was — not a half-written credential with a username and no secret.
        var listed = Assert.Single(await admin.ListIndexersAsync(), i => i.Name == "Untouched");
        Assert.Equal(IndexerCredentialState.None, listed.CredentialState);
        Assert.Null(listed.CredentialUsername);
    }

    [Fact]
    public async Task An_indexer_added_with_a_credential_is_not_added_at_all()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var added = await admin.AddIndexerAsync(
            "Keyless install", IndexerProtocol.Torznab, "https://idx.example/api", 1,
            credential: new NewIndexerCredential("s3cr3t"));

        Assert.Equal(IndexerCredentialProtector.SecretsUnavailableErrorCode, added.Error.Code);
        Assert.DoesNotContain(await admin.ListIndexersAsync(), i => i.Name == "Keyless install");
    }

    [Fact]
    public async Task Searching_still_works_because_an_unkeyed_indexer_is_an_ordinary_indexer()
    {
        await AddIndexerAsync("Ordinary");

        await using (var scope = _provider.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
            await search.SearchAsync(new SearchCriterion("Interstellar", 2014, null, null, "Movie"));
        }

        var recorded = Assert.Single(
            _provider.GetRequiredService<FakeIndexerCatalog>().Queries, q => q.IndexerName == "Ordinary");
        Assert.Null(recorded.Credential);
    }

    private async Task<IndexerId> AddIndexerAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var added = await admin.AddIndexerAsync(name, IndexerProtocol.Torznab, "https://idx.example/api", 1);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        return added.Value;
    }
}
