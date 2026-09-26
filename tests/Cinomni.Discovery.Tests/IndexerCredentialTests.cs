using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// The master key lives in a process-wide environment variable and is read once, when
/// <c>AddOperations</c> composes the cipher — so a class that sets it cannot run beside one that
/// clears it. Mirrors <c>SettingsSecretsCollection</c> in the Operations tests, which exists for the
/// same reason and is not visible from this assembly.
/// </summary>
[CollectionDefinition(Serial, DisableParallelization = true)]
public sealed class IndexerSecretsCollection
{
    public const string Serial = "indexer-credential-master-key";
}

/// <summary>
/// Integration tests for the per-indexer credential store against a real PostgreSQL instance: a
/// stored secret reaches the transport, never comes back out, is bound to the indexer that owns it,
/// and degrades to an unauthenticated query rather than a failed search when it cannot be read.
/// </summary>
[Collection(IndexerSecretsCollection.Serial)]
[Trait("Category", "RequiresDatabase")]
public sealed class IndexerCredentialTests : IAsyncLifetime
{
    /// <summary>32 fixed bytes. A test key, never a real one, and it never leaves this file.</summary>
    private const string MasterKeyBase64 = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    private const string DefinitionJson = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/s?q={{term}}" }],
            "responseFormat": "Html",
            "rows": { "selector": "tr", "maxRows": 10 },
            "fields": {
              "title": { "selector": "a", "attribute": "Text" },
              "downloadUrl": { "selector": "a", "attribute": "Href" }
            }
          }
        }
        """;

    private string? _previousKey;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _previousKey = Environment.GetEnvironmentVariable("CINOMNI_SECRET_KEY");
        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", MasterKeyBase64);

        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_credentials", services =>
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
    public async Task A_stored_credential_reaches_the_transport_as_an_api_key()
    {
        var indexerId = await AddIndexerAsync("Keyed");

        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            var stored = await admin.SetCredentialAsync(indexerId, username: null, "s3cr3t");
            Assert.True(stored.IsSuccess, stored.IsFailure ? stored.Error.Message : null);
        }

        await SearchAsync();

        var recorded = RecordedFor("Keyed");
        Assert.Equal("s3cr3t", recorded.Credential?.Secret);
        // The whole point of storing it: the composed URL actually carries the key.
        Assert.Contains("apikey=s3cr3t", recorded.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_credential_reaches_the_installed_indexer_test()
    {
        var indexerId = await AddIndexerAsync("Tested");

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        await admin.SetCredentialAsync(indexerId, "operator", "s3cr3t");

        var result = await admin.TestIndexerAsync(indexerId);

        Assert.True(result.IsSuccess);
        var recorded = RecordedFor("Tested");
        Assert.Equal("operator", recorded.Credential?.Username);
        Assert.Equal("s3cr3t", recorded.Credential?.Secret);
    }

    [Fact]
    public async Task An_indexer_with_no_credential_is_still_searched_unauthenticated()
    {
        await AddIndexerAsync("Open");

        await SearchAsync();

        var recorded = RecordedFor("Open");
        Assert.Null(recorded.Credential);
        Assert.DoesNotContain("apikey", recorded.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_listing_reports_that_a_credential_exists_and_never_what_it_is()
    {
        var indexerId = await AddIndexerAsync("Listed");

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        await admin.SetCredentialAsync(indexerId, "operator", "s3cr3t");

        var listed = Assert.Single(await admin.ListIndexersAsync(), i => i.Name == "Listed");

        Assert.Equal(IndexerCredentialState.Readable, listed.CredentialState);
        // The username is an identifier and is meant to be visible; the secret has no field to be in.
        Assert.Equal("operator", listed.CredentialUsername);
        Assert.DoesNotContain("s3cr3t", System.Text.Json.JsonSerializer.Serialize(listed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_a_credential_returns_the_indexer_to_unauthenticated_queries()
    {
        var indexerId = await AddIndexerAsync("Cleared");

        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            await admin.SetCredentialAsync(indexerId, "operator", "s3cr3t");
            Assert.True((await admin.ClearCredentialAsync(indexerId)).IsSuccess);

            var listed = Assert.Single(await admin.ListIndexersAsync(), i => i.Name == "Cleared");
            Assert.Equal(IndexerCredentialState.None, listed.CredentialState);
            Assert.Null(listed.CredentialUsername);
        }

        await SearchAsync();
        Assert.Null(RecordedFor("Cleared").Credential);
    }

    [Fact]
    public async Task A_secret_is_bound_to_its_own_indexer_and_does_not_decrypt_for_another()
    {
        // The additional-authenticated-data binding, driven the way a stolen row would arrive: the
        // ciphertext is valid and the master key is right, but it belongs to a different indexer.
        var owner = await AddIndexerAsync("Owner");
        var thief = await AddIndexerAsync("Thief");

        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            await admin.SetCredentialAsync(owner, username: null, "s3cr3t");

            var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var ownerRow = await dbContext.Indexers.SingleAsync(i => i.Id == owner.Value);
            var thiefRow = await dbContext.Indexers.SingleAsync(i => i.Id == thief.Value);
            thiefRow.SetCredential(null, ownerRow.SecretCipher!, ownerRow.SecretNonce!, ownerRow.SecretKeyId!);
            await dbContext.SaveChangesAsync();
        }

        await SearchAsync();

        // The owner still reads its own secret; the copy authenticates against nothing and is
        // discarded, so that indexer is queried unauthenticated instead of with somebody else's key.
        Assert.Equal("s3cr3t", RecordedFor("Owner").Credential?.Secret);
        Assert.Null(RecordedFor("Thief").Credential);

        // And the listing says so, rather than reporting a populated row as a working credential:
        // the search degraded silently and correctly, the console must not repeat the silence.
        await using var scope2 = _provider.CreateAsyncScope();
        var listed = await scope2.ServiceProvider.GetRequiredService<IIndexerAdministration>().ListIndexersAsync();
        Assert.Equal(IndexerCredentialState.Readable, Assert.Single(listed, i => i.Name == "Owner").CredentialState);
        Assert.Equal(IndexerCredentialState.Corrupt, Assert.Single(listed, i => i.Name == "Thief").CredentialState);
    }

    [Fact]
    public async Task An_empty_secret_is_refused_rather_than_stored_as_a_blank_credential()
    {
        var indexerId = await AddIndexerAsync("Blank");

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var result = await admin.SetCredentialAsync(indexerId, "operator", "   ");

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.invalid_credential", result.Error.Code);
        Assert.Equal(
            IndexerCredentialState.None,
            Assert.Single(await admin.ListIndexersAsync(), i => i.Name == "Blank").CredentialState);
    }

    [Fact]
    public async Task An_unknown_indexer_is_reported_rather_than_silently_ignored()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var set = await admin.SetCredentialAsync(new IndexerId(Guid.NewGuid()), null, "s3cr3t");
        var cleared = await admin.ClearCredentialAsync(new IndexerId(Guid.NewGuid()));

        Assert.Equal("discovery.indexer_not_found", set.Error.Code);
        Assert.Equal("discovery.indexer_not_found", cleared.Error.Code);
    }

    [Fact]
    public async Task A_definition_indexer_carries_its_credential_even_though_login_is_not_executed_yet()
    {
        // The store is protocol-agnostic on purpose: the value is resolved and delivered, and only
        // the login sequence that would spend it is still missing.
        Guid definitionId;
        IndexerId indexerId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            var uploaded = await admin.UploadDefinitionAsync("Private Tracker", DefinitionJson);
            Assert.True(uploaded.IsSuccess);
            definitionId = uploaded.Value.Value;

            var added = await admin.AddIndexerAsync(
                "Private", IndexerProtocol.Definition, "https://idx.example", 1, uploaded.Value);
            Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
            indexerId = added.Value;

            await admin.SetCredentialAsync(indexerId, "operator", "hunter2");
        }

        await SearchAsync();

        var recorded = RecordedFor("Private");
        Assert.Equal("operator", recorded.Credential?.Username);
        Assert.Equal("hunter2", recorded.Credential?.Secret);
        Assert.Equal(definitionId, recorded.Indexer.DefinitionId?.Value);
    }

    [Fact]
    public async Task An_indexer_added_with_a_username_and_password_is_searched_with_them()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            var added = await admin.AddIndexerAsync(
                "Proxied", IndexerProtocol.Torznab, "https://proxy.example/torznab", 1,
                credential: new NewIndexerCredential("hunter2", "operator"));
            Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);

            var listed = Assert.Single(await admin.ListIndexersAsync(), i => i.Name == "Proxied");
            Assert.Equal(IndexerCredentialState.Readable, listed.CredentialState);
            Assert.Equal("operator", listed.CredentialUsername);
        }

        await SearchAsync();

        var recorded = RecordedFor("Proxied");
        Assert.Equal("operator", recorded.Credential?.Username);
        Assert.Equal("hunter2", recorded.Credential?.Secret);
        Assert.DoesNotContain("hunter2", recorded.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_username_and_password_are_refused_for_a_plain_http_torznab_indexer()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var added = await admin.AddIndexerAsync(
            "Cleartext", IndexerProtocol.Torznab, "http://idx.example/api", 1,
            credential: new NewIndexerCredential("hunter2", "operator"));
        var plain = await admin.AddIndexerAsync("Plain", IndexerProtocol.Torznab, "http://plain.example/api", 1);
        Assert.True(plain.IsSuccess, plain.IsFailure ? plain.Error.Message : null);
        var set = await admin.SetCredentialAsync(plain.Value, "operator", "hunter2");
        // An API key on the same indexer stays accepted: it is the indexer's own, not a reused password.
        var key = await admin.SetCredentialAsync(plain.Value, null, "k3y");

        Assert.Equal("discovery.insecure_credential", added.Error.Code);
        Assert.Equal("discovery.insecure_credential", set.Error.Code);
        Assert.True(key.IsSuccess);
        // A refused add adds nothing: the indexer never exists without the credential it was given.
        Assert.DoesNotContain(await admin.ListIndexersAsync(), i => i.Name == "Cleartext");
    }

    [Fact]
    public async Task A_colon_in_a_basic_auth_username_is_refused()
    {
        var indexerId = await AddIndexerAsync("Colon");

        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var result = await admin.SetCredentialAsync(indexerId, "op:erator", "hunter2");

        Assert.Equal("discovery.invalid_credential", result.Error.Code);
    }

    [Fact]
    public async Task An_indexer_added_with_a_blank_secret_is_not_added()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        var added = await admin.AddIndexerAsync(
            "Blank at birth", IndexerProtocol.Torznab, "https://idx.example/api", 1,
            credential: new NewIndexerCredential("  ", "operator"));

        Assert.Equal("discovery.invalid_credential", added.Error.Code);
        Assert.DoesNotContain(await admin.ListIndexersAsync(), i => i.Name == "Blank at birth");
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<IndexerId> AddIndexerAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var added = await admin.AddIndexerAsync(name, IndexerProtocol.Torznab, "https://idx.example/api", 1);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        return added.Value;
    }

    private async Task SearchAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
        await search.SearchAsync(new SearchCriterion("Interstellar", 2014, null, null, "Movie"));
    }

    private FakeIndexerCatalog.RecordedQuery RecordedFor(string indexerName) =>
        Assert.Single(_provider.GetRequiredService<FakeIndexerCatalog>().Queries, q => q.IndexerName == indexerName);
}
