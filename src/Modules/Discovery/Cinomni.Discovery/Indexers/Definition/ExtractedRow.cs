namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// One result row's raw, untransformed field values, exactly as the row extractor read them —
/// before <see cref="FieldTransforms"/> turns a string into the typed value a
/// <see cref="Cinomni.Discovery.Contracts.ReleaseCandidate"/> needs. A missing field reads null,
/// never an exception; a hostile response yields fewer fields, not a crash.
/// </summary>
internal sealed record ExtractedRow(
    string? Title,
    string? DownloadUrl,
    string? SizeBytes,
    string? Seeders,
    string? PublishedAt,
    string? Leechers = null);
