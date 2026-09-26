namespace Cinomni.Catalog.Persistence;

/// <summary>
/// One title a trending list has already been asked about. The unique key is the external identity,
/// so a second run of the same list updates the row instead of adding the work again.
/// </summary>
public sealed class ImportListEntry
{
    public const int ProviderMaxLength = 20;
    public const int ExternalIdMaxLength = 40;
    public const int TitleMaxLength = 500;
    public const int OutcomeMaxLength = 20;

    public Guid Id { get; init; }

    public required string Provider { get; init; }

    public required string ExternalId { get; init; }

    public required string Kind { get; init; }

    public required string Title { get; set; }

    public int? Year { get; set; }

    public Guid? WorkId { get; set; }

    /// <summary><c>Added</c> when this list created the work; <c>AlreadyKnown</c> when Catalog already had it.</summary>
    public required string Outcome { get; set; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }
}
