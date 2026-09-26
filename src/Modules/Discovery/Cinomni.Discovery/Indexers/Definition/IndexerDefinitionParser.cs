using System.Text.Json;
using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Results;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// Raw definition JSON to a validated <see cref="IndexerDefinitionDocument"/>, or a named error —
/// never a thrown exception. Pure and side-effect free, mirroring <see cref="TorznabFeedParser"/>:
/// the fiddly, security-relevant part (rejecting a hostile or malformed definition) is unit-tested
/// directly, with no HTTP, no filesystem, and no persistence involved.
/// <para>
/// Validation is first-error-wins (the same discipline <c>IndexerAdministration</c> already uses),
/// not accumulate-every-error: a definition is operator-authored content that gets fixed and
/// re-submitted, not a form with many independent fields to report at once.
/// </para>
/// </summary>
internal static class IndexerDefinitionParser
{
    /// <summary>Only version this parser understands; a future format change bumps this.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Definitions are hand-authored rules, not data dumps — this is generous but not unbounded.</summary>
    public const int MaxRawContentLength = 64 * 1024;

    public const int MaxAllowedRows = 500;
    public const int MaxAllowedPages = 10;
    public const int MaxDetailRequests = 20;

    private static readonly string[] AllowedContentKinds = ["Movie", "Series", "Season", "Episode"];
    private static readonly HashSet<string> SearchPlaceholders = new(StringComparer.OrdinalIgnoreCase) { "term", "category" };
    private static readonly HashSet<string> LoginUrlPlaceholders = new(StringComparer.OrdinalIgnoreCase);
    // A login field may carry the credential, or the CSRF token read from the login page — the
    // latter only when the definition actually declares where to read it (validated below), so a
    // placeholder that would resolve to an empty string is a rejected definition, not a silent one.
    private static readonly HashSet<string> LoginFieldPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "credential.username", "credential.password", "csrfToken",
    };
    private static readonly HashSet<string> CredentialFieldPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "credential.username", "credential.password",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <param name="rawJson">The definition as submitted or stored.</param>
    /// <param name="strictSelectors">
    /// Whether a CSS selector AngleSharp cannot compile is refused. On for what an administrator submits
    /// now — an upload, a dry run, a catalog install — and off for a definition already stored, which
    /// must keep parsing as it did: a broken optional selector there degrades (a session check that
    /// never matches falls back to status-code expiry) rather than taking the whole indexer down.
    /// </param>
    public static Result<IndexerDefinitionDocument> Parse(string rawJson, bool strictSelectors = false)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Fail("discovery.definition.empty", "Definition content must not be empty.");
        }

        if (rawJson.Length > MaxRawContentLength)
        {
            return Fail(
                "discovery.definition.too_large",
                $"Definition content must not exceed {MaxRawContentLength} characters.");
        }

        DefinitionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DefinitionDto>(rawJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Fail("discovery.definition.invalid_json", $"Definition is not valid JSON: {ex.Message}");
        }

        if (dto is null)
        {
            return Fail("discovery.definition.invalid_json", "Definition must be a JSON object.");
        }

        return Validate(dto, strictSelectors);
    }

    private static Result<IndexerDefinitionDocument> Validate(DefinitionDto dto, bool strictSelectors)
    {
        if (dto.SchemaVersion != SupportedSchemaVersion)
        {
            return Fail(
                "discovery.definition.unsupported_schema_version",
                $"Only schema version {SupportedSchemaVersion} is supported.");
        }

        if (!Enum.TryParse<ReleaseProtocol>(dto.ResultKind, ignoreCase: true, out var resultKind)
            || !Enum.IsDefined(resultKind))
        {
            return Fail("discovery.definition.invalid_result_kind", $"'{dto.ResultKind}' is not a known release protocol.");
        }

        if (dto.Search is null)
        {
            return Fail("discovery.definition.missing_search", "Definition must declare a 'search' section.");
        }

        var search = ValidateSearch(dto.Search);
        if (search.IsFailure)
        {
            return Result<IndexerDefinitionDocument>.Failure(search.Error);
        }

        DefinitionSession? session = null;
        if (dto.Session is not null)
        {
            var sessionResult = ValidateSession(dto.Session);
            if (sessionResult.IsFailure)
            {
                return Result<IndexerDefinitionDocument>.Failure(sessionResult.Error);
            }

            session = sessionResult.Value;
        }

        if (strictSelectors && InvalidSelectorPath(search.Value, session) is { } path)
        {
            return Fail("discovery.definition.invalid_selector", $"'{path}' is not a valid CSS selector.");
        }

        return Result<IndexerDefinitionDocument>.Success(
            new IndexerDefinitionDocument(dto.SchemaVersion, resultKind, search.Value, session));
    }

    /// <summary>
    /// The first CSS selector AngleSharp cannot compile, by its path in the definition, or null. Only
    /// selectors run against HTML are CSS: under a JSON response the row and field selectors are paths,
    /// while the detail page and the session pages are always HTML.
    /// </summary>
    private static string? InvalidSelectorPath(DefinitionSearch search, DefinitionSession? session)
    {
        var selectors = new List<(string Path, string? Selector)>();
        if (search.ResponseFormat == DefinitionResponseFormat.Html)
        {
            selectors.Add(("search.rows.selector", search.Rows.Selector));
            selectors.Add(("search.fields.title.selector", search.Fields.Title.Selector));
            selectors.Add(("search.fields.downloadUrl.selector", search.Fields.DownloadUrl.Selector));
            selectors.Add(("search.fields.sizeBytes.selector", search.Fields.SizeBytes?.Selector));
            selectors.Add(("search.fields.seeders.selector", search.Fields.Seeders?.Selector));
            selectors.Add(("search.fields.leechers.selector", search.Fields.Leechers?.Selector));
            selectors.Add(("search.fields.publishedAt.selector", search.Fields.PublishedAt?.Selector));
        }

        selectors.Add(("search.details.downloadUrl.selector", search.Details?.DownloadUrl.Selector));
        selectors.Add(("session.login.csrfToken.selector", session?.Login.CsrfToken?.Selector));
        selectors.Add(("session.check.selector", session?.Check?.Selector));

        return selectors
            .Where(entry => entry.Selector is not null && !CssSelectors.IsValid(entry.Selector))
            .Select(entry => entry.Path)
            .FirstOrDefault();
    }

    // -- search -----------------------------------------------------------------------------------

    private static Result<DefinitionSearch> ValidateSearch(SearchDto dto)
    {
        if (dto.Requests is not { Count: > 0 })
        {
            return FailSearch("discovery.definition.missing_requests", "Definition must declare at least one search request.");
        }

        var requests = new List<DefinitionSearchRequest>(dto.Requests.Count);
        foreach (var requestDto in dto.Requests)
        {
            var request = ValidateSearchRequest(requestDto);
            if (request.IsFailure)
            {
                return Result<DefinitionSearch>.Failure(request.Error);
            }

            requests.Add(request.Value);
        }

        if (!Enum.TryParse<DefinitionResponseFormat>(dto.ResponseFormat, ignoreCase: true, out var responseFormat)
            || !Enum.IsDefined(responseFormat))
        {
            return FailSearch("discovery.definition.invalid_response_format", $"'{dto.ResponseFormat}' is not 'Html' or 'Json'.");
        }

        if (dto.Rows is null || string.IsNullOrWhiteSpace(dto.Rows.Selector))
        {
            return FailSearch("discovery.definition.missing_row_selector", "Definition must declare 'search.rows.selector'.");
        }

        if (dto.Rows.MaxRows is not int maxRows || maxRows is < 1 or > MaxAllowedRows)
        {
            return FailSearch(
                "discovery.definition.invalid_max_rows",
                $"'search.rows.maxRows' must be between 1 and {MaxAllowedRows}.");
        }

        if (dto.Fields is null)
        {
            return FailSearch("discovery.definition.missing_fields", "Definition must declare 'search.fields'.");
        }

        var fields = ValidateFields(dto.Fields);
        if (fields.IsFailure)
        {
            return Result<DefinitionSearch>.Failure(fields.Error);
        }

        // 'search.pagination' is read and validated but not followed: a search reads the first page
        // only, because a definition has no way yet to say where the next page is. It is still
        // accepted so a stored definition that declares it keeps parsing; the model no longer carries
        // it, so nothing can mistake it for a ceiling that is being enforced.
        if (dto.Pagination is not null
            && (dto.Pagination.MaxPages is not int maxPages || maxPages is < 1 or > MaxAllowedPages))
        {
            return FailSearch(
                "discovery.definition.invalid_max_pages",
                $"'search.pagination.maxPages' must be between 1 and {MaxAllowedPages}.");
        }

        DefinitionDetails? details = null;
        if (dto.Details is not null)
        {
            if (dto.Details.DownloadUrl is null)
            {
                return FailSearch("discovery.definition.details.missing_download_url", "'search.details.downloadUrl' is required.");
            }

            if (dto.Details.MaxRequests is not int maxRequests || maxRequests is < 1 or > MaxDetailRequests)
            {
                return FailSearch("discovery.definition.details.invalid_max_requests",
                    $"'search.details.maxRequests' must be between 1 and {MaxDetailRequests}.");
            }

            var detailRule = ValidateFieldRule(dto.Details.DownloadUrl, "search.details.downloadUrl");
            if (detailRule.IsFailure)
            {
                return Result<DefinitionSearch>.Failure(detailRule.Error);
            }

            details = new DefinitionDetails(detailRule.Value, maxRequests);
        }

        return Result<DefinitionSearch>.Success(new DefinitionSearch(
            requests, responseFormat, new DefinitionRowRule(dto.Rows.Selector.Trim(), maxRows), fields.Value, details));
    }

    private static Result<DefinitionSearchRequest> ValidateSearchRequest(SearchRequestDto dto)
    {
        if (dto.ContentKinds is not { Count: > 0 })
        {
            return FailRequest("discovery.definition.missing_content_kinds", "Each search request must declare at least one content kind.");
        }

        foreach (var kind in dto.ContentKinds)
        {
            if (!AllowedContentKinds.Contains(kind))
            {
                return FailRequest("discovery.definition.invalid_content_kind", $"'{kind}' is not a known content kind.");
            }
        }

        if (!Enum.TryParse<DefinitionHttpMethod>(dto.Method, ignoreCase: true, out var method) || !Enum.IsDefined(method))
        {
            return FailRequest("discovery.definition.invalid_method", $"'{dto.Method}' is not 'Get' or 'Post'.");
        }

        var urlTemplate = ValidateUrlTemplate(dto.UrlTemplate, SearchPlaceholders, "search.requests[].urlTemplate");
        if (urlTemplate.IsFailure)
        {
            return Result<DefinitionSearchRequest>.Failure(urlTemplate.Error);
        }

        return Result<DefinitionSearchRequest>.Success(
            new DefinitionSearchRequest(dto.ContentKinds, method, urlTemplate.Value, dto.CategoryMap));
    }

    private static Result<DefinitionFields> ValidateFields(FieldsDto dto)
    {
        if (dto.Title is null)
        {
            return FailFields("discovery.definition.missing_title_field", "Definition must declare 'search.fields.title'.");
        }

        if (dto.DownloadUrl is null)
        {
            return FailFields("discovery.definition.missing_download_url_field", "Definition must declare 'search.fields.downloadUrl'.");
        }

        var title = ValidateFieldRule(dto.Title, "search.fields.title");
        if (title.IsFailure)
        {
            return Result<DefinitionFields>.Failure(title.Error);
        }

        var downloadUrl = ValidateFieldRule(dto.DownloadUrl, "search.fields.downloadUrl");
        if (downloadUrl.IsFailure)
        {
            return Result<DefinitionFields>.Failure(downloadUrl.Error);
        }

        var sizeBytes = ValidateOptionalFieldRule(dto.SizeBytes, "search.fields.sizeBytes");
        if (sizeBytes.IsFailure)
        {
            return Result<DefinitionFields>.Failure(sizeBytes.Error);
        }

        var seeders = ValidateOptionalFieldRule(dto.Seeders, "search.fields.seeders");
        if (seeders.IsFailure)
        {
            return Result<DefinitionFields>.Failure(seeders.Error);
        }

        var publishedAt = ValidateOptionalFieldRule(dto.PublishedAt, "search.fields.publishedAt");
        if (publishedAt.IsFailure)
        {
            return Result<DefinitionFields>.Failure(publishedAt.Error);
        }

        var leechers = ValidateOptionalFieldRule(dto.Leechers, "search.fields.leechers");
        if (leechers.IsFailure)
        {
            return Result<DefinitionFields>.Failure(leechers.Error);
        }

        return Result<DefinitionFields>.Success(
            new DefinitionFields(title.Value, downloadUrl.Value, sizeBytes.Value, seeders.Value, publishedAt.Value, leechers.Value));
    }

    private static Result<DefinitionFieldRule?> ValidateOptionalFieldRule(FieldRuleDto? dto, string path)
    {
        if (dto is null)
        {
            return Result<DefinitionFieldRule?>.Success(null);
        }

        var rule = ValidateFieldRule(dto, path);
        return rule.IsFailure
            ? Result<DefinitionFieldRule?>.Failure(rule.Error)
            : Result<DefinitionFieldRule?>.Success(rule.Value);
    }

    private static Result<DefinitionFieldRule> ValidateFieldRule(FieldRuleDto dto, string path)
    {
        if (string.IsNullOrWhiteSpace(dto.Selector))
        {
            return Result<DefinitionFieldRule>.Failure(
                new Error("discovery.definition.missing_field_selector", $"'{path}.selector' must not be empty."));
        }

        if (!Enum.TryParse<DefinitionFieldAttribute>(dto.Attribute, ignoreCase: true, out var attribute)
            || !Enum.IsDefined(attribute))
        {
            return Result<DefinitionFieldRule>.Failure(
                new Error("discovery.definition.invalid_field_attribute", $"'{path}.attribute' ('{dto.Attribute}') is not 'Text', 'Href', 'Src' or 'Value'."));
        }

        var transform = DefinitionFieldTransform.None;
        if (!string.IsNullOrWhiteSpace(dto.Transform)
            && (!Enum.TryParse(dto.Transform, ignoreCase: true, out transform) || !Enum.IsDefined(transform)))
        {
            return Result<DefinitionFieldRule>.Failure(
                new Error("discovery.definition.invalid_transform", $"'{path}.transform' ('{dto.Transform}') is not a known transform."));
        }

        if (transform == DefinitionFieldTransform.ParseDate && string.IsNullOrWhiteSpace(dto.DateFormat))
        {
            return Result<DefinitionFieldRule>.Failure(
                new Error("discovery.definition.missing_date_format", $"'{path}.dateFormat' is required when the transform is 'ParseDate'."));
        }

        return Result<DefinitionFieldRule>.Success(
            new DefinitionFieldRule(dto.Selector.Trim(), attribute, transform, dto.DateFormat));
    }

    // -- session ------------------------------------------------------------------------------------

    private static Result<DefinitionSession> ValidateSession(SessionDto dto)
    {
        if (dto.Login is null)
        {
            return Result<DefinitionSession>.Failure(
                new Error("discovery.definition.missing_login", "A declared 'session' must include a 'login' block."));
        }

        if (!Enum.TryParse<DefinitionHttpMethod>(dto.Login.Method, ignoreCase: true, out var method) || !Enum.IsDefined(method))
        {
            return Result<DefinitionSession>.Failure(
                new Error("discovery.definition.invalid_method", $"'session.login.method' ('{dto.Login.Method}') is not 'Get' or 'Post'."));
        }

        var urlTemplate = ValidateUrlTemplate(dto.Login.UrlTemplate, LoginUrlPlaceholders, "session.login.urlTemplate");
        if (urlTemplate.IsFailure)
        {
            return Result<DefinitionSession>.Failure(urlTemplate.Error);
        }

        if (dto.Login.Fields is not { Count: > 0 })
        {
            return Result<DefinitionSession>.Failure(
                new Error("discovery.definition.missing_login_fields", "'session.login.fields' must declare at least one field."));
        }

        var fields = new List<DefinitionLoginField>(dto.Login.Fields.Count);
        foreach (var fieldDto in dto.Login.Fields)
        {
            if (string.IsNullOrWhiteSpace(fieldDto.Name))
            {
                return Result<DefinitionSession>.Failure(
                    new Error("discovery.definition.missing_login_field_name", "Every 'session.login.fields[]' entry needs a name."));
            }

            // {{csrfToken}} is only a known placeholder in a definition that declares the reader it
            // resolves from; without one it validates as unknown rather than submitting an empty token.
            var allowedPlaceholders = dto.Login.CsrfToken is null ? CredentialFieldPlaceholders : LoginFieldPlaceholders;
            var placeholders = ValidatePlaceholders(
                fieldDto.ValueTemplate ?? string.Empty, allowedPlaceholders, $"session.login.fields['{fieldDto.Name}'].valueTemplate");
            if (placeholders.IsFailure)
            {
                return Result<DefinitionSession>.Failure(placeholders.Error);
            }

            fields.Add(new DefinitionLoginField(fieldDto.Name.Trim(), fieldDto.ValueTemplate ?? string.Empty));
        }

        DefinitionCsrfToken? csrfToken = null;
        if (dto.Login.CsrfToken is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Login.CsrfToken.Selector))
            {
                return Result<DefinitionSession>.Failure(
                    new Error("discovery.definition.missing_csrf_selector", "'session.login.csrfToken.selector' must not be empty."));
            }

            if (!Enum.TryParse<DefinitionFieldAttribute>(dto.Login.CsrfToken.Attribute, ignoreCase: true, out var csrfAttribute)
                || !Enum.IsDefined(csrfAttribute))
            {
                return Result<DefinitionSession>.Failure(new Error(
                    "discovery.definition.invalid_field_attribute",
                    $"'session.login.csrfToken.attribute' ('{dto.Login.CsrfToken.Attribute}') is not 'Text', 'Href', 'Src' or 'Value'."));
            }

            csrfToken = new DefinitionCsrfToken(dto.Login.CsrfToken.Selector.Trim(), csrfAttribute);
        }

        // A GET login puts every field in the query string, where it reaches the site access log
        // and any TLS-terminating middlebox on the way, and where one debugging environment variable
        // is enough to put it back into this application log too. A password has no business there,
        // so the combination is refused rather than documented.
        if (method == DefinitionHttpMethod.Get
            && fields.Any(field => FieldTransforms.ExtractPlaceholders(field.ValueTemplate)
                .Contains("credential.password", StringComparer.OrdinalIgnoreCase)))
        {
            return Result<DefinitionSession>.Failure(new Error(
                "discovery.definition.password_in_query",
                "A 'Get' login would carry the password in the query string; declare 'session.login.method' as 'Post'."));
        }

        var login = new DefinitionSessionLogin(method, urlTemplate.Value, fields, csrfToken);

        DefinitionSessionCheck? check = null;
        if (dto.Check is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Check.Selector))
            {
                return Result<DefinitionSession>.Failure(
                    new Error("discovery.definition.missing_check_selector", "'session.check.selector' must not be empty."));
            }

            check = new DefinitionSessionCheck(dto.Check.Selector.Trim());
        }

        return Result<DefinitionSession>.Success(new DefinitionSession(login, check));
    }

    // -- shared -------------------------------------------------------------------------------------

    private static Result<string> ValidateUrlTemplate(string? template, HashSet<string> allowedPlaceholders, string path)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return Result<string>.Failure(new Error("discovery.definition.missing_url_template", $"'{path}' must not be empty."));
        }

        // A leading '//' is protocol-relative and resolves to another host, not to a path on this
        // one. SameOrigin refuses it at request time, so it was never a way out of the origin; but a
        // definition carrying one validated cleanly in the dry run and then failed every search,
        // which is the validator claiming something it should not.
        if (template.StartsWith("//", StringComparison.Ordinal)
            || (!template.StartsWith("/", StringComparison.Ordinal)
                && !template.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !template.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            return Result<string>.Failure(new Error(
                "discovery.definition.invalid_url_scheme", $"'{path}' must be an absolute http(s) URL or a root-relative path."));
        }

        var placeholders = ValidatePlaceholders(template, allowedPlaceholders, path);
        return placeholders.IsFailure
            ? Result<string>.Failure(placeholders.Error)
            : Result<string>.Success(template.Trim());
    }

    private static Result ValidatePlaceholders(string template, HashSet<string> allowed, string path)
    {
        foreach (var placeholder in FieldTransforms.ExtractPlaceholders(template))
        {
            if (!allowed.Contains(placeholder))
            {
                return Result.Failure(new Error(
                    "discovery.definition.unknown_placeholder", $"'{path}' references unknown placeholder '{{{{{placeholder}}}}}'."));
            }
        }

        return Result.Success();
    }

    private static Result<IndexerDefinitionDocument> Fail(string code, string message) =>
        Result<IndexerDefinitionDocument>.Failure(new Error(code, message));

    private static Result<DefinitionSearch> FailSearch(string code, string message) =>
        Result<DefinitionSearch>.Failure(new Error(code, message));

    private static Result<DefinitionSearchRequest> FailRequest(string code, string message) =>
        Result<DefinitionSearchRequest>.Failure(new Error(code, message));

    private static Result<DefinitionFields> FailFields(string code, string message) =>
        Result<DefinitionFields>.Failure(new Error(code, message));

    // -- wire DTOs (raw JSON shape, distinct from the validated domain model above) -----------------

    private sealed record DefinitionDto(
        int SchemaVersion,
        string? ResultKind,
        SearchDto? Search,
        SessionDto? Session);

    private sealed record SearchDto(
        IReadOnlyList<SearchRequestDto>? Requests,
        string? ResponseFormat,
        RowsDto? Rows,
        FieldsDto? Fields,
        PaginationDto? Pagination,
        DetailsDto? Details);

    private sealed record SearchRequestDto(
        IReadOnlyList<string>? ContentKinds,
        string? Method,
        string? UrlTemplate,
        IReadOnlyDictionary<string, string>? CategoryMap);

    private sealed record RowsDto(string? Selector, int? MaxRows);

    private sealed record FieldRuleDto(string? Selector, string? Attribute, string? Transform, string? DateFormat);

    private sealed record FieldsDto(
        FieldRuleDto? Title,
        FieldRuleDto? DownloadUrl,
        FieldRuleDto? SizeBytes,
        FieldRuleDto? Seeders,
        FieldRuleDto? PublishedAt,
        FieldRuleDto? Leechers = null);

    private sealed record PaginationDto(int? MaxPages);

    private sealed record DetailsDto(FieldRuleDto? DownloadUrl, int? MaxRequests);

    private sealed record SessionDto(LoginDto? Login, CheckDto? Check);

    private sealed record CheckDto(string? Selector);

    private sealed record LoginDto(
        string? Method,
        string? UrlTemplate,
        IReadOnlyList<LoginFieldDto>? Fields,
        CsrfTokenDto? CsrfToken);

    private sealed record LoginFieldDto(string? Name, string? ValueTemplate);

    private sealed record CsrfTokenDto(string? Selector, string? Attribute);
}
