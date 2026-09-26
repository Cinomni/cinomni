using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Unit tests for the definition-driven query composer. Each fact pins the exact substituted URL:
/// a wrong one reaches a real site, or comes back as an empty result indistinguishable from a
/// genuine "nothing available" — the same stakes <see cref="TorznabQueryBuilderTests"/> covers.
/// </summary>
public sealed class DefinitionQueryBuilderTests
{
    private static readonly DefinitionFieldRule DummyField = new("a", DefinitionFieldAttribute.Text);
    private static readonly DefinitionFields Fields = new(DummyField, DummyField);
    private static readonly DefinitionRowRule Rows = new("tr.result", 50);

    [Fact]
    public void Substitutes_term_and_category_and_url_encodes_them()
    {
        var document = Document(new DefinitionSearchRequest(
            ["Movie"], DefinitionHttpMethod.Get, "https://idx.example/search?q={{term}}&cat={{category}}",
            new Dictionary<string, string> { ["Movie"] = "51" }));
        var criterion = new SearchCriterion("The Wire & Co", 2002, null, null, "Movie");

        var result = DefinitionQueryBuilder.Build(document, criterion);

        Assert.True(result.IsSuccess);
        Assert.Equal(DefinitionHttpMethod.Get, result.Value.Method);
        Assert.Equal("https://idx.example/search?q=The%20Wire%20%26%20Co&cat=51", result.Value.Url.AbsoluteUri);
    }

    [Fact]
    public void Folds_season_and_episode_into_the_free_text_term()
    {
        var document = Document(new DefinitionSearchRequest(
            ["Series", "Season", "Episode"], DefinitionHttpMethod.Get, "https://idx.example/search?q={{term}}"));
        var criterion = new SearchCriterion("The Wire", 2002, null, null, "Episode", SeasonNumber: 2, EpisodeNumber: 5);

        var result = DefinitionQueryBuilder.Build(document, criterion);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://idx.example/search?q=The%20Wire%20S02E05", result.Value.Url.AbsoluteUri);
    }

    [Fact]
    public void Empty_category_map_entry_substitutes_nothing()
    {
        var document = Document(new DefinitionSearchRequest(
            ["Movie"], DefinitionHttpMethod.Get, "https://idx.example/search?q={{term}}&cat={{category}}"));
        var criterion = new SearchCriterion("Interstellar", 2014, null, null, "Movie");

        var result = DefinitionQueryBuilder.Build(document, criterion);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://idx.example/search?q=Interstellar&cat=", result.Value.Url.AbsoluteUri);
    }

    [Fact]
    public void Resolves_root_relative_templates_against_the_configured_origin()
    {
        var document = Document(new DefinitionSearchRequest(
            ["Movie"], DefinitionHttpMethod.Get, "/search/{{term}}/1/"));

        var result = DefinitionQueryBuilder.Build(
            document,
            new SearchCriterion("Sintel", 2010, null, null, "Movie"),
            new Uri("https://indexer.example/"));

        Assert.True(result.IsSuccess);
        Assert.Equal("https://indexer.example/search/Sintel/1/", result.Value.Url.AbsoluteUri);
    }

    [Fact]
    public void Fails_gracefully_when_no_request_matches_the_content_kind()
    {
        var document = Document(new DefinitionSearchRequest(
            ["Movie"], DefinitionHttpMethod.Get, "https://idx.example/search?q={{term}}"));
        var criterion = new SearchCriterion("The Wire", 2002, null, null, "Episode");

        var result = DefinitionQueryBuilder.Build(document, criterion);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.query.no_matching_request", result.Error.Code);
    }

    private static IndexerDefinitionDocument Document(DefinitionSearchRequest request) => new(
        1, ReleaseProtocol.Torrent, new DefinitionSearch([request], DefinitionResponseFormat.Html, Rows, Fields));
}

/// <summary>
/// Links on an indexer's site resolve to the same http(s) address on every operating system. On Linux a
/// root-relative path parses as an absolute file:/// URI, and trying "absolute?" first sent it there.
/// </summary>
public sealed class DefinitionLinkResolutionTests
{
    private static readonly Uri Page = new("https://tracker.example/torrents/details.php?id=7");

    [Theory]
    [InlineData("/dl/1.torrent", "https://tracker.example/dl/1.torrent")]
    [InlineData("dl/1.torrent", "https://tracker.example/torrents/dl/1.torrent")]
    [InlineData("https://tracker.example/dl/1.torrent", "https://tracker.example/dl/1.torrent")]
    [InlineData("//cdn.example/1.torrent", "https://cdn.example/1.torrent")]
    [InlineData("  /dl/2.torrent  ", "https://tracker.example/dl/2.torrent")]
    public void A_link_resolves_against_the_site_it_was_found_on(string link, string expected)
    {
        Assert.True(DefinitionQueryBuilder.TryResolve(Page, link, out var resolved));
        Assert.Equal(expected, resolved.ToString());
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://tracker.example/1.torrent")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Anything_that_is_not_http_is_refused(string link) =>
        Assert.False(DefinitionQueryBuilder.TryResolve(Page, link, out _));

    [Fact]
    public void A_magnet_passes_the_resolve_transform_untouched()
    {
        var resolved = FieldTransforms.ResolveRelativeUrl("magnet:?xt=urn:btih:abc", Page);

        Assert.True(resolved.IsSuccess);
        Assert.Equal("magnet:?xt=urn:btih:abc", resolved.Value);
    }
}
