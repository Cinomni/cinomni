using System.Security.Cryptography;
using System.Text;
using Cinomni.Discovery.Contracts;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// Turns a definition-driven search response into the same <see cref="ReleaseCandidate"/> shape
/// <see cref="TorznabFeedParser"/> produces, so <c>ReleaseSearch</c>'s federation/dedup/persistence
/// code needs no changes for a definition-backed indexer. Pure and side-effect free: row extraction
/// (HTML or JSON) and the closed <see cref="FieldTransforms"/> vocabulary do all the parsing; this
/// type only wires the two together and skips a row that cannot become a valid candidate, exactly
/// as <see cref="TorznabFeedParser"/> skips an item with no title or download URL.
/// <para>
/// Every declared rule that produced nothing is reported alongside the candidates rather than
/// swallowed. A definition is operator-authored against a site that can change under it, so "this
/// rule stopped working" is a first-class outcome: without it a broken size rule and a site that
/// really says 0 bytes are the same answer, and the dry run built for debugging a definition cannot
/// tell the operator which one they are looking at.
/// </para>
/// </summary>
internal static class DefinitionResponseParser
{
    /// <param name="requestUri">
    /// The concrete URL the request was actually issued to — a relative <c>href</c>/<c>src</c> the
    /// response contains is resolved against this, never against a separately-declared "site" URL.
    /// </param>
    public static DefinitionExtractionResult Parse(
        IndexerDefinitionDocument document,
        string responseBody,
        string indexerName,
        Uri requestUri)
    {
        var rows = document.Search.ResponseFormat switch
        {
            DefinitionResponseFormat.Html => HtmlRowExtractor.ExtractRows(responseBody, document.Search),
            DefinitionResponseFormat.Json => JsonRowExtractor.ExtractRows(responseBody, document.Search),
            _ => [],
        };

        var candidates = new List<ReleaseCandidate>(rows.Count);
        var issues = new List<DefinitionFieldIssue>();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var candidate = ToCandidate(rows[rowIndex], rowIndex, document, indexerName, requestUri, issues);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return new DefinitionExtractionResult(candidates, issues);
    }

    private static ReleaseCandidate? ToCandidate(
        ExtractedRow row,
        int rowIndex,
        IndexerDefinitionDocument document,
        string indexerName,
        Uri requestUri,
        List<DefinitionFieldIssue> issues)
    {
        var fields = document.Search.Fields;

        var title = FieldTransforms.Trim(row.Title);
        if (title.Length == 0)
        {
            issues.Add(DefinitionFieldIssues.MissingValue(rowIndex, DefinitionFieldIssues.Names.Title, fields.Title));
            return null;
        }

        var downloadUrl = ResolveDownloadUrl(row.DownloadUrl, fields.DownloadUrl, requestUri, rowIndex, issues);
        if (downloadUrl is null)
        {
            return null;
        }

        // A failed size rule still yields the release, with the same neutral zero an indexer that
        // declares no size produces. That is a deliberate degradation and not an oversight: a
        // definition-backed site can change its size column overnight, and dropping every one of its
        // releases — or failing the whole federated search — would turn a formatting change into a
        // total loss of that indexer, which is exactly what Discovery's per-indexer isolation exists
        // to prevent. Discovery does not judge candidates; Decision does. The failure is reported
        // instead: the dry run shows it to the operator, and the search path logs it.
        var sizeBytes = ApplySizeTransform(row.SizeBytes, fields.SizeBytes, rowIndex, issues) ?? 0L;
        var seeders = ApplyCountTransform(row.Seeders, fields.Seeders, DefinitionFieldIssues.Names.Seeders, rowIndex, issues);
        var leechers = ApplyCountTransform(row.Leechers, fields.Leechers, DefinitionFieldIssues.Names.Leechers, rowIndex, issues);
        var publishedAt = ApplyDateTransform(row.PublishedAt, fields.PublishedAt, rowIndex, issues);

        // A definition has no indexer-supplied identity. Hash the URL rather than copying it into the
        // bounded release-guid fields used downstream; tracker-rich magnet links can be much longer.
        return new ReleaseCandidate(
            StableGuid(downloadUrl), title, downloadUrl, document.ResultKind, sizeBytes, seeders, publishedAt, indexerName,
            Leechers: leechers);
    }

    internal static string StableGuid(string downloadUrl) =>
        $"definition:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(downloadUrl)))}";

