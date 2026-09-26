namespace Cinomni.Discovery.Persistence;

/// <summary>
/// An operator-uploaded declarative indexer definition (post-MVP): the submitted content
/// verbatim, for audit/re-display, plus the identity fields a listing needs. The engine never
/// reads this row directly — it re-parses <see cref="RawContent"/> through
/// <c>IndexerDefinitionParser</c> at use time, so the validated shape can never drift from what the
/// current parser actually produces.
/// </summary>
public sealed class IndexerDefinition
{
    public const int NameMaxLength = 200;

    public Guid Id { get; init; }

    public required string Name { get; set; }

    public int SchemaVersion { get; set; }

    /// <summary>SHA-256 of <see cref="RawContent"/>, for audit and cheap change detection.</summary>
    public required string ContentHash { get; set; }

    /// <summary>The submitted JSON verbatim. Bounded by <c>IndexerDefinitionParser.MaxRawContentLength</c>.</summary>
    public required string RawContent { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
