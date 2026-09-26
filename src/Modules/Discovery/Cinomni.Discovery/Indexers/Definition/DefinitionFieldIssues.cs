using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Results;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// The vocabulary <see cref="DefinitionResponseParser"/> reports a field rule's failure in, plus the
/// one way that report is allowed to reach a log. Kept beside the parser because the names are the
/// definition's own JSON keys: an issue has to name the rule an operator will go and edit.
/// </summary>
internal static class DefinitionFieldIssues
{
    /// <summary>A declared rule matched nothing, or matched an empty value.</summary>
    public const string MissingValueCode = "discovery.definition.field.no_value";

    /// <summary>A declared rule carries a transform that cannot produce the field's type at all.</summary>
    public const string UnsupportedTransformCode = "discovery.definition.field.unsupported_transform";

    /// <summary>A hostile response must not be able to inflate a dry-run body through its raw values.</summary>
    private const int MaxRawValueLength = 128;

    /// <summary>The definition's own field names, as an operator wrote them.</summary>
    public static class Names
    {
        public const string Title = "title";
        public const string DownloadUrl = "downloadUrl";
        public const string SizeBytes = "sizeBytes";
        public const string Seeders = "seeders";
        public const string Leechers = "leechers";
        public const string PublishedAt = "publishedAt";
    }

    public static DefinitionFieldIssue MissingValue(int rowIndex, string field, DefinitionFieldRule rule) => new(
        rowIndex,
        field,
        RawValue: null,
        MissingValueCode,
        $"Selector '{rule.Selector}' produced no value for '{field}' in this row.");

    public static DefinitionFieldIssue UnsupportedTransform(
        int rowIndex, string field, string? raw, DefinitionFieldRule rule) => new(
        rowIndex,
        field,
        Truncate(raw),
        UnsupportedTransformCode,
        $"Transform '{rule.Transform}' on '{field}' cannot produce that field's value.");

    public static DefinitionFieldIssue TransformFailed(int rowIndex, string field, string? raw, Error error) =>
        new(rowIndex, field, Truncate(raw), error.Code, error.Message);

    /// <summary>
    /// A one-line summary for the search path's log: how many values each rule dropped, and why.
    /// Deliberately without the raw values — the response body of a third-party site is not log
    /// material, and the operator-facing detail belongs in the dry run, which the operator drives.
    /// </summary>
    public static string Summarize(IReadOnlyList<DefinitionFieldIssue> issues) =>
        string.Join(
            ", ",
            issues
                .GroupBy(issue => (issue.Field, issue.Code))
                .OrderBy(group => group.Key.Field, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Code, StringComparer.Ordinal)
                .Select(group => $"{group.Key.Field}:{group.Key.Code} x{group.Count()}"));

    private static string? Truncate(string? raw) =>
        raw is null || raw.Length <= MaxRawValueLength ? raw : raw[..MaxRawValueLength];
}
