using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Unit tests for the declarative indexer definition parser — a hostile/malformed definition must
/// come back as a named error, never a thrown exception, and every valid document must round-trip.
/// Every fixture below describes a fictional test site invented for these tests.
/// </summary>
public sealed class IndexerDefinitionParserTests
{
    private const string ValidDefinition = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [
              {
                "contentKinds": ["Movie"],
                "method": "Get",
                "urlTemplate": "https://idx.example/search?q={{term}}&cat={{category}}",
                "categoryMap": { "Movie": "51" }
              }
            ],
            "responseFormat": "Html",
            "rows": { "selector": "table.results tr.result", "maxRows": 200 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text" },
              "downloadUrl": { "selector": "td.dl a", "attribute": "Href", "transform": "ResolveRelativeUrl" },
              "sizeBytes": { "selector": "td.size", "attribute": "Text", "transform": "ParseSize" },
              "seeders": { "selector": "td.seed", "attribute": "Text", "transform": "ParseInt" },
              "publishedAt": { "selector": "td.date", "attribute": "Text", "transform": "ParseDate", "dateFormat": "yyyy-MM-dd" }
            },
            "pagination": { "maxPages": 3 }
          }
        }
        """;

    [Fact]
    public void Parses_a_valid_definition()
    {
        var result = IndexerDefinitionParser.Parse(ValidDefinition);

        Assert.True(result.IsSuccess);
        var document = result.Value;
        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(ReleaseProtocol.Torrent, document.ResultKind);
        Assert.Single(document.Search.Requests);
        Assert.Equal(DefinitionResponseFormat.Html, document.Search.ResponseFormat);
        Assert.Equal(200, document.Search.Rows.MaxRows);
        Assert.Equal(DefinitionFieldTransform.ParseSize, document.Search.Fields.SizeBytes?.Transform);
        Assert.Null(document.Session);
    }

    [Fact]
    public void Parses_a_definition_with_a_login_session()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.dl", "attribute": "Href" }
                }
              },
              "session": {
                "login": {
                  "method": "Post",
                  "urlTemplate": "https://idx.example/login",
                  "fields": [
                    { "name": "username", "valueTemplate": "{{credential.username}}" },
                    { "name": "password", "valueTemplate": "{{credential.password}}" }
                  ],
                  "csrfToken": { "selector": "input[name=csrf]", "attribute": "Text" }
                }
              }
            }
            """;

        var result = IndexerDefinitionParser.Parse(definition);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.Session);
        Assert.Equal(2, result.Value.Session!.Login.Fields.Count);
        Assert.NotNull(result.Value.Session.Login.CsrfToken);
        Assert.Null(result.Value.Session.Check);
    }

    [Fact]
    public void Parses_a_session_with_a_logged_out_check()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.dl", "attribute": "Href" }
                }
              },
              "session": {
                "login": {
                  "method": "Post",
                  "urlTemplate": "/login",
                  "fields": [{ "name": "username", "valueTemplate": "{{credential.username}}" }]
                },
                "check": { "selector": "form#login" }
              }
            }
            """;

        var result = IndexerDefinitionParser.Parse(definition);

        Assert.True(result.IsSuccess);
        Assert.Equal("form#login", result.Value.Session!.Check?.Selector);
    }

    [Fact]
    public void Rejects_a_session_check_with_an_empty_selector()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.dl", "attribute": "Href" }
                }
              },
              "session": {
                "login": {
                  "method": "Post",
                  "urlTemplate": "/login",
                  "fields": [{ "name": "username", "valueTemplate": "{{credential.username}}" }]
                },
                "check": { "selector": "   " }
              }
            }
            """;

        var result = IndexerDefinitionParser.Parse(definition);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.missing_check_selector", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_get_login_that_would_carry_the_password_in_the_query_string()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.dl", "attribute": "Href" }
                }
              },
              "session": {
                "login": {
                  "method": "Get",
                  "urlTemplate": "/login",
                  "fields": [
                    { "name": "username", "valueTemplate": "{{credential.username}}" },
                    { "name": "password", "valueTemplate": "{{credential.password}}" }
                  ]
                }
              }
            }
            """;

        var result = IndexerDefinitionParser.Parse(definition);

        // A query string reaches the site access log and every TLS-terminating middlebox on the way,
        // and one debugging environment variable puts it back in this application log too. The
        // combination is refused rather than documented as a caveat.
        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.password_in_query", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_protocol_relative_url_template()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "//other.example/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.dl", "attribute": "Href" }
                }
              }
            }
            """;

        var result = IndexerDefinitionParser.Parse(definition);

        // It looks root-relative and resolves to another host. SameOrigin refuses it at request time,
        // so it was never a way out of the origin — but a definition carrying one used to validate
        // cleanly in the dry run and then fail every search, which is the validator vouching for
        // something it should not.
        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_url_scheme", result.Error.Code);
    }

    [Fact]
    public void Allows_a_login_field_to_carry_the_csrf_token_when_the_reader_is_declared()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.dl", "attribute": "Href" }
                }
              },
              "session": {
                "login": {
                  "method": "Post",
                  "urlTemplate": "/login",
                  "fields": [
                    { "name": "csrf", "valueTemplate": "{{csrfToken}}" },
                    { "name": "username", "valueTemplate": "{{credential.username}}" }
                  ],
                  "csrfToken": { "selector": "input[name=csrf]", "attribute": "Value" }
                }
              }
            }
            """;

        var result = IndexerDefinitionParser.Parse(definition);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    [Fact]
    public void Rejects_a_csrf_placeholder_without_a_reader_to_resolve_it_from()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.dl", "attribute": "Href" }
                }
              },
              "session": {
                "login": {
                  "method": "Post",
                  "urlTemplate": "/login",
                  "fields": [{ "name": "csrf", "valueTemplate": "{{csrfToken}}" }]
                }
              }
            }
            """;

        var result = IndexerDefinitionParser.Parse(definition);

        // Not a new error code: with no declared reader, {{csrfToken}} is exactly as resolvable as
        // any other unknown placeholder, and the existing code already says so precisely.
        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.unknown_placeholder", result.Error.Code);
    }

    [Fact]
    public void Rejects_empty_content()
    {
        var result = IndexerDefinitionParser.Parse("   ");

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.empty", result.Error.Code);
    }

    [Fact]
    public void Rejects_content_over_the_size_ceiling()
    {
        var oversized = new string('x', IndexerDefinitionParser.MaxRawContentLength + 1);

        var result = IndexerDefinitionParser.Parse(oversized);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.too_large", result.Error.Code);
    }

    [Fact]
    public void Rejects_malformed_json_without_throwing()
    {
        var result = IndexerDefinitionParser.Parse("this is not { json");

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_json", result.Error.Code);
    }

    [Fact]
    public void Rejects_an_unsupported_schema_version()
    {
        var result = IndexerDefinitionParser.Parse(ValidDefinition.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.unsupported_schema_version", result.Error.Code);
    }

    [Fact]
    public void Rejects_an_unknown_result_kind()
    {
        var result = IndexerDefinitionParser.Parse(ValidDefinition.Replace("\"resultKind\": \"Torrent\"", "\"resultKind\": \"Carrier Pigeon\""));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_result_kind", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_result_kind_supplied_as_a_raw_undefined_number()
    {
        // Enum.TryParse also accepts a numeric string; an out-of-range number must not sneak past
        // as if it were a named value.
        var result = IndexerDefinitionParser.Parse(ValidDefinition.Replace("\"resultKind\": \"Torrent\"", "\"resultKind\": \"99\""));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_result_kind", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_url_template_with_a_non_http_scheme()
    {
        var result = IndexerDefinitionParser.Parse(
            ValidDefinition.Replace("https://idx.example/search", "javascript:alert(1)//"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_url_scheme", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_url_template_referencing_an_unknown_placeholder()
    {
        var result = IndexerDefinitionParser.Parse(
            ValidDefinition.Replace("{{category}}", "{{apikey}}"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.unknown_placeholder", result.Error.Code);
    }

    [Fact]
    public void Rejects_zero_max_rows()
    {
        var result = IndexerDefinitionParser.Parse(ValidDefinition.Replace("\"maxRows\": 200", "\"maxRows\": 0"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_max_rows", result.Error.Code);
    }

    [Fact]
    public void Rejects_max_rows_over_the_ceiling()
    {
        var result = IndexerDefinitionParser.Parse(
            ValidDefinition.Replace("\"maxRows\": 200", $"\"maxRows\": {IndexerDefinitionParser.MaxAllowedRows + 1}"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_max_rows", result.Error.Code);
    }

    [Fact]
    public void Rejects_max_pages_over_the_ceiling()
    {
        var result = IndexerDefinitionParser.Parse(
            ValidDefinition.Replace("\"maxPages\": 3", $"\"maxPages\": {IndexerDefinitionParser.MaxAllowedPages + 1}"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_max_pages", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_missing_title_field()
    {
        var result = IndexerDefinitionParser.Parse(ValidDefinition.Replace(
            """"
            "title": { "selector": "td.name a", "attribute": "Text" },
            """",
            string.Empty));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.missing_title_field", result.Error.Code);
    }

    [Fact]
    public void Rejects_an_unknown_field_attribute()
    {
        var result = IndexerDefinitionParser.Parse(ValidDefinition.Replace("\"attribute\": \"Href\"", "\"attribute\": \"innerHTML\""));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_field_attribute", result.Error.Code);
    }

    [Fact]
    public void Rejects_an_unknown_transform()
    {
        var result = IndexerDefinitionParser.Parse(ValidDefinition.Replace("\"transform\": \"ParseSize\"", "\"transform\": \"Eval\""));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.invalid_transform", result.Error.Code);
    }

    [Fact]
    public void Rejects_parse_date_without_a_date_format()
    {
        var result = IndexerDefinitionParser.Parse(ValidDefinition.Replace(
            """"
            { "selector": "td.date", "attribute": "Text", "transform": "ParseDate", "dateFormat": "yyyy-MM-dd" }
            """",
            """"
            { "selector": "td.date", "attribute": "Text", "transform": "ParseDate" }
            """"));

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.missing_date_format", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_login_field_value_template_with_an_unknown_placeholder()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "resultKind": "Torrent",
              "search": {
                "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "https://idx.example/search?q={{term}}" }],
                "responseFormat": "Html",
                "rows": { "selector": "tr.result", "maxRows": 50 },
                "fields": {
                  "title": { "selector": "a.title", "attribute": "Text" },
                  "downloadUrl": { "selector": "a.dl", "attribute": "Href" }
                }
              },
              "session": {
                "login": {
                  "method": "Post",
                  "urlTemplate": "https://idx.example/login",
                  "fields": [{ "name": "username", "valueTemplate": "{{term}}" }]
                }
              }
            }
            """;

        var result = IndexerDefinitionParser.Parse(definition);

        Assert.True(result.IsFailure);
        Assert.Equal("discovery.definition.unknown_placeholder", result.Error.Code);
    }
}
