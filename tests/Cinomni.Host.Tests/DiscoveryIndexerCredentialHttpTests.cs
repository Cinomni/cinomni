using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Identity.Application;
using Cinomni.Operations.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Boots the real Discovery HTTP surface three times over one PostgreSQL database — under the master
/// key the credentials were written with, under a rotated one, and under none — and reads
/// <c>GET /api/discovery/indexers</c> as an operator reads it.
/// <para>
/// The restart is the whole point: a stored credential only becomes unreadable when the process that
/// reads it comes up with a different key, and nothing short of composing the cipher again from the
/// environment reproduces that. Every body is captured once here and asserted by the facts below.
/// </para>
/// </summary>
public sealed class IndexerCredentialHttpFixture : IAsyncLifetime
{
    /// <summary>32 fixed bytes. Test keys, never real ones, and they never leave this file.</summary>
    public const string MasterKeyBase64 = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    /// <summary>A different 32 bytes: the same installation after someone rotated or lost the key.</summary>
    public const string RotatedMasterKeyBase64 = "ISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=";

    /// <summary>The value that must never appear in any response body, under any of the three keys.</summary>
    public const string Secret = "s3cr3t-indexer-api-key";

    public const string ReadableIndexer = "Keyed";
    public const string OpenIndexer = "Open";
    public const string MovedRowIndexer = "Moved";

    private const string Database = "cinomni_test_discovery_indexer_credential_http";
    private const string AdminUsername = "operator";
    private const string AdminPassword = "correct horse battery staple";

    /// <summary>The listing while the installation still holds the key the credentials were written with.</summary>
    public string BodyUnderOriginalKey { get; private set; } = string.Empty;

    /// <summary>The listing after a restart under a different master key.</summary>
    public string BodyUnderRotatedKey { get; private set; } = string.Empty;

    /// <summary>The listing after a restart with no master key at all.</summary>
    public string BodyWithoutMasterKey { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var previousKey = Environment.GetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, MasterKeyBase64);
            var token = await SeedAsync();

            BodyUnderOriginalKey = await ReadListingAsync(token, MasterKeyBase64);
            BodyUnderRotatedKey = await ReadListingAsync(token, RotatedMasterKeyBase64);
            BodyWithoutMasterKey = await ReadListingAsync(token, masterKey: null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, previousKey);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Creates the administrator and the three indexers, and stores the two credentials through the
    /// real write route. Returns the session token, which outlives every restart because it is stored
    /// (hashed) in the database rather than held in memory.
    /// </summary>
    private static async Task<string> SeedAsync()
    {
        var app = await DiscoveryDefinitionHttpTestHost.StartAsync(Database);
        try
        {
            string token;
            IndexerId readable;
            IndexerId moved;

            await using (var scope = app.Services.CreateAsyncScope())
            {
                var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
                    .CreateAdminAsync(AdminUsername, AdminPassword);
                Assert.True(admin.IsSuccess, admin.IsFailure ? admin.Error.Message : null);
                token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;

                var administration = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
                readable = await AddAsync(administration, ReadableIndexer);
                await AddAsync(administration, OpenIndexer);
                moved = await AddAsync(administration, MovedRowIndexer);
            }

            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await StoreCredentialAsync(client, readable);
            await StoreCredentialAsync(client, moved);

            // The row-theft case, driven the way it would actually arrive: valid ciphertext under the
            // right master key, sitting on an indexer that does not own it. The additional-authenticated
            // data is the indexer's own id, so this row can never be decrypted where it now sits.
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
                var owner = await dbContext.Indexers.SingleAsync(i => i.Id == readable.Value);
                var target = await dbContext.Indexers.SingleAsync(i => i.Id == moved.Value);
                target.SetCredential(
                    target.CredentialUsername, owner.SecretCipher!, owner.SecretNonce!, owner.SecretKeyId!);
                await dbContext.SaveChangesAsync();
            }

            return token;
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<IndexerId> AddAsync(IIndexerAdministration administration, string name)
    {
        var added = await administration.AddIndexerAsync(
            name, IndexerProtocol.Torznab, "https://idx.example/api", 1);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        return added.Value;
    }

    private static async Task StoreCredentialAsync(HttpClient client, IndexerId indexerId)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/discovery/indexers/{indexerId.Value}/credential",
            new { secret = Secret, username = AdminUsername });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Restarts the host with <paramref name="masterKey"/> in the environment, over the database the
    /// seed left behind, and returns the raw listing body.
    /// </summary>
    private static async Task<string> ReadListingAsync(string token, string? masterKey)
    {
        Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, masterKey);

