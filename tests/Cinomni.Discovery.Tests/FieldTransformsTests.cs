using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Unit tests for the closed vocabulary of definition field transforms — each one must fail
/// cleanly on hostile/malformed input rather than throw, since the input comes from parsing an
/// indexer's response.
/// </summary>
public sealed class FieldTransformsTests
{
    [Theory]
    [InlineData("120", 120)]
    [InlineData("  42  ", 42)]
    [InlineData("22,451", 22451)]
    public void ParseInt_reads_a_whole_number(string raw, int expected)
    {
        var result = FieldTransforms.ParseInt(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.5")]
    public void ParseInt_fails_cleanly_on_hostile_input(string? raw)
    {
        var result = FieldTransforms.ParseInt(raw);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.transform.parse_int_failed", result.Error.Code);
    }

    [Theory]
    [InlineData("1.2 GB", 1288490189L)]
    [InlineData("700MB", 734003200L)]
    [InlineData("2 TB", 2199023255552L)]
    [InlineData("512B", 512L)]
    [InlineData("4.8 GB1", 5153960755L)]
    [InlineData("749.2 MB 0", 785593139L)]
    [InlineData("351 bytes", 351L)]
    public void ParseSize_reads_a_human_readable_size(string raw, long expectedBytes)
    {
        var result = FieldTransforms.ParseSize(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedBytes, result.Value);
    }

    /// <summary>
    /// A directory-listing style suffix. Apache-style listings and a great many tracker result
    /// tables abbreviate the unit to a single letter ("473K", "54.9M"), and every one of them means
    /// the binary unit. Rejecting these made every release from such a site report zero bytes.
    /// </summary>
    [Theory]
    [InlineData("473K", 484352L)]
    [InlineData("34.5K", 35328L)]
    [InlineData("54.9M", 57566822L)]
    [InlineData("2G", 2147483648L)]
    [InlineData("1 T", 1099511627776L)]
    public void ParseSize_reads_a_single_letter_unit_as_its_binary_unit(string raw, long expectedBytes)
    {
        var result = FieldTransforms.ParseSize(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedBytes, result.Value);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2 Furlongs")]
    [InlineData("GB")]
    public void ParseSize_fails_cleanly_on_hostile_input(string? raw)
    {
        var result = FieldTransforms.ParseSize(raw);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.transform.parse_size_failed", result.Error.Code);
    }

    [Fact]
    public void ParseDate_reads_an_exact_declared_format()
    {
        var result = FieldTransforms.ParseDate("2024-11-12", "yyyy-MM-dd");

        Assert.True(result.IsSuccess);
        Assert.Equal(2024, result.Value.Year);
        Assert.Equal(11, result.Value.Month);
        Assert.Equal(12, result.Value.Day);
    }

    [Fact]
    public void ParseDate_normalizes_a_non_zero_offset_to_utc()
    {
        // Npgsql refuses to persist a DateTimeOffset whose Offset isn't zero into timestamptz — a
        // site publishing in its own local time (here Sydney, +10:00) must come out of the parser
        // already converted, not merely tagged "universal" the way AssumeUniversal alone would leave it.
        var result = FieldTransforms.ParseDate("2024-11-12T09:30:00+10:00", "yyyy-MM-ddTHH:mm:sszzz");

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.Zero, result.Value.Offset);
        Assert.Equal(new DateTimeOffset(2024, 11, 11, 23, 30, 0, TimeSpan.Zero), result.Value);
    }

    [Theory]
    [InlineData("12/11/2024", "yyyy-MM-dd")]
    [InlineData("not a date", "yyyy-MM-dd")]
    [InlineData(null, "yyyy-MM-dd")]
    public void ParseDate_fails_cleanly_when_the_value_does_not_match_the_format(string? raw, string format)
    {
        var result = FieldTransforms.ParseDate(raw, format);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.transform.parse_date_failed", result.Error.Code);
    }

    [Fact]
    public void Trim_never_throws_on_null()
    {
        Assert.Equal(string.Empty, FieldTransforms.Trim(null));
        Assert.Equal("hello", FieldTransforms.Trim("  hello  "));
    }

    [Fact]
    public void UrlEncode_never_throws_on_null()
    {
        Assert.Equal(string.Empty, FieldTransforms.UrlEncode(null));
        Assert.Equal("a%20b", FieldTransforms.UrlEncode("a b"));
    }

    [Fact]
    public void ResolveRelativeUrl_resolves_against_the_base_address()
    {
        var result = FieldTransforms.ResolveRelativeUrl("/download/1.torrent", new Uri("https://idx.example/search"));

        Assert.True(result.IsSuccess);
        Assert.Equal("https://idx.example/download/1.torrent", result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveRelativeUrl_fails_cleanly_on_hostile_input(string? raw)
    {
        var result = FieldTransforms.ResolveRelativeUrl(raw, new Uri("https://idx.example/"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.transform.resolve_url_failed", result.Error.Code);
    }

    [Fact]
    public void ExtractPlaceholders_finds_every_named_token_in_order()
    {
        var placeholders = FieldTransforms.ExtractPlaceholders("https://idx.example/search?q={{term}}&cat={{category}}");

        Assert.Equal(["term", "category"], placeholders);
    }

    [Fact]
    public void ExtractPlaceholders_returns_empty_for_a_template_with_none()
    {
        var placeholders = FieldTransforms.ExtractPlaceholders("https://idx.example/search");

        Assert.Empty(placeholders);
    }
}
