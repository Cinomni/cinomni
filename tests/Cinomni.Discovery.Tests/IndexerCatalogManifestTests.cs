using System.Text.Json;
using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// A catalog manifest comes from a URL an administrator chose and is hostile input. Every rule that
/// keeps a bad one out is exercised here without I/O; a single bad entry rejects the whole manifest.
/// </summary>
public sealed class IndexerCatalogManifestTests
{
    private const string InvalidManifest = IndexerCatalogManifest.InvalidManifestCode;

    [Fact]
    public void A_valid_manifest_yields_its_entries_in_order_with_normalised_values()
    {
        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(
            CatalogManifests.Entry("alpha"),
            CatalogManifests.Entry("beta", "https://beta.indexer.example", requiresFlareSolverr: true, version: 3)));

        Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : null);
        Assert.Equal(["alpha", "beta"], parsed.Value.Select(entry => entry.Key));
        var beta = parsed.Value[1];
        Assert.Equal(3, beta.Version);
        Assert.Equal("https://beta.indexer.example/", Assert.Single(beta.BaseUrls));
        Assert.True(beta.RequiresFlareSolverr);
        Assert.True(beta.DefaultSettings.UseFlareSolverr);
        Assert.Equal(20, beta.DefaultPriority);
        Assert.Equal(ReleaseProtocol.Torrent, beta.ReleaseProtocol);
        Assert.True(IndexerDefinitionParser.Parse(beta.RawDefinition, strictSelectors: true).IsSuccess);
    }

    [Fact]
    public void An_empty_entry_list_is_a_valid_manifest()
    {
        var parsed = IndexerCatalogManifest.Parse("""{ "schemaVersion": 1, "entries": [] }""");

        Assert.True(parsed.IsSuccess);
        Assert.Empty(parsed.Value);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{ "schemaVersion": 2, "entries": [] }""")]
    [InlineData("""{ "entries": [] }""")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("""{ "schemaVersion": "1", "entries": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "entries": [ null ] }""")]
    public void A_malformed_manifest_is_rejected_by_name(string json)
    {
        var parsed = IndexerCatalogManifest.Parse(json);

        Assert.True(parsed.IsFailure);
        Assert.Equal(InvalidManifest, parsed.Error.Code);
    }

    [Fact]
    public void A_manifest_nested_deeper_than_allowed_is_rejected_without_throwing()
    {
        var deep = """{ "schemaVersion": 1, "entries": [], "x": """ + new string('[', 200) + new string(']', 200) + "}";

        var parsed = IndexerCatalogManifest.Parse(deep);

        Assert.Equal(InvalidManifest, parsed.Error.Code);
    }

    [Fact]
    public void An_entry_declaring_a_login_rejects_the_whole_manifest()
    {
        // The definition is valid on its own, so the refusal is the catalog's rule, not the parser's.
        Assert.True(IndexerDefinitionParser.Parse(CatalogManifests.DefinitionWithLogin, strictSelectors: true).IsSuccess);

        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(
            CatalogManifests.Entry("fine"),
            CatalogManifests.Entry("private", definition: CatalogManifests.DefinitionWithLogin)));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
        Assert.Contains("Entry 2", parsed.Error.Message, StringComparison.Ordinal);
        Assert.Contains("login", parsed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_key_rejects_the_whole_manifest()
    {
        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(
            CatalogManifests.Entry("same"), CatalogManifests.Entry("same", "https://other.indexer.example/")));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
        Assert.Contains("more than once", parsed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_entries_than_the_cap_are_rejected()
    {
        var entries = Enumerable.Range(0, IndexerCatalogManifest.MaxEntries + 1)
            .Select(i => CatalogManifests.Entry($"entry-{i}"))
            .ToArray();

        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(entries));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
    }

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://10.0.0.8/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://user:secret@indexer.example/")]
    [InlineData("ftp://indexer.example/")]
    [InlineData("/relative/path")]
    public void A_base_url_that_is_not_public_http_is_rejected(string baseUrl)
    {
        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(CatalogManifests.Entry("x", baseUrl)));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
        // Never echoed: the value could carry a token.
        Assert.DoesNotContain(baseUrl, parsed.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("key", "Upper")]
    [InlineData("key", "")]
    [InlineData("key", "-leading")]
    [InlineData("key", "has space")]
    [InlineData("key", "trailing\n")]
    [InlineData("key", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("version", 0)]
    [InlineData("protocol", "Torznab")]
    [InlineData("releaseProtocol", "Usenet")]
    [InlineData("defaultPriority", 51)]
    [InlineData("defaultPriority", 0)]
    [InlineData("baseUrls", null)]
    [InlineData("definition", "not an object")]
    [InlineData("definition", null)]
    public void An_entry_member_out_of_bounds_is_rejected(string member, object? value)
    {
        var entry = CatalogManifests.Entry("x");
        entry[member] = member == "baseUrls" ? Array.Empty<string>() : value;

        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(entry));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
    }

    [Fact]
    public void Names_and_descriptions_are_bounded_and_printable()
    {
        var longName = CatalogManifests.Entry("x");
        longName["name"] = new string('n', 201);
        var controlName = CatalogManifests.Entry("y");
        controlName["name"] = "bell\u0007";
        var longDescription = CatalogManifests.Entry("z");
        longDescription["description"] = new string('d', 1001);

        Assert.Equal(InvalidManifest, IndexerCatalogManifest.Parse(CatalogManifests.Manifest(longName)).Error.Code);
        Assert.Equal(InvalidManifest, IndexerCatalogManifest.Parse(CatalogManifests.Manifest(controlName)).Error.Code);
        Assert.Equal(InvalidManifest, IndexerCatalogManifest.Parse(CatalogManifests.Manifest(longDescription)).Error.Code);
    }

    [Fact]
    public void An_entry_that_requires_FlareSolverr_but_defaults_it_off_could_never_install()
    {
        var entry = CatalogManifests.Entry("x", requiresFlareSolverr: true);
        ((Dictionary<string, object?>)entry["defaultSettings"]!)["useFlareSolverr"] = false;

        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(entry));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
    }

    [Theory]
    [InlineData("minimumSeeders", -1)]
    [InlineData("queryLimit", 0)]
    [InlineData("grabLimit", -3)]
    [InlineData("limitsUnit", "Fortnight")]
    [InlineData("limitsUnit", "1")]
    public void Default_settings_out_of_bounds_are_rejected(string member, object value)
    {
        var entry = CatalogManifests.Entry("x");
        ((Dictionary<string, object?>)entry["defaultSettings"]!)[member] = value;

        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(entry));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
    }

    [Fact]
    public void An_invalid_definition_is_reported_with_the_parser_code()
    {
        var broken = CatalogManifests.Definition.Replace("table.results tbody tr", "tr[", StringComparison.Ordinal);

        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(CatalogManifests.Entry("x", definition: broken)));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
        Assert.Contains("discovery.definition.invalid_selector", parsed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_whose_result_kind_disagrees_with_the_entry_is_rejected()
    {
        var usenet = CatalogManifests.Definition.Replace("\"Torrent\"", "\"Usenet\"", StringComparison.Ordinal);

        var parsed = IndexerCatalogManifest.Parse(CatalogManifests.Manifest(CatalogManifests.Entry("x", definition: usenet)));

        Assert.Equal(InvalidManifest, parsed.Error.Code);
    }

    [Fact]
    public void The_documented_example_manifest_is_valid_and_names_only_example_hosts()
    {
        var reference = File.ReadAllText(FindRepositoryFile("docs/indexer-catalog-format.md"));
        var start = reference.IndexOf("```json", StringComparison.Ordinal) + "```json".Length;
        var example = reference[start..reference.IndexOf("```", start, StringComparison.Ordinal)];

        var parsed = IndexerCatalogManifest.Parse(example);

        Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : null);
        Assert.All(parsed.Value.SelectMany(entry => entry.BaseUrls),
            url => Assert.EndsWith(".example.org", new Uri(url).Host, StringComparison.Ordinal));
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate {relativePath} above the test binary.");
    }

    [Fact]
    public void Unknown_members_are_ignored_for_forward_compatibility()
    {
        var entry = CatalogManifests.Entry("x");
        entry["futureField"] = new { nested = true };
        var manifest = JsonSerializer.Serialize(new { schemaVersion = 1, entries = new[] { entry }, publisher = "someone" });

        Assert.True(IndexerCatalogManifest.Parse(manifest).IsSuccess);
    }
}
