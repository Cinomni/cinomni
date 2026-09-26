namespace Cinomni.Discovery.Persistence;

/// <summary>
/// An administrator-subscribed catalog source: an HTTPS URL publishing a JSON manifest of indexer
/// entries. Cinomni ships with none. The last successfully fetched manifest is kept as rows of
/// <see cref="IndexerCatalogSourceEntry"/>, so the catalog survives a restart and the source being
/// down; a failed refresh records its outcome here and leaves those rows alone.
/// </summary>
public sealed class IndexerCatalogSource
{
    public const int NameMaxLength = 200;

    public const int UrlMaxLength = 1000;

    public const int RefreshCodeMaxLength = 100;

    public const int RefreshMessageMaxLength = 500;

    public Guid Id { get; init; }

    public required string Name { get; set; }

    /// <summary>Normalised absolute https URL; unique, so one manifest is never subscribed twice.</summary>
    public required string Url { get; init; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When a refresh last reached an outcome, successful or not. Null before the first one.</summary>
    public DateTimeOffset? LastRefreshedAt { get; private set; }

    public bool? LastRefreshSucceeded { get; private set; }

    public string? LastRefreshCode { get; private set; }

    public string? LastRefreshMessage { get; private set; }

    /// <summary>Writes the four refresh fields together, so a stale message never outlives its code.</summary>
    public void RecordRefresh(DateTimeOffset at, bool succeeded, string code, string message)
    {
        LastRefreshedAt = at;
        LastRefreshSucceeded = succeeded;
        LastRefreshCode = code.Length > RefreshCodeMaxLength ? code[..RefreshCodeMaxLength] : code;
        LastRefreshMessage = message.Length > RefreshMessageMaxLength ? message[..RefreshMessageMaxLength] : message;
    }
}