        var app = await DiscoveryDefinitionHttpTestHost.StartAsync(Database, resetDatabase: false);
        try
        {
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync("/api/discovery/indexers");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

/// <summary>
/// The console must not claim a credential is configured when this installation cannot read it. The
/// listing used to answer with one boolean meaning "there are bytes in the row", so a rotated or lost
/// <c>CINOMNI_SECRET_KEY</c> produced a green "Configured" badge for a secret every search silently
/// ignored — the search path degraded correctly and the interface said nothing at all.
/// </summary>
[Collection(MasterKeyCollection.Serial)]
[Trait("Category", "RequiresDatabase")]
public sealed class DiscoveryIndexerCredentialHttpTests(IndexerCredentialHttpFixture fixture)
    : IClassFixture<IndexerCredentialHttpFixture>
{
    [Fact]
    public void An_indexer_with_no_credential_reports_no_credential()
    {
        Assert.Equal("None", StateOf(fixture.BodyUnderOriginalKey, IndexerCredentialHttpFixture.OpenIndexer));
        Assert.Equal("None", StateOf(fixture.BodyUnderRotatedKey, IndexerCredentialHttpFixture.OpenIndexer));
        Assert.Equal("None", StateOf(fixture.BodyWithoutMasterKey, IndexerCredentialHttpFixture.OpenIndexer));
    }

    [Fact]
    public void A_credential_this_installation_can_read_reports_readable()
    {
        Assert.Equal("Readable", StateOf(fixture.BodyUnderOriginalKey, IndexerCredentialHttpFixture.ReadableIndexer));
    }

    [Fact]
    public void A_credential_written_under_another_master_key_reports_the_rotation_rather_than_configured()
    {
        // The defect exactly: this is the installation that answered "Configured" for a secret it
        // could not decrypt and was already ignoring on every search.
        Assert.Equal(
            "MasterKeyChanged", StateOf(fixture.BodyUnderRotatedKey, IndexerCredentialHttpFixture.ReadableIndexer));
    }

    [Fact]
    public void A_credential_on_an_installation_with_no_master_key_reports_the_missing_key()
    {
        Assert.Equal(
            "MasterKeyMissing", StateOf(fixture.BodyWithoutMasterKey, IndexerCredentialHttpFixture.ReadableIndexer));
    }

    [Fact]
    public void A_row_that_fails_authentication_under_the_right_key_reports_a_corrupt_credential()
    {
        Assert.Equal("Corrupt", StateOf(fixture.BodyUnderOriginalKey, IndexerCredentialHttpFixture.MovedRowIndexer));
    }

    /// <summary>
    /// The username is an identifier and stays visible in every state — an operator who cannot see
    /// which account is configured cannot tell a wrong one from an unreadable one.
    /// </summary>
    [Fact]
    public void The_username_survives_a_credential_that_cannot_be_read()
    {
        Assert.Equal("operator", UsernameOf(fixture.BodyUnderRotatedKey, IndexerCredentialHttpFixture.ReadableIndexer));
        Assert.Equal(
            "operator", UsernameOf(fixture.BodyWithoutMasterKey, IndexerCredentialHttpFixture.ReadableIndexer));
    }

    /// <summary>
    /// The write-only contract, asserted against the bytes on the wire rather than against the record
    /// that produced them: no field, no state and no reason may ever carry the secret back out.
    /// </summary>
    [Fact]
    public void No_listing_body_carries_the_secret_in_any_state()
    {
        foreach (var body in AllBodies)
        {
            Assert.DoesNotContain(IndexerCredentialHttpFixture.Secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("cipher", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nonce", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Pins the whole shape of one indexer, in every state, so a field the secret could occupy cannot
    /// appear here by accident: adding one to the listing has to fail this test first.
    /// </summary>
    [Fact]
    public void The_listing_exposes_exactly_the_fields_it_is_allowed_to()
    {
        string[] allowed =
        [
            "id", "name", "protocol", "baseUrl", "priority", "enabled", "capabilities", "definitionId",
            "credentialUsername", "credentialState",
            "declaresLogin", "sessionState", "lastLoginAt",
            "settings", "catalogKey", "catalogVersion", "catalogSourceId",
            "lastTestedAt", "lastTestSucceeded", "lastTestCode", "lastTestMessage",
        ];

        foreach (var body in AllBodies)
        {
            using var document = JsonDocument.Parse(body);
            foreach (var indexer in document.RootElement.EnumerateArray())
            {
                Assert.Equal(allowed, indexer.EnumerateObject().Select(p => p.Name).ToArray());
            }
        }
    }

    /// <summary>
    /// A reason names a deployment fact, never a value: the environment variable an operator has to
    /// set is safe to say, the key material and the credential are not.
    /// </summary>
    [Fact]
    public void No_listing_body_carries_master_key_material()
    {
        foreach (var body in AllBodies)
        {
            Assert.DoesNotContain(IndexerCredentialHttpFixture.MasterKeyBase64, body, StringComparison.Ordinal);
            Assert.DoesNotContain(
                IndexerCredentialHttpFixture.RotatedMasterKeyBase64, body, StringComparison.Ordinal);
        }
    }

    private string[] AllBodies =>
        [fixture.BodyUnderOriginalKey, fixture.BodyUnderRotatedKey, fixture.BodyWithoutMasterKey];

    private static string? StateOf(string body, string indexerName) =>
        IndexerIn(body, indexerName).GetProperty("credentialState").GetString();

    private static string? UsernameOf(string body, string indexerName) =>
        IndexerIn(body, indexerName).GetProperty("credentialUsername").GetString();

    private static JsonElement IndexerIn(string body, string indexerName)
    {
        using var document = JsonDocument.Parse(body);
        return Assert.Single(
            document.RootElement.EnumerateArray().Where(i => i.GetProperty("name").GetString() == indexerName)
                .Select(i => i.Clone()));
    }
}