    private static string? ResolveDownloadUrl(
        string? raw, DefinitionFieldRule rule, Uri requestUri, int rowIndex, List<DefinitionFieldIssue> issues)
    {
        if (rule.Transform != DefinitionFieldTransform.ResolveRelativeUrl)
        {
            var trimmed = FieldTransforms.Trim(raw);
            if (trimmed.Length > 0)
            {
                return trimmed;
            }

            issues.Add(DefinitionFieldIssues.MissingValue(rowIndex, DefinitionFieldIssues.Names.DownloadUrl, rule));
            return null;
        }

        var resolved = FieldTransforms.ResolveRelativeUrl(raw, requestUri);
        if (resolved.IsSuccess)
        {
            return resolved.Value;
        }

        issues.Add(string.IsNullOrWhiteSpace(raw)
            ? DefinitionFieldIssues.MissingValue(rowIndex, DefinitionFieldIssues.Names.DownloadUrl, rule)
            : DefinitionFieldIssues.TransformFailed(
                rowIndex, DefinitionFieldIssues.Names.DownloadUrl, raw, resolved.Error));
        return null;
    }

    private static long? ApplySizeTransform(
        string? raw, DefinitionFieldRule? rule, int rowIndex, List<DefinitionFieldIssue> issues)
    {
        const string field = DefinitionFieldIssues.Names.SizeBytes;
        if (rule is null || !TryRequireValue(raw, rule, field, rowIndex, issues))
        {
            return null;
        }

        switch (rule.Transform)
        {
            case DefinitionFieldTransform.ParseSize:
                var size = FieldTransforms.ParseSize(raw);
                if (size.IsSuccess)
                {
                    return size.Value;
                }

                issues.Add(DefinitionFieldIssues.TransformFailed(rowIndex, field, raw, size.Error));
                return null;

            case DefinitionFieldTransform.ParseInt:
                var count = FieldTransforms.ParseInt(raw);
                if (count.IsSuccess)
                {
                    return count.Value;
                }

                issues.Add(DefinitionFieldIssues.TransformFailed(rowIndex, field, raw, count.Error));
                return null;

            default:
                issues.Add(DefinitionFieldIssues.UnsupportedTransform(rowIndex, field, raw, rule));
                return null;
        }
    }

    /// <summary>A peer count (seeders or leechers): optional, and only ever read with <c>ParseInt</c>.</summary>
    private static int? ApplyCountTransform(
        string? raw, DefinitionFieldRule? rule, string field, int rowIndex, List<DefinitionFieldIssue> issues)
    {
        if (rule is null || !TryRequireValue(raw, rule, field, rowIndex, issues))
        {
            return null;
        }

        if (rule.Transform != DefinitionFieldTransform.ParseInt)
        {
            issues.Add(DefinitionFieldIssues.UnsupportedTransform(rowIndex, field, raw, rule));
            return null;
        }

        var result = FieldTransforms.ParseInt(raw);
        if (result.IsSuccess)
        {
            return result.Value;
        }

        issues.Add(DefinitionFieldIssues.TransformFailed(rowIndex, field, raw, result.Error));
        return null;
    }

    private static DateTimeOffset? ApplyDateTransform(
        string? raw, DefinitionFieldRule? rule, int rowIndex, List<DefinitionFieldIssue> issues)
    {
        const string field = DefinitionFieldIssues.Names.PublishedAt;
        if (rule is null || !TryRequireValue(raw, rule, field, rowIndex, issues))
        {
            return null;
        }

        if (rule is not { Transform: DefinitionFieldTransform.ParseDate, DateFormat: { } format })
        {
            issues.Add(DefinitionFieldIssues.UnsupportedTransform(rowIndex, field, raw, rule));
            return null;
        }

        var result = FieldTransforms.ParseDate(raw, format);
        if (result.IsSuccess)
        {
            return result.Value;
        }

        issues.Add(DefinitionFieldIssues.TransformFailed(rowIndex, field, raw, result.Error));
        return null;
    }

    /// <summary>
    /// A declared rule that matched nothing is reported as a missing value rather than as a broken
    /// transform: "my selector is wrong" and "my transform is wrong" are different edits.
    /// </summary>
    private static bool TryRequireValue(
        string? raw, DefinitionFieldRule rule, string field, int rowIndex, List<DefinitionFieldIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        issues.Add(DefinitionFieldIssues.MissingValue(rowIndex, field, rule));
        return false;
    }
}
